using SmartBase.Database;
using SmartBase.Integrations;

namespace SmartBase.Tests.Integration;

/// <summary>Рабочий журнал обмена: записи, фильтры, страницы, сохранность после обновления и очистка по сроку.</summary>
[Collection(EgressCollection.Name)]
public sealed class RunJournalTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"sbjournal_{Guid.NewGuid():N}.db");

    public void Dispose()
    {
        RunJournal.Attach(null);
        try { SqliteGate.ClearAllPools(); } catch { /* best-effort */ }
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
                     ok ? "" : "сторонняя программа ответила 500", rows: 10, bytes: bytes,
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
            "сторонняя программа ответила 422", rows: 0, bytes: 120,
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

    }

    // ── Сценарий и шаг ────────────────────────────────────────────────────────

    /// <summary>
    /// Чей это был запуск и какой шаг. Без этих двух полей разбор сценария из пяти шагов
    /// начинается с пересчета строк по времени, а два шага, дергающих один и тот же запрос, не
    /// различить вовсе.
    /// </summary>
    [Fact]
    public void Scenario_and_step_read_back_with_the_entry()
    {
        var journal = Journal();

        journal.Write(RunEntry.New(RunChannel.Request, "Заказы", "https://api.zavod.local", true,
                                   "", rows: 1, bytes: 10, elapsed: TimeSpan.Zero, dbId: 0,
                                   at: Now, counts: true, direction: 7,
                                   scenario: "Ночной обмен", step: "шаг 2 из 5 · запрос"));

        var row = journal.Recent().Single();

        Assert.Equal("Ночной обмен",        row.Scenario);
        Assert.Equal("шаг 2 из 5 · запрос", row.Step);
    }

    /// <summary>Запуск руками сценарию не принадлежит — и поля у него пустые, а не выдуманные.</summary>
    [Fact]
    public void A_run_by_hand_has_no_scenario_and_no_step()
    {
        var journal = Journal();

        journal.Write(Request("Заказы", 100, Now));

        var row = journal.Recent().Single();

        Assert.Equal("", row.Scenario);
        Assert.Equal("", row.Step);
    }

    // ── Отбор и выгрузка ──────────────────────────────────────────────────────

    /// <summary>
    /// Сужение уходит в SQL, а не в память. Проверяется именно это: берется страница РАЗМЕРОМ В
    /// ОДНУ строку, и в нее обязана попасть неудача, лежащая под двумя удачными. Отбери «после
    /// чтения» — и в такой странице ее не окажется.
    /// </summary>
    [Fact]
    public void Narrowing_happens_in_sql_and_reaches_past_the_page()
    {
        var journal = Journal();

        journal.Write(Request("Старая неудача", 100, Now - TimeSpan.FromHours(3), ok: false));
        journal.Write(Request("Удача",          100, Now - TimeSpan.FromHours(2)));
        journal.Write(Request("Еще удача",      100, Now - TimeSpan.FromHours(1)));

        var failed = journal.Page(new RunFilter(FailedOnly: true), take: 1);

        Assert.Equal("Старая неудача", failed.Single().Name);
    }

    [Fact]
    public void Filter_by_channel_and_count_agree_with_each_other()
    {
        var journal = Journal();

        journal.Write(Request("Заказы", 100, Now));
        journal.Write(Inbox("отгрузка.json", Now));
        journal.Write(Inbox("остатки.json",  Now));

        var files = new RunFilter(Channel: RunChannel.Inbox);

        Assert.Equal(2, journal.Count(files));
        Assert.Equal(2, journal.Page(files).Count);
        Assert.Equal(2, journal.Stream(files, page: 1).Count());   // страницы склеиваются

        Assert.Equal(3, journal.Count(RunFilter.Any));
        Assert.False(RunFilter.Any.Narrow);
        Assert.True(files.Narrow);
    }

    /// <summary>
    /// Колонок в строке столько же, сколько заголовков. Разойдись они — файл откроется со
    /// сдвинутыми колонками, и заметит это тот, кто будет по нему разбираться, а не тот, кто
    /// добавил поле.
    /// </summary>
    [Fact]
    public void Csv_row_matches_the_headers()
    {
        var journal = Journal();

        journal.Write(RunEntry.New(RunChannel.Request, "Заказы", "https://api.zavod.local", true,
                                   "", rows: 1, bytes: 10, elapsed: TimeSpan.Zero, dbId: 0,
                                   at: Now, counts: true, direction: 7,
                                   sentBody: "{\"a\":1}", gotBody: "[]",
                                   scenario: "Ночной обмен", step: "шаг 1 из 1 · запрос"));

        var row = RunJournal.CsvRow(journal.Recent().Single());

        Assert.Equal(RunJournal.CsvHeaders.Count, row.Count);
        Assert.Contains("Ночной обмен",        row);
        Assert.Contains("шаг 1 из 1 · запрос", row);

        // Тел в выгрузке нет: восемь килобайт с переносами строк превратили бы таблицу в
        // нечитаемую, а смотрят их на экране, у одной строки.
        Assert.DoesNotContain(row, cell => cell.Contains("\"a\":1", StringComparison.Ordinal));
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
    // ── Отбор по колонкам ─────────────────────────────────────────────────────

    /// <summary>
    /// Отбор «как в табличном редакторе» уходит в SQL, а не отсеивает прочитанную пачку: журнал копится
    /// годами, и одна ночная неудача полугодовой давности иначе не нашлась бы.
    /// </summary>
    [Fact]
    public void Column_filter_narrows_by_value()
    {
        var journal = Journal();

        journal.Write(Request("Заказы",   2048, Now));
        journal.Write(Request("Остатки",  1024, Now));
        journal.Write(Inbox("КД-17.json", Now));

        var only = new Dictionary<string, IReadOnlyCollection<string>>(StringComparer.Ordinal)
        {
            ["Name"] = new[] { "Заказы" },
        };

        var rows = journal.Page(new RunFilter(Columns: only));

        Assert.Equal("Заказы", Assert.Single(rows).Name);
    }

    /// <summary>Несколько значений одной колонки складываются союзом «или».</summary>
    [Fact]
    public void Several_values_of_one_column_are_an_or()
    {
        var journal = Journal();

        journal.Write(Request("Заказы",  2048, Now));
        journal.Write(Request("Остатки", 1024, Now));
        journal.Write(Request("Цены",     512, Now));

        var only = new Dictionary<string, IReadOnlyCollection<string>>(StringComparer.Ordinal)
        {
            ["Name"] = new[] { "Заказы", "Цены" },
        };

        Assert.Equal(["Цены", "Заказы"],
                     journal.Page(new RunFilter(Columns: only)).Select(r => r.Name).ToArray());
    }

    /// <summary>Колонки складываются союзом «и»: канал И название.</summary>
    [Fact]
    public void Different_columns_are_an_and()
    {
        var journal = Journal();

        journal.Write(Request("Заказы", 2048, Now));
        journal.Write(Inbox("Заказы",   Now));

        var only = new Dictionary<string, IReadOnlyCollection<string>>(StringComparer.Ordinal)
        {
            ["Name"]    = new[] { "Заказы" },
            ["Channel"] = new[] { ((int)RunChannel.Inbox).ToString() },
        };

        Assert.Equal(RunChannel.Inbox, Assert.Single(journal.Page(new RunFilter(Columns: only))).Channel);
    }

    /// <summary>
    /// Имя колонки уходит в SQL как есть, поэтому оно берется из белого списка: чужое имя
    /// отбор просто не сужает — и уж точно не роняет журнал.
    /// </summary>
    [Fact]
    public void An_unknown_column_narrows_nothing()
    {
        var journal = Journal();

        journal.Write(Request("Заказы", 2048, Now));

        var only = new Dictionary<string, IReadOnlyCollection<string>>(StringComparer.Ordinal)
        {
            ["Name = 'x' OR 1=1 --"] = new[] { "что угодно" },
        };

        Assert.Single(journal.Page(new RunFilter(Columns: only)));
    }

    [Fact]
    public void Values_of_a_column_come_with_counts()
    {
        var journal = Journal();

        journal.Write(Request("Заказы",  2048, Now));
        journal.Write(Request("Заказы",  1024, Now));
        journal.Write(Request("Остатки",  512, Now));

        var values = journal.Values("Name", RunFilter.Any);

        Assert.Equal(("Заказы", 2), values[0]);
        Assert.Contains(("Остатки", 1), values);
    }

    /// <summary>
    /// Значения колонки считаются под текущим отбором, но БЕЗ условия по ней самой: иначе
    /// снятое галочкой значение исчезло бы из списка и вернуть его было бы нечем.
    /// </summary>
    [Fact]
    public void Values_ignore_the_filter_on_their_own_column()
    {
        var journal = Journal();

        journal.Write(Request("Заказы",  2048, Now));
        journal.Write(Request("Остатки", 1024, Now));
        journal.Write(Inbox("КД-17.json", Now));

        var only = new Dictionary<string, IReadOnlyCollection<string>>(StringComparer.Ordinal)
        {
            ["Name"]    = new[] { "Заказы" },
            ["Channel"] = new[] { ((int)RunChannel.Request).ToString() },
        };

        var filter = new RunFilter(Columns: only);

        // По своей колонке условие снято — видны оба запроса…
        Assert.Equal(["Заказы", "Остатки"],
                     journal.Values("Name", filter).Select(v => v.Value).OrderBy(v => v).ToArray());

        // …а по чужой действует: файл из папки под отбор по названию не подошел.
        Assert.Equal([((int)RunChannel.Request).ToString()],
                     journal.Values("Channel", filter).Select(v => v.Value).ToArray());
    }

    // ── Период ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Период сужает выборку В ФАЙЛЕ. Отбери его из прочитанной пачки — и единственная неудача
    /// недельной давности не нашлась бы вовсе: свежие строки вытеснили бы ее раньше, чем до нее
    /// дошел бы отбор. У журнала безопасности период был с самого начала, у рабочего — нет, хотя
    /// разбирают по нему чаще.
    /// </summary>
    [Fact]
    public void The_period_narrows_the_selection_in_the_file()
    {
        var journal = Journal();

        journal.Write(Request("Позавчерашний", 100, Now.AddDays(-2)));
        journal.Write(Request("Сегодняшний",   200, Now));

        var week = journal.Page(new RunFilter(Since: Now.AddDays(-7)));
        var day  = journal.Page(new RunFilter(Since: Now.AddDays(-1)));

        Assert.Equal(2, week.Count);
        Assert.Equal("Сегодняшний", Assert.Single(day).Name);
    }

    /// <summary>Период — тоже сужение: окно выгрузки обязано сказать об этом вслух.</summary>
    [Fact]
    public void A_period_counts_as_narrowing()
    {
        Assert.False(RunFilter.Any.Narrow);
        Assert.True(new RunFilter(Since: Now.AddDays(-7)).Narrow);
    }

    /// <summary>
    /// Счет запусков одного сценария за период — то, чем карточка отвечает на «идет ли обмен».
    /// Момент последнего запуска на этот вопрос не отвечает: «в 07:00» выглядит одинаково и когда
    /// сценарий отработал раз, и когда он с тех пор молчит.
    /// </summary>
    [Fact]
    public void Runs_of_one_scenario_are_counted_for_the_period()
    {
        var journal = Journal();

        journal.Write(Request("Заказы",  100, Now.AddHours(-2)) with { Scenario = "Ночной обмен" });
        journal.Write(Request("Остатки", 100, Now.AddHours(-1)) with { Scenario = "Ночной обмен" });
        journal.Write(Request("Заказы",  100, Now)              with { Scenario = "Чужой" });
        journal.Write(Request("Заказы",  100, Now.AddDays(-3))  with { Scenario = "Ночной обмен" });

        var mine = new Dictionary<string, IReadOnlyCollection<string>>(StringComparer.Ordinal)
        {
            ["Scenario"] = new[] { "Ночной обмен" },
        };

        Assert.Equal(2, journal.Count(new RunFilter(Columns: mine, Since: Now.AddHours(-6))));
        Assert.Equal(3, journal.Count(new RunFilter(Columns: mine)));
    }

    [Fact]
    public void A_column_filter_counts_as_narrowing()
    {
        Assert.False(RunFilter.Any.Narrow);

        var only = new Dictionary<string, IReadOnlyCollection<string>>(StringComparer.Ordinal)
        {
            ["Name"] = new[] { "Заказы" },
        };

        Assert.True(new RunFilter(Columns: only).Narrow);
    }

}
