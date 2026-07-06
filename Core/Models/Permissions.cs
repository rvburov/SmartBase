namespace DataBase.Core.Models;

/// <summary>
/// Действия, на которые администратор базы выдаёт права (панель администратора модуля
/// «Профиль»). Битовая маска: у пользователя одна int-маска на область (вся база или
/// конкретная коллекция, db_permissions.ActionsMask) — один SELECT на пользователя,
/// дёшево кешируется, новое действие = новый бит. CreateCollection имеет смысл только
/// на уровне базы (у несозданной коллекции нет переопределений).
/// </summary>
[Flags]
public enum PermAction
{
    None                = 0,
    ViewCollection      = 1 << 0,
    EditCollection      = 1 << 1,
    DeleteCollection    = 1 << 2,
    DuplicateCollection = 1 << 3,
    CreateCollection    = 1 << 4,
    CreateRecord        = 1 << 5,
    EditRecord          = 1 << 6,
    DeleteRecord        = 1 << 7,
    DuplicateRecord     = 1 << 8,
    CreateField         = 1 << 9,
    EditField           = 1 << 10,
    DeleteField         = 1 << 11,

    /// <summary>Полная маска — права допущенного пользователя по умолчанию
    /// (администратор снимает лишнее в панели).</summary>
    All = ViewCollection | EditCollection | DeleteCollection | DuplicateCollection |
          CreateCollection | CreateRecord | EditRecord | DeleteRecord | DuplicateRecord |
          CreateField | EditField | DeleteField,
}

/// <summary>Русские подписи действий для панели администратора и сообщений об отказе.</summary>
public static class PermActionLabels
{
    /// <summary>Подпись действия для чекбокса панели администратора.</summary>
    public static string Label(PermAction a) => a switch
    {
        PermAction.ViewCollection      => "Просматривать коллекцию",
        PermAction.EditCollection      => "Изменять коллекцию",
        PermAction.DeleteCollection    => "Удалять коллекцию",
        PermAction.DuplicateCollection => "Дублировать коллекцию",
        PermAction.CreateCollection    => "Создавать коллекции",
        PermAction.CreateRecord        => "Создавать записи",
        PermAction.EditRecord          => "Изменять записи",
        PermAction.DeleteRecord        => "Удалять записи",
        PermAction.DuplicateRecord     => "Дублировать записи",
        PermAction.CreateField         => "Создавать свойства",
        PermAction.EditField           => "Изменять свойства",
        PermAction.DeleteField         => "Удалять свойства",
        _                              => a.ToString(),
    };

    /// <summary>Текст отказа для тоста/диалога: «Недостаточно прав: …».</summary>
    public static string DeniedMessage(PermAction a) => a switch
    {
        PermAction.ViewCollection      => "просмотр коллекции запрещён администратором базы данных",
        PermAction.EditCollection      => "изменение коллекции запрещено администратором базы данных",
        PermAction.DeleteCollection    => "удаление коллекции запрещено администратором базы данных",
        PermAction.DuplicateCollection => "дублирование коллекции запрещено администратором базы данных",
        PermAction.CreateCollection    => "создание коллекций запрещено администратором базы данных",
        PermAction.CreateRecord        => "создание записей запрещено администратором базы данных",
        PermAction.EditRecord          => "изменение записей запрещено администратором базы данных",
        PermAction.DeleteRecord        => "удаление записей запрещено администратором базы данных",
        PermAction.DuplicateRecord     => "дублирование записей запрещено администратором базы данных",
        PermAction.CreateField         => "создание свойств запрещено администратором базы данных",
        PermAction.EditField           => "изменение свойств запрещено администратором базы данных",
        PermAction.DeleteField         => "удаление свойств запрещено администратором базы данных",
        _                              => "действие запрещено администратором базы данных",
    };
}

/// <summary>
/// Действие запрещено правами пользователя. Наследует InvalidOperationException —
/// существующие обработчики (баннеры диалогов) показывают Message без доработки.
/// UI обычно прячет запрещённые кнопки заранее; исключение — страховка на гонку
/// «право отозвали, пока меню было открыто» и на прямые вызовы репозитория.
/// </summary>
public sealed class PermissionDeniedException(PermAction action)
    : InvalidOperationException($"Недостаточно прав: {PermActionLabels.DeniedMessage(action)}.")
{
    public PermAction Action { get; } = action;
}
