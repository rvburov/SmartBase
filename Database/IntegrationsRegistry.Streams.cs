using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SmartBase.Database.Entities;
using SmartBase.Integrations;

namespace SmartBase.Database;

/// <summary>
/// Потоки событий в системной app.db — та же кладовая, что у соединений, запросов и сценариев, и
/// по той же причине: поток слушает одна машина, та, где включен сценарий
/// (docs/ROADMAP-API.md, раздел 3).
///
/// Целостность держит реестр, а не внешний ключ (таблицы заводятся сырым DDL): поток без
/// существующего соединения не сохраняется, а поток, события которого заводят сценарии, не
/// удаляется — молча оставить сценарий без того, что его заводит, хуже, чем отказать.
/// </summary>
public sealed partial class IntegrationsRegistry
{
    /// <summary>Все потоки по имени.</summary>
    public IReadOnlyList<IntegrationStream> Streams() =>
        [.. db.IntegrationStreams.AsNoTracking()
             .OrderBy(s => s.Name)
             .AsEnumerable()
             .Select(ToModel)];

    /// <summary>Поток по ключу; <c>null</c> — такого нет.</summary>
    public IntegrationStream? FindStream(int id)
    {
        var entity = db.IntegrationStreams.AsNoTracking().FirstOrDefault(s => s.Id == id);
        return entity is null ? null : ToModel(entity);
    }

    /// <summary>Потоки одного соединения — для карточки соединения и при его удалении.</summary>
    public IReadOnlyList<IntegrationStream> StreamsOf(int connectionId) =>
        [.. db.IntegrationStreams.AsNoTracking()
             .Where(s => s.ConnectionId == connectionId)
             .OrderBy(s => s.Name)
             .AsEnumerable()
             .Select(ToModel)];

    /// <summary>Создать или обновить поток. Возвращает сохраненный — с ключом, если он новый.</summary>
    public IntegrationStream SaveStream(IntegrationStream stream)
    {
        var problems = stream.Problems();
        if (problems.Count > 0)
            throw new InvalidOperationException("Поток не сохранен: " + string.Join("; ", problems));

        if (!db.IntegrationConnections.Any(c => c.Id == stream.ConnectionId))
            throw new InvalidOperationException("Поток не сохранен: соединение не найдено");

        var entity = stream.Id > 0
            ? db.IntegrationStreams.FirstOrDefault(s => s.Id == stream.Id)
            : null;

        if (entity is null)
        {
            entity = new IntegrationStreamEntity();
            db.IntegrationStreams.Add(entity);
        }

        entity.Name         = stream.Name.Trim();
        entity.ConnectionId = stream.ConnectionId;
        entity.Path         = stream.Path.Trim();
        entity.Protocol     = ProtocolToStorage(stream.Protocol);
        entity.TopicsJson   = JsonSerializer.Serialize(stream.Subscriptions);
        entity.ItemsPath    = stream.ItemsPath.Trim();
        entity.KeyField     = stream.KeyField.Trim();

        db.SaveChanges();
        return ToModel(entity);
    }

    /// <summary>
    /// Удалить поток. Поток, события которого заводят сценарии, не удаляется — то же правило,
    /// что у запроса с шагами: решать, переключить сценарий или удалить его, должен человек.
    /// </summary>
    public void DeleteStream(int id)
    {
        var entity = db.IntegrationStreams.FirstOrDefault(s => s.Id == id);
        if (entity is null) return;

        var used = ScenariosOfStream(id);
        if (used.Count > 0)
            throw new InvalidOperationException(
                $"Поток не удален: его события заводят сценарии ({used.Count}). "
                + "Переключите сценарий на другой запуск или удалите его.");

        db.IntegrationStreams.Remove(entity);
        db.SaveChanges();
    }

    // ── Перевод строки в модель и обратно ─────────────────────────────────────

    private static IntegrationStream ToModel(IntegrationStreamEntity entity) =>
        new(entity.Id,
            entity.Name,
            entity.ConnectionId,
            entity.Path,
            ProtocolFromStorage(entity.Protocol),
            ParseTopics(entity.TopicsJson),
            entity.ItemsPath ?? "",
            entity.KeyField ?? "");

    private static string ProtocolToStorage(StreamProtocol protocol) => protocol switch
    {
        StreamProtocol.Stomp => "stomp",
        _                    => "plain",
    };

    /// <summary>
    /// Незнакомый способ подписки читается как простой поток. Строку мог записать более новый
    /// выпуск программы, и падать на открытии настроек из-за нее незачем; подписки при этом не
    /// будет, событий тоже — и человек увидит это на карточке, а не в отказе открыть экран.
    /// </summary>
    private static StreamProtocol ProtocolFromStorage(string? stored) => stored switch
    {
        "stomp" => StreamProtocol.Stomp,
        _       => StreamProtocol.Plain,
    };

    /// <summary>Темы из JSON; битая строка — пустой список, карточка остается открываемой.</summary>
    private static IReadOnlyList<string> ParseTopics(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];

        try
        {
            return JsonSerializer.Deserialize<List<string>>(json) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }
}
