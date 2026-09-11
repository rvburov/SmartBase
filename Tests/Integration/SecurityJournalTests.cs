using Microsoft.Data.Sqlite;
using SmartBase.Database;

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
        try { SqliteConnection.ClearAllPools(); } catch { /* best-effort */ }
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
        Assert.Equal(0, journal.Verify());
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
}
