using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace SmartBase.Database.Entities;

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

// Индексная проекция календаря: по строке на каждое видимое значение поля-даты
// (запись × поле-дата с IsCalStart). Денормализованный кэш под быстрый запрос диапазона —
// календарь читает отсюда, а не сканирует db_field_values. Start/End — ISO-строки
// (yyyy-MM-ddTHH:mm:ss), сортируются лексикографически. FK-каскады чистят строки при
// удалении записи/поля (как у db_field_values). Содержимое целиком derived из
// db_field_values + DbFields.IsCalStart.
public class CalendarIndexEntity
{
    public int    RecordId     { get; set; }
    public int    FieldId      { get; set; }
    public int    CollectionId { get; set; }
    public string StartIso     { get; set; } = "";   // ISO "o"-формат, сортируется лексикографически
    public string EndIso       { get; set; } = "";
    public bool   AllDay       { get; set; }

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

// ── Пользователи, права, журнал (модуль «Профиль») ─────────────────────────────
//
// Живут в базе КОНСТРУКТОРА (не в app.db): права и история относятся к конкретной
// базе и путешествуют вместе с её файлом. Профиль самого пользователя (ФИО) — в
// app.db (Utils/CurrentUser), сюда попадают только снапшоты.

// Пользователь, известный этой базе. Появляется при первом открытии базы этим
// пользователем (Utils/CurrentUser.Key). Первый открывший базу (когда админов ещё
// нет) становится администратором; остальные ждут допуска (IsApproved) — его даёт
// администратор в панели или верный пароль базы (db_meta).
public class DbUserEntity
{
    [Key] public int    Id          { get; set; }
    [Required] public string UserKey { get; set; } = "";   // «учётка@компьютер», нижний регистр
    public string DisplayName { get; set; } = "";           // снапшот ФИО, обновляется при монтировании
    public bool   IsAdmin     { get; set; } = false;
    public bool   IsApproved  { get; set; } = false;
    public string CreatedAt   { get; set; } = "";           // ISO "o", когда впервые открыл базу

    public ICollection<DbPermissionEntity> Permissions { get; set; } = [];
}

// Права пользователя: битовая маска действий (Core/Models/Permissions.PermAction).
// CollectionId = null — права на всю базу; не-null — переопределение для коллекции
// (при проверке маска коллекции имеет приоритет над маской базы). Админ строк не
// имеет — у него всегда всё. Строки на коллекцию каскадно чистятся при её удалении.
public class DbPermissionEntity
{
    [Key] public int  Id           { get; set; }
    public int  UserId       { get; set; }
    public int? CollectionId { get; set; }
    public int  ActionsMask  { get; set; }

    [ForeignKey(nameof(UserId))]
    public DbUserEntity User { get; set; } = null!;
    [ForeignKey(nameof(CollectionId))]
    public DbCollectionEntity? Collection { get; set; }
}

// Ключ-значение базы конструктора: хеш пароля (password.hash/salt/iterations) и
// будущие мета-настройки. Аналог app_settings, но для конкретной базы.
public class DbMetaEntity
{
    [Key] public string Key { get; set; } = "";
    public string Value     { get; set; } = "";
}

// Журнал изменений: только факты (кто/когда/что), без старых и новых значений.
// UserKey/UserName — снапшоты (не FK): журнал переживает удаление пользователя.
// Пакетные операции (импорт, массовое удаление/дублирование) пишут ОДНУ строку
// с Count. Чтение — keyset-пагинация по Id DESC (rowid, отдельный индекс не нужен).
public class ChangeLogEntity
{
    [Key] public long   Id  { get; set; }
    public string Ts        { get; set; } = "";   // ISO "o" UTC
    public string UserKey   { get; set; } = "";
    public string UserName  { get; set; } = "";
    public string Action    { get; set; } = "";   // create/update/delete/duplicate/import/move
    public string TargetType { get; set; } = "";  // collection/group/field/record/records
    public string TargetName { get; set; } = "";  // снапшот имени объекта
    public string? CollectionName { get; set; }   // контекст для записей/свойств
    public long?  Count     { get; set; }         // для пакетных операций
}
