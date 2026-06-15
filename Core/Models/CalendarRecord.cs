namespace DataBase.Core.Models;

public class CalendarRecord
{
    public const string UntitledPlaceholder = "Без названия";

    public int?     Id             { get; set; }
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
