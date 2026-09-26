using Microsoft.EntityFrameworkCore;
using SmartBase.Core.Models;
using SmartBase.Database.Repositories;

namespace SmartBase.Tests.Integration;

/// <summary>
/// Сводка длительностей календаря (<c>calendar_spans</c>, миграция AddCalendarSpans): наибольшая
/// длительность события на коллекцию и признак «весь день». По ней чтения окна ищут события,
/// начавшиеся до окна, и самый поздний конец в полосе у границы окна, а не во всем будущем
/// коллекции. Закреплено: сводка никогда не меньше настоящей наибольшей длительности на любом
/// пути записи (иначе чтения теряли бы события), окна сеток больше не теряют timed длиннее двух
/// месяцев, а агрегаты Диаграммы равны прямому определению на любых окнах.
/// </summary>
public sealed class CalendarSpansTests
{
    private static string Timed(DateTime start, DateTime end) => new DateValue(start, end, false).ToStorage();
    private static string AllDay(DateTime start, DateTime end) => new DateValue(start, end, true).ToStorage();

    private static Dictionary<(int, int), long> Spans(TempDatabase db)
    {
        var map  = new Dictionary<(int, int), long>();
        var conn = db.Context.Database.GetDbConnection();
        var opened = conn.State != System.Data.ConnectionState.Open;
        if (opened) conn.Open();
        try
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT CollectionId, AllDay, MaxSeconds FROM calendar_spans";
            using var rdr = cmd.ExecuteReader();
            while (rdr.Read()) map[(rdr.GetInt32(0), rdr.GetInt32(1))] = rdr.GetInt64(2);
        }
        finally { if (opened) conn.Close(); }
        return map;
    }

    // Сводка покрывает проекцию: у каждой пары (коллекция, вид) с событиями есть граница, и она не
    // меньше настоящей наибольшей длительности.
    private static void AssertSpansCover(TempDatabase db, string step)
    {
        var spans = Spans(db);
        foreach (var g in db.Context.CalendarIndex.AsNoTracking().ToList().GroupBy(x => (x.CollectionId, AllDay: x.AllDay ? 1 : 0)))
        {
            var actual = g.Max(x => (DateTime.Parse(x.EndIso) - DateTime.Parse(x.StartIso)).TotalSeconds);
            Assert.True(spans.TryGetValue(g.Key, out var bound), $"{step}: нет границы для {g.Key}");
            Assert.True(bound >= actual, $"{step}: граница {bound} с меньше длительности {actual} с для {g.Key}");
        }
    }

    [Fact]
    public void The_bound_covers_every_write_path_and_follows_the_longest_event()
    {
        using var db = new TempDatabase();
        var repo = db.Constructor;
        var d    = new DateTime(2026, 7, 1);

        var a  = SampleData.AddCollection(repo, "Встречи").Id!.Value;
        var fa = SampleData.AddCalendarDateField(repo, a, name: "Начало").Id!.Value;
        var b  = SampleData.AddCollection(repo, "Отпуска").Id!.Value;
        var fb = SampleData.AddCalendarDateField(repo, b, name: "Начало").Id!.Value;

        int Event(int col, int field, string value)
        {
            var id = SampleData.AddRecord(repo, col, "Событие").Id!.Value;
            repo.SetFieldValue(id, field, value);
            return id;
        }

        var hour = Event(a, fa, Timed(d.AddHours(10), d.AddHours(11)));
        Event(b, fb, AllDay(d, d.AddDays(2)));
        AssertSpansCover(db, "заполнение");
        Assert.InRange(Spans(db)[(a, 0)], 3600, 3602);
        Assert.False(Spans(db).ContainsKey((b, 0)), "у «Отпусков» нет событий со временем — строки нет");

        repo.SetFieldValue(hour, fa, Timed(d.AddHours(10), d.AddDays(100)));   // растянули на 100 дней
        AssertSpansCover(db, "растяжение события");
        repo.SetFieldValue(hour, fa, Timed(d.AddHours(10), d.AddHours(12)));   // вернули к двум часам
        AssertSpansCover(db, "укорочение события");
        Assert.True(Spans(db)[(a, 0)] >= (d.AddDays(100) - d.AddHours(10)).TotalSeconds,
                    "граница только растет — укорочение ее не уменьшает");

        Event(a, fa, AllDay(d.AddDays(5), d.AddDays(40)));
        var copy = repo.DuplicateCollection(a).Id!.Value;
        AssertSpansCover(db, "дубль коллекции");
        Assert.InRange(Spans(db)[(copy, 0)], 7200, 7202);   // копия — своя точная граница, не завышенная исходной

        repo.DeleteRecord(hour);
        repo.DeleteCollectionData(b);
        AssertSpansCover(db, "удаление");
    }

    // ── окна сеток ──────────────────────────────────────────────────────────

    [Fact]
    public void Grid_window_keeps_a_timed_event_longer_than_two_months()
    {
        // Без сводки сетки искали timed, начавшиеся до окна, не дальше 62 дней назад: растянутое
        // мышью на полгода событие пропадало из Месяца, Недели и Дня, которые оно лишь пересекает.
        using var db = new TempDatabase();
        var repo = db.Constructor;
        var col  = SampleData.AddCollection(repo).Id!.Value;
        var date = SampleData.AddCalendarDateField(repo, col).Id!.Value;
        var rec  = SampleData.AddRecord(repo, col, "полгода").Id!.Value;
        repo.SetFieldValue(rec, date, Timed(new DateTime(2026, 1, 5, 10, 0, 0), new DateTime(2026, 7, 20, 11, 0, 0)));

        var month = repo.GetCalendarRecordsByRangeCapped(new DateTime(2026, 6, 1), new DateTime(2026, 7, 1), null, perDayCap: 50);
        var week  = repo.GetCalendarRecordsByRangeCapped(new DateTime(2026, 7, 13), new DateTime(2026, 7, 20), null, perDayCap: 50);
        var byRange = repo.GetCalendarRecordsByRange(new DateTime(2026, 6, 10), new DateTime(2026, 6, 11));

        Assert.Equal(["полгода"], month.Select(r => r.Title));
        Assert.Equal(["полгода"], week.Select(r => r.Title));
        Assert.Equal(["полгода"], byRange.Select(r => r.Title));
        Assert.Empty(repo.GetCalendarRecordsByRangeCapped(new DateTime(2026, 7, 21), new DateTime(2026, 7, 28), null, perDayCap: 50));
    }

    [Fact]
    public void Month_read_keeps_the_evening_multi_day_event_of_a_crowded_day()
    {
        // Месяц читает по 24 timed на день — больше ячейке не показать. Многодневное, начавшееся
        // вечером переполненного дня, раньше отсекалось тем же cap вместе с однодневными; теперь
        // оно приходит отдельным проходом, и без повторов.
        using var db = new TempDatabase();
        var repo = db.Constructor;
        var col  = SampleData.AddCollection(repo).Id!.Value;
        var date = SampleData.AddCalendarDateField(repo, col).Id!.Value;
        var d    = new DateTime(2026, 7, 8);
        for (var i = 0; i < 60; i++)
        {
            var rec = SampleData.AddRecord(repo, col, $"встреча {i}").Id!.Value;
            repo.SetFieldValue(rec, date, Timed(d.AddMinutes(i * 10), d.AddMinutes(i * 10 + 30)));
        }
        var night = SampleData.AddRecord(repo, col, "ночная смена").Id!.Value;
        repo.SetFieldValue(night, date, Timed(d.AddHours(23), d.AddDays(1).AddHours(7)));
        var early = SampleData.AddRecord(repo, col, "с утра до завтра").Id!.Value;
        repo.SetFieldValue(early, date, Timed(d.AddMinutes(5), d.AddDays(1).AddHours(1)));

        var month = repo.GetCalendarRecordsByRangeCapped(new DateTime(2026, 6, 29), new DateTime(2026, 8, 10), null,
                                                         perDayCap: 24, allDayCap: 2000, multiDayPerDay: 24);

        Assert.Contains(month, r => r.Title == "ночная смена");
        Assert.Contains(month, r => r.Title == "с утра до завтра");
        Assert.Equal(month.Count, month.Select(r => (r.Id, r.FieldId)).Distinct().Count());
        // 24 первых по началу (в них и утреннее многодневное) и вечернее многодневное сверх cap.
        Assert.Equal(25, month.Count(r => r.StartDt.Date == d));
    }

    // ── агрегаты Диаграммы против прямого определения ───────────────────────

    private static Dictionary<int, DiagramGroupStat> ExpectedStats(TempDatabase db, DateTime start, DateTime end, ISet<int>? active)
    {
        var s = DateTime.SpecifyKind(start, DateTimeKind.Unspecified).ToString("o");
        var e = DateTime.SpecifyKind(end,   DateTimeKind.Unspecified).ToString("o");
        var visible = db.Context.DbFields.AsNoTracking()
            .Where(f => f.FieldType == "date" && f.IsCalStart).Select(f => f.Id).ToHashSet();
        return db.Context.CalendarIndex.AsNoTracking().ToList()
            .Where(x => visible.Contains(x.FieldId)
                        && (active is null || active.Contains(x.CollectionId))
                        && string.CompareOrdinal(x.StartIso, e) < 0
                        && string.CompareOrdinal(x.EndIso, s) >= 0)
            .GroupBy(x => x.CollectionId)
            .ToDictionary(g => g.Key, g => new DiagramGroupStat
            {
                Count        = g.Count(),
                AllDayCount  = g.Count(x => x.AllDay),
                MinStart     = g.Min(x => DateTime.Parse(x.StartIso)),
                MaxEndTimed  = g.Where(x => !x.AllDay).Select(x => (DateTime?)DateTime.Parse(x.EndIso)).Max(),
                MaxEndAllDay = g.Where(x => x.AllDay).Select(x => (DateTime?)DateTime.Parse(x.EndIso)).Max(),
            });
    }

    private static string Show(DiagramGroupStat st) =>
        $"{st.Count}/{st.AllDayCount} {st.MinStart:s} {st.MaxEndTimed:s} {st.MaxEndAllDay:s}";

    private static void AssertStats(ConstructorRepository repo, TempDatabase db, DateTime start, DateTime end, ISet<int>? active, string step)
    {
        var expected = ExpectedStats(db, start, end, active);
        var actual   = repo.GetDiagramStats(start, end, active);
        var label    = $"{step}, окно {start:s} — {end:s}";
        Assert.True(expected.Keys.Order().SequenceEqual(actual.Keys.Order()),
                    $"{label}: группы {string.Join(",", expected.Keys.Order())} ≠ {string.Join(",", actual.Keys.Order())}");
        foreach (var (col, st) in expected)
            Assert.True(Show(st) == Show(actual[col]), $"{label}, коллекция {col}: {Show(st)} ≠ {Show(actual[col])}");
    }

    [Fact]
    public void Diagram_stats_match_the_definition_on_every_window()
    {
        using var db = new TempDatabase();
        var repo = db.Constructor;
        var d    = new DateTime(2026, 7, 1);

        var a   = SampleData.AddCollection(repo, "Встречи").Id!.Value;
        var fa  = SampleData.AddCalendarDateField(repo, a, name: "Начало").Id!.Value;
        var fa2 = SampleData.AddCalendarDateField(repo, a, name: "Напоминание").Id!.Value;   // будет скрыто
        var b   = SampleData.AddCollection(repo, "Проекты").Id!.Value;
        var fb  = SampleData.AddCalendarDateField(repo, b, name: "Срок").Id!.Value;
        var c   = SampleData.AddCollection(repo, "Пустая").Id!.Value;
        SampleData.AddCalendarDateField(repo, c, name: "Дата");

        int Event(int col, int field, string value)
        {
            var id = SampleData.AddRecord(repo, col, "Событие").Id!.Value;
            repo.SetFieldValue(id, field, value);
            return id;
        }

        for (var i = 0; i < 40; i++)   // встречи по часу каждые 19 часов, через полночь и границы окон
            Event(a, fa, Timed(d.AddHours(i * 19 + 0.5), d.AddHours(i * 19 + 1.5)));
        var longTimed = Event(a, fa, Timed(d.AddDays(-80).AddHours(9), d.AddDays(20).AddHours(18)));
        Event(a, fa2, Timed(d.AddDays(-400), d.AddDays(400)));                   // скрытое поле — не считается
        Event(a, fa, AllDay(d.AddDays(3), d.AddDays(3)));
        Event(b, fb, AllDay(d.AddDays(-30), d.AddDays(5)));
        Event(b, fb, AllDay(d.AddDays(10), d.AddDays(11)));
        Event(b, fb, Timed(d.AddDays(12).AddHours(23), d.AddDays(13).AddHours(1)));
        repo.SetDateFieldVisible(fa2, false);

        var windows = new (DateTime Start, DateTime End)[]
        {
            (d, d.AddMonths(1)),
            (d.AddDays(-1), d.AddDays(1)),
            (d.AddDays(12), d.AddDays(13)),
            (d.AddDays(2).AddHours(12), d.AddDays(9).AddHours(6)),   // не по полуночам
            (d.AddDays(-90), d.AddDays(-85)),                        // до всех, кроме пустоты
            (d.AddDays(21), d.AddDays(40)),                          // после длинного
            (new DateTime(2020, 1, 1), new DateTime(2030, 1, 1)),
        };
        var sets = new ISet<int>?[] { null, new HashSet<int> { a }, new HashSet<int> { b, c }, new HashSet<int>() };

        foreach (var (start, end) in windows)
            foreach (var active in sets)
                AssertStats(repo, db, start, end, active, "заполнение");

        // Граница только растет: после удаления длинного события она прежняя, а агрегаты — по-прежнему
        // точные (шире полоса — больше строк скана, но не другой ответ).
        repo.DeleteRecord(longTimed);
        foreach (var (start, end) in windows)
            AssertStats(repo, db, start, end, null, "после удаления длинного");

        // Окно Диаграммы доставляет те же события, что считают агрегаты.
        foreach (var (start, end) in windows)
        {
            var shown = repo.GetDiagramRecordsCapped(start, end, null, perCollectionCap: 1000);
            var stats = repo.GetDiagramStats(start, end, null);
            foreach (var g in shown.GroupBy(r => r.CollectionId!.Value))
                Assert.Equal(stats[g.Key].Count, g.Count());
        }
    }
}
