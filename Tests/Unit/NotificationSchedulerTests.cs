using SmartBase.Core.Models;
using SmartBase.Notifications;

namespace SmartBase.Tests.Unit;

/// <summary>
/// Ядро службы уведомлений (<see cref="NotificationScheduler"/>) — чистая логика отбора:
/// окно <c>(watermark, now]</c>, дедуп по (БД, запись, поле), сводка при переполнении и
/// сдвиг метки. Стоит за нативными баннерами и значком-счетчиком, но не зависит от ОС/БД.
/// </summary>
public sealed class NotificationSchedulerTests
{
    private static readonly DateTime W   = new(2026, 7, 1, 9, 0, 0);   // watermark
    private static readonly DateTime Now = new(2026, 7, 1, 12, 0, 0);  // «сейчас»

    private static CalendarRecord Ev(DateTime start, string title = "E",
                                     int id = 1, int field = 1, int db = 0,
                                     bool allDay = false, string col = "") =>
        new()
        {
            DbId = db, Id = id, FieldId = field, Title = title,
            StartDt = start, EndDt = start.AddHours(1), AllDay = allDay, CollectionName = col,
        };

    [Fact]
    public void Fires_events_with_start_inside_window()
    {
        var due = Ev(new DateTime(2026, 7, 1, 10, 0, 0), "Встреча", id: 5, field: 3, db: 2);
        var d = NotificationScheduler.Decide([due], W, Now, maxBanners: 5);

        Assert.Equal(1, d.FiredCount);
        Assert.Single(d.Banners);
        Assert.Equal("Встреча", d.Banners[0].Title);
        // Плашка несет метку «db:id:field» и саму запись — для маршрутизации клика в окно.
        Assert.Equal("2:5:3", d.Banners[0].Tag);
        Assert.Equal(due, d.Banners[0].Record);
    }

    [Fact]
    public void Summary_banner_has_no_record_or_tag()
    {
        var many = Enumerable.Range(0, 8)
            .Select(i => Ev(new DateTime(2026, 7, 1, 10, i, 0), $"E{i}", id: i))
            .ToList();

        var d = NotificationScheduler.Decide(many, W, Now, maxBanners: 5);

        Assert.Equal("", d.Banners[0].Tag);
        Assert.Null(d.Banners[0].Record);
    }

    [Fact]
    public void Watermark_is_exclusive_and_now_is_inclusive()
    {
        var atWatermark = Ev(W, "atW");            // == watermark → НЕ показываем
        var atNow       = Ev(Now, "atNow", id: 2); // == now       → показываем
        var future      = Ev(Now.AddMinutes(1), "future", id: 3);

        var d = NotificationScheduler.Decide([atWatermark, atNow, future], W, Now, 5);

        Assert.Equal(1, d.FiredCount);
        Assert.Equal("atNow", d.Banners[0].Title);
    }

    [Fact]
    public void Deduplicates_same_record_and_field()
    {
        var a = Ev(new DateTime(2026, 7, 1, 10, 0, 0), "A", id: 7, field: 3);
        var b = Ev(new DateTime(2026, 7, 1, 11, 0, 0), "A", id: 7, field: 3);

        var d = NotificationScheduler.Decide([a, b], W, Now, 5);

        Assert.Equal(1, d.FiredCount);
    }

    [Fact]
    public void Summarizes_when_over_cap()
    {
        var many = Enumerable.Range(0, 8)
            .Select(i => Ev(new DateTime(2026, 7, 1, 10, i, 0), $"E{i}", id: i))
            .ToList();

        var d = NotificationScheduler.Decide(many, W, Now, maxBanners: 5);

        Assert.Single(d.Banners);              // одна сводка вместо восьми плашек
        Assert.Equal(8, d.FiredCount);         // но значок знает про все восемь
        Assert.Contains("8", d.Banners[0].Body);
    }

    [Fact]
    public void Advances_watermark_to_now()
    {
        var d = NotificationScheduler.Decide([], W, Now, 5);

        Assert.Equal(0, d.FiredCount);
        Assert.Empty(d.Banners);
        Assert.Equal(Now, d.NewWatermark);
    }

    [Fact]
    public void Watermark_never_goes_backwards()
    {
        // Часы «скакнули» назад: метку не откатываем ниже прежней.
        var d = NotificationScheduler.Decide([], W, W.AddMinutes(-30), 5);
        Assert.Equal(W, d.NewWatermark);
    }

    [Fact]
    public void Body_has_time_and_collection_for_timed_event()
    {
        var e = Ev(new DateTime(2026, 7, 1, 10, 30, 0), "Звонок", col: "Дела");
        var d = NotificationScheduler.Decide([e], W, Now, 5);

        Assert.Equal("10:30 · Дела", d.Banners[0].Body);
    }

    [Fact]
    public void Body_says_all_day_for_all_day_event()
    {
        var e = Ev(new DateTime(2026, 7, 1, 0, 0, 0), "Отпуск", allDay: true);
        // all-day начинается в 00:00 — попадет в окно, если оно включает начало дня.
        var d = NotificationScheduler.Decide([e], new DateTime(2026, 6, 30, 23, 0, 0), Now, 5);

        Assert.StartsWith("Весь день", d.Banners[0].Body);
    }
}
