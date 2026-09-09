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
