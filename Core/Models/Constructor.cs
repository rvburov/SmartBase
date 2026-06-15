namespace DataBase.Core.Models;

public class DbGroup
{
    public int?   Id       { get; set; }
    public string Name     { get; set; } = "";
    public int?   ParentId { get; set; }
    public int    Position { get; set; } = 1000;
}

public class DbCollection
{
    public int?   Id       { get; set; }
    public string Name     { get; set; } = "";
    public int?   GroupId  { get; set; }
    public int    Position { get; set; } = 1000;
    public string Icon     { get; set; } = "table";
    public string Color    { get; set; } = "#007AFF";
}

public class DbField
{
    public int?   Id           { get; set; }
    public int    CollectionId { get; set; }
    public string Name         { get; set; } = "";
    public string FieldType    { get; set; } = "text";  // text | number | date | select | checkbox | url
    public string ConfigJson   { get; set; } = "{}";
    public int    Position     { get; set; } = 1000;
    public bool   IsCalStart   { get; set; } = false;
    public bool   IsCalEnd     { get; set; } = false;
    public bool   IsSystem     { get; set; } = false;
    public bool   IsRequired   { get; set; } = false;
}

public class DbRecord
{
    public int?     Id           { get; set; }
    public int      CollectionId { get; set; }
    public string?  CreatedAt    { get; set; }
    public int      Position     { get; set; } = 1000;
    public string?  StartDt      { get; set; }
    public string?  EndDt        { get; set; }
    public bool     AllDay       { get; set; } = false;
    public string   Title        { get; set; } = "";
    public string?  Description  { get; set; }

    public string   DisplayTitle => string.IsNullOrWhiteSpace(Title) ? "Без названия" : Title;
}
