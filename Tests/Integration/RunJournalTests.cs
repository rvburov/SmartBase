using SmartBase.Database;
using SmartBase.Integrations;

namespace SmartBase.Tests.Integration;

/// <summary>
/// Журнал запусков обмена (<see cref="RunJournal"/>) на настоящем файле.
///
/// Проверяется то, ради чего он и заведен: числа, по которым считаются накопительные квоты, и
/// правило «в квоты идет только то, что уходит наружу». Плюс живучесть — журнал не должен
/// ронять обмен, о котором он всего лишь рассказывает.
///
/// Набор идет в общей очереди шлюза, хотя сети не касается: он трогает ОДИН НА ПРИЛОЖЕНИЕ
/// журнал (<c>RunJournal.Attach</c>), а на него же смотрят сценарии и прием. Отними у них
/// журнал посреди прогона — и строки, которые они только что записали, окажутся в чужом файле.
/// </summary>
[Collection(EgressCollection.Name)]
public sealed class RunJournalTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"sbjournal_{Guid.NewGuid():N}.db");

    public void Dispose()
    {
        RunJournal.Attach(null);
        try { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); } catch { /* best-effort */ }
        foreach (var suffix in new[] { "", "-wal", "-shm" })
        {
            try { if (File.Exists(_path + suffix)) File.Delete(_path + suffix); } catch { /* временный файл */ }
        }
    }

    private static readonly DateTimeOffset Now = new(2026, 9, 10, 12, 0, 0, TimeSpan.Zero);

    private RunJournal Journal() => new(_path);

    private static RunEntry Request(string name, long bytes, DateTimeOffset at, bool ok = true,
                                    int direction = 7) =>
        RunEntry.New(RunChannel.Request, name, "https://api.zavod.local/orders?key=***", ok,
                     ok ? "" : "чужая программа ответила 500", rows: 10, bytes: bytes,
                     elapsed: TimeSpan.FromMilliseconds(250), dbId: 0, at: at, counts: true,
                     direction: direction);

    private static RunEntry Inbox(string file, DateTimeOffset at) =>
        RunEntry.New(RunChannel.Inbox, file, @"P:\Обмен\Входящие", ok: true, "",
                     rows: 5, bytes: 4096, elapsed: TimeSpan.Zero, dbId: 3, at: at, counts: false,
                     direction: 0);

    // ── Запись и чтение ───────────────────────────────────────────────────────

    [Fact]
    public void Written_entry_reads_back_whole()
    {
        var journal = Journal();

        Assert.True(journal.Write(Request("Заказы", 2048, Now)) > 0);

        var row = journal.Recent().Single();

        Assert.Equal(RunChannel.Request, row.Channel);
        Assert.Equal("Заказы", row.Name);
        Assert.Equal(10, row.Rows);
        Assert.Equal(2048, row.Bytes);
        Assert.Equal(250, row.Ms);
        Assert.True(row.Ok);
        Assert.Equal(Now, row.Moment);
    }

    [Fact]
    public void Recent_puts_the_freshest_on_top()
    {
        var journal = Journal();

        journal.Write(Request("Первый", 1, Now - TimeSpan.FromMinutes(10)));
        journal.Write(Request("Второй", 1, Now));

        Assert.Equal(["Второй", "Первый"], journal.Recent().Select(r => r.Name));
    }

    /// <summary>
    /// Файл журнала не пересоздается: обновление программы не должно стирать историю обмена.
    /// </summary>
    [Fact]
    public void Journal_survives_a_reopen()
    {
        Journal().Write(Request("Заказы", 1024, Now));

        Assert.Single(Journal().Recent());
    }

    // ── Накопительные счетчики ────────────────────────────────────────────────

    [Fact]
    public void Counters_add_up_what_falls_into_the_window()
    {
        var journal = Journal();

        journal.Write(Request("Свежий",  1000, Now - TimeSpan.FromMinutes(10)));
        journal.Write(Request("Старый",  9000, Now - TimeSpan.FromHours(5)));

        var (runs, bytes) = journal.Since(Now - TimeSpan.FromHours(1));

        Assert.Equal(1, runs);
        Assert.Equal(1000, bytes);
    }

    /// <summary>
    /// Квоты ограничивают то, что уходит НАРУЖУ. Файл из папки идет внутрь и к ним отношения не
    /// имеет — иначе разбор входящих съедал бы право на исходящие запросы.
    /// </summary>
    [Fact]
    public void Inbox_files_are_journalled_but_not_counted()
    {
        var journal = Journal();

        journal.Write(Inbox("заказы.json", Now));
        journal.Write(Request("Заказы", 500, Now));

        var (runs, bytes) = journal.Since(Now - TimeSpan.FromHours(1));

        Assert.Equal(2, journal.Recent().Count);
        Assert.Equal(1, runs);
        Assert.Equal(500, bytes);
    }

    /// <summary>
    /// Неудачный запуск считается тоже: отказ чужой программы все равно был обращением к ней,
    /// и не считать его значило бы разрешить бесконечный перебор в ответ на ошибки.
    /// </summary>
    [Fact]
    public void Failed_runs_count_towards_the_quota()
    {
        var journal = Journal();

        journal.Write(Request("Заказы", 300, Now, ok: false));

        var (runs, bytes) = journal.Since(Now - TimeSpan.FromHours(1));

        Assert.Equal(1, runs);
        Assert.Equal(300, bytes);
    }

    /// <summary>Счетчик поверх журнала — то, что видит шлюз.</summary>
    [Fact]
    public void Counter_for_the_gate_reads_the_same_numbers()
    {
        var journal = Journal();
        journal.Write(Request("Заказы", 700, Now));

        var usage = journal.AsUsage();

        Assert.Equal(1,   usage.RunsSince(Now - TimeSpan.FromHours(1)));
        Assert.Equal(700, usage.BytesSince(Now - TimeSpan.FromHours(1), direction: 7));
    }

    // ── Расход по направлениям ────────────────────────────────────────────────

    /// <summary>
    /// Суточный потолок объема назначен направлению — ключу карточки запроса (10.6), — поэтому и
    /// счет ведется по нему. Сложи все вместе, и один шумный обмен останавливал бы прочие,
    /// настроенные правильно.
    /// </summary>
    [Fact]
    public void Bytes_are_counted_per_direction()
    {
        var journal = Journal();

        journal.Write(Request("Заказы",  500, Now, direction: 7));
        journal.Write(Request("Остатки", 300, Now, direction: 8));
        journal.Write(Request("Заказы",  200, Now, direction: 7));

        var since = Now - TimeSpan.FromHours(1);

        Assert.Equal(700,  journal.BytesSince(since, direction: 7));
        Assert.Equal(300,  journal.BytesSince(since, direction: 8));
        Assert.Equal(0,    journal.BytesSince(since, direction: 9));
        Assert.Equal(1000, journal.Since(since).Bytes);   // общая сумма осталась для экрана
    }

    /// <summary>
    /// Экрану нужен разрез по направлениям: общая цифра на вопрос «почему отказали» не отвечает,
    /// когда отказало одно направление при почти пустом счете у остальных. Имя берется у самой
    /// свежей строки — карточку могли переименовать.
    /// </summary>
    [Fact]
    public void Spending_by_direction_names_the_freshest_card_name()
    {
        var journal = Journal();

        journal.Write(Request("Заказы за сутки", 100, Now - TimeSpan.FromMinutes(10), direction: 7));
        journal.Write(Request("Заказы",          400, Now, direction: 7));
        journal.Write(Request("Остатки",          50, Now, direction: 8));
        journal.Write(Inbox("заказы.json", Now));   // файл идет внутрь и в разрез не попадает

        var spent = journal.SpentByDirection(Now - TimeSpan.FromHours(1));

        Assert.Equal(2, spent.Count);
        Assert.Equal((7, "Заказы", 500L), spent[0]);   // самое нагруженное — первым
        Assert.Equal((8, "Остатки", 50L), spent[1]);
    }

    /// <summary>
    /// Журнал, заведенный прежней версией программы, колонки направления не имеет. Она
    /// достраивается, а история остается: журнал копится годами, и ронять его при обновлении
    /// нельзя. Строки, написанные до правки, получают направление 0 — раскладывать их по
    /// карточкам задним числом не по чему.
    /// </summary>
    [Fact]
    public void An_old_journal_gains_the_direction_column_and_keeps_its_history()
    {
        using (var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={_path}"))
        {
            connection.Open();
            using var command = connection.CreateCommand();

            command.CommandText = """
                CREATE TABLE runs (
                    Id          INTEGER PRIMARY KEY AUTOINCREMENT,
                    At          TEXT    NOT NULL,
                    Channel     INTEGER NOT NULL,
                    Name        TEXT    NOT NULL,
                    Address     TEXT    NOT NULL,
                    Ok          INTEGER NOT NULL,
                    Explanation TEXT    NOT NULL,
                    Rows        INTEGER NOT NULL,
                    Bytes       INTEGER NOT NULL,
                    Ms          INTEGER NOT NULL,
                    DbId        INTEGER NOT NULL,
                    Counts      INTEGER NOT NULL
                );
                INSERT INTO runs (At, Channel, Name, Address, Ok, Explanation, Rows, Bytes, Ms, DbId, Counts)
                VALUES ('2026-09-10T11:00:00.0000000+00:00', 0, 'Старый запуск', 'https://api.zavod.local', 1, '', 3, 900, 10, 0, 1);
                """;

            command.ExecuteNonQuery();
        }

        var journal = Journal();
        var row     = journal.Recent().Single();

        Assert.Equal("Старый запуск", row.Name);
        Assert.Equal(0, row.Direction);
        Assert.Equal(900, journal.BytesSince(Now - TimeSpan.FromHours(2), direction: 0));

        // И новая строка ложится рядом со старой, а не вместо нее.
        journal.Write(Request("Заказы", 100, Now, direction: 7));
        Assert.Equal(2, journal.Recent().Count);
    }

    // ── Тела запроса и ответа ─────────────────────────────────────────────────

    /// <summary>
    /// Тела — то, ради чего журнал отладки и открывают: без них панель подробностей отвечает
    /// «не получилось» и не отвечает «почему».
    /// </summary>
    [Fact]
    public void Bodies_read_back_with_the_entry()
    {
        var journal = Journal();

        journal.Write(RunEntry.New(
            RunChannel.Request, "Заказы", "https://api.zavod.local/orders", ok: false,
            "чужая программа ответила 422", rows: 0, bytes: 120,
            elapsed: TimeSpan.FromMilliseconds(30), dbId: 0, at: Now, counts: true, direction: 7,
            sentBody: """{"since":"2026-09-10"}""",
            gotBody:  """{"error":"unknown field since"}"""));

        var row = journal.Recent().Single();

        Assert.Contains("since", row.SentBody, StringComparison.Ordinal);
        Assert.Contains("unknown field", row.GotBody, StringComparison.Ordinal);
    }

    /// <summary>
    /// Числа в журнале дешевы и нужны долго — по ним считаются квоты. Тела дороги и нужны
    /// коротко. Поэтому уборка стирает тела у строк старше своего порога, ОСТАВЛЯЯ сами строки:
    /// «запуск был, тело уже не хранится» честнее, чем пропавшая строка. Иначе журнал отладки
    /// вырос бы в сотни мегабайт на ровном месте.
    /// </summary>
    [Fact]
    public void Trim_drops_old_bodies_but_keeps_the_rows()
    {
        var journal = Journal();

        for (var i = 0; i < 10; i++)
            journal.Write(RunEntry.New(
                RunChannel.Request, $"Запуск {i}", "https://api.zavod.local", ok: true, "",
                rows: 1, bytes: 10, elapsed: TimeSpan.Zero, dbId: 0, at: Now,
                counts: true, direction: 7,
                sentBody: $"запрос {i}", gotBody: $"ответ {i}"));

        // Строк оставляем все десять, тел — только у трех последних.
        Assert.Equal(0, journal.Trim(keep: 100, keepBodies: 3));

        var rows = journal.Recent();

        Assert.Equal(10, rows.Count);
        Assert.All(rows.Take(3), r => Assert.NotEqual("", r.SentBody));
        Assert.All(rows.Skip(3), r =>
        {
            Assert.Equal("", r.SentBody);
            Assert.Equal("", r.GotBody);
            Assert.NotEqual("", r.Name);   // сама строка на месте
        });
    }

    // ── Уборка ────────────────────────────────────────────────────────────────

    /// <summary>Уборка идет по ключу, а не по дате: часы на машине переводят, а ключ растет всегда.</summary>
    [Fact]
    public void Trim_keeps_the_last_entries()
    {
        var journal = Journal();

        for (var i = 0; i < 10; i++)
            journal.Write(Request($"Запуск {i}", 1, Now));

        Assert.Equal(7, journal.Trim(keep: 3));

        var rows = journal.Recent();
        Assert.Equal(3, rows.Count);
        Assert.Equal("Запуск 9", rows[0].Name);
    }

    /// <summary>Рабочий журнал чистить пользователю можно — в отличие от журнала безопасности.</summary>
    [Fact]
    public void Clear_empties_the_journal()
    {
        var journal = Journal();
        journal.Write(Request("Заказы", 1, Now));

        journal.Clear();

        Assert.Empty(journal.Recent());
        Assert.Equal((0, 0L), journal.Since(Now - TimeSpan.FromDays(1)));
    }

    // ── Живучесть ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Обмен не должен падать из-за того, что о нем не удалось рассказать. Журнал не подключен —
    /// запись просто ничего не делает.
    /// </summary>
    [Fact]
    public void Writing_without_an_attached_journal_does_nothing()
    {
        RunJournal.Attach(null);

        RunJournal.Add(Request("Заказы", 1, Now));   // не бросает

        Assert.Null(RunJournal.Current);
    }

    [Fact]
    public void Attached_journal_takes_entries_from_anywhere()
    {
        var journal = Journal();
        RunJournal.Attach(journal);

        RunJournal.Add(Request("Заказы", 1, Now));

        Assert.Single(journal.Recent());
    }
}
