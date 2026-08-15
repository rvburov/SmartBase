using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace SmartBase.Database.Entities;

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
    // ISO "o", последнее монтирование базы этим пользователем (панель администратора
    // показывает колонкой «Последняя активность»). Отмечается при монтировании и НЕ пишется
    // в журнал: иначе каждое открытие базы будило бы поллеры соседей строкой-пустышкой.
    public string LastSeenAt  { get; set; } = "";

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

// Журнал изменений: факты (кто/когда/что) + компактное «до → после» для правок (Detail).
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
    // Готовый короткий текст «до → после» (Utils/ChangeDetail): переименования и правки
    // значений; длинные тексты свёрнуты до изменившегося фрагмента ещё при записи —
    // журнал не раздувается. NULL — событие без деталей (создание/удаление/пакетные).
    public string? Detail   { get; set; }
}
