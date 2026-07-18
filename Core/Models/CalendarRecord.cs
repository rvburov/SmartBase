namespace SmartBase.Core.Models;

public class CalendarRecord
{
    public const string UntitledPlaceholder = "Без названия";

    public int      DbId           { get; set; }   // владелец-БД (мульти-маунт); 0 = по умолчанию
    public int?     Id             { get; set; }
    // Поле-дата, из которого получено событие. Одна запись может давать несколько событий
    // (по полю на каждую видимую дату), поэтому событие опознаётся парой (Id, FieldId).
    public int      FieldId        { get; set; }
    public string   Title          { get; set; } = "";
    public string   DisplayTitle   => string.IsNullOrWhiteSpace(Title) ? UntitledPlaceholder : Title;
    public DateTime StartDt        { get; set; }
    public DateTime EndDt          { get; set; }
    public string   Description    { get; set; } = "";
    public bool     AllDay         { get; set; } = false;
    public int?     CollectionId   { get; set; }
    public string   CollectionName { get; set; } = "";
    public string   Color          { get; set; } = "#007AFF";
}
