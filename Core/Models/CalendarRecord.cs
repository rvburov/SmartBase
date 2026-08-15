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
    // Для показа — первая непустая строка названия (см. TitleText): плашка события и строка
    // списка рисуют название одним блоком, многострочный текст в них наезжает сам на себя.
    public string   DisplayTitle   => TitleText.FirstLine(Title) is { Length: > 0 } line
                                          ? line : UntitledPlaceholder;
    public DateTime StartDt        { get; set; }
    public DateTime EndDt          { get; set; }
    public string   Description    { get; set; } = "";
    public bool     AllDay         { get; set; } = false;
    public int?     CollectionId   { get; set; }
    public string   CollectionName { get; set; } = "";
    public string   Color          { get; set; } = "#007AFF";
}
