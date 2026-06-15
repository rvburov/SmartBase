using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace MyPlanner.Database.Entities;

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

    [ForeignKey(nameof(CollectionId))]
    public DbCollectionEntity              Collection  { get; set; } = null!;
    public ICollection<DbFieldValueEntity> FieldValues { get; set; } = [];
}

public class DbRecordEntity
{
    [Key] public int     Id           { get; set; }
    public int     CollectionId { get; set; }
    public string  CreatedAt    { get; set; } = "";
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

public class DbFieldValueEntity
{
    [Key] public int    Id       { get; set; }
    public int    RecordId { get; set; }
    public int    FieldId  { get; set; }
    public string Value    { get; set; } = "";

    [ForeignKey(nameof(RecordId))]
    public DbRecordEntity Record { get; set; } = null!;
    [ForeignKey(nameof(FieldId))]
    public DbFieldEntity  Field  { get; set; } = null!;
}

public class UiColWidthEntity
{
    public int CollectionId { get; set; }
    public int FieldId      { get; set; }
    public int Width        { get; set; }
}

public class UiRowHeightEntity
{
    public int CollectionId { get; set; }
    public int RecordId     { get; set; }
    public int Height       { get; set; }
}

public class UiCollectionSettingEntity
{
    [Key] public int    CollectionId { get; set; }
    public bool   ColResize    { get; set; } = false;
    public bool   RowResize    { get; set; } = false;
    public string ViewMode     { get; set; } = "table";
}

// Метаданные системных колонок «Название»/«Описание» (Title/Description в db_records).
// Сами колонки физические, поэтому их подпись храним отдельно, наложением.
// Key = TableRowVm.TitleKey ("__title__") | TableRowVm.DescKey ("__desc__").
public class SystemFieldMetaEntity
{
    public int     CollectionId { get; set; }
    public string  Key          { get; set; } = "";
    public string? DisplayName  { get; set; }   // null = показывать имя по умолчанию
    public int     Position     { get; set; }   // не используется (порядок — в column_order)
}

// Единый порядок отображения колонок таблицы (Title/Desc + пользовательские поля).
// Key = TableRowVm.TitleKey/DescKey или строковый id поля. Источник правды только
// для порядка; DbFields.Position остаётся для внутренней логики.
public class ColumnOrderEntity
{
    public int    CollectionId { get; set; }
    public string Key          { get; set; } = "";
    public int    Position     { get; set; }
}

// Отображение колонок (окно «Отображение»). Key = TableRowVm.TitleKey/DescKey либо
// строковый id поля (как в column_order). Visible=false — колонка скрыта в таблице.
// Level — пресет-уровень колонки (по умолчанию 1); блок уровней массово включает
// чекбоксы видимости по уровню ≤ выбранного. Строки только для изменённых колонок;
// отсутствие строки = видима, уровень 1.
public class ColumnDisplayEntity
{
    public int    CollectionId { get; set; }
    public string Key          { get; set; } = "";
    public bool   Visible      { get; set; } = true;
    public int    Level        { get; set; } = 1;
}

// Отображение записей (окно «Отображение записей») — аналог ColumnDisplayEntity, но
// для строк-записей. Visible=false — запись скрыта в таблице (фильтруется на уровне
// SQL в постраничной загрузке, виртуализация сохраняется). Level — пресет-уровень
// записи; блок уровней массово включает видимость по уровню ≤ выбранного (только
// внутри окна, на отрисовку таблицы не влияет). Строки только для изменённых записей;
// отсутствие строки = видима, уровень 1.
public class RecordDisplayEntity
{
    public int  CollectionId { get; set; }
    public int  RecordId     { get; set; }
    public bool Visible      { get; set; } = true;
    public int  Level        { get; set; } = 1;
}
