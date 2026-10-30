using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace SmartBase.Database.Entities;

// Имена участников и история принадлежат базе конструктора. Служебные колонки старых
// версий сохраняются для совместимости схемы; вход и права по ним не рассчитываются.
public class DbUserEntity
{
    [Key] public int    Id          { get; set; }
    [Required] public string UserKey { get; set; } = "";   // «учетка@компьютер», нижний регистр
    public string DisplayName { get; set; } = "";           // снапшот ФИО, обновляется при монтировании
    // Исторические колонки: приложение их не читает и не использует как ограничения.
    public bool   IsAdmin     { get; set; } = false;
    public bool   IsApproved  { get; set; } = false;
    public bool   AccessDecided { get; set; } = false;
    public string CreatedAt   { get; set; } = "";           // ISO "o", когда впервые открыл базу
    // ISO "o", последнее монтирование. Отметка активности не создает событие истории.
    public string LastSeenAt  { get; set; } = "";

    public ICollection<DbPermissionEntity> Permissions { get; set; } = [];
}

// Историческая таблица сохраняется в модели и миграциях без активного использования.
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

// Настройки базы конструктора. Неизвестные и исторические ключи сохраняются.
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
    // значений; длинные тексты свернуты до изменившегося фрагмента еще при записи —
    // журнал не раздувается. NULL — событие без деталей (создание/удаление/пакетные).
    public string? Detail   { get; set; }
}
