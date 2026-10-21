using System.Globalization;
using Microsoft.EntityFrameworkCore;
using SmartBase.Core.Models;

namespace SmartBase.Database.Repositories;

/// <summary>Подключенная на этом компьютере база: номер реестра, постоянный номер и адрес.</summary>
public sealed record RelationBaseCandidate(int RegistryId, int LinkId, string Path);

/// <summary>Итог перевода старых межбазовых связей одной базы.</summary>
public sealed record RelationBaseMigrationResult(int Migrated, int Unresolved);

public partial class ConstructorRepository
{
    // Оставшиеся старые группы «поле:номер» после прошлого прохода; пусто — переводить нечего.
    // Нет ключа — база еще не проверялась: нужен полный просмотр ребер (один раз).
    internal const string RelationLegacyKey = "relation.base.legacy";

    /// <summary>
    /// Перевести старые межбазовые связи базы <paramref name="sourcePath"/> с номера реестра того
    /// компьютера, где их создали, на постоянный номер базы. Номер реестра однозначен только на
    /// своем компьютере, поэтому группа ребер одного свойства переводится, лишь когда на ЭТОМ
    /// компьютере под этим номером подключена база, где есть целевая коллекция, все связанные
    /// записи и свойства показа, поиска и автоподбора, и ни одна другая подключенная база этим
    /// признакам не отвечает. Иначе группа остается «база не определена» до следующего прохода
    /// или до выбора базы в окне свойства — ребра не удаляются и не переводятся наугад.
    /// </summary>
    public static RelationBaseMigrationResult MigrateLegacyRelationTargets(string sourcePath,
        IReadOnlyList<RelationBaseCandidate> candidates, CancellationToken ct = default)
    {
        using var source = new AppDbContext(sourcePath);
        var state = GetMeta(source, RelationLegacyKey);
        if (state == "") return new(0, 0);

        var fields = source.DbFields.AsNoTracking().Where(f => f.FieldType == "relation").ToList()
            .ToDictionary(f => f.Id, f => RelationConfig.FromConfig(f.ConfigJson));
        var groups = state is null ? ScanLegacyGroups(source, fields) : ParseLegacyGroups(state);
        var contexts = new Dictionary<string, AppDbContext>(StringComparer.Ordinal);
        try
        {
            var remaining = new List<(int Field, int Legacy)>();
            var migrated = 0;
            foreach (var (fieldId, legacy) in groups)
            {
                ct.ThrowIfCancellationRequested();
                fields.TryGetValue(fieldId, out var config);
                var targets = source.DbRecordLinks.AsNoTracking()
                    .Where(l => l.FieldId == fieldId && l.TargetDbId == legacy)
                    .Select(l => l.ToRecordId).Distinct().ToList();
                if (targets.Count == 0 && config?.TargetDbId != legacy) continue;   // группы уже нет
                var collection = config?.TargetDbId == legacy ? config.TargetCollectionId : (int?)null;
                var keys = collection is null ? [] : LegacyFieldKeys(config!);

                var passing = candidates.Where(c => IsLegacyTarget(Context(c.Path), collection, targets, keys)).ToList();
                var expected = candidates.FirstOrDefault(c => c.RegistryId == legacy);
                if (expected is null || passing.Count != 1 || passing[0] != expected || !RelationDbResolver.IsLinkId(expected.LinkId))
                {
                    remaining.Add((fieldId, legacy));
                    continue;
                }
                RebindLegacyTargets(source, fieldId, legacy, expected.LinkId, config?.TargetDbId == legacy ? config : null);
                migrated++;
                Serilog.Log.Information("Межбазовые связи свойства {FieldId}: номер реестра {Legacy} заменен постоянным номером базы",
                    fieldId, legacy);
            }
            SetMeta(source, RelationLegacyKey, string.Join(";", remaining.Select(g =>
                $"{g.Field.ToString(CultureInfo.InvariantCulture)}:{g.Legacy.ToString(CultureInfo.InvariantCulture)}")));
            return new(migrated, remaining.Count);
        }
        finally
        {
            foreach (var context in contexts.Values) context.Dispose();
        }

        AppDbContext Context(string path)
        {
            if (path == sourcePath) return source;
            if (!contexts.TryGetValue(path, out var context)) contexts[path] = context = new AppDbContext(path);
            return context;
        }
    }

    /// <summary>Старый номер в настройке или ребрах свойства: связь создана до постоянных
    /// номеров баз и на этом компьютере однозначно не определена.</summary>
    public static bool IsLegacyRelationDb(int dbId) => dbId > 0 && !RelationDbResolver.IsLinkId(dbId);

    /// <summary>Подпись цели, базу которой прочитать не удалось.</summary>
    internal static string UnavailableLabel(int dbId) =>
        IsLegacyRelationDb(dbId) ? RelationProperty.UnknownBase : RelationProperty.Unavailable;

    private static List<(int Field, int Legacy)> ScanLegacyGroups(AppDbContext source, Dictionary<int, RelationConfig?> fields)
    {
        var groups = source.DbRecordLinks.AsNoTracking()
            .Where(l => l.TargetDbId > 0 && l.TargetDbId < RelationDbResolver.MinLinkId)
            .Select(l => new { l.FieldId, l.TargetDbId }).Distinct().ToList()
            .Select(g => (g.FieldId, g.TargetDbId)).ToList();
        foreach (var (fieldId, config) in fields)
            if (config is not null && IsLegacyRelationDb(config.TargetDbId) && !groups.Contains((fieldId, config.TargetDbId)))
                groups.Add((fieldId, config.TargetDbId));
        return groups.OrderBy(g => g.Item1).ThenBy(g => g.Item2).ToList();
    }

    private static List<(int Field, int Legacy)> ParseLegacyGroups(string state)
    {
        var groups = new List<(int, int)>();
        foreach (var pair in state.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = pair.Split(':');
            if (parts.Length == 2
                && int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var field)
                && int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var legacy)
                && IsLegacyRelationDb(legacy))
                groups.Add((field, legacy));
        }
        return groups;
    }

    private static List<int> LegacyFieldKeys(RelationConfig config) =>
        new[] { config.DisplayField, config.SearchField, config.Auto?.TargetField }
            .Where(k => k is not null && k != RelationConfig.TitleField)
            .Select(k => int.Parse(k!, CultureInfo.InvariantCulture)).Distinct().ToList();

    // Признаки цели: коллекция, все связанные записи в ней и свойства из настройки. У старого
    // ребра, оставшегося от прежней цели свойства, коллекция неизвестна — проверяются записи.
    private static bool IsLegacyTarget(AppDbContext candidate, int? collection, IReadOnlyList<int> targets, List<int> keys)
    {
        if (collection is int col)
        {
            if (!candidate.DbCollections.AsNoTracking().Any(c => c.Id == col)) return false;
            if (keys.Count > 0 && candidate.DbFields.AsNoTracking().Count(f => keys.Contains(f.Id) && f.CollectionId == col) != keys.Count)
                return false;
        }
        else if (targets.Count == 0) return false;
        foreach (var chunk in targets.Chunk(500).Select(c => c.ToList()))
        {
            var found = collection is int c
                ? candidate.DbRecords.AsNoTracking().Count(r => chunk.Contains(r.Id) && r.CollectionId == c)
                : candidate.DbRecords.AsNoTracking().Count(r => chunk.Contains(r.Id));
            if (found != chunk.Count) return false;
        }
        return true;
    }

    // Ребро, уже записанное новым кодом с постоянным номером, — дубль старого: старое убираем,
    // иначе уникальный индекс не даст сменить номер.
    private static void RebindLegacyTargets(AppDbContext source, int fieldId, int legacy, int linkId, RelationConfig? config)
    {
        using var tx = source.Database.BeginTransaction();
        source.DbRecordLinks
            .Where(l => l.FieldId == fieldId && l.TargetDbId == legacy && source.DbRecordLinks.Any(d =>
                d.FieldId == fieldId && d.FromRecordId == l.FromRecordId && d.TargetDbId == linkId && d.ToRecordId == l.ToRecordId))
            .ExecuteDelete();
        source.DbRecordLinks.Where(l => l.FieldId == fieldId && l.TargetDbId == legacy)
            .ExecuteUpdate(s => s.SetProperty(l => l.TargetDbId, linkId));
        if (config is not null && source.DbFields.FirstOrDefault(f => f.Id == fieldId) is { } field)
        {
            field.ConfigJson = (config with { TargetDbId = linkId }).ToConfigJson();
            source.SaveChanges();
        }
        tx.Commit();
    }
}
