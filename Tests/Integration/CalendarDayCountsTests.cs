using Microsoft.EntityFrameworkCore;
using SmartBase.Core.Models;

namespace SmartBase.Tests.Integration;

/// <summary>
/// Счетчики дней календаря (<c>ConstructorRepository.GetCalendarDayCountsByRange</c>) — «Журнал»,
/// карта «+N еще» Месяца и числа Года. Полные дни идут из сводки <c>calendar_day_counts</c> (ее
/// держат триггеры проекции), неполные крайние — по индексу; эталон здесь —
/// прямое определение: строки проекции, чье начало в окне, по дню начала, с теми же правилами
/// видимости (скрытое поле-дата не считается, коллекция с целиком скрытыми датами — тоже,
/// пустой набор коллекций — ни одной). Границы, на которых такой счет ошибся бы первым:
/// полночь, «весь день», многодневное (считается по дню начала), окно с середины дня.
/// </summary>
public sealed class CalendarDayCountsTests
{
    private static string Timed(DateTime start, DateTime end) => new DateValue(start, end, false).ToStorage();
    private static string AllDay(DateTime start, DateTime end) => new DateValue(start, end, true).ToStorage();

    private static List<(string Day, int Count)> Expected(TempDatabase db, DateTime start, DateTime end, ISet<int>? active)
    {
        var s = DateTime.SpecifyKind(start, DateTimeKind.Unspecified).ToString("o");
        var e = DateTime.SpecifyKind(end,   DateTimeKind.Unspecified).ToString("o");
        var visibleFields = db.Context.DbFields.AsNoTracking()
            .Where(f => f.FieldType == "date" && f.IsCalStart).Select(f => f.Id).ToHashSet();
        return db.Context.CalendarIndex.AsNoTracking().ToList()
            .Where(x => visibleFields.Contains(x.FieldId)
                        && (active is null || active.Contains(x.CollectionId))
                        && string.CompareOrdinal(x.StartIso, s) >= 0
                        && string.CompareOrdinal(x.StartIso, e) < 0)
            .GroupBy(x => x.StartIso[..10])
            .OrderBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => (g.Key, g.Count()))
            .ToList();
    }

    [Fact]
    public void Day_counts_match_the_definition_on_every_window_and_collection_set()
    {
        using var db = new TempDatabase();
        var repo = db.Constructor;

        var a  = SampleData.AddCollection(repo, "Встречи").Id!.Value;
        var fa = SampleData.AddCalendarDateField(repo, a, name: "Начало").Id!.Value;
        var fa2 = SampleData.AddCalendarDateField(repo, a, name: "Напоминание").Id!.Value;   // будет скрыто
        var b  = SampleData.AddCollection(repo, "Отпуска").Id!.Value;
        var fb = SampleData.AddCalendarDateField(repo, b, name: "Начало").Id!.Value;
        var c  = SampleData.AddCollection(repo, "Архив").Id!.Value;
        var fc = SampleData.AddCalendarDateField(repo, c, name: "Начало").Id!.Value;

        void Event(int col, int field, string value) =>
            repo.SetFieldValue(SampleData.AddRecord(repo, col, "Событие").Id!.Value, field, value);

        var d = new DateTime(2026, 7, 1);
        Event(a, fa,  Timed(d.AddHours(10), d.AddHours(11)));
        Event(a, fa,  Timed(d.AddHours(23).AddMinutes(59), d.AddDays(1)));
        Event(a, fa,  Timed(d.AddDays(1), d.AddDays(1).AddHours(1)));             // ровно полночь
        Event(a, fa,  Timed(d.AddDays(2).AddHours(9), d.AddDays(4).AddHours(18))); // многодневное
        Event(a, fa2, Timed(d.AddHours(8), d.AddHours(9)));                        // скрытое поле
        Event(b, fb,  AllDay(d, d));
        Event(b, fb,  AllDay(d.AddDays(1), d.AddDays(3)));                          // весь день, три дня
        Event(b, fb,  Timed(d.AddDays(-1).AddHours(23), d.AddHours(1)));           // до окна
        Event(b, fb,  Timed(d.AddDays(9), d.AddDays(9).AddHours(2)));              // после окна
        Event(c, fc,  Timed(d.AddHours(12), d.AddHours(13)));
        for (var i = 0; i < 5; i++) Event(a, fa, Timed(d.AddDays(2).AddMinutes(i), d.AddDays(2).AddMinutes(i + 30)));

        repo.SetDateFieldVisible(fa2, false);
        foreach (var f in repo.GetFields(c).Where(f => f.FieldType == "date"))
            repo.SetDateFieldVisible(f.Id!.Value, false);   // у «Архива» скрыты ВСЕ даты

        var windows = new (DateTime Start, DateTime End)[]
        {
            (d, d.AddDays(5)),
            (d.AddHours(12), d.AddDays(2)),                                 // с середины дня
            (new DateTime(2000, 1, 1), new DateTime(2100, 1, 1)),          // вся история
            (d.AddDays(20), d.AddDays(30)),                                 // пусто
        };
        var sets = new ISet<int>?[] { null, new HashSet<int> { a }, new HashSet<int> { b, c }, new HashSet<int>() };

        foreach (var (start, end) in windows)
            foreach (var active in sets)
                Assert.Equal(Expected(db, start, end, active), repo.GetCalendarDayCountsByRange(start, end, active));

        // Смысл, а не только совпадение с эталоном: скрытое поле и скрытая коллекция не считаются.
        var day1 = repo.GetCalendarDayCountsByRange(d, d.AddDays(1), null);
        Assert.Equal([("2026-07-01", 3)], day1);   // две встречи и «весь день»
    }

    // Сама сводка против проекции: (коллекция, день начала, поле) → число строк.
    private static void AssertSummaryMatchesProjection(TempDatabase db, string step)
    {
        var projection = db.Context.CalendarIndex.AsNoTracking().ToList()
            .GroupBy(x => (x.CollectionId, Day: x.StartIso[..10], x.FieldId))
            .ToDictionary(g => g.Key, g => g.Count());

        var summary = new Dictionary<(int, string, int), int>();
        var conn = db.Context.Database.GetDbConnection();
        var opened = conn.State != System.Data.ConnectionState.Open;
        if (opened) conn.Open();
        try
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT CollectionId, StartDay, FieldId, Events FROM calendar_day_counts WHERE Events > 0";
            using var rdr = cmd.ExecuteReader();
            while (rdr.Read())
                summary[(rdr.GetInt32(0), rdr.GetString(1), rdr.GetInt32(2))] = rdr.GetInt32(3);
        }
        finally { if (opened) conn.Close(); }

        Assert.True(projection.Count == summary.Count && projection.All(kv => summary.GetValueOrDefault(kv.Key) == kv.Value),
                    $"{step}: сводка разошлась с проекцией — в проекции {projection.Count} групп, в сводке {summary.Count}");
    }

    /// <summary>
    /// Сводку держат триггеры проекции на ЛЮБОМ пути записи: перенос события на другой день и
    /// смена времени внутри дня (upsert ON CONFLICT DO UPDATE), очистка даты, удаление записи
    /// (каскад), удаление поля (каскад), пакетный дубль коллекции (INSERT … SELECT), пакетное
    /// удаление коллекции (явный DELETE пакетами). После каждого шага сводка равна проекции, а
    /// счетчики дней — определению; уборка нулевых строк ничего не ломает.
    /// </summary>
    [Fact]
    public void The_summary_follows_every_write_path()
    {
        using var db = new TempDatabase();
        var repo = db.Constructor;
        var d    = new DateTime(2026, 7, 1);

        var a  = SampleData.AddCollection(repo, "Встречи").Id!.Value;
        var fa = SampleData.AddCalendarDateField(repo, a, name: "Начало").Id!.Value;
        var fx = SampleData.AddCalendarDateField(repo, a, name: "Напоминание").Id!.Value;
        var b  = SampleData.AddCollection(repo, "Отпуска").Id!.Value;
        var fb = SampleData.AddCalendarDateField(repo, b, name: "Начало").Id!.Value;

        int Event(int col, int field, string value)
        {
            var id = SampleData.AddRecord(repo, col, "Событие").Id!.Value;
            repo.SetFieldValue(id, field, value);
            return id;
        }

        var moved   = Event(a, fa, Timed(d.AddHours(10), d.AddHours(11)));
        var retimed = Event(a, fa, Timed(d.AddHours(12), d.AddHours(13)));
        var cleared = Event(a, fa, Timed(d.AddHours(14), d.AddHours(15)));
        var removed = Event(a, fa, Timed(d.AddDays(1).AddHours(9), d.AddDays(1).AddHours(10)));
        Event(a, fx, Timed(d.AddHours(8), d.AddHours(9)));
        for (var i = 0; i < 4; i++) Event(b, fb, AllDay(d.AddDays(i), d.AddDays(i + 1)));

        void Check(string step)
        {
            AssertSummaryMatchesProjection(db, step);
            foreach (var active in new ISet<int>?[] { null, new HashSet<int> { a }, new HashSet<int> { b } })
                Assert.Equal(Expected(db, d.AddDays(-5), d.AddDays(30), active),
                             repo.GetCalendarDayCountsByRange(d.AddDays(-5), d.AddDays(30), active));
        }

        Check("заполнение");
        repo.SetFieldValue(moved, fa, Timed(d.AddDays(3).AddHours(10), d.AddDays(3).AddHours(11)));
        Check("перенос на другой день");
        repo.SetFieldValue(retimed, fa, Timed(d.AddHours(16), d.AddHours(17)));
        Check("другое время того же дня");
        repo.SetFieldValue(cleared, fa, "");
        Check("очистка даты");
        repo.DeleteRecord(removed);
        Check("удаление записи");
        repo.DeleteField(fx);
        Check("удаление поля");
        var copy = repo.DuplicateCollection(b).Id!.Value;
        Check("дубль коллекции");
        repo.DeleteCollectionData(a);
        Check("пакетное удаление коллекции");
        repo.TidyCalendarDayCounts();
        Check("уборка нулевых строк");
        Assert.NotEqual(0, copy);
    }
}
