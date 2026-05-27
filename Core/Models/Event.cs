namespace MyPlanner.Core.Models;

public class Event
{
    public int?     Id             { get; set; }
    public string   Title          { get; set; } = "";
    public DateTime StartDt        { get; set; }
    public DateTime EndDt          { get; set; }
    public string   Description    { get; set; } = "";
    public bool     AllDay         { get; set; } = false;
    public int?     CollectionId   { get; set; }
    public string   CollectionName { get; set; } = "";
    public string   Color          { get; set; } = "#007AFF";
}
