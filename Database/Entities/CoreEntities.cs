using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace SmartBase.Database.Entities;

// ── Ядро данных конструктора: группы → коллекции → поля/записи → значения ──────

public class CategoryEntity
{
    [Key] public int    Id    { get; set; }
    [Required] public string Name  { get; set; } = "";
    [Required] public string Color { get; set; } = "#007AFF";
}

public class DbGroupEntity
{
    [Key] public int     Id       { get; set; }
    [Required] public string  Name     { get; set; } = "";
    public int?    ParentId { get; set; }
    public int     Position { get; set; } = 1000;

    [ForeignKey(nameof(ParentId))]
    public DbGroupEntity?                  Parent      { get; set; }
    public ICollection<DbGroupEntity>      Children    { get; set; } = [];
    public ICollection<DbCollectionEntity> Collections { get; set; } = [];
}

public class DbCollectionEntity
{
    [Key] public int    Id       { get; set; }
    [Required] public string Name     { get; set; } = "";
    public int?   GroupId  { get; set; }
    public int    Position { get; set; } = 1000;
    public string Icon     { get; set; } = "table";
    public string Color    { get; set; } = "#007AFF";

    [ForeignKey(nameof(GroupId))]
    public DbGroupEntity?              Group   { get; set; }
    public ICollection<DbFieldEntity>  Fields  { get; set; } = [];
    public ICollection<DbRecordEntity> Records { get; set; } = [];
}

public class DbFieldEntity
{
    [Key] public int    Id           { get; set; }
    public int    CollectionId { get; set; }
    [Required] public string Name         { get; set; } = "";
    [Required] public string FieldType    { get; set; } = "text";
    public string ConfigJson   { get; set; } = "{}";
    public int    Position     { get; set; } = 1000;
    public bool   IsCalStart   { get; set; } = false;
    public bool   IsCalEnd     { get; set; } = false;
    public bool   IsSystem     { get; set; } = false;
    public bool   IsRequired   { get; set; } = false;
    public bool   IsTaskExec     { get; set; } = false; // user-поле — источник задач (исполнитель)
    public bool   IsTaskAssigner { get; set; } = false; // поле «Назначил»
    public bool   IsTaskStatus   { get; set; } = false; // поле «Статус»

    [ForeignKey(nameof(CollectionId))]
    public DbCollectionEntity              Collection  { get; set; } = null!;
    public ICollection<DbFieldValueEntity> FieldValues { get; set; } = [];
}

public class DbRecordEntity
{
    [Key] public int     Id           { get; set; }
    public int     CollectionId { get; set; }
    public string  CreatedAt    { get; set; } = "";
    // Аудит-метаданные записи (для типов свойств «Дата/Создание/Изменение …»):
    // UpdatedAt — время последней правки записи; CreatedBy/UpdatedBy — снимок имени
    // пользователя (CurrentUser.DisplayName, как ChangeLogEntity.UserName), переживает
    // переименование/удаление автора. Заполняются при создании и правках записи.
    public string  UpdatedAt    { get; set; } = "";
    public string  CreatedBy    { get; set; } = "";
    public string  UpdatedBy    { get; set; } = "";
    public int     Position     { get; set; } = 1000;
    public string? StartDt      { get; set; }
    public string? EndDt        { get; set; }
    public bool    AllDay       { get; set; } = false;

    [Required] public string  Title       { get; set; } = "";
    public string? Description { get; set; }

    [ForeignKey(nameof(CollectionId))]
    public DbCollectionEntity              Collection  { get; set; } = null!;
    public ICollection<DbFieldValueEntity> FieldValues { get; set; } = [];
}

// Значение поля записи (EAV): одна строка на пару «запись × поле». Ключ — естественный
// (RecordId, FieldId), он же PK; суррогатного Id нет. Физически таблица пересоздана
// WITHOUT ROWID (см. миграцию SchemaCompact): сама таблица кластеризована по PK — значения
// одной записи лежат рядом, отдельный уникальный индекс пары не нужен.
public class DbFieldValueEntity
{
    public int    RecordId { get; set; }
    public int    FieldId  { get; set; }
    public string Value    { get; set; } = "";

    [ForeignKey(nameof(RecordId))]
    public DbRecordEntity Record { get; set; } = null!;
    [ForeignKey(nameof(FieldId))]
    public DbFieldEntity  Field  { get; set; } = null!;
}
