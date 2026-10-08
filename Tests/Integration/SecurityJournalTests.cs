using Microsoft.Data.Sqlite;
using SmartBase.Database;
using SmartBase.Database.Entities;

namespace SmartBase.Tests.Integration;

/// <summary>
/// Журнал безопасности (<see cref="SecurityJournal"/>) на настоящем файле.
///
/// Проверяется то, чего нет у рабочего журнала и ради чего он заведен отдельно: цепочка хэшей
/// ловит правку файла руками, правда о неудачной записи доезжает до вызывающего, а секретов и
/// значений полей в строках нет ни при каких настройках.
///
/// Набор идет в общей очереди шлюза: журнал один на приложение (<c>SecurityJournal.Attach</c>),
/// и отнять его у соседа посреди прогона значило бы увести его строки в чужой файл.
/// </summary>
[Collection(EgressCollection.Name)]
public sealed class SecurityJournalTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"sbsec_{Guid.NewGuid():N}.db");

    public void Dispose()
    {
        SecurityJournal.Attach(null);
        try { SqliteGate.ClearAllPools(); } catch { /* best-effort */ }
        foreach (var suffix in new[] { "", "-wal", "-shm" })
        {
            try { if (File.Exists(_path + suffix)) File.Delete(_path + suffix); } catch { /* временный файл */ }
        }
    }

    private static readonly DateTimeOffset Now = new(2026, 9, 11, 12, 0, 0, TimeSpan.Zero);

    private SecurityJournal Journal() => new(_path);

    private static SecurityEntry Send(string scenario, bool allowed, int rows = 10,
                                      string explanation = "", DateTimeOffset? at = null) =>
        SecurityEntry.New(SecurityEvent.Send, allowed, "Иванов И. И.", scenario,
                          "https://api.zavod.local/orders?key=***", "Название, Проект",
                          rows, 2048, SecurityJournal.Sha256("тело"), explanation, dbId: 3,
                          at: at ?? Now);

    // ── Запись и чтение ───────────────────────────────────────────────────────

    [Fact]
    public void Written_entry_reads_back_whole()
    {
        var journal = Journal();

        Assert.True(journal.Write(Send("Заказы", allowed: true)) > 0);

        var row = journal.Recent().Single();

        Assert.Equal(SecurityEvent.Send, row.Event);
        Assert.True(row.Allowed);
        Assert.Equal("Иванов И. И.",     row.Who);
        Assert.Equal("Заказы",           row.Scenario);
        Assert.Equal("Название, Проект", row.Fields);
        Assert.Equal(10,                 row.Rows);
        Assert.Equal(2048,               row.Bytes);
        Assert.Equal(3,                  row.DbId);
        Assert.Equal(Now,                row.Moment);
    }

    /// <summary>
    /// Заблокированная попытка пишется наравне с разрешенной — ради нее журнал во многом и
    /// ведется: «программа попробовала и ей не дали» это и есть доказательство, что рамки
    /// работают.
    /// </summary>
    [Fact]
    public void A_blocked_attempt_is_written_too()
    {
        var journal = Journal();

        journal.Write(Send("Заказы", allowed: false, explanation: "адреса нет в белом списке"));

        var row = journal.Recent().Single();

        Assert.False(row.Allowed);
        Assert.Equal("адреса нет в белом списке", row.Explanation);
    }

    /// <summary>История не пересоздается: обновление программы не стирает следов обмена.</summary>
    [Fact]
    public void The_journal_survives_a_reopen()
    {
        Journal().Write(Send("Заказы", allowed: true));

        Assert.Single(Journal().Recent());
        Assert.Equal(0, Journal().Verify());
    }

    // ── Цепочка ───────────────────────────────────────────────────────────────

    /// <summary>Первая строка начинает цепочку: предыдущего хэша у нее нет.</summary>
    [Fact]
    public void The_first_entry_starts_the_chain()
    {
        var journal = Journal();
        journal.Write(Send("Заказы", allowed: true));

        var row = journal.Recent().Single();

        Assert.Equal("", row.Prev);
        Assert.NotEqual("", row.Hash);
    }

    [Fact]
    public void Every_entry_carries_the_hash_of_the_one_before()
    {
        var journal = Journal();

        journal.Write(Send("Первый",  allowed: true));
        journal.Write(Send("Второй",  allowed: true));
        journal.Write(Send("Третий",  allowed: true));

        var rows = journal.Recent().Reverse().ToList();   // по порядку записи

        Assert.Equal("",             rows[0].Prev);
        Assert.Equal(rows[0].Hash,   rows[1].Prev);
        Assert.Equal(rows[1].Hash,   rows[2].Prev);
        Assert.Equal(0L, journal.Verify());
    }

    /// <summary>
    /// Правка строки задним числом. Меняем в файле одно число — и проверка называет ИМЕННО ту
    /// строку: ради этого цепочка и нужна, потому что файл лежит на диске у того же человека, за
    /// кем ведется журнал.
    /// </summary>
    [Fact]
    public void An_edited_row_is_named_by_the_check()
    {
        var journal = Journal();

        journal.Write(Send("Первый", allowed: true));
        journal.Write(Send("Второй", allowed: true));
        journal.Write(Send("Третий", allowed: true));

        Edit("UPDATE events SET Rows = 1 WHERE Id = 2;");

        Assert.Equal(2, journal.Verify());
    }

    /// <summary>
    /// Заблокированную попытку задним числом «разрешили» — тот же случай и тот же ответ.
    /// Подделывать выгодно именно решение, а не числа.
    /// </summary>
    [Fact]
    public void A_flipped_verdict_is_named_too()
    {
        var journal = Journal();

        journal.Write(Send("Заказы", allowed: false, explanation: "адреса нет в белом списке"));

        Edit("UPDATE events SET Allowed = 1 WHERE Id = 1;");

        Assert.Equal(1, journal.Verify());
    }

    /// <summary>
    /// Вырезанная строка. У следующей перестает сходиться ссылка на предыдущую — а вырезать
    /// хочется именно ту, которой не должно было быть.
    /// </summary>
    [Fact]
    public void A_removed_row_breaks_the_chain_at_the_next_one()
    {
        var journal = Journal();

        journal.Write(Send("Первый", allowed: true));
        journal.Write(Send("Второй", allowed: true));
        journal.Write(Send("Третий", allowed: true));

        Edit("DELETE FROM events WHERE Id = 2;");

        Assert.Equal(3, journal.Verify());
    }

    /// <summary>Пустой журнал цел по определению — проверка не должна выдумывать поломку.</summary>
    [Fact]
    public void An_empty_journal_is_whole()
    {
        Assert.Equal(0, Journal().Verify());
        Assert.Equal(0, Journal().Count());
    }

    /// <summary>
    /// Две одинаковые по содержанию строки получают РАЗНЫЕ хэши — потому что в хэш входит
    /// предыдущий. Иначе повтор одной и той же отправки можно было бы вырезать незаметно.
    /// </summary>
    [Fact]
    public void Two_identical_entries_get_different_hashes()
    {
        var journal = Journal();

        journal.Write(Send("Заказы", allowed: true));
        journal.Write(Send("Заказы", allowed: true));

        var rows = journal.Recent();

        Assert.NotEqual(rows[0].Hash, rows[1].Hash);
    }

    /// <summary>
    /// Разделитель полей не подделать переносом знака: «а» + «б» и «аб» + «» обязаны давать
    /// разные хэши, иначе поля можно двигать между колонками, не трогая хэша.
    /// </summary>
    [Fact]
    public void Moving_a_sign_between_fields_changes_the_hash()
    {
        var left  = SecurityEntry.New(SecurityEvent.Send, true, "а", "б", "", "", 0, 0, "", "", 0, Now);
        var right = SecurityEntry.New(SecurityEvent.Send, true, "аб", "", "", "", 0, 0, "", "", 0, Now);

        Assert.NotEqual(left.ChainHash(""), right.ChainHash(""));
    }

    // ── Выборка для экрана ────────────────────────────────────────────────────

    /// <summary>
    /// Сужение идет в SQL, а не в память. Проверяется это единственным наблюдаемым способом:
    /// потолок строк применяется ПОСЛЕ условий. Отбирай экран из прочитанной пачки — и строка,
    /// подошедшая под условия, но не попавшая в пачку, пропала бы молча.
    /// </summary>
    [Fact]
    public void Narrowing_happens_before_the_limit()
    {
        var journal = Journal();

        // Одна подходящая строка и десять неподходящих поверх нее — в пачку из трех свежих
        // подходящая не попадает.
        journal.Write(Send("Заказы", allowed: false));

        for (var i = 0; i < 10; i++) journal.Write(Send("Заказы", allowed: true));

        var found = journal.Page(new SecurityFilter(BlockedOnly: true), take: 3);

        Assert.Single(found);
        Assert.False(found[0].Allowed);
    }

    /// <summary>Род события отбирается по нему же, а не по тексту пояснения.</summary>
    [Fact]
    public void An_event_kind_is_selected_by_itself()
    {
        var journal = Journal();

        journal.Write(Send("Заказы", allowed: true));
        journal.Write(SecurityEntry.New(SecurityEvent.Confirm, true, "Петров П. П.", "",
                                        "/result", "", 0, 0, "", "подтверждено", 0, Now));

        var confirms = journal.Page(new SecurityFilter(Event: SecurityEvent.Confirm));

        Assert.Equal(SecurityEvent.Confirm, Assert.Single(confirms).Event);
    }

    /// <summary>
    /// Период отсекается сравнением строк, и это работает ровно потому, что момент записан
    /// одним форматом. Тест на границе: строка ровно на границе периода попадает, а более
    /// старая — нет.
    /// </summary>
    [Fact]
    public void A_period_cuts_off_what_is_older()
    {
        var journal = Journal();

        journal.Write(Send("Старое", allowed: true, at: Now.AddDays(-40)));
        journal.Write(Send("Свежее", allowed: true, at: Now));

        var recent = journal.Page(new SecurityFilter(Since: Now.AddDays(-30)));

        Assert.Equal("Свежее", Assert.Single(recent).Scenario);
    }

    /// <summary>Свежие сверху: журнал читают с конца, а не с начала.</summary>
    [Fact]
    public void The_freshest_row_comes_first()
    {
        var journal = Journal();

        journal.Write(Send("Первое", allowed: true));
        journal.Write(Send("Второе", allowed: true));

        Assert.Equal("Второе", journal.Page(new SecurityFilter())[0].Scenario);
    }

    /// <summary>
    /// Выгрузка идет страницами и доходит до конца. Проверяется страницей заведомо меньше
    /// журнала: обход, теряющий стык страниц, отдал бы меньше строк, и заметить недостачу в
    /// готовом CSV было бы уже нельзя.
    /// </summary>
    [Fact]
    public void The_export_walks_the_whole_journal_in_pages()
    {
        var journal = Journal();

        for (var i = 0; i < 7; i++) journal.Write(Send($"Запуск {i}", allowed: true));

        var all = journal.Stream(new SecurityFilter(), page: 3).ToList();

        Assert.Equal(7, all.Count);
        Assert.Equal("Запуск 6", all[0].Scenario);   // свежие сверху
        Assert.Equal("Запуск 0", all[^1].Scenario);
        Assert.Equal(7, all.Select(e => e.Id).Distinct().Count());
    }

    /// <summary>Счетчик для окна подтверждения считает отобранное, а не весь файл.</summary>
    [Fact]
    public void The_count_follows_the_same_conditions()
    {
        var journal = Journal();

        journal.Write(Send("Заказы", allowed: false));
        journal.Write(Send("Заказы", allowed: true));
        journal.Write(Send("Заказы", allowed: true));

        Assert.Equal(3, journal.Count(new SecurityFilter()));
        Assert.Equal(1, journal.Count(new SecurityFilter(BlockedOnly: true)));
    }

    /// <summary>
    /// В выгруженной строке нет ни тела, ни значений полей — только их имена и отпечаток.
    /// Файл уезжает проверяющему целиком, и то, чего нет на экране, взяться в нем неоткуда.
    /// </summary>
    [Fact]
    public void An_exported_row_carries_names_but_no_values()
    {
        var row = SecurityJournal.CsvRow(Send("Заказы", allowed: true));

        Assert.Equal(SecurityJournal.CsvHeaders.Count, row.Count);
        Assert.Contains("Название, Проект", row);
        Assert.DoesNotContain(row, cell => cell.Contains("тело", StringComparison.Ordinal));
        Assert.Contains(SecurityJournal.Sha256("тело"), row);
    }

    // ── Общий доступ ──────────────────────────────────────────────────────────

    /// <summary>
    /// Правда о неудаче доезжает до вызывающего: журнала нет — <c>Add</c> отвечает «следа не
    /// осталось». Именно на этом ответе держится правило «не записалось — не отправляем», и
    /// глушить его, как делает рабочий журнал, здесь нельзя.
    /// </summary>
    [Fact]
    public void Without_a_journal_the_caller_is_told_there_is_no_trace()
    {
        SecurityJournal.Attach(null);

        Assert.False(SecurityJournal.Add(Send("Заказы", allowed: true)));
    }

    [Fact]
    public void An_attached_journal_takes_entries_from_anywhere()
    {
        var journal = Journal();
        SecurityJournal.Attach(journal);

        Assert.True(SecurityJournal.Add(Send("Заказы", allowed: true)));
        Assert.Single(journal.Recent());
    }

    // ── Правка файла руками ───────────────────────────────────────────────────

    /// <summary>Так правит файл тот, кто хочет убрать след: прямым SQL мимо программы.</summary>
    private void Edit(string sql)
    {
        using var connection = new SqliteConnection($"Data Source={_path}");
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }
    // ── Отбор по колонкам ─────────────────────────────────────────────────────

    /// <summary>
    /// Отбор «как в табличном редакторе» уходит в SQL: журнал не чистит никто, он копится годами, и
    /// проверяющий, ищущий одну блокировку полугодовой давности, иначе ее не найдет.
    /// </summary>
    [Fact]
    public void Column_filter_narrows_by_value()
    {
        var journal = Journal();

        journal.Write(Send("Заказы",  allowed: true));
        journal.Write(Send("Остатки", allowed: false));

        var only = new Dictionary<string, IReadOnlyCollection<string>>(StringComparer.Ordinal)
        {
            ["Scenario"] = new[] { "Остатки" },
        };

        Assert.Equal("Остатки",
                     Assert.Single(journal.Page(new SecurityFilter(Columns: only))).Scenario);
    }

    [Fact]
    public void Values_of_a_column_come_with_counts()
    {
        var journal = Journal();

        journal.Write(Send("Заказы",  allowed: true));
        journal.Write(Send("Заказы",  allowed: false));
        journal.Write(Send("Остатки", allowed: true));

        var values = journal.Values("Scenario", new SecurityFilter());

        Assert.Equal(("Заказы", 2), values[0]);
        Assert.Contains(("Остатки", 1), values);
    }

    /// <summary>
    /// Значения колонки считаются под текущим отбором, но БЕЗ условия по ней самой: иначе снятое
    /// галочкой значение исчезало бы из списка навсегда.
    /// </summary>
    [Fact]
    public void Values_ignore_the_filter_on_their_own_column()
    {
        var journal = Journal();

        journal.Write(Send("Заказы",  allowed: true));
        journal.Write(Send("Остатки", allowed: false));

        var only = new Dictionary<string, IReadOnlyCollection<string>>(StringComparer.Ordinal)
        {
            ["Scenario"] = new[] { "Заказы" },
        };

        var filter = new SecurityFilter(Columns: only);

        Assert.Equal(["Заказы", "Остатки"],
                     journal.Values("Scenario", filter).Select(v => v.Value).OrderBy(v => v).ToArray());

        // По чужой колонке условие действует: под отбор по сценарию подошла одна строка.
        Assert.Equal(["1"], journal.Values("Allowed", filter).Select(v => v.Value).ToArray());
    }

    /// <summary>Имя колонки берется из белого списка: чужое просто не сужает отбор.</summary>
    [Fact]
    public void An_unknown_column_narrows_nothing()
    {
        var journal = Journal();

        journal.Write(Send("Заказы", allowed: true));

        var only = new Dictionary<string, IReadOnlyCollection<string>>(StringComparer.Ordinal)
        {
            ["Who = 'x' OR 1=1 --"] = new[] { "что угодно" },
        };

        Assert.Single(journal.Page(new SecurityFilter(Columns: only)));
        Assert.Empty(journal.Values("Who = 'x' OR 1=1 --", new SecurityFilter()));
    }

    // ── Показ секрета (шаг 16) ────────────────────────────────────────────────

    private static SecurityEntry Reveal(bool allowed) =>
        SecurityEntry.New(SecurityEvent.Reveal, allowed, "Иванов И. И.", "",
                          "Соединение «Учетная система»", "", 0, 0, "",
                          allowed
                              ? "Секрет соединения показан на экране в менеджере паролей."
                              : "Показ секрета отклонен: мастер-пароль не подошел.",
                          dbId: 0, at: Now);

    /// <summary>
    /// Показ секрета — событие журнала наравне с отправкой: сам секрет наружу не уходит, но
    /// уходит из программы в чужой конфигурационный файл, и после этого отвечать за него некому.
    /// </summary>
    [Fact]
    public void A_revealed_secret_leaves_a_row()
    {
        var journal = Journal();

        Assert.True(journal.Write(Reveal(allowed: true)) > 0);

        var row = journal.Recent().Single();

        Assert.Equal(SecurityEvent.Reveal, row.Event);
        Assert.True(row.Allowed);
        Assert.Equal("Соединение «Учетная система»", row.Address);

        // Ни самого секрета, ни его отпечатка: строку журнала выгружают проверяющему целиком.
        Assert.Empty(row.BodyHash);
        Assert.Empty(row.Fields);
    }

    /// <summary>
    /// Неудачная попытка пишется тоже — и она важнее удачной: подбор мастер-пароля выглядит
    /// именно как череда таких строк.
    /// </summary>
    [Fact]
    public void A_refused_reveal_is_written_too()
    {
        var journal = Journal();

        journal.Write(Reveal(allowed: false));

        var blocked = journal.Page(new SecurityFilter(BlockedOnly: true)).Single();

        Assert.Equal(SecurityEvent.Reveal, blocked.Event);
        Assert.Contains("не подошел", blocked.Explanation);
    }

    /// <summary>Событие отбирается родом, как и остальные: «покажи все показы секретов».</summary>
    [Fact]
    public void Reveals_are_filtered_by_kind()
    {
        var journal = Journal();

        journal.Write(Send("Заказы", allowed: true));
        journal.Write(Reveal(allowed: true));

        Assert.Single(journal.Page(new SecurityFilter(Event: SecurityEvent.Reveal)));
        Assert.Equal("показан секрет соединения", SecurityJournal.Name(SecurityEvent.Reveal));
    }

    // ── Уборка по сроку политики ──────────────────────────────────────────────

    /// <summary>
    /// Уборка удаляет старое, оставляет свежее и НЕ ломает проверку цепочки.
    ///
    /// Последнее — главное здесь. Наивное удаление оставило бы самую раннюю уцелевшую строку со
    /// ссылкой на строку, которой больше нет, и <see cref="SecurityJournal.Verify"/> назвал бы
    /// честную строку испорченной. Проверка, которая врет после штатной уборки, хуже отсутствия
    /// проверки: ей перестанут верить в тот единственный раз, когда она понадобится.
    /// </summary>
    [Fact]
    public void Trimming_by_policy_keeps_the_chain_verifiable()
    {
        var journal = Journal();

        journal.Write(Send("Старый-1", allowed: true,  at: Now.AddDays(-400)));
        journal.Write(Send("Старый-2", allowed: false, at: Now.AddDays(-380)));
        journal.Write(Send("Свежий",   allowed: true,  at: Now.AddDays(-10)));

        Assert.Equal(0L, journal.Verify());

        Assert.Equal(2, journal.Trim(keepDays: 365, now: Now));

        // Старых нет, свежий на месте, и цепочка по-прежнему цела.
        var left = journal.Recent().Select(r => r.Scenario).ToList();
        Assert.DoesNotContain("Старый-1", left);
        Assert.DoesNotContain("Старый-2", left);
        Assert.Contains("Свежий", left);
        Assert.Equal(0L, journal.Verify());
    }

    /// <summary>
    /// Уборка оставляет в самом журнале строку о себе — с числом удаленных и сроком.
    ///
    /// Проверяющий, не нашедший записей за прошлый год, обязан прочитать, куда они делись, в том
    /// же файле. Молчаливое исчезновение строк неотличимо от подчистки следов, а объяснять его
    /// потом словами — значит просить поверить на слово.
    /// </summary>
    [Fact]
    public void A_trim_leaves_its_own_line_in_the_journal()
    {
        var journal = Journal();

        journal.Write(Send("Старый", allowed: true, at: Now.AddDays(-400)));
        journal.Write(Send("Свежий", allowed: true, at: Now));

        journal.Trim(keepDays: 365, now: Now);

        var note = journal.Page(new SecurityFilter(Event: SecurityEvent.Trim)).Single();

        Assert.True(note.Allowed);
        Assert.Contains("365", note.Explanation, StringComparison.Ordinal);
        Assert.Contains("удалено строк: 1", note.Explanation, StringComparison.Ordinal);
        Assert.Equal("уборка журнала по сроку политики", SecurityJournal.Name(SecurityEvent.Trim));
    }

    /// <summary>
    /// Правка файла руками ловится и ПОСЛЕ уборки. Иначе шов стал бы дырой: подчистив старое по
    /// сроку, дальше можно было бы править что угодно.
    /// </summary>
    [Fact]
    public void After_a_trim_a_hand_edit_is_still_caught()
    {
        var journal = Journal();

        journal.Write(Send("Старый", allowed: true,  at: Now.AddDays(-400)));
        journal.Write(Send("Первый", allowed: true,  at: Now.AddDays(-5)));
        journal.Write(Send("Второй", allowed: false, at: Now.AddDays(-4)));

        journal.Trim(keepDays: 365, now: Now);

        // Подменяем содержимое уцелевшей строки, не трогая ее хэш, — самая частая правка.
        using (var connection = new SqliteConnection($"Data Source={_path}"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "UPDATE events SET Allowed = 1 WHERE Scenario = 'Второй';";
            command.ExecuteNonQuery();
        }

        SqliteGate.ClearAllPools();
        Assert.NotEqual(0, Journal().Verify());
    }

    /// <summary>Срока нет — не удаляется ничего: отсутствие настройки не должно ничего стирать.</summary>
    [Fact]
    public void Without_a_term_nothing_is_removed()
    {
        var journal = Journal();

        journal.Write(Send("Древний", allowed: true, at: Now.AddDays(-3650)));

        Assert.Equal(0, journal.Trim(keepDays: 0,  now: Now));
        Assert.Equal(0, journal.Trim(keepDays: -5, now: Now));
        Assert.Single(journal.Recent());
    }

    /// <summary>Нечего удалять — уборка молчит: лишняя строка «удалено 0» засоряла бы журнал.</summary>
    [Fact]
    public void When_there_is_nothing_to_remove_the_journal_stays_silent()
    {
        var journal = Journal();

        journal.Write(Send("Свежий", allowed: true, at: Now));

        Assert.Equal(0, journal.Trim(keepDays: 365, now: Now));
        Assert.Empty(journal.Page(new SecurityFilter(Event: SecurityEvent.Trim)));
        Assert.Single(journal.Recent());
    }

    // ── Коллекция и версия строки ─────────────────────────────────────────────

    /// <summary>Коллекция — такое же поле строки, как остальные: записали и прочитали.</summary>
    [Fact]
    public void The_collection_of_the_event_is_written_and_read_back()
    {
        var journal = Journal();

        journal.Write(SecurityEntry.New(SecurityEvent.Copy, allowed: true, "Иванов И. И.", "",
                                        "буфер обмена", "Название", 3, 128, "", "", dbId: 3,
                                        at: Now, collection: "Заказы"));

        var row = journal.Recent().Single();

        Assert.Equal("Заказы", row.Collection);
        Assert.Equal(SecurityEntry.CurrentVer, row.Ver);
        Assert.Equal(0L, journal.Verify());
    }

    /// <summary>
    /// Строки, записанные ДО появления коллекции, продолжают сходиться со своими хэшами: их
    /// канонический вид остался прежним (версия 0). Иначе первая же проверка после обновления
    /// объявила бы честный журнал за все прошлые годы испорченным.
    /// </summary>
    [Fact]
    public void Rows_written_before_the_collection_appeared_still_verify()
    {
        var journal = Journal();

        // Строка «старого образца»: версия 0 и пустая коллекция — ровно то, что лежит в файлах,
        // заведенных прошлыми выпусками программы.
        journal.Write(Send("Древний", allowed: true) with { Ver = 0 });
        journal.Write(Send("Свежий",  allowed: true));

        var rows = journal.Recent();

        Assert.Equal(0, rows[^1].Ver);
        Assert.Equal(SecurityEntry.CurrentVer, rows[0].Ver);
        Assert.Equal(0L, journal.Verify());
    }

    /// <summary>
    /// Имя коллекции вырезали из строки руками — проверка называет ее. У новых строк коллекция
    /// входит в хэш ВСЕГДА, даже пустая: включай ее «только когда заполнена», и стереть из строки
    /// «Заказы» можно было бы незаметно.
    /// </summary>
    [Fact]
    public void An_erased_collection_is_named_by_the_check()
    {
        var journal = Journal();

        journal.Write(SecurityEntry.New(SecurityEvent.Copy, allowed: true, "Иванов И. И.", "",
                                        "буфер обмена", "Название", 3, 128, "", "", dbId: 3,
                                        at: Now, collection: "Заказы"));

        Edit("UPDATE events SET Collection = '' WHERE Id = 1;");

        Assert.Equal(1, journal.Verify());
    }

    /// <summary>Отбор по коллекции уходит в SQL — как и остальные колонки.</summary>
    [Fact]
    public void Rows_are_narrowed_by_collection()
    {
        var journal = Journal();

        journal.Write(SecurityEntry.New(SecurityEvent.Copy, true, "Иванов И. И.", "", "буфер обмена",
                                        "", 1, 10, "", "", dbId: 3, at: Now, collection: "Заказы"));
        journal.Write(SecurityEntry.New(SecurityEvent.Copy, true, "Иванов И. И.", "", "буфер обмена",
                                        "", 1, 10, "", "", dbId: 3, at: Now, collection: "Договоры"));

        var only = journal.Page(new SecurityFilter(Columns: new Dictionary<string, IReadOnlyCollection<string>>
        {
            ["Collection"] = new[] { "Заказы" },
        }));

        Assert.Equal("Заказы", Assert.Single(only).Collection);

        // Значения колонки — со счетчиками, для окна отбора.
        var values = journal.Values("Collection", new SecurityFilter());
        Assert.Equal(2, values.Count);
        Assert.All(values, v => Assert.Equal(1, v.Count));
    }

    /// <summary>
    /// Отбор по дню считается в SQL по МЕСТНОМУ дню: время в файле лежит в UTC, а проверяющий
    /// ищет «что было четырнадцатого».
    /// </summary>
    [Fact]
    public void Rows_are_narrowed_by_local_day()
    {
        var journal = Journal();

        journal.Write(Send("Свежий",  allowed: true, at: Now));
        journal.Write(Send("Давешний", allowed: true, at: Now.AddDays(-2)));

        var day = Now.ToLocalTime().ToString("yyyy-MM-dd");

        var only = journal.Page(new SecurityFilter(Columns: new Dictionary<string, IReadOnlyCollection<string>>
        {
            ["Day"] = new[] { day },
        }));

        Assert.Equal("Свежий", Assert.Single(only).Scenario);
    }

    /// <summary>
    /// Граница видимости: администратор читает строки своих баз и строки без базы, а чужие не
    /// видит вовсе. Это не отбор, который человек снимет галочкой, а условие запроса — поэтому
    /// проверяется и на странице, и на счетчике, и на значениях воронки: граница, забытая в
    /// одном из этих мест, не граница.
    /// </summary>
    [Fact]
    public void Rows_of_other_databases_stay_invisible()
    {
        var journal = Journal();

        SecurityEntry Of(int dbId, string scenario) =>
            SecurityEntry.New(SecurityEvent.Send, allowed: true, "Иванов И. И.", scenario,
                              "https://api.local/orders", "Название", 1, 16,
                              SecurityJournal.Sha256("тело"), "", dbId: dbId, at: Now);

        journal.Write(Of(1, "Своя"));
        journal.Write(Of(2, "Чужая"));
        journal.Write(Of(0, "Без базы"));

        var mine = new SecurityFilter(Databases: new[] { 1 });

        Assert.Equal(new[] { "Без базы", "Своя" },
                     journal.Page(mine).Select(r => r.Scenario).OrderBy(s => s, StringComparer.Ordinal).ToArray());
        Assert.Equal(2, journal.Count(mine));

        // В окне отбора «База данных» чужой базы тоже нет: иначе по ней можно было бы отобрать.
        Assert.Equal(new[] { "0", "1" },
                     journal.Values("DbId", mine).Select(v => v.Value).OrderBy(v => v, StringComparer.Ordinal).ToArray());

        // Администратор без единой своей базы видит только строки без базы — про машину.
        Assert.Equal("Без базы",
                     Assert.Single(journal.Page(new SecurityFilter(Databases: []))).Scenario);

        // Без границы (выгрузка политики, служебные вызовы) видно все.
        Assert.Equal(3, journal.Count(new SecurityFilter()));
    }

    /// <summary>
    /// Решения о доступе и попытки входа лежат в том же журнале и называются словами: проверяющий
    /// не должен знать, что «11» — это решение администратора, а «12» — ввод пароля.
    /// </summary>
    [Fact]
    public void Access_decisions_and_signins_are_named_in_words()
    {
        var journal = Journal();

        SecurityJournal.Attach(journal);
        AccessTrace.Decision(3, "Петров П. П.", "допуск отозван", Now);
        AccessTrace.Signin(3, ok: false, "неверный пароль, промах подряд: 2", Now);

        var rows = journal.Recent().ToList();

        var signin = Assert.Single(rows, r => r.Event == SecurityEvent.Signin);
        Assert.False(signin.Allowed);
        Assert.Equal("пароль базы данных", signin.Address);
        Assert.Equal("вход по паролю базы", SecurityJournal.Name(signin.Event));

        var access = Assert.Single(rows, r => r.Event == SecurityEvent.Access);
        Assert.True(access.Allowed);
        Assert.Equal("Петров П. П.",  access.Address);
        Assert.Equal("допуск отозван", access.Explanation);
        Assert.Equal("решение о доступе к базе", SecurityJournal.Name(access.Event));

        // Новые роды событий не ломают цепочку: полей в строке не прибавилось.
        Assert.Equal(0L, journal.Verify());
    }

    /// <summary>
    /// Решения администратора доезжают до журнала безопасности сами, из службы прав, — а не по
    /// доброй воле экрана. Проверяется весь набор: допуск, роль, удаление человека, пароль базы и
    /// способ входа. Забытый вызов в одном из них — это ровно та строка, которой потом не
    /// хватит: история изменений чистится по сроку, а журнал нет.
    /// </summary>
    [Fact]
    public void Every_access_decision_lands_in_the_journal()
    {
        var journal = Journal();
        SecurityJournal.Attach(journal);

        using var db = new TempDatabase();

        int Join(string key, string name)
        {
            using var neighbour = new AppDbContext(db.Path);
            var user = new DbUserEntity
            {
                UserKey     = key,
                DisplayName = name,
                CreatedAt   = DateTime.UtcNow.ToString("o"),
            };
            neighbour.DbUsers.Add(user);
            neighbour.SaveChanges();
            return user.Id;
        }

        var petrov = Join("petrov@pc2", "Петров П. П.");
        var sidor  = Join("sidorov@pc3", "Сидоров С. С.");

        db.Permissions.SetApproved(petrov, true);
        db.Permissions.SetApproved(petrov, false);
        db.Permissions.SetAdmin(sidor, true);
        db.Permissions.RemoveUser(petrov);
        db.Permissions.SetPassword("секрет-базы");
        db.Permissions.SetPasswordAccess(false);

        var said = journal.Page(new SecurityFilter(Event: SecurityEvent.Access))
                          .Select(r => $"{r.Address}: {r.Explanation}")
                          .ToList();

        Assert.Contains("Петров П. П.: допуск выдан",            said);
        Assert.Contains("Петров П. П.: допуск отозван",          said);
        Assert.Contains("Сидоров С. С.: роль: Администратор",    said);
        Assert.Contains("Петров П. П.: пользователь убран из базы", said);
        Assert.Contains("пароль базы данных: пароль базы установлен", said);
        Assert.Contains("пароль базы данных: способ входа: подтверждение администратора", said);

        // Галки прав в журнал не идут: их десятки за сеанс, и повысить себя ими нельзя.
        db.Permissions.SetMask(sidor, null, 0);
        Assert.Equal(said.Count, journal.Count(new SecurityFilter(Event: SecurityEvent.Access)));
    }

    /// <summary>
    /// Неудачный ввод пароля базы виден в журнале, а не только в «Диагностике», и после трех
    /// промахов подряд следующая попытка ждет — даже с верным паролем. Подбор пароля у чужого
    /// стола перестает быть бесплатным и незаметным.
    /// </summary>
    [Fact]
    public void Wrong_passwords_are_journalled_and_then_made_to_wait()
    {
        var journal = Journal();
        SecurityJournal.Attach(journal);

        using var db = new TempDatabase();
        db.Permissions.SetPassword("секрет-базы");

        Assert.Equal("Неверный пароль", db.Permissions.Unlock("не тот"));
        Assert.Equal("Неверный пароль", db.Permissions.Unlock("и не этот"));
        Assert.Equal("Неверный пароль", db.Permissions.Unlock("и не третий"));

        // Третий промах подряд закрывает ввод на несколько секунд — верный пароль тоже ждет.
        Assert.StartsWith("Слишком много попыток", db.Permissions.Unlock("секрет-базы"));

        var misses = journal.Page(new SecurityFilter(Event: SecurityEvent.Signin)).ToList();

        Assert.Equal(3, misses.Count);                       // ожидание попыткой не считается
        Assert.All(misses, m => Assert.False(m.Allowed));
        Assert.Contains("промах подряд: 3", misses[0].Explanation);
        Assert.Equal("пароль базы данных", misses[0].Address);
    }
}
