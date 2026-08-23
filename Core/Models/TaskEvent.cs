namespace SmartBase.Core.Models;

/// <summary>Виды событий задач: их же значения лежат в столбце Kind таблицы task_events.</summary>
public static class TaskEventKinds
{
    /// <summary>Исполнителем задачи назначили получателя.</summary>
    public const string Assigned = "assigned";
    /// <summary>У задачи сменилась стадия; получатель — тот, кто ее назначил.</summary>
    public const string Status   = "status";
}

/// <summary>
/// Событие задачи, прочитанное из очереди базы (task_events) для показа баннером.
/// Читается фоновым тиком уведомлений; <see cref="DbId"/> проставляет читатель
/// (Notifications/TaskEventReader) — в самой базе его нет и быть не может.
/// </summary>
public class TaskEvent
{
    public int    DbId    { get; set; }   // владелец-БД (мульти-маунт)
    public long   EventId { get; set; }   // Id строки очереди: он же курсор и метка баннера

    public string Kind { get; set; } = "";
    public DateTime Ts { get; set; }

    /// <summary>Кто сделал правку (снапшот ФИО) — для тела баннера «кто назначил».</summary>
    public string ActorName { get; set; } = "";

    public int    RecordId       { get; set; }
    public int    CollectionId   { get; set; }
    public int    FieldId        { get; set; }
    public int    ExecFieldId    { get; set; }
    public string Title          { get; set; } = "";
    public string CollectionName { get; set; } = "";
    public string OldValue       { get; set; } = "";
    public string NewValue       { get; set; } = "";

    /// <summary>Название одной строкой — как у карточки задачи и события календаря.</summary>
    public string DisplayTitle => TitleText.FirstLine(Title) is { Length: > 0 } line
                                      ? line : CalendarRecord.UntitledPlaceholder;
}
