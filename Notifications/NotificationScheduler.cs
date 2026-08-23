using SmartBase.Core.Models;

namespace SmartBase.Notifications;

/// <summary>
/// Чистое ядро службы уведомлений: без БД, IO и платформы — решает, какие события
/// показать в этот проход и куда сдвинуть водяную метку. Вынесено отдельно, чтобы
/// покрываться быстрыми модульными тестами (см. Tests/Unit/NotificationSchedulerTests).
///
/// Правило отбора: событие «наступило», если его <see cref="CalendarRecord.StartDt"/>
/// попадает в окно <c>(watermark, now]</c>. Источник событий (GetCalendarRecordsByRange)
/// сознательно ОТДАЕТ ЛИШНЕЕ (буфер многодневных ±62 дня), поэтому окончательную точную
/// отсечку по StartDt делаем здесь. Метка всегда двигается в <paramref name="now"/> —
/// даже если ничего не наступило, — чтобы одно и то же событие не показать дважды.
/// </summary>
public static class NotificationScheduler
{
    /// <summary>Готовая к показу плашка. <see cref="Tag"/> — id записи «db:id:field» для
    /// маршрутизации клика (пусто у сводки), <see cref="Record"/> — сама запись (null у сводки),
    /// чтобы по клику показать окно с полной информацией без повторного запроса в БД.</summary>
    public readonly record struct Banner(string Title, string Body, string Tag, CalendarRecord? Record);

    /// <summary>Метка записи для маршрутизации клика по баннеру.</summary>
    public static string TagOf(CalendarRecord r) => $"{r.DbId}:{r.Id ?? 0}:{r.FieldId}";

    /// <summary>Итог прохода: что показать, сколько всего событий наступило и новая метка.</summary>
    public sealed record Decision(IReadOnlyList<Banner> Banners, int FiredCount, DateTime NewWatermark);

    /// <param name="candidates">Кандидаты из источника (могут выходить за окно — отфильтруем).</param>
    /// <param name="watermark">Нижняя (исключающая) граница окна: до нее уже показано.</param>
    /// <param name="now">Верхняя (включающая) граница окна — «сейчас».</param>
    /// <param name="maxBanners">Порог: больше — вместо N плашек одна сводка «Пропущено…».</param>
    public static Decision Decide(IReadOnlyList<CalendarRecord> candidates,
                                  DateTime watermark, DateTime now, int maxBanners)
    {
        // Метка не идет назад (защита от рассинхронизации часов), но окно не пустеет,
        // если now == watermark: тогда наступивших нет и показывать нечего.
        var newWatermark = now > watermark ? now : watermark;

        var due = candidates
            .Where(r => r.StartDt > watermark && r.StartDt <= now)
            // Одна запись может дать несколько событий (по полю-дате) — ключ (БД, Id, поле).
            .GroupBy(r => (r.DbId, r.Id, r.FieldId))
            .Select(g => g.First())
            .OrderBy(r => r.StartDt)
            .ThenBy(r => r.DisplayTitle, StringComparer.CurrentCulture)
            .ToList();

        if (due.Count == 0)
            return new Decision([], 0, newWatermark);

        // Много наступивших разом (обычно догон после закрытия) — не заваливаем ОС
        // десятками плашек, а показываем одну сводку (без привязки к записи).
        if (due.Count > maxBanners)
        {
            var summary = new Banner("Пропущенные события", $"Наступило событий: {due.Count}", "", null);
            return new Decision([summary], due.Count, newWatermark);
        }

        var banners = due.Select(r => new Banner(r.DisplayTitle, DetailLine(r), TagOf(r), r)).ToList();
        return new Decision(banners, due.Count, newWatermark);
    }

    /// <summary>Вторая строка плашки: время (или «Весь день») и, если есть, коллекция.</summary>
    private static string DetailLine(CalendarRecord r)
    {
        var when = r.AllDay ? "Весь день" : r.StartDt.ToString("HH:mm");
        return string.IsNullOrWhiteSpace(r.CollectionName) ? when : $"{when} · {r.CollectionName}";
    }
}
