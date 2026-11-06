using System.Globalization;
using System.Text.Json;

namespace SmartBase.Extensions.DocRegistry;

public sealed record BotUser(string Id, string FullName, string Division = "", string Post = "",
    string Email = "", bool Complete = true);

/// <summary>Настройки запросов и кеш имен для интерфейса. Кеш не удостоверяет отправителя.</summary>
public sealed record BotDirectory(int ListRequestId = 0, int ProfileRequestId = 0, BotUser[]? Users = null)
{
    [System.Text.Json.Serialization.JsonIgnore]
    public IReadOnlyList<BotUser> People => Users ?? [];
}

public static class BotUsers
{
    public const string DirectoryKey = "bot-directory";
    public const string Denied = "У вас нет доступа к боту регистрации КД. Обратитесь к администратору.";

    public static BotDirectory Directory(string? json)
    {
        try { return JsonSerializer.Deserialize<BotDirectory>(json ?? "") ?? new(); }
        catch (JsonException) { return new(); }
    }

    public static string Save(BotDirectory directory) => JsonSerializer.Serialize(directory);
    public static bool Allowed(string? list, string sender) => MessageRegistration.Senders(list).Contains(sender);

    /// <summary>Карточки API: порядок фамилия, имя, отчество; псевдоним не используется.</summary>
    public static IReadOnlyList<BotUser> Read(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var rows = root.ValueKind == JsonValueKind.Array ? root.EnumerateArray().ToArray() : [root];
        var users = new List<BotUser>();
        foreach (var row in rows)
        {
            if (row.ValueKind != JsonValueKind.Object) throw new InvalidOperationException("API вернул неверную карточку пользователя.");
            string Value(string name) => row.TryGetProperty(name, out var value)
                && value.ValueKind is JsonValueKind.String or JsonValueKind.Number ? value.ToString().Trim() : "";
            if (!long.TryParse(Value("userId"), NumberStyles.None, CultureInfo.InvariantCulture, out var id) || id <= 0)
                throw new InvalidOperationException("В карточке API отсутствует идентификатор пользователя.");
            var parts = new[] { Value("family"), Value("fname"), Value("sname") };
            var fullName = MessageRegistration.Collapse(string.Join(" ", parts));
            users.Add(new(id.ToString(CultureInfo.InvariantCulture), fullName, Value("division"),
                Value("post"), Value("email"), parts.All(p => p.Length > 0) && RegistryImport.IsFullName(fullName)));
        }
        if (users.Select(u => u.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count() != users.Count)
            throw new InvalidOperationException("API вернул несколько карточек с одним идентификатором.");
        return users.OrderBy(u => u.FullName, StringComparer.CurrentCultureIgnoreCase).ToArray();
    }

    public static IReadOnlyList<BroadcastRecipient> Choices(BotDirectory directory, string? selected = null)
    {
        var rows = directory.People.Where(user => user.Complete).Select(user =>
        {
            var name = user.FullName.Length > 0 ? user.FullName : "ФИО не заполнено";
            var details = string.Join(" · ", new[] { user.Division, user.Post }.Where(p => p.Length > 0));
            var label = details.Length > 0 ? name + " · " + details : name;
            if (!user.Complete) label += " · неполное ФИО";
            return (User: user, Label: label);
        }).ToArray();
        var duplicates = rows.GroupBy(r => r.Label, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1).Select(g => g.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var result = rows.Select(row => new BroadcastRecipient(row.User.Id,
            duplicates.Contains(row.Label)
                ? row.Label + " · " + (row.User.Email.Length > 0 ? row.User.Email + " · " : "") + "учетная запись №" + row.User.Id
                : row.Label)).ToList();
        // Уже выбранные не исчезают при ошибке API или неполном списке контактов.
        var known = result.Select(r => r.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in MessageRegistration.SenderEntries(selected))
            if (known.Add(entry.Id))
                result.Add(new(entry.Id, "ФИО недоступно · учетная запись №" + entry.Id));
        return result;
    }

    public static IReadOnlyList<BroadcastRecipient> Recipients(string? selected, string? directoryJson)
    {
        var ids = MessageRegistration.Senders(selected);
        return Choices(Directory(directoryJson), selected).Where(p => ids.Contains(p.Id)).ToArray();
    }

    public static async Task<ActionPreparation> PrepareAsync(ActionInput input,
        IReadOnlyDictionary<string, string> settings, IExtensionRequests requests, CancellationToken ct)
    {
        ActionPreparation Refused(string message) => new(input, ReplyMarkup.Refused(message));
        var sender = input.Value(RegisterMessageAction.SenderField);
        if (!Allowed(settings.GetValueOrDefault(DocRegistryExtension.SendersKey), sender)) return Refused(Denied);
        if (!long.TryParse(sender, NumberStyles.None, CultureInfo.InvariantCulture, out var id) || id <= 0)
            return Refused("Не удалось определить пользователя по идентификатору отправителя.");
        var directory = Directory(settings.GetValueOrDefault(DirectoryKey));
        if (directory.ProfileRequestId <= 0) return Refused("Не настроен запрос данных отправителя. Обратитесь к администратору.");
        if (directory.ListRequestId > 0)
        {
            var list = requests.Requests.FirstOrDefault(r => r.Id == directory.ListRequestId);
            var profile = requests.Requests.FirstOrDefault(r => r.Id == directory.ProfileRequestId);
            if (list is null || profile is null || list.ConnectionId != profile.ConnectionId)
                return Refused("Запросы списка и карточки пользователей должны использовать одно соединение бота.");
        }
        try
        {
            var read = await requests.ReadAsync(directory.ProfileRequestId,
                new Dictionary<string, string> { ["sender"] = id.ToString(CultureInfo.InvariantCulture) }, ct);
            if (!read.Ok) return Refused("Не удалось получить ФИО отправителя из API: " + read.Problem);
            var user = Read(read.Json).SingleOrDefault(u => u.Id == id.ToString(CultureInfo.InvariantCulture));
            if (user is null) return Refused("API не вернул карточку отправителя сообщения.");
            if (!user.Complete) return Refused("В карточке отправителя API не заполнено полное ФИО. Заполните фамилию, имя и отчество в профиле.");
            return new(input with { Identity = new(sender, user.FullName) });
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception error) when (error is JsonException or InvalidOperationException)
        { return Refused("Не удалось прочитать ФИО отправителя из API: " + error.Message); }
    }
}
