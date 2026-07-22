namespace SmartBase.Core.Models;

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
    public string FieldType    { get; set; } = "text";  // text | number | date | select | status | checkbox | url | user | created_*/updated_*
    public string ConfigJson   { get; set; } = "{}";
    public int    Position     { get; set; } = 1000;
    public bool   IsCalStart   { get; set; } = false;
    public bool   IsCalEnd     { get; set; } = false;
    public bool   IsSystem     { get; set; } = false;
    public bool   IsRequired   { get; set; } = false;
    public bool   IsTaskExec     { get; set; } = false; // user-поле — источник задач (исполнитель)
    public bool   IsTaskAssigner { get; set; } = false; // поле «Назначил»
    public bool   IsTaskStatus   { get; set; } = false; // поле «Статус»
}

public class DbRecord
{
    public int?     Id           { get; set; }
    public int      CollectionId { get; set; }
    public string?  CreatedAt    { get; set; }
    // Аудит-метаданные записи (типы свойств «Дата/Создание/Изменение …»). Могут быть пусты
    // у старых записей (бэкфилл проставил только UpdatedAt = CreatedAt).
    public string?  UpdatedAt    { get; set; }
    public string?  CreatedBy    { get; set; }
    public string?  UpdatedBy    { get; set; }
    public int      Position     { get; set; } = 1000;
    public string?  StartDt      { get; set; }
    public string?  EndDt        { get; set; }
    public bool     AllDay       { get; set; } = false;
    public string   Title        { get; set; } = "";
    public string?  Description  { get; set; }

    public string   DisplayTitle => string.IsNullOrWhiteSpace(Title) ? "Без названия" : Title;
}
