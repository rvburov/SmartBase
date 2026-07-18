namespace SmartBase.Core.Models;

public class TaskRecord
{
    public int       DbId           { get; set; }   // владелец-БД (мульти-маунт); 0 = по умолчанию
    public int?      Id             { get; set; }
    // User-поле (IsTaskExec), из которого получена задача. Одна запись может давать несколько
    // задач (по включённому полю-исполнителю), поэтому задача опознаётся парой (Id, ExecFieldId).
    public int       ExecFieldId    { get; set; }
    public string    Title          { get; set; } = "";
    public string    DisplayTitle   => string.IsNullOrWhiteSpace(Title) ? CalendarRecord.UntitledPlaceholder : Title;
    public string    Description    { get; set; } = "";
    public string    Executor       { get; set; } = "";
    public string    Assigner       { get; set; } = "";
    public string    Status         { get; set; } = "";
    // Срок — первая (по началу) календарная дата записи (поле-дата с IsCalStart); null = «без даты».
    public DateTime? DueStart       { get; set; }
    public DateTime? DueEnd         { get; set; }
    public bool      AllDay         { get; set; } = false;
    public int?      DueFieldId     { get; set; }
    public int?      CollectionId   { get; set; }
    public string    CollectionName { get; set; } = "";
    public string    Color          { get; set; } = "#007AFF";
}
