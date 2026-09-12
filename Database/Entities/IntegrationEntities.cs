using System.ComponentModel.DataAnnotations;

namespace SmartBase.Database.Entities;

/// <summary>
/// Соединение с чужой программой (системная БД app.db, таблица «integration_connections»).
///
/// Лежит именно в app.db, а не в файле базы данных, и это решение, а не случайность: файл
/// базы ездит по сетевой папке и открыт на нескольких машинах разом. Секрет в нем оказался бы
/// доступен всем, у кого есть доступ к папке, а сценарий, привязанный к соединению, сработал
/// бы на каждой открытой копии программы (docs/ROADMAP-API.md, раздел 3).
///
/// <see cref="Secret"/> хранится защищенным — <see cref="SmartBase.Utils.SecretProtector"/>
/// (Windows: DPAPI ключом пользователя; macOS: Связка ключей; Linux и сбой хранилища:
/// открытый текст, как у паролей PostgreSQL). Расшифровывается в одном месте —
/// <c>IntegrationsRegistry</c>, и наружу отдается только в момент запроса.
///
/// Таблица заводится аддитивно (<c>DatabaseRegistry.EnsureIntegrationTables</c>): app.db
/// никогда не пересоздается, чтобы не терять реестр баз и настройки.
/// </summary>
public class IntegrationConnectionEntity
{
    [Key] public int Id { get; set; }

    /// <summary>Имя для человека.</summary>
    [Required] public string Name { get; set; } = "";

    /// <summary>Базовый адрес: <c>https://сервер/путь</c>.</summary>
    [Required] public string Address { get; set; } = "";

    /// <summary>Способ авторизации ключом: none, header, bearer, basic, query.</summary>
    [Required] public string Auth { get; set; } = "none";

    /// <summary>Имя заголовка или параметра; у basic — имя пользователя.</summary>
    public string? AuthName { get; set; }

    /// <summary>Секрет в защищенном виде. Пусто — авторизации нет.</summary>
    public string? Secret { get; set; }

    /// <summary>Постоянные заголовки соединения — JSON-массив пар имя-значение.</summary>
    public string? HeadersJson { get; set; }

    /// <summary>Сколько ждать ответа.</summary>
    public int TimeoutSeconds { get; set; }

    /// <summary>Сколько раз повторить при обрыве связи.</summary>
    public int Retries { get; set; }
}

/// <summary>
/// Запрос к чужой программе (системная БД app.db, таблица «integration_requests»).
///
/// Живет рядом с соединением и по тем же причинам — локально: запрос без соединения
/// бессмыслен, а соединение хранится в app.db, потому что содержит секрет.
///
/// Секрета здесь нет вовсе: он принадлежит соединению. Заголовки запроса лежат открытым
/// текстом, и это правильно — при разборе строки cURL заголовок авторизации из них
/// выносится в секрет соединения (<c>CurlParser</c>).
///
/// Таблица заводится аддитивно (<c>DatabaseRegistry.EnsureIntegrationTables</c>).
/// </summary>
public class IntegrationRequestEntity
{
    [Key] public int Id { get; set; }

    /// <summary>Имя для человека: «Заказы за сутки».</summary>
    [Required] public string Name { get; set; } = "";

    /// <summary>Соединение, через которое уходит запрос.</summary>
    public int ConnectionId { get; set; }

    /// <summary>Метод: get, post, put, patch, delete.</summary>
    [Required] public string Method { get; set; } = "get";

    /// <summary>Путь с параметрами относительно адреса соединения.</summary>
    [Required] public string Path { get; set; } = "/";

    /// <summary>Заголовки запроса — JSON-массив пар имя-значение.</summary>
    public string? HeadersJson { get; set; }

    /// <summary>Тело запроса.</summary>
    public string? Body { get; set; }

    /// <summary>Способ разбивки на страницы: none, page, offset, cursor.</summary>
    [Required] public string Paging { get; set; } = "none";

    /// <summary>Имя параметра страницы или поля ответа с меткой следующей порции.</summary>
    public string? PagingField { get; set; }

    /// <summary>Путь к массиву записей в ответе: <c>data.items</c>.</summary>
    public string? ItemsPath { get; set; }

    /// <summary>
    /// Привязка «ответ → коллекция» одной строкой JSON: колонки, ключ сопоставления, что делать
    /// с найденным. Пусто — запрос еще ни разу не укладывали в коллекцию.
    ///
    /// Строкой, а не своими колонками, намеренно: состав привязки — это список переменной
    /// длины, и раскладывать его по таблицам ради настройки, которую читает один экран, значило
    /// бы завести две сущности вместо одной.
    /// </summary>
    public string? BindingJson { get; set; }

    /// <summary>
    /// Исходящая привязка «коллекция → JSON» одной строкой: какие поля и под какими именами
    /// уходят наружу, как обернуто тело. Пусто — запрос ничего из коллекции не уносит, и тело
    /// берется из карточки как напечатано.
    ///
    /// Отдельной колонкой, а не внутри <see cref="BindingJson"/>: привязки разнонаправленные и
    /// живут по отдельности. Один и тот же запрос может и уносить записи, и укладывать ответ.
    /// </summary>
    public string? OutgoingJson { get; set; }

    /// <summary>
    /// Когда запрос последний раз отработал успешно (ISO-8601). Отсюда берется значение
    /// подстановки <c>{{последний_запуск}}</c> — ради нее вся инкрементальная выгрузка и
    /// существует. Пусто — еще ни разу.
    /// </summary>
    public string? LastRunAt { get; set; }
}

/// <summary>
/// Сценарий — «когда и что» (системная БД app.db, таблица «integration_scenarios»).
///
/// Лежит в app.db, а не в файле базы данных, и это не мелочь: файл базы ездит по сетевой папке
/// и открыт на нескольких машинах разом. Сценарий, лежащий в нем, сработал бы на каждой открытой
/// копии программы — и один обмен по расписанию превратился бы в пять одновременных
/// (docs/ROADMAP-API.md, раздел 3).
///
/// Таблица заводится аддитивно (<c>DatabaseRegistry.EnsureIntegrationTables</c>).
/// </summary>
public class IntegrationScenarioEntity
{
    [Key] public int Id { get; set; }

    /// <summary>Имя для человека: «Заказы из учетной системы».</summary>
    [Required] public string Name { get; set; } = "";

    /// <summary>Ходит ли сценарий сам. Выключенный запускается только кнопкой.</summary>
    public bool Enabled { get; set; }

    /// <summary>Триггер одной строкой: <c>manual</c>, <c>every 30</c>, <c>daily 07:00</c>.</summary>
    [Required] public string Trigger { get; set; } = "manual";

    /// <summary>
    /// Шаги одной строкой JSON — как привязка у запроса и по той же причине: список переменной
    /// длины, который читает один экран, не стоит второй таблицы со своими ключами.
    /// </summary>
    public string? StepsJson { get; set; }

    /// <summary>
    /// Когда сценарий отработал в прошлый раз (ISO-8601). Отсюда считается следующий запуск, и
    /// сюда же служба ставит якорь при включении: пусто означает «еще ни разу», и по расписанию
    /// такой сценарий не сработает, пока якорь не проставлен, — иначе включенный вечером
    /// «ежедневно в 07:00» пошел бы немедленно.
    /// </summary>
    public string? LastRunAt { get; set; }
}

/// <summary>
/// Подтвержденное исходящее направление (<see cref="SmartBase.Integrations.OutgoingDirection"/>).
///
/// Ключ — сам запрос: направление у него одно, и отдельный счетчик строк тут только повод
/// завести два подтверждения на один обмен.
/// </summary>
public class IntegrationDirectionEntity
{
    /// <summary>Запрос, которому разрешено отправлять; он же ключ строки.</summary>
    [Key] public int RequestId { get; set; }

    /// <summary>Отпечаток запроса на момент подтверждения: правка сбрасывает разрешение.</summary>
    [Required] public string Fingerprint { get; set; } = "";

    /// <summary>Кто подтвердил.</summary>
    [Required] public string Confirmed { get; set; } = "";

    /// <summary>Когда подтвердили (ISO-8601).</summary>
    [Required] public string At { get; set; } = "";

    /// <summary>До какого момента действует включительно (ISO-8601).</summary>
    [Required] public string Until { get; set; } = "";

    /// <summary>Когда направлением пользовались в последний раз (ISO-8601); пусто — ни разу.</summary>
    public string? UsedAt { get; set; }
}

/// <summary>
/// Токен доступа для входящего канала (<see cref="SmartBase.Integrations.AccessToken"/>).
///
/// Лежит в app.db, а не в файле базы, и причина та же, что у соединений: файл базы ездит по
/// сетевой папке. Хэш токена в нем был бы доступен всем, у кого есть доступ к папке, а токен
/// выдается на ОДНОЙ машине — той, которая слушает порт.
///
/// Сам секрет не хранится нигде: в <see cref="Hash"/> лежит проверочный PBKDF2, а текст токена
/// показывается один раз, при создании.
/// </summary>
public class IntegrationTokenEntity
{
    [Key] public int Id { get; set; }

    /// <summary>Имя для человека; оно же имя пользователя-робота в целевой базе.</summary>
    [Required] public string Name { get; set; } = "";

    /// <summary>База, в которую токену разрешено писать.</summary>
    public int DbId { get; set; }

    /// <summary>Проверочный хэш секрета.</summary>
    [Required] public string Hash { get; set; } = "";

    /// <summary>Ключ пользователя-робота в <c>db_users</c> целевой базы.</summary>
    [Required] public string RobotKey { get; set; } = "";

    /// <summary>Когда выдан (ISO-8601).</summary>
    [Required] public string CreatedAt { get; set; } = "";

    /// <summary>Когда токеном пользовались в последний раз (ISO-8601); пусто — ни разу.</summary>
    public string? UsedAt { get; set; }

    /// <summary>Выключенный токен не пускают; строку при этом не удаляют.</summary>
    public bool Enabled { get; set; } = true;
}

/// <summary>
/// Выдача чтения (<see cref="SmartBase.Integrations.ReadGrant"/>): токену разрешено читать одну
/// коллекцию, поименованные поля, до срока.
///
/// Лежит в app.db рядом с токеном и направлениями — и по той же причине: читает порт ОДНОЙ
/// машины, а подтверждение выдано на ней и этому человеку. Пароль сюда не попадает никогда:
/// его проверяет политика, здесь остается только след.
/// </summary>
public class IntegrationGrantEntity
{
    [Key] public int Id { get; set; }

    /// <summary>Токен, которому разрешено читать.</summary>
    public int TokenId { get; set; }

    /// <summary>Коллекция — ключом: имя может смениться, выдача остается.</summary>
    public int CollectionId { get; set; }

    /// <summary>Имя коллекции на момент выдачи — для экрана и паспорта.</summary>
    [Required] public string Collection { get; set; } = "";

    /// <summary>Ключи полей строкой: <c>t,12,15</c>. Источник истины о составе.</summary>
    [Required] public string FieldKeys { get; set; } = "";

    /// <summary>Имена полей на момент выдачи — для экрана и паспорта.</summary>
    [Required] public string Fields { get; set; } = "";

    /// <summary>Кто подтвердил.</summary>
    [Required] public string Confirmed { get; set; } = "";

    /// <summary>Когда подтверждена (ISO-8601).</summary>
    [Required] public string At { get; set; } = "";

    /// <summary>До какого момента действует включительно (ISO-8601).</summary>
    [Required] public string Until { get; set; } = "";

    /// <summary>Когда читали в последний раз (ISO-8601); пусто — ни разу.</summary>
    public string? UsedAt { get; set; }
}
