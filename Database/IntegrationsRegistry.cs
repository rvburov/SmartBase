using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SmartBase.Database.Entities;
using SmartBase.Integrations;
using SmartBase.Utils;

namespace SmartBase.Database;

/// <summary>
/// Хранилище соединений и запросов интеграций поверх системной app.db. Все локальное:
/// настройки обмена и секреты не попадают в файл базы данных, потому что он ездит по сетевой
/// папке и открыт на нескольких машинах разом (docs/ROADMAP-API.md, раздел 3).
///
/// Единственная точка, где секрет расшифровывается, — <see cref="Secret"/>. Все остальное
/// работает с <see cref="IntegrationConnection"/>, в котором секрета нет вовсе: соединения
/// показываются на экранах, экспортируются и попадают в отчеты, и секрету там делать нечего.
/// </summary>
public sealed partial class IntegrationsRegistry(SystemDbContext db)
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
    /// Секрета нет или хранилище ОС его потеряло — пустая строка (сторонняя система ответит
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

    /// <summary>
    /// Удалить соединение вместе с его секретом. Соединение, на которое ссылаются запросы, не
    /// удаляется: молча унести с собой чужие карточки хуже, чем отказать, а человек должен сам
    /// решить, переносить их на другое соединение или удалять.
    /// </summary>
    public void Delete(int id)
    {
        var entity = db.IntegrationConnections.FirstOrDefault(c => c.Id == id);
        if (entity is null) return;

        var used = db.IntegrationRequests.Count(r => r.ConnectionId == id);
        if (used > 0)
            throw new InvalidOperationException(
                $"Соединение не удалено: на него ссылаются запросы ({used}). "
                + "Удалите их или переключите на другое соединение.");

        var streams = db.IntegrationStreams.Count(s => s.ConnectionId == id);
        if (streams > 0)
            throw new InvalidOperationException(
                $"Соединение не удалено: через него открываются потоки событий ({streams}). "
                + "Удалите их или переключите на другое соединение.");

        if (!string.IsNullOrEmpty(entity.Secret))
            SecretProtector.Delete(entity.Secret, SecretProtector.IntegrationsService);

        db.IntegrationConnections.Remove(entity);
        db.SaveChanges();
    }

    // ── Запросы ───────────────────────────────────────────────────────────────

    /// <summary>Все запросы по имени.</summary>
    public IReadOnlyList<IntegrationRequest> Requests() =>
        [.. db.IntegrationRequests.AsNoTracking()
             .OrderBy(r => r.Name)
             .AsEnumerable()
             .Select(ToModel)];

    /// <summary>Запросы одного соединения — для карточки соединения и при его удалении.</summary>
    public IReadOnlyList<IntegrationRequest> RequestsOf(int connectionId) =>
        [.. db.IntegrationRequests.AsNoTracking()
             .Where(r => r.ConnectionId == connectionId)
             .OrderBy(r => r.Name)
             .AsEnumerable()
             .Select(ToModel)];

    /// <summary>Запрос по ключу; <c>null</c> — такого нет.</summary>
    public IntegrationRequest? FindRequest(int id)
    {
        var entity = db.IntegrationRequests.AsNoTracking().FirstOrDefault(r => r.Id == id);
        return entity is null ? null : ToModel(entity);
    }

    /// <summary>
    /// Создать или обновить запрос. Соединение должно существовать: запрос без него — это
    /// карточка, которую невозможно ни отправить, ни показать.
    /// </summary>
    public IntegrationRequest SaveRequest(IntegrationRequest request)
    {
        var problems = request.Problems();
        if (problems.Count > 0)
            throw new InvalidOperationException("Запрос не сохранен: " + string.Join("; ", problems));

        if (!db.IntegrationConnections.Any(c => c.Id == request.ConnectionId))
            throw new InvalidOperationException("Запрос не сохранен: соединение не найдено");

        var entity = request.Id > 0
            ? db.IntegrationRequests.FirstOrDefault(r => r.Id == request.Id)
            : null;

        if (entity is null)
        {
            entity = new IntegrationRequestEntity();
            db.IntegrationRequests.Add(entity);
        }

        entity.Name         = request.Name.Trim();
        entity.ConnectionId = request.ConnectionId;
        entity.Method       = MethodToStorage(request.Method);
        entity.Path         = request.Path.Trim();
        entity.HeadersJson  = JsonSerializer.Serialize(request.Headers);
        entity.Body         = request.Body;
        entity.Paging       = PagingToStorage(request.Paging);
        entity.PagingField  = request.PagingField.Trim();
        entity.ItemsPath    = request.ItemsPath.Trim();

        db.SaveChanges();
        return ToModel(entity);
    }

    // ── Привязка к коллекции и отметка о запуске ──────────────────────────────

    /// <summary>
    /// Привязка «ответ → коллекция»; <c>null</c> — запрос еще ни разу не укладывали. Битая
    /// строка тоже дает <c>null</c>: человек соберет привязку заново, а падать при открытии
    /// экрана из-за настройки одного запроса незачем.
    /// </summary>
    public CollectionBinding? Binding(int requestId)
    {
        var json = db.IntegrationRequests.AsNoTracking()
                     .FirstOrDefault(r => r.Id == requestId)?.BindingJson;

        if (string.IsNullOrWhiteSpace(json)) return null;

        try
        {
            return JsonSerializer.Deserialize<CollectionBinding>(json);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Запомнить привязку; <c>null</c> — забыть ее.</summary>
    public void SaveBinding(int requestId, CollectionBinding? binding)
    {
        var entity = db.IntegrationRequests.FirstOrDefault(r => r.Id == requestId);
        if (entity is null) return;

        entity.BindingJson = binding is null ? null : JsonSerializer.Serialize(binding);
        db.SaveChanges();
    }

    /// <summary>
    /// Исходящая привязка «коллекция → JSON»; <c>null</c> — запрос ничего из коллекции не
    /// уносит. Битая строка тоже дает <c>null</c>, по той же причине, что и у входящей.
    /// </summary>
    public OutgoingBinding? Outgoing(int requestId)
    {
        var json = db.IntegrationRequests.AsNoTracking()
                     .FirstOrDefault(r => r.Id == requestId)?.OutgoingJson;

        if (string.IsNullOrWhiteSpace(json)) return null;

        try
        {
            return JsonSerializer.Deserialize<OutgoingBinding>(json);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Запомнить исходящую привязку; <c>null</c> — забыть ее.
    ///
    /// Подтверждение направления отдельно сбрасывать не надо: состав полей входит в отпечаток
    /// запроса (<see cref="OutgoingBinding.Mark"/>), и дописанное поле роняет подтверждение тем
    /// же способом, что и правка тела, — «запрос правили после подтверждения».
    /// </summary>
    public void SaveOutgoing(int requestId, OutgoingBinding? binding)
    {
        var entity = db.IntegrationRequests.FirstOrDefault(r => r.Id == requestId);
        if (entity is null) return;

        entity.OutgoingJson = binding is null ? null : JsonSerializer.Serialize(binding);
        db.SaveChanges();
    }

    /// <summary>
    /// Когда запрос последний раз отработал успешно; <c>null</c> — еще ни разу. Отсюда берется
    /// значение подстановки <c>{{последний_запуск}}</c>.
    /// </summary>
    public DateTimeOffset? LastRun(int requestId)
    {
        var stored = db.IntegrationRequests.AsNoTracking()
                       .FirstOrDefault(r => r.Id == requestId)?.LastRunAt;

        return DateTimeOffset.TryParse(stored, CultureInfo.InvariantCulture,
                                       DateTimeStyles.RoundtripKind, out var moment)
            ? moment
            : null;
    }

    /// <summary>
    /// Отметить удачный запуск. Зовется ТОЛЬКО после того, как записи легли в коллекцию:
    /// отметь его раньше — и неудавшийся обмен сдвинул бы границу «отдай изменения с прошлого
    /// раза», а пропущенные записи не пришли бы уже никогда.
    /// </summary>
    public void MarkRun(int requestId, DateTimeOffset moment)
    {
        var entity = db.IntegrationRequests.FirstOrDefault(r => r.Id == requestId);
        if (entity is null) return;

        entity.LastRunAt = moment.ToString("o", CultureInfo.InvariantCulture);
        db.SaveChanges();
    }

    /// <summary>
    /// Удалить запрос. Соединение и его секрет остаются: ими пользуются другие запросы.
    ///
    /// Запрос, на который ссылается шаг сценария, не удаляется — то же правило, что у
    /// соединения с запросами: молча оставить сценарий с дырой посреди списка шагов хуже, чем
    /// отказать, а решать, переключить шаг или удалить сценарий, должен человек.
    /// </summary>
    public void DeleteRequest(int id)
    {
        var entity = db.IntegrationRequests.FirstOrDefault(r => r.Id == id);
        if (entity is null) return;

        var used = ScenariosUsing(id);
        if (used.Count > 0)
            throw new InvalidOperationException(
                $"Запрос не удален: его выполняют сценарии ({used.Count}). "
                + "Уберите шаг из сценария или удалите сценарий.");

        db.IntegrationRequests.Remove(entity);
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
    private static IntegrationRequest ToModel(IntegrationRequestEntity entity) =>
        new(entity.Id,
            entity.Name,
            entity.ConnectionId,
            MethodFromStorage(entity.Method),
            entity.Path,
            ParseHeaders(entity.HeadersJson),
            entity.Body ?? "",
            PagingFromStorage(entity.Paging),
            entity.PagingField ?? "",
            entity.ItemsPath ?? "");

    private static string MethodToStorage(RequestMethod method) => method switch
    {
        RequestMethod.Post   => "post",
        RequestMethod.Put    => "put",
        RequestMethod.Patch  => "patch",
        RequestMethod.Delete => "delete",
        _                    => "get",
    };

    /// <summary>
    /// Незнакомый метод читается как GET — самый безобидный из возможных. Строку мог записать
    /// более новый выпуск программы, и превращать это в отказ открыть настройки незачем.
    /// </summary>
    private static RequestMethod MethodFromStorage(string? stored) => stored switch
    {
        "post"   => RequestMethod.Post,
        "put"    => RequestMethod.Put,
        "patch"  => RequestMethod.Patch,
        "delete" => RequestMethod.Delete,
        _        => RequestMethod.Get,
    };

    private static string PagingToStorage(RequestPaging paging) => paging switch
    {
        RequestPaging.Page   => "page",
        RequestPaging.Offset => "offset",
        RequestPaging.Cursor => "cursor",
        _                    => "none",
    };

    /// <summary>Незнакомая разбивка читается как «все одним ответом» — без домыслов.</summary>
    private static RequestPaging PagingFromStorage(string? stored) => stored switch
    {
        "page"   => RequestPaging.Page,
        "offset" => RequestPaging.Offset,
        "cursor" => RequestPaging.Cursor,
        _        => RequestPaging.None,
    };

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
