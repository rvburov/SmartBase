using SmartBase.Integrations;

namespace SmartBase.Tests.Integration;

/// <summary>
/// Папка-приемник как протокол (<see cref="InboxFolder"/>) на настоящей файловой системе.
///
/// Проверяется не «читаются ли файлы», а четыре подводных камня сетевой шары: недописанный файл
/// не берется, захват достается ровно одному, брошенный захват возвращается, а квитанция служит
/// памятью о принятых сообщениях. Каждый из них на общем диске выстрелит обязательно, и каждый
/// легко потерять правкой.
/// </summary>
[Collection(EgressCollection.Name)]
public sealed class InboxFolderTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"sbinbox_{Guid.NewGuid():N}");

    public void Dispose()
    {
        EgressGate.Close();
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
        catch { /* временная папка: не убралась — уберет система */ }
    }

    private static readonly DateTimeOffset Now = new(2026, 9, 10, 12, 0, 0, TimeSpan.Zero);

    /// <summary>Политика, разрешающая ровно этот корень: приемник спрашивает ее сам.</summary>
    private void AllowRoot(string? root = null) =>
        EgressGate.AttachPolicy(EgressPolicySet.Of(new EgressPolicy(
            EgressMode.Internal,
            Hosts:           [],
            Networks:        [],
            Folders:         [root ?? _root],
            MaxRowsPerRun:   1000,
            MaxBytesPerDay:  1024 * 1024,
            MaxRunsPerHour:  100,
            ValidUntil:      null,
            UserMayAddHosts: false,
            Source:          "тест")));

    private InboxLayout Layout()
    {
        AllowRoot();
        var layout = InboxFolder.Open(_root, out var explanation);
        Assert.NotNull(layout);
        Assert.Equal("", explanation);
        return layout!;
    }

    /// <summary>Файл во «Входящих» с заданным возрастом — возраст решает, готов ли он.</summary>
    private static InboxFile Put(InboxLayout layout, string name, string text = "{}",
                                 TimeSpan? age = null)
    {
        var path = Path.Combine(layout.Incoming, name);
        File.WriteAllText(path, text);
        File.SetLastWriteTimeUtc(path, (Now - (age ?? TimeSpan.FromMinutes(1))).UtcDateTime);
        return new InboxFile(path, name);
    }

    // ── Корень обмена ─────────────────────────────────────────────────────────

    [Fact]
    public void Open_lays_out_the_four_folders()
    {
        var layout = Layout();

        Assert.True(Directory.Exists(layout.Incoming));
        Assert.True(Directory.Exists(layout.Processed));
        Assert.True(Directory.Exists(layout.Failed));
        Assert.True(Directory.Exists(layout.Receipts));
    }

    /// <summary>
    /// Папка-приемник — такая же дверь, как сетевой адрес: открыть ее себе самому пользователь
    /// не должен. Разрешение спрашивается там же, где появляются пути обмена, — обойти нечем.
    /// </summary>
    [Fact]
    public void Folder_outside_the_policy_is_refused()
    {
        AllowRoot(Path.Combine(Path.GetTempPath(), "sbinbox_чужой_корень"));

        Assert.Null(InboxFolder.Open(_root, out var explanation));
        Assert.NotEqual("", explanation);
        Assert.False(Directory.Exists(_root));   // и папок не наделал
    }

    [Fact]
    public void Without_a_policy_nothing_opens()
    {
        EgressGate.Close();

        Assert.Null(InboxFolder.Open(_root, out var explanation));
        Assert.NotEqual("", explanation);
    }

    [Fact]
    public void Empty_root_is_refused()
    {
        AllowRoot();
        Assert.Null(InboxFolder.Open("  ", out var explanation));
        Assert.Contains("не выбрана", explanation, StringComparison.Ordinal);
    }

    // ── Что готово к разбору ──────────────────────────────────────────────────

    /// <summary>
    /// Файл, который еще пишут, брать нельзя: половина выгрузки, уложенная в коллекцию, стоит
    /// дороже пяти секунд ожидания.
    /// </summary>
    [Fact]
    public void File_written_just_now_is_not_taken()
    {
        var layout = Layout();
        Put(layout, "заказы.json", age: TimeSpan.FromSeconds(1));

        Assert.Empty(InboxFolder.Ready(layout, Now));
        Assert.Single(InboxFolder.Ready(layout, Now + TimeSpan.FromSeconds(10)));
    }

    [Fact]
    public void Temporary_name_is_not_taken()
    {
        var layout = Layout();
        Put(layout, "заказы.json.tmp");

        Assert.Empty(InboxFolder.Ready(layout, Now));
    }

    [Fact]
    public void Claimed_file_is_not_taken_again()
    {
        var layout = Layout();
        var file   = Put(layout, "заказы.json");

        Assert.NotNull(InboxFolder.TryClaim(file, "ПК17", Now));
        Assert.Empty(InboxFolder.Ready(layout, Now));
    }

    /// <summary>
    /// Пока отправитель держит файл открытым, монопольное открытие не удастся — это и есть
    /// запасное правило готовности для тех, кто не переименовывает.
    /// </summary>
    [Fact]
    public void File_held_open_by_someone_else_is_not_taken()
    {
        var layout = Layout();
        var file   = Put(layout, "заказы.json");

        using (var held = new FileStream(file.Path, FileMode.Open, FileAccess.Read,
                                         FileShare.ReadWrite))
        {
            Assert.Empty(InboxFolder.Ready(layout, Now));
        }

        Assert.Single(InboxFolder.Ready(layout, Now));
    }

    /// <summary>Порядок важен, когда отправитель шлет правки одной записи подряд.</summary>
    [Fact]
    public void Ready_files_come_in_the_order_they_were_written()
    {
        var layout = Layout();
        Put(layout, "второй.json", age: TimeSpan.FromMinutes(1));
        Put(layout, "первый.json", age: TimeSpan.FromMinutes(5));

        var ready = InboxFolder.Ready(layout, Now);

        Assert.Equal(["первый.json", "второй.json"], ready.Select(f => f.Name));
    }

    // ── Захват ────────────────────────────────────────────────────────────────

    /// <summary>
    /// База открыта на нескольких машинах, файл видят все. Захват достается ровно одному —
    /// проигравший узнает об этом отказом файловой системы и идет дальше.
    /// </summary>
    [Fact]
    public void Claim_is_won_by_exactly_one()
    {
        var layout = Layout();
        var file   = Put(layout, "заказы.json");

        var first  = InboxFolder.TryClaim(file, "ПК17", Now);
        var second = InboxFolder.TryClaim(file, "ПК18", Now);

        Assert.NotNull(first);
        Assert.Null(second);
        Assert.True(File.Exists(first!));
        Assert.Contains("ПК17", first, StringComparison.Ordinal);
    }

    [Fact]
    public void Released_claim_comes_back_under_its_old_name()
    {
        var layout = Layout();
        var file   = Put(layout, "заказы.json");

        var claimed = InboxFolder.TryClaim(file, "ПК17", Now)!;
        InboxFolder.Release(claimed, file);

        Assert.True(File.Exists(file.Path));
        Assert.False(File.Exists(claimed));

        // Время записи у вернувшегося файла — момент захвата, поэтому выдержку он выждет
        // заново: пять секунд при обходе раз в полминуты. Сказано здесь, а не умолчано,
        // потому что это видимое следствие отметки времени при захвате.
        Assert.Empty(InboxFolder.Ready(layout, Now));
        Assert.Single(InboxFolder.Ready(layout, Now + InboxFolder.Settle));
    }

    /// <summary>
    /// Выключенная посреди обмена машина иначе унесла бы файл с собой навсегда, а отправитель
    /// никогда не узнал бы почему.
    /// </summary>
    [Fact]
    public void Abandoned_claim_returns_to_incoming()
    {
        var layout = Layout();
        var file   = Put(layout, "заказы.json");

        var claimed = InboxFolder.TryClaim(file, "ПК17", Now)!;
        File.SetLastWriteTimeUtc(claimed, (Now - TimeSpan.FromDays(1)).UtcDateTime);

        Assert.Equal(1, InboxFolder.ReclaimStale(layout, Now));
        Assert.True(File.Exists(file.Path));
    }

    /// <summary>
    /// Окно намеренно большое: отобрать файл у работающего соседа хуже, чем подождать лишний
    /// час. Свежий захват не трогается.
    /// </summary>
    [Fact]
    public void Fresh_claim_is_left_alone()
    {
        var layout = Layout();
        var file   = Put(layout, "заказы.json");

        var claimed = InboxFolder.TryClaim(file, "ПК17", Now)!;
        File.SetLastWriteTimeUtc(claimed, (Now - TimeSpan.FromMinutes(10)).UtcDateTime);

        Assert.Equal(0, InboxFolder.ReclaimStale(layout, Now));
        Assert.True(File.Exists(claimed));
    }

    /// <summary>
    /// Брошенность считается от ЗАХВАТА, а не от того, когда файл написали. Переименование
    /// время записи не трогает, поэтому без отметки файл, пролежавший во «Входящих» дольше
    /// <see cref="InboxFolder.StaleClaim"/> — ночная доставка, база была недоступна, — сразу
    /// после захвата выглядел бы брошенным: сосед вернул бы его во «Входящие» посреди
    /// обработки, и сообщение разобралось бы дважды.
    ///
    /// Этот тест намеренно НЕ ставит время руками, в отличие от соседних: именно рукой,
    /// поставленной после захвата, дефект и был закрыт от проверки.
    /// </summary>
    [Fact]
    public void Claim_of_a_long_waiting_file_is_not_abandoned_from_the_start()
    {
        var layout = Layout();
        var file   = Put(layout, "заказы.json", age: TimeSpan.FromDays(1));

        var claimed = InboxFolder.TryClaim(file, "ПК17", Now)!;

        Assert.Equal(0, InboxFolder.ReclaimStale(layout, Now));
        Assert.True(File.Exists(claimed));
        Assert.False(File.Exists(file.Path));
    }

    /// <summary>Отправитель прислал файл заново — брошенный захват на его место не возвращается.</summary>
    [Fact]
    public void Abandoned_claim_does_not_overwrite_a_resent_file()
    {
        var layout = Layout();
        var file   = Put(layout, "заказы.json", """{"новый":1}""");

        var claimed = InboxFolder.TryClaim(file, "ПК17", Now)!;
        File.SetLastWriteTimeUtc(claimed, (Now - TimeSpan.FromDays(1)).UtcDateTime);
        Put(layout, "заказы.json", """{"свежий":1}""");

        Assert.Equal(0, InboxFolder.ReclaimStale(layout, Now));
        Assert.Contains("свежий", File.ReadAllText(file.Path), StringComparison.Ordinal);
    }

    // ── Чтение файла ──────────────────────────────────────────────────────────

    /// <summary>
    /// Файл в общую папку кладет кто угодно с правами на запись, а читается он в строку целиком
    /// и разбирается поверх нее. Потолок спрашивается у файловой системы ДО чтения: узнать о
    /// беде, когда тридцать мегабайт уже в памяти, — не защита.
    /// </summary>
    [Fact]
    public void A_file_over_the_ceiling_is_refused_before_it_is_read()
    {
        var layout = Layout();
        var path   = Path.Combine(layout.Incoming, "огромный.json");

        // Длина ставится, а не пишется: на диск ложится дырявый файл, а размер файловая
        // система показывает настоящий — именно его потолок и спрашивает.
        using (var big = new FileStream(path, FileMode.Create, FileAccess.Write))
            big.SetLength(InboxFolder.MaxFileBytes + 1);

        var text = InboxFolder.ReadText(path, out var trouble);

        Assert.Null(text);
        Assert.Contains("слишком велик", trouble, StringComparison.Ordinal);
    }

    [Fact]
    public void A_file_within_the_ceiling_is_read_as_it_is()
    {
        var layout = Layout();
        var file   = Put(layout, "заказы.json", """{"Название":"Молоток"}""");

        var text = InboxFolder.ReadText(file.Path, out var trouble);

        Assert.Equal("""{"Название":"Молоток"}""", text);
        Assert.Equal("", trouble);
    }

    // ── Память о принятых сообщениях ──────────────────────────────────────────

    /// <summary>
    /// Память лежит в папке обмена, а не в app.db: папку видят все машины, и вторая доставка
    /// вполне может попасть на соседний компьютер.
    /// </summary>
    [Fact]
    public void Receipt_is_the_memory_of_accepted_messages()
    {
        var layout = Layout();

        Assert.False(InboxFolder.AlreadySeen(layout, "2026-09-10-0042"));

        InboxFolder.WriteReceipt(layout, InboxReceipt.Accept(
            "заказы.json", "2026-09-10-0042", "Заказы", 3,
            new BindingResult(3, 0, 0, 0, 0), Now));

        Assert.True(InboxFolder.AlreadySeen(layout, "2026-09-10-0042"));

        var past = InboxFolder.PastReceipt(layout, "2026-09-10-0042");
        Assert.NotNull(past);
        Assert.Equal(3, past!.Created);
    }

    /// <summary>
    /// Битая квитанция все равно означает, что сообщение уже приходило: обработать его второй
    /// раз хуже, чем не понять подробности прошлого раза.
    /// </summary>
    [Fact]
    public void Broken_receipt_still_counts_as_memory()
    {
        var layout = Layout();
        File.WriteAllText(Path.Combine(layout.Receipts, "x1.json"), "{ это не json");

        Assert.True(InboxFolder.AlreadySeen(layout, "x1"));
        Assert.Null(InboxFolder.PastReceipt(layout, "x1"));
    }

    /// <summary>
    /// Без идентификатора остается только ответ отправителю рядом с именем его файла, и защиты
    /// от повтора нет; поэтому режим «добавить» идентификатор и требует.
    /// </summary>
    [Fact]
    public void Message_without_an_identifier_leaves_no_memory()
    {
        var layout = Layout();

        InboxFolder.WriteReceipt(layout, InboxReceipt.Rejected(
            "заказы.json", "", "Заказы", 0, "коллекция не найдена", Now));

        Assert.True(File.Exists(Path.Combine(layout.Receipts, "заказы.result.json")));
        Assert.False(InboxFolder.AlreadySeen(layout, ""));
    }

    /// <summary>Правило «пиши через временное имя» приемник соблюдает и сам.</summary>
    [Fact]
    public void Receipt_leaves_no_half_written_file_behind()
    {
        var layout = Layout();

        InboxFolder.WriteReceipt(layout, InboxReceipt.Accept(
            "заказы.json", "x1", "Заказы", 1, new BindingResult(1, 0, 0, 0, 0), Now));

        Assert.Empty(Directory.EnumerateFiles(layout.Receipts, "*.tmp"));
    }

    // ── Раскладка обработанного ───────────────────────────────────────────────

    [Fact]
    public void Accepted_file_goes_to_the_month_folder()
    {
        var layout  = Layout();
        var file    = Put(layout, "заказы.json");
        var claimed = InboxFolder.TryClaim(file, "ПК17", Now)!;

        var target = InboxFolder.Finish(layout, claimed, file, accepted: true, Now);

        Assert.Equal(Path.Combine(layout.Processed, "2026-09", "заказы.json"), target);
        Assert.True(File.Exists(target));
        Assert.Empty(InboxFolder.Ready(layout, Now));
    }

    [Fact]
    public void Rejected_file_goes_to_errors()
    {
        var layout  = Layout();
        var file    = Put(layout, "заказы.json");
        var claimed = InboxFolder.TryClaim(file, "ПК17", Now)!;

        var target = InboxFolder.Finish(layout, claimed, file, accepted: false, Now);

        Assert.Equal(Path.Combine(layout.Failed, "заказы.json"), target);
        Assert.True(File.Exists(target));
    }

    /// <summary>
    /// Отправитель шлет файлы с повторяющимися именами постоянно; затирать вчерашний разбор
    /// сегодняшним нельзя — по нему разбираются, когда что-то разошлось.
    /// </summary>
    [Fact]
    public void Second_file_with_the_same_name_does_not_overwrite_the_first()
    {
        var layout = Layout();

        var first = Put(layout, "заказы.json", """{"первый":1}""");
        InboxFolder.Finish(layout, InboxFolder.TryClaim(first, "ПК17", Now)!, first, true, Now);

        var second = Put(layout, "заказы.json", """{"второй":1}""");
        var target = InboxFolder.Finish(layout, InboxFolder.TryClaim(second, "ПК17", Now)!, second, true, Now);

        Assert.Equal(Path.Combine(layout.Processed, "2026-09", "заказы (2).json"), target);
        Assert.Contains("первый",
            File.ReadAllText(Path.Combine(layout.Processed, "2026-09", "заказы.json")),
            StringComparison.Ordinal);
    }
}
