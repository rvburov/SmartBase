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
    /// Когда запрос последний раз отработал успешно (ISO-8601). Отсюда берется значение
    /// подстановки <c>{{последний_запуск}}</c> — ради нее вся инкрементальная выгрузка и
    /// существует. Пусто — еще ни разу.
    /// </summary>
    public string? LastRunAt { get; set; }
}
