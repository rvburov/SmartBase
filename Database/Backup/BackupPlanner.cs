namespace SmartBase.Database.Backup;

/// <summary>
/// Чистая логика «когда делать следующий бэкап» — отделена от службы-таймера ради
/// юнит-тестов, как NotificationScheduler и FreezeDetector: время приходит параметром,
/// на часы и диск класс не смотрит.
///
/// Семантика догона: бэкап должен случиться, как только текущее время достигло
/// ближайшего запуска ПОСЛЕ последнего выполненного (<see cref="IsDue"/>). Если
/// приложение было выключено в назначенный момент, NextRun(lastRun) остается в прошлом —
/// и первый же тик после запуска выполнит пропущенную копию. Якорь при включении
/// тумблера — «сейчас» (пишет служба): первый бэкап идет по ближайшему расписанию,
/// а не немедленно.
/// </summary>
public static class BackupPlanner
{
    /// <summary>Ближайший запуск по расписанию СТРОГО ПОСЛЕ момента <paramref name="after"/>.</summary>
    public static DateTime NextRun(BackupSchedule s, DateTime after)
    {
        var time = s.Time.ToTimeSpan();
        switch (s.Frequency)
        {
            case BackupFrequency.Weekly:
            {
                // Не больше недели вперед: перебор дней короткий и очевидный.
                for (var i = 0; i <= 7; i++)
                {
                    var date = after.Date.AddDays(i);
                    var candidate = date.Add(time);
                    if (candidate > after && s.Days.Contains(date.DayOfWeek)) return candidate;
                }
                // Days пуст (после Parse не бывает) — ведем себя как daily, а не зацикливаемся.
                return NextRun(s with { Frequency = BackupFrequency.Daily }, after);
            }

            case BackupFrequency.Monthly:
            {
                // 31-е число в коротком месяце — его последний день (не пропускать месяц).
                for (var i = 0; i <= 13; i++)
                {
                    var first = new DateTime(after.Year, after.Month, 1).AddMonths(i);
                    var day   = Math.Min(s.DayOfMonth, DateTime.DaysInMonth(first.Year, first.Month));
                    var candidate = new DateTime(first.Year, first.Month, day).Add(time);
                    if (candidate > after) return candidate;
                }
                goto case BackupFrequency.Daily;   // недостижимо: 13 месяцев покрывают любой сдвиг
            }

            case BackupFrequency.Yearly:
            {
                // 29.02 в невисокосный год — 28.02 (копия раз в 4 года была бы сюрпризом).
                for (var i = 0; i <= 2; i++)
                {
                    var year = after.Year + i;
                    var day  = Math.Min(s.Day, DateTime.DaysInMonth(year, s.Month));
                    var candidate = new DateTime(year, s.Month, day).Add(time);
                    if (candidate > after) return candidate;
                }
                goto case BackupFrequency.Daily;   // недостижимо: два года покрывают любой сдвиг
            }

            case BackupFrequency.Daily:
            default:
            {
                var candidate = after.Date.Add(time);
                return candidate > after ? candidate : candidate.AddDays(1);
            }
        }
    }

    /// <summary>Пора ли делать бэкап: текущее время достигло ближайшего запуска после последнего.</summary>
    public static bool IsDue(BackupSchedule s, DateTime lastRun, DateTime now) =>
        now >= NextRun(s, lastRun);
}
