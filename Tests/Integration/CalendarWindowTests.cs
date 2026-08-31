using SmartBase.Core.Models;
using SmartBase.Database.Repositories;

namespace SmartBase.Tests.Integration;

/// <summary>
/// Чтение окон календаря там, где длительность события длиннее самого окна: окно Диаграммы
/// (<c>GetDiagramRecordsCapped</c>) с его агрегатами (<c>GetDiagramStats</c>) и многодневные
/// вида «Год» (<c>GetCalendarSpanningRecords</c>). Общее правило, которое тут закреплено:
/// событие обязано попадать в КАЖДОЕ окно, которое пересекает, — независимо от длительности
/// и от того, timed оно или all-day. Оба места про это спотыкались: диаграмма теряла timed
/// длиннее буфера, а «Год» знал только день начала. Как устроено —
/// docs/developer/features/calendar/diagram.md и .../year.md.
/// </summary>
public sealed class CalendarWindowTests
{
    private static string Timed(DateTime start, DateTime end) =>
        new DateValue(start, end, false).ToStorage();

    private static string AllDay(DateTime start, DateTime end) =>
        new DateValue(start, end, true).ToStorage();

    // Коллекция с полем-датой и записями «название → значение даты».
    private static (ConstructorRepository Repo, int ColId) Seed(
        TempDatabase db, params (string Title, string Value)[] events)
    {
        var repo = db.Constructor;
        var col  = SampleData.AddCollection(repo);
        var date = SampleData.AddCalendarDateField(repo, col.Id!.Value);
        foreach (var (title, value) in events)
        {
            var rec = SampleData.AddRecord(repo, col.Id!.Value, title);
            repo.SetFieldValue(rec.Id!.Value, date.Id!.Value, value);
        }
        return (repo, col.Id!.Value);
    }

    private static List<string> TitlesIn(ConstructorRepository repo, DateTime start, DateTime end) =>
        repo.GetDiagramRecordsCapped(start, end, null, perCollectionCap: 200)
            .Select(r => r.Title).OrderBy(t => t, StringComparer.Ordinal).ToList();

    // ── масштаб «месяц» ───────────────────────────────────────────────────────

    [Fact]
    public void Month_window_shows_every_event_it_crosses()
    {
        using var db = new TempDatabase();
        var (repo, _) = Seed(db,
            ("внутри",        Timed(new DateTime(2026, 8, 4, 10, 0, 0),  new DateTime(2026, 8, 4, 11, 0, 0))),
            ("хвост",         Timed(new DateTime(2026, 7, 30, 10, 0, 0), new DateTime(2026, 8, 2, 11, 0, 0))),
            ("накрывает",     Timed(new DateTime(2026, 6, 1, 10, 0, 0),  new DateTime(2026, 9, 30, 11, 0, 0))),
            ("весь день",     AllDay(new DateTime(2026, 2, 1),           new DateTime(2029, 1, 12))),
            ("раньше",        Timed(new DateTime(2026, 7, 1, 10, 0, 0),  new DateTime(2026, 7, 1, 11, 0, 0))),
            ("позже",         Timed(new DateTime(2026, 9, 1, 10, 0, 0),  new DateTime(2026, 9, 1, 11, 0, 0))));

        var titles = TitlesIn(repo, new DateTime(2026, 8, 1), new DateTime(2026, 9, 1));

        Assert.Equal(["весь день", "внутри", "накрывает", "хвост"], titles);
    }

    [Fact]
    public void Long_timed_event_is_not_lost_by_the_month_it_only_crosses()
    {
        // Событие длиннее буфера timed-подзапроса (62 дня), начавшееся ДО окна: в диаграмме
        // такие нормальны (масштаб — до 10 лет), терять их нельзя.
        using var db = new TempDatabase();
        var (repo, _) = Seed(db,
            ("длинное", Timed(new DateTime(2026, 1, 5, 10, 0, 0), new DateTime(2026, 12, 20, 11, 0, 0))));

        Assert.Equal(["длинное"], TitlesIn(repo, new DateTime(2026, 8, 1), new DateTime(2026, 9, 1)));
    }

    // ── масштабы «год» … «10 лет» ─────────────────────────────────────────────

    [Fact]
    public void Year_window_keeps_a_timed_event_that_started_in_the_previous_year()
    {
        using var db = new TempDatabase();
        var (repo, _) = Seed(db,
            ("через год", Timed(new DateTime(2025, 9, 1, 10, 0, 0), new DateTime(2026, 3, 1, 11, 0, 0))));

        Assert.Equal(["через год"], TitlesIn(repo, new DateTime(2026, 1, 1), new DateTime(2027, 1, 1)));
    }

    [Fact]
    public void Decade_window_keeps_a_timed_event_that_started_before_it()
    {
        using var db = new TempDatabase();
        var (repo, _) = Seed(db,
            ("через десятилетие", Timed(new DateTime(2019, 6, 1, 10, 0, 0), new DateTime(2021, 6, 1, 11, 0, 0))));

        Assert.Equal(["через десятилетие"], TitlesIn(repo, new DateTime(2020, 1, 1), new DateTime(2030, 1, 1)));
    }

    [Fact]
    public void Windows_outside_the_event_stay_empty()
    {
        using var db = new TempDatabase();
        var (repo, _) = Seed(db,
            ("длинное", Timed(new DateTime(2026, 1, 5, 10, 0, 0), new DateTime(2026, 3, 20, 11, 0, 0))));

        Assert.Empty(TitlesIn(repo, new DateTime(2025, 1, 1), new DateTime(2026, 1, 1)));
        Assert.Empty(TitlesIn(repo, new DateTime(2026, 4, 1), new DateTime(2026, 5, 1)));
    }

    // ── многодневные для вида «Год» ───────────────────────────────────────────

    private static List<string> SpanningIn(ConstructorRepository repo, DateTime start, DateTime end) =>
        repo.GetCalendarSpanningRecords(start, end, null, perCollectionCap: 2000)
            .Select(r => r.Title).OrderBy(t => t, StringComparer.Ordinal).ToList();

    [Fact]
    public void Year_spanning_query_returns_multi_day_events_and_skips_single_day_ones()
    {
        // Вид «Год» сам события не читает — числам хватает карты счетчиков. Но карта считает
        // событие по дню НАЧАЛА, поэтому многодневные ему доставляются отдельно: иначе запись
        // на три года подсветила бы одно число вместо всего диапазона.
        using var db = new TempDatabase();
        var (repo, _) = Seed(db,
            ("три года",   AllDay(new DateTime(2026, 2, 16),          new DateTime(2029, 1, 26))),
            ("два дня",    Timed(new DateTime(2026, 5, 1, 22, 0, 0),  new DateTime(2026, 5, 2, 2, 0, 0))),
            ("один день",  Timed(new DateTime(2026, 6, 1, 10, 0, 0),  new DateTime(2026, 6, 1, 11, 0, 0))),
            ("сутки",      AllDay(new DateTime(2026, 7, 1),           new DateTime(2026, 7, 1))),
            ("чужой год",  AllDay(new DateTime(2024, 1, 1),           new DateTime(2024, 3, 1))));

        var titles = SpanningIn(repo, new DateTime(2026, 1, 1), new DateTime(2027, 1, 1));

        Assert.Equal(["два дня", "три года"], titles);
    }

    [Fact]
    public void Year_spanning_query_keeps_events_that_started_in_earlier_years()
    {
        // Запись 16.02.2026 – 26.01.2029 обязана прийти во ВСЕ три года, а не только в свой первый.
        using var db = new TempDatabase();
        var (repo, _) = Seed(db,
            ("три года", AllDay(new DateTime(2026, 2, 16), new DateTime(2029, 1, 26))));

        Assert.Equal(["три года"], SpanningIn(repo, new DateTime(2026, 1, 1), new DateTime(2027, 1, 1)));
        Assert.Equal(["три года"], SpanningIn(repo, new DateTime(2027, 1, 1), new DateTime(2028, 1, 1)));
        Assert.Equal(["три года"], SpanningIn(repo, new DateTime(2028, 1, 1), new DateTime(2029, 1, 1)));
        Assert.Equal(["три года"], SpanningIn(repo, new DateTime(2029, 1, 1), new DateTime(2030, 1, 1)));
        Assert.Empty(SpanningIn(repo, new DateTime(2030, 1, 1), new DateTime(2031, 1, 1)));
        Assert.Empty(SpanningIn(repo, new DateTime(2025, 1, 1), new DateTime(2026, 1, 1)));
    }

    // ── агрегаты окна ─────────────────────────────────────────────────────────

    [Fact]
    public void Stats_count_the_same_events_the_window_shows()
    {
        using var db = new TempDatabase();
        var (repo, colId) = Seed(db,
            ("внутри",    Timed(new DateTime(2026, 8, 4, 10, 0, 0), new DateTime(2026, 8, 4, 11, 0, 0))),
            ("длинное",   Timed(new DateTime(2026, 1, 5, 10, 0, 0), new DateTime(2026, 12, 20, 11, 0, 0))),
            ("весь день", AllDay(new DateTime(2026, 2, 1),          new DateTime(2029, 1, 12))),
            ("позже",     Timed(new DateTime(2026, 9, 1, 10, 0, 0), new DateTime(2026, 9, 1, 11, 0, 0))));

        var start = new DateTime(2026, 8, 1);
        var end   = new DateTime(2026, 9, 1);

        var shown = repo.GetDiagramRecordsCapped(start, end, null, perCollectionCap: 200).Count;
        var stats = repo.GetDiagramStats(start, end, null);

        Assert.Equal(3, shown);
        Assert.Equal(3, stats[colId].Count);
    }
}
