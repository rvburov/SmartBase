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

    private static RunEntry Request(string name, long bytes, DateTimeOffset at, bool ok = true) =>
        RunEntry.New(RunChannel.Request, name, "https://api.zavod.local/orders?key=***", ok,
                     ok ? "" : "чужая программа ответила 500", rows: 10, bytes: bytes,
                     elapsed: TimeSpan.FromMilliseconds(250), dbId: 0, at: at, counts: true);

    private static RunEntry Inbox(string file, DateTimeOffset at) =>
        RunEntry.New(RunChannel.Inbox, file, @"P:\Обмен\Входящие", ok: true, "",
                     rows: 5, bytes: 4096, elapsed: TimeSpan.Zero, dbId: 3, at: at, counts: false);

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
        Assert.Equal(700, usage.BytesSince(Now - TimeSpan.FromHours(1)));
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
