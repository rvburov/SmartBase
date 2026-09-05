using SmartBase.Database.Backup;

namespace SmartBase.Tests.Unit;

/// <summary>
/// Расписание резервного копирования: строка настройки разбирается строго (мусор молча
/// дает стандарт «daily 03:00», а не роняет старт), Parse/ToString образуют round-trip,
/// а планировщик считает следующий запуск с догоном пропущенного и держит краевые
/// случаи календаря (31-е в коротком месяце, 29 февраля).
/// </summary>
public sealed class BackupScheduleTests
{
    // ── Разбор и канонический вид ─────────────────────────────────────────────

    public static TheoryData<string> CanonicalStrings =>
    [
        "daily 03:00",
        "daily 23:45",
        "weekly mon 03:00",
        "weekly mon,thu 14:30",
        "weekly mon,tue,wed,thu,fri,sat,sun 00:00",
        "monthly 1 03:00",
        "monthly 31 12:00",
        "yearly 15.01 03:00",
        "yearly 29.02 06:00",
    ];

    [Theory]
    [MemberData(nameof(CanonicalStrings))]
    public void Parse_and_tostring_round_trip_canonical_forms(string text)
    {
        Assert.Equal(text, BackupSchedule.Parse(text).ToString());
    }

    public static TheoryData<string?> Garbage =>
    [
        null, "", "  ", "daily", "daily 3:00:00", "daily 25:00", "daily 03:60",
        "weekly 03:00", "weekly  03:00", "weekly xxx 03:00", "weekly mon,mon 03:00",
        "weekly mon,extra,thu", "monthly 0 03:00", "monthly 32 03:00", "monthly abc 03:00",
        "yearly 30.02 03:00", "yearly 32.01 03:00", "yearly 15.13 03:00", "yearly 15-01 03:00",
        "hourly 03:00", "daily 03:00 extra",
    ];

    [Theory]
    [MemberData(nameof(Garbage))]
    public void Garbage_falls_back_to_default(string? text)
    {
        Assert.Equal(BackupSchedule.Default, BackupSchedule.Parse(text));
    }

    [Fact]
    public void Weekly_days_are_normalized_to_monday_first_order()
    {
        // Дни в произвольном порядке — канонический вид сортирует Пн → Вс,
        // иначе один набор писался бы в app.db разными строками.
        Assert.Equal("weekly mon,fri,sun 09:00", BackupSchedule.Parse("weekly sun,fri,mon 09:00").ToString());
    }

    [Fact]
    public void Default_is_daily_at_three()
    {
        var d = BackupSchedule.Default;
        Assert.Equal(BackupFrequency.Daily, d.Frequency);
        Assert.Equal(new TimeOnly(3, 0), d.Time);
        Assert.Equal("daily 03:00", d.ToString());
    }

    // ── Следующий запуск ──────────────────────────────────────────────────────

    [Fact]
    public void Daily_next_run_is_today_before_time_and_tomorrow_after()
    {
        var s = BackupSchedule.Parse("daily 03:00");
        Assert.Equal(new DateTime(2026, 9, 5, 3, 0, 0),
                     BackupPlanner.NextRun(s, new DateTime(2026, 9, 5, 1, 0, 0)));
        Assert.Equal(new DateTime(2026, 9, 6, 3, 0, 0),
                     BackupPlanner.NextRun(s, new DateTime(2026, 9, 5, 3, 0, 0)));   // строго после
        Assert.Equal(new DateTime(2026, 9, 6, 3, 0, 0),
                     BackupPlanner.NextRun(s, new DateTime(2026, 9, 5, 12, 0, 0)));
    }

    [Fact]
    public void Weekly_next_run_lands_on_selected_days_only()
    {
        var s = BackupSchedule.Parse("weekly mon,thu 03:00");
        // 05.09.2026 — суббота: ближайший — понедельник 07.09.
        Assert.Equal(new DateTime(2026, 9, 7, 3, 0, 0),
                     BackupPlanner.NextRun(s, new DateTime(2026, 9, 5, 12, 0, 0)));
        // Понедельник после времени — четверг той же недели.
        Assert.Equal(new DateTime(2026, 9, 10, 3, 0, 0),
                     BackupPlanner.NextRun(s, new DateTime(2026, 9, 7, 4, 0, 0)));
        // Четверг после времени — понедельник следующей недели.
        Assert.Equal(new DateTime(2026, 9, 14, 3, 0, 0),
                     BackupPlanner.NextRun(s, new DateTime(2026, 9, 10, 4, 0, 0)));
    }

    [Fact]
    public void Monthly_31st_falls_back_to_last_day_of_short_month()
    {
        var s = BackupSchedule.Parse("monthly 31 03:00");
        // После 31 января — 28 февраля (2026 не високосный), а не пропуск месяца.
        Assert.Equal(new DateTime(2026, 2, 28, 3, 0, 0),
                     BackupPlanner.NextRun(s, new DateTime(2026, 1, 31, 3, 0, 0)));
        // После 28 февраля — 31 марта.
        Assert.Equal(new DateTime(2026, 3, 31, 3, 0, 0),
                     BackupPlanner.NextRun(s, new DateTime(2026, 2, 28, 3, 0, 0)));
    }

    [Fact]
    public void Yearly_feb_29_runs_on_feb_28_of_common_years()
    {
        var s = BackupSchedule.Parse("yearly 29.02 03:00");
        Assert.Equal(new DateTime(2026, 2, 28, 3, 0, 0),
                     BackupPlanner.NextRun(s, new DateTime(2026, 1, 1, 0, 0, 0)));
        // В високосном 2028 — честное 29-е.
        Assert.Equal(new DateTime(2028, 2, 29, 3, 0, 0),
                     BackupPlanner.NextRun(s, new DateTime(2027, 3, 1, 0, 0, 0)));
    }

    [Fact]
    public void Yearly_next_run_moves_to_next_year_after_date_passed()
    {
        var s = BackupSchedule.Parse("yearly 15.01 03:00");
        Assert.Equal(new DateTime(2027, 1, 15, 3, 0, 0),
                     BackupPlanner.NextRun(s, new DateTime(2026, 9, 5, 12, 0, 0)));
    }

    // ── Пора ли (IsDue) и догон ───────────────────────────────────────────────

    [Fact]
    public void Not_due_before_scheduled_time()
    {
        var s = BackupSchedule.Parse("daily 03:00");
        var lastRun = new DateTime(2026, 9, 4, 3, 0, 0);
        Assert.False(BackupPlanner.IsDue(s, lastRun, now: new DateTime(2026, 9, 5, 2, 59, 0)));
        Assert.True(BackupPlanner.IsDue(s, lastRun, now: new DateTime(2026, 9, 5, 3, 0, 0)));
    }

    [Fact]
    public void Missed_run_while_app_was_closed_is_due_immediately()
    {
        // Копия делалась 1-го, приложение не работало неделю: запуск 8-го в 10 утра —
        // пропущенные срабатывания дают due сразу, а не ожидание следующей ночи.
        var s = BackupSchedule.Parse("daily 03:00");
        Assert.True(BackupPlanner.IsDue(s,
            lastRun: new DateTime(2026, 9, 1, 3, 0, 0),
            now:     new DateTime(2026, 9, 8, 10, 0, 0)));
    }

    [Fact]
    public void Anchor_at_enable_time_waits_for_next_scheduled_run()
    {
        // Тумблер включили днем (якорь = «сейчас»): бэкап не должен стартовать немедленно —
        // только ближайшей ночью по расписанию.
        var s = BackupSchedule.Parse("daily 03:00");
        var anchor = new DateTime(2026, 9, 5, 15, 0, 0);
        Assert.False(BackupPlanner.IsDue(s, anchor, now: new DateTime(2026, 9, 5, 15, 1, 0)));
        Assert.True(BackupPlanner.IsDue(s, anchor, now: new DateTime(2026, 9, 6, 3, 0, 0)));
    }
}
