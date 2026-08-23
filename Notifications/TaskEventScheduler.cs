using SmartBase.Core.Models;

namespace SmartBase.Notifications;

/// <summary>
/// Чистое ядро уведомлений по задачам: без БД, IO и платформы — решает, какие события
/// показать в этот проход и каким текстом. Брат <see cref="NotificationScheduler"/>
/// (календарь), вынесен отдельно по той же причине: покрывается быстрыми модульными
/// тестами (см. Tests/Unit/TaskEventSchedulerTests).
///
/// Отбирать по времени здесь нечего — очередь в базе уже отдала только новое (курсор), —
/// зато нужны свертка повторов и разделение по видам: тумблеры «назначение» и «смена
/// стадии» независимы.
/// </summary>
public static class TaskEventScheduler
{
    /// <summary>Готовая к показу плашка. <see cref="Tag"/> — метка события для маршрутизации
    /// клика (пусто у сводки), <see cref="Event"/> — само событие (null у сводки), чтобы по
    /// клику открыть карточку задачи без повторного запроса в БД.</summary>
    public readonly record struct Banner(string Title, string Body, string Tag, TaskEvent? Event);

    /// <summary>Итог прохода: что показать и сколько всего событий пришло (это прибавка к значку).</summary>
    public sealed record Decision(IReadOnlyList<Banner> Banners, int FiredCount);

    /// <summary>Метка события. Префикс отличает ее от календарной («db:id:field»), чтобы
    /// служба знала, куда вести по клику.</summary>
    public static string TagOf(TaskEvent e) => $"task:{e.DbId}:{e.EventId}";

    /// <param name="events">Новые события из очередей всех допущенных баз (уже мои).</param>
    /// <param name="assignedOn">Включены уведомления о назначении задачи.</param>
    /// <param name="statusOn">Включены уведомления о смене стадии.</param>
    /// <param name="maxBanners">Порог: больше — вместо N плашек одна сводка.</param>
    public static Decision Decide(IReadOnlyList<TaskEvent> events,
                                  bool assignedOn, bool statusOn, int maxBanners)
    {
        var wanted = events
            .Where(e => e.Kind == TaskEventKinds.Assigned ? assignedOn
                      : e.Kind == TaskEventKinds.Status   ? statusOn
                      : false)
            .OrderBy(e => e.EventId)
            .ToList();

        if (wanted.Count == 0) return new Decision([], 0);

        // Свертка повторов по одной задаче: за время простоя стадия могла пройти
        // «Запланировано → В работе → Выполнено». Показываем ОДНУ плашку с последним
        // состоянием, но «было» берем от ПЕРВОГО события — иначе текст соврет о том,
        // откуда задача пришла.
        var due = wanted
            .GroupBy(e => (e.DbId, e.RecordId, e.FieldId, e.Kind))
            .Select(g =>
            {
                var last = g.Last();
                // Событие одно — отдаем его же: копия ничего не добавит, а клик по баннеру
                // получит ровно то, что пришло из очереди.
                if (ReferenceEquals(g.First(), last)) return last;

                return new TaskEvent
                {
                    DbId           = last.DbId,
                    EventId        = last.EventId,
                    Kind           = last.Kind,
                    Ts             = last.Ts,
                    ActorName      = last.ActorName,
                    RecordId       = last.RecordId,
                    CollectionId   = last.CollectionId,
                    FieldId        = last.FieldId,
                    ExecFieldId    = last.ExecFieldId,
                    Title          = last.Title,
                    CollectionName = last.CollectionName,
                    OldValue       = g.First().OldValue,
                    NewValue       = last.NewValue,
                };
            })
            .OrderBy(e => e.EventId)
            .ToList();

        // Много событий разом (обычно догон после закрытия приложения) — не заваливаем ОС
        // плашками, показываем одну сводку без привязки к задаче.
        if (due.Count > maxBanners)
            return new Decision([new Banner("Обновления по задачам", SummaryLine(due), "", null)], due.Count);

        var banners = due.Select(e => new Banner(TitleOf(e), BodyOf(e), TagOf(e), e)).ToList();
        return new Decision(banners, due.Count);
    }

    /// <summary>Первая строка плашки — сразу говорит, что случилось: в ОС она жирная и
    /// читается первой, а календарные напоминания начинаются с названия записи.</summary>
    public static string TitleOf(TaskEvent e) =>
        e.Kind == TaskEventKinds.Assigned ? "Вам назначена задача" : "Задача сменила стадию";

    /// <summary>Вторая строка плашки. Важное — в начале: длинный хвост ОС обрезает сама.</summary>
    public static string BodyOf(TaskEvent e)
    {
        var title = e.DisplayTitle;
        if (e.Kind == TaskEventKinds.Assigned)
        {
            // Кто назначил важнее коллекции; автора нет (историческое событие) — показываем,
            // где задача лежит.
            var who = e.ActorName.Length > 0 ? e.ActorName : e.CollectionName;
            return who.Length > 0 ? $"{title} · {who}" : title;
        }

        var was = e.OldValue.Length > 0 ? e.OldValue : "не задана";
        return $"{title}: {was} → {e.NewValue}";
    }

    // Сводка называет состав: «пришло 12 чего-то» человеку ничего не говорит.
    private static string SummaryLine(IReadOnlyList<TaskEvent> due)
    {
        var assigned = due.Count(e => e.Kind == TaskEventKinds.Assigned);
        var status   = due.Count - assigned;

        if (assigned == 0) return $"Смен стадии: {status}";
        if (status   == 0) return $"Назначено задач: {assigned}";
        return $"Назначено задач: {assigned} · Смен стадии: {status}";
    }
}
