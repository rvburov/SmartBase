using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SmartBase.Database.Entities;
using SmartBase.Integrations;
using SmartBase.Utils;

namespace SmartBase.Database;

/// <summary>
/// Хранилище соединений интеграций поверх системной app.db. Все локальное: соединения и их
/// секреты не попадают в файл базы данных, потому что он ездит по сетевой папке и открыт на
/// нескольких машинах разом (docs/ROADMAP-API.md, раздел 3).
///
/// Единственная точка, где секрет расшифровывается, — <see cref="Secret"/>. Все остальное
/// работает с <see cref="IntegrationConnection"/>, в котором секрета нет вовсе: соединения
/// показываются на экранах, экспортируются и попадают в отчеты, и секрету там делать нечего.
/// </summary>
public sealed class IntegrationsRegistry(SystemDbContext db)
{
    /// <summary>Все соединения по имени. Секретов здесь нет — только их наличие.</summary>
    public IReadOnlyList<IntegrationConnection> Connections() =>
        [.. db.IntegrationConnections.AsNoTracking()
             .OrderBy(c => c.Name)
             .AsEnumerable()
             .Select(ToModel)];

    /// <summary>Соединение по ключу; <c>null</c> — такого нет.</summary>
    public IntegrationConnection? Find(int id)
    {
        var entity = db.IntegrationConnections.AsNoTracking().FirstOrDefault(c => c.Id == id);
        return entity is null ? null : ToModel(entity);
    }

    /// <summary>Есть ли у соединения сохраненный секрет.</summary>
    public bool HasSecret(int id) =>
        !string.IsNullOrEmpty(db.IntegrationConnections.AsNoTracking()
                                .FirstOrDefault(c => c.Id == id)?.Secret);

    /// <summary>
    /// Секрет соединения в открытом виде. Зовется в момент запроса и только оттуда: держать
    /// расшифрованный секрет в модели соединения значило бы носить его по всем экранам.
    /// Секрета нет или хранилище ОС его потеряло — пустая строка (чужая система ответит
    /// отказом, и это честнее, чем притвориться, что все хорошо).
    /// </summary>
    public string Secret(int id)
    {
        var stored = db.IntegrationConnections.AsNoTracking().FirstOrDefault(c => c.Id == id)?.Secret;
        return string.IsNullOrEmpty(stored)
            ? ""
            : SecretProtector.Unprotect(stored, SecretProtector.IntegrationsService);
    }

    /// <summary>
    /// Создать или обновить соединение. Возвращает сохраненное — с ключом, если оно новое.
    ///
    /// <paramref name="secret"/>: <c>null</c> — секрет не трогаем (человек правит адрес, а не
    /// токен, и заново вводить его не должен); пустая строка — секрет убираем; иначе —
    /// заменяем. Прежний секрет при замене удаляется из хранилища ОС, иначе Связка ключей
    /// копит мусор, который уже никто не прочитает.
    /// </summary>
    public IntegrationConnection Save(IntegrationConnection connection, string? secret = null)
    {
        var problems = connection.Problems();
        if (problems.Count > 0)
            throw new InvalidOperationException("Соединение не сохранено: " + string.Join("; ", problems));

        var entity = connection.Id > 0
            ? db.IntegrationConnections.FirstOrDefault(c => c.Id == connection.Id)
            : null;

        if (entity is null)
        {
            entity = new IntegrationConnectionEntity();
            db.IntegrationConnections.Add(entity);
        }

        entity.Name           = connection.Name.Trim();
        entity.Address        = connection.Address.Trim();
        entity.Auth           = AuthToStorage(connection.Auth);
        entity.AuthName       = connection.AuthName.Trim();
        entity.HeadersJson    = JsonSerializer.Serialize(connection.Headers);
        entity.TimeoutSeconds = connection.TimeoutSeconds;
        entity.Retries        = connection.Retries;

        if (secret is not null)
        {
            if (!string.IsNullOrEmpty(entity.Secret))
                SecretProtector.Delete(entity.Secret, SecretProtector.IntegrationsService);

            entity.Secret = secret.Length == 0
                ? null
                : SecretProtector.Protect(secret, SecretProtector.IntegrationsService);
        }

        db.SaveChanges();
        return ToModel(entity);
    }

    /// <summary>Удалить соединение вместе с его секретом.</summary>
    public void Delete(int id)
    {
        var entity = db.IntegrationConnections.FirstOrDefault(c => c.Id == id);
        if (entity is null) return;

        if (!string.IsNullOrEmpty(entity.Secret))
            SecretProtector.Delete(entity.Secret, SecretProtector.IntegrationsService);

        db.IntegrationConnections.Remove(entity);
        db.SaveChanges();
    }

    // ── Перевод строки в модель и обратно ─────────────────────────────────────

    private static IntegrationConnection ToModel(IntegrationConnectionEntity entity) =>
        new(entity.Id,
            entity.Name,
            entity.Address,
            AuthFromStorage(entity.Auth),
            entity.AuthName ?? "",
            ParseHeaders(entity.HeadersJson),
            entity.TimeoutSeconds,
            entity.Retries);

    private static string AuthToStorage(ConnectionAuth auth) => auth switch
    {
        ConnectionAuth.Header => "header",
        ConnectionAuth.Bearer => "bearer",
        ConnectionAuth.Basic  => "basic",
        ConnectionAuth.Query  => "query",
        _                     => "none",
    };

    /// <summary>
    /// Незнакомый способ авторизации читается как «без авторизации», а не как ошибка: строку
    /// мог записать более новый выпуск программы. Секрет при этом никуда не уйдет — запрос
    /// просто отправится без него и получит отказ, что и правильно.
    /// </summary>
    private static ConnectionAuth AuthFromStorage(string? stored) => stored switch
    {
        "header" => ConnectionAuth.Header,
        "bearer" => ConnectionAuth.Bearer,
        "basic"  => ConnectionAuth.Basic,
        "query"  => ConnectionAuth.Query,
        _        => ConnectionAuth.None,
    };

    /// <summary>Заголовки из JSON; битая строка — пустой список, соединение остается рабочим.</summary>
    private static IReadOnlyList<IntegrationHeader> ParseHeaders(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];

        try
        {
            return JsonSerializer.Deserialize<List<IntegrationHeader>>(json) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }
}
