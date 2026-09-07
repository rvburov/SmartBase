using System.Diagnostics;
using Serilog;
using SmartBase.Logging;

namespace SmartBase.Database;

/// <summary>
/// Шина СВОИХ изменений: каждый коммит в пользовательскую базу из ЭТОГО процесса — общий
/// контекст раздела, изолированное фоновое подключение массовой операции, окно расширения —
/// поднимает <see cref="Committed"/> с путем базы и тем, что в ней изменилось. Подписчик один:
/// главное окно, оно сводит события в одно правило «раздел перечитывает данные, если читал их
/// раньше последнего коммита» (см. <c>MainWindow.FlushLocalChanges</c>).
///
/// Зачем отдельная шина, когда есть <see cref="ChangePoller"/>. Поллер ловит ЧУЖИЕ правки по
/// хвосту <c>change_log</c> и свои строки отбрасывает по <c>UserKey</c> — для соседей это
/// верно: тот, кто правил, уже обновил свой экран. Но «свой экран» — это один раздел, а
/// разделов несколько: правка ячейки в конструкторе не доходила до «Задач», перенос события в
/// календаре — до «Задач» и конструктора, запись из «Регистрации КД» — ни до кого, пока
/// человек не переключит раздел или галочку. Точечные события между разделами
/// (<c>CollectionsChanged</c>, <c>RecordValueEdited</c>, …) закрывали лишь часть пар, а расширения
/// о разделах не знают вовсе. Поэтому источник события — сам контекст EF
/// (<see cref="AppDbContext"/>): мимо него в базу не пишет никто, и расширениям ничего
/// подключать не нужно.
///
/// Событие поднимается СИНХРОННО внутри <c>SaveChanges</c>/<c>Commit</c> и на ТОМ ЖЕ потоке,
/// где шла запись (UI-поток у общего контекста, фоновый — у изолированных). Подписчик обязан
/// лишь запомнить факт и уйти: любая работа с базой отсюда — реентерабельный вызов контекста
/// посреди его сохранения. Исключение подписчика глушится с записью в журнал: запись в базу
/// уже состоялась, и ронять ее из-за обновления экрана нельзя.
/// </summary>
public static class ChangeBus
{
    /// <summary>
    /// Что изменилось: действие и тип объекта — теми же словами, что в <c>change_log</c>
    /// (<c>record</c>/<c>records</c>, <c>field</c>, <c>collection</c>, <c>group</c>, <c>user</c>),
    /// чтобы главное окно решало, что обновлять, одним правилом с чужими правками.
    /// <paramref name="CollectionName"/> — как в журнале: есть у строк журнала, у правок сущностей
    /// без него пусто.
    /// </summary>
    public sealed record Change(string Action, string TargetType, string? CollectionName);

    /// <summary>Коммит в базу (путь базы / строка подключения PostgreSQL, что изменилось).
    /// Поднимается из потока записи — подписчик маршалит в UI сам.</summary>
    public static event Action<string, IReadOnlyList<Change>>? Committed;

    /// <summary>
    /// Монотонная отметка времени для сравнения «когда читали» с «когда коммитили»:
    /// разделы штампуют ею начало чтения (<c>DataStamp</c>), копилка — момент коммита.
    /// </summary>
    public static long Now() => Stopwatch.GetTimestamp();

    /// <summary>Типы объектов, изменение которых перестраивает деревья коллекций и колонки
    /// открытой таблицы (в отличие от правок записей, которые обновляют строки на месте).</summary>
    public static bool IsStructural(string targetType) =>
        targetType is "collection" or "group" or "field";

    internal static void Publish(string dbPath, IReadOnlyList<Change> changes)
    {
        if (changes.Count == 0) return;
        var handlers = Committed;
        if (handlers is null) return;

        try { handlers(dbPath, changes); }
        catch (Exception ex)
        {
            // Одна запись в минуту: ошибка обработчика повторялась бы на каждом коммите.
            if (Diag.Throttle("change-bus-handler", TimeSpan.FromMinutes(1)))
                Log.Warning(ex, "Шина изменений: обработчик упал — экран может отстать от базы ({Db:l})",
                            Utils.DbErrors.Display(dbPath));
        }
    }
}
