namespace SmartBase.Core.Models;

/// <summary>
/// Действия, на которые администратор базы выдает права (панель администратора модуля
/// «Профиль»). Битовая маска: у пользователя одна int-маска на область (вся база или
/// конкретная коллекция, db_permissions.ActionsMask) — один SELECT на пользователя,
/// дешево кешируется, новое действие = новый бит. CreateCollection имеет смысл только
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
    DuplicateField      = 1 << 12,

    /// <summary>Настройка обмена: соединения, запросы, сценарии, папка приема
    /// (docs/ROADMAP-API.md, 10.8). Право уровня базы.</summary>
    IntegrationsSetup   = 1 << 13,

    /// <summary>Включение исходящих направлений — тех запросов, которые уносят данные наружу
    /// (docs/ROADMAP-API.md, 10.8). Право уровня базы.</summary>
    IntegrationsOutward = 1 << 14,

    /// <summary>Полная маска — права допущенного пользователя по умолчанию
    /// (администратор снимает лишнее в панели).</summary>
    All = ViewCollection | EditCollection | DeleteCollection | DuplicateCollection |
          CreateCollection | CreateRecord | EditRecord | DeleteRecord | DuplicateRecord |
          CreateField | EditField | DeleteField | DuplicateField |
          IntegrationsSetup | IntegrationsOutward,
}

/// <summary>Русские подписи действий для панели администратора и сообщений об отказе.</summary>
public static class PermActionLabels
{
    /// <summary>
    /// Заголовок колонки в таблице пользователей: что именно делает галка — «Удаление
    /// записи», а не «Удаление». Название полное и по одному шаблону «действие + объект»,
    /// чтобы колонку можно было прочитать, не разглядывая иконку группы.
    /// </summary>
    public static string Title(PermAction a) => a switch
    {
        PermAction.ViewCollection      => "Просмотр коллекции",
        PermAction.EditCollection      => "Изменение коллекции",
        PermAction.DeleteCollection    => "Удаление коллекции",
        PermAction.DuplicateCollection => "Дублирование коллекции",
        PermAction.CreateCollection    => "Создание коллекции",
        PermAction.CreateRecord        => "Создание записи",
        PermAction.EditRecord          => "Изменение записи",
        PermAction.DeleteRecord        => "Удаление записи",
        PermAction.DuplicateRecord     => "Дублирование записи",
        PermAction.CreateField         => "Создание свойства",
        PermAction.EditField           => "Изменение свойства",
        PermAction.DeleteField         => "Удаление свойства",
        PermAction.DuplicateField      => "Дублирование свойства",
        PermAction.IntegrationsSetup   => "Интеграции: настройка",
        PermAction.IntegrationsOutward => "Интеграции: исходящие",
        _                              => a.ToString(),
    };

    /// <summary>
    /// Право выдается только на всю базу: переопределять его у отдельной коллекции нечем.
    /// У создания коллекции — потому что у несозданной коллекции переопределений нет; у
    /// интеграций — потому что обмен один на все базы, открытые на этой машине, и «настройка
    /// обмена в коллекции „Заказы“» не означала бы ничего.
    /// </summary>
    public static bool DatabaseOnly(PermAction a) =>
        a is PermAction.CreateCollection
          or PermAction.IntegrationsSetup
          or PermAction.IntegrationsOutward;

    /// <summary>
    /// Что право разрешает — текст подсказки над заголовком колонки. Перечисляет все
    /// операции, которые открывает галка, включая неочевидные (перенос спрашивает право
    /// удаления у источника и создания у цели), чтобы администратор не выяснял охват
    /// опытным путем. Тот же охват описан в
    /// docs/developer/architecture/users-permissions.md — при правке менять обе стороны.
    /// </summary>
    public static string Description(PermAction a) => a switch
    {
        PermAction.ViewCollection =>
            "Открывать коллекцию и видеть ее содержимое. Без права коллекция показана " +
            "в сайдбаре замком и не попадает в экспорт.",
        PermAction.EditCollection =>
            "Переименовывать коллекции и группы, менять иконку, цвет и настройки коллекции.",
        PermAction.DeleteCollection =>
            "Удалять коллекции и группы со всем содержимым; отдавать коллекцию " +
            "при переносе в другое место или базу данных.",
        PermAction.DuplicateCollection =>
            "Создавать копию коллекции или группы вместе со свойствами и записями.",
        PermAction.CreateCollection =>
            "Создавать коллекции и группы, импортировать файл как коллекцию, принимать " +
            "перенос из другой базы данных. Право уровня базы: для отдельной коллекции " +
            "не выдается.",
        PermAction.CreateRecord =>
            "Добавлять записи в таблице, календаре и задачах; принимать записи " +
            "при переносе из другой коллекции.",
        PermAction.EditRecord =>
            "Править значения ячеек, заголовок и описание записи, связи между записями, " +
            "менять порядок строк.",
        PermAction.DeleteRecord =>
            "Удалять записи по одной и пакетно; отдавать записи при переносе в другую " +
            "коллекцию.",
        PermAction.DuplicateRecord =>
            "Создавать копии записей, в том числе пакетно.",
        PermAction.CreateField =>
            "Добавлять в коллекцию новые свойства — вручную и при импорте файла.",
        PermAction.EditField =>
            "Переименовывать свойство, менять его тип и настройки, видимость дат " +
            "и роли исполнителя в задачах.",
        PermAction.DeleteField =>
            "Удалять свойства вместе со значениями во всех записях коллекции.",
        PermAction.DuplicateField =>
            "Создавать копию свойства вместе с его значениями во всех записях коллекции.",
        PermAction.IntegrationsSetup =>
            "Заводить и править соединения, запросы и сценарии обмена, настраивать папку " +
            "приема и чистить журнал запусков. Запускать настроенный обмен вручную можно " +
            "и без этого права. Право уровня базы: настройки обмена общие для всех баз, " +
            "открытых на этой машине.",
        PermAction.IntegrationsOutward =>
            "Подтверждать и отзывать исходящие направления — запросы, которые уносят данные " +
            "наружу. Само подтверждение сверх этого требует мастер-пароля политики " +
            "организации, и без политики его не выдать вовсе. Право уровня базы.",
        _ => "Действие в базе данных.",
    };

    /// <summary>Текст отказа для тоста/диалога: «Недостаточно прав: …».</summary>
    public static string DeniedMessage(PermAction a) => a switch
    {
        PermAction.ViewCollection      => "просмотр коллекции запрещен администратором базы данных",
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
        PermAction.DuplicateField      => "дублирование свойств запрещено администратором базы данных",
        PermAction.IntegrationsSetup   => "настройка интеграций запрещена администратором базы данных",
        PermAction.IntegrationsOutward =>
            "включение исходящих направлений запрещено администратором базы данных",
        _                              => "действие запрещено администратором базы данных",
    };
}

/// <summary>
/// Действие запрещено правами пользователя. Наследует InvalidOperationException —
/// существующие обработчики (баннеры диалогов) показывают Message без доработки.
/// UI обычно прячет запрещенные кнопки заранее; исключение — страховка на гонку
/// «право отозвали, пока меню было открыто» и на прямые вызовы репозитория.
/// </summary>
public sealed class PermissionDeniedException(PermAction action)
    : InvalidOperationException($"Недостаточно прав: {PermActionLabels.DeniedMessage(action)}.")
{
    public PermAction Action { get; } = action;
}
