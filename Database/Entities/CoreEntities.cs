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
    // Роль «Описание»: обычное поле (удаляется, переименовывается, меняет тип), но панель
    // записи рисует его нижним markdown-блоком, а календарь/задачи пишут в него описание.
    // Заводится только календарным и задачным путём — см. EnsureDescriptionField.
    public bool   IsDescription  { get; set; } = false;

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

    // «Название» — единственный системный атрибут записи. «Описание» больше не колонка:
    // это обычное поле коллекции с ролью IsDescription, значения — в db_field_values.
    [Required] public string  Title       { get; set; } = "";

    // НЕ ИСПОЛЬЗУЕТСЯ. Была попытка сквозного цвета записи: одна пометка видна и в таблице,
    // и в календаре, и в задачах. В работе оказалось не то, что нужно — цвет в таблице
    // читается как оформление таблицы, а не как свойство записи, и «подсветка» строки
    // вылезала полосками в других разделах. Уровень убран, значения переехали в
    // record_display.Bg (миграция RowBackgroundBack), цвет строки/ячейки/колонки живёт
    // только в UI-таблицах.
    //
    // Колонка ОСТАВЛЕНА пустой намеренно: DropColumn на SQLite — перестройка таблицы, а
    // db_records в рабочей базе миллионы строк; цена несопоставима с пустой колонкой.
    // Копируется при дубле и переносе как есть — если уровень когда-нибудь вернут, данные
    // уже поедут правильно.
    public string? Color       { get; set; }

    [ForeignKey(nameof(CollectionId))]
    public DbCollectionEntity              Collection  { get; set; } = null!;
    public ICollection<DbFieldValueEntity> FieldValues { get; set; } = [];
}

// Связь между записями (тип поля «relation»): ребро графа «из записи FromRecordId в запись
// ToRecordId по полю-связи FieldId». Many-to-many из коробки (one-to-many/one — ограничения в
// логике/UI). Значения связи НЕ в db_field_values — здесь, чтобы работали каскады. Position —
// порядок чипов в ячейке.
//
// TargetDbId — межбазовая цель: 0 = цель в ЭТОЙ же БД (обычный случай, FK-каскад на ToRecordId
// работает); >0 = стабильный DbId цели из реестра app.db, тогда ToRecordId — id записи в ДРУГОМ
// файле (FK на него невозможен между файлами — снят; чистку при удалении цели делает прикладной
// код). FK-каскады на поле и запись-источник остаются (они всегда в этой же БД). Уникальность
// (FieldId, FromRecordId, TargetDbId, ToRecordId) — не даёт задвоить связь и различает одинаковые
// id в разных целевых БД.
public class DbRecordLinkEntity
{
    [Key] public int Id           { get; set; }
    public int        FieldId      { get; set; }
    public int        FromRecordId { get; set; }
    public int        ToRecordId   { get; set; }
    public int        TargetDbId   { get; set; }   // 0 = та же БД; >0 = DbId цели в реестре
    public int        Position     { get; set; } = 1000;
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
