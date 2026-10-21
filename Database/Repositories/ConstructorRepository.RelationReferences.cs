using Microsoft.EntityFrameworkCore;
using SmartBase.Core.Models;

namespace SmartBase.Database.Repositories;

/// <summary>Кто ссылается на записи: свойство-связь, его коллекция, число записей-источников и
/// запрещает ли настройка связи удалять цель.</summary>
public sealed record RelationReference(int FieldId, string Field, int CollectionId, string Collection, int Sources,
    bool Restrict);

/// <summary>Удаление или перенос записи запрещены настройкой связи «Запретить удаление».</summary>
public sealed class RelationDeleteRestrictedException(string message) : InvalidOperationException(message);

/// <summary>
/// Входящие ссылки перед удалением: окно удаления называет, кто ссылается, а связь с настройкой
/// «Запретить удаление» не дает удалить или перенести цель. Проверка в репозитории не дает обойти
/// запрет ни из окна, ни из обмена данными; ссылки из других баз проверяет окно по открытым базам.
/// </summary>
public partial class ConstructorRepository
{
    /// <summary>Ссылки на записи <paramref name="recordIds"/> из ребер с базой цели из
    /// <paramref name="targetDbIds"/>; источники из <paramref name="excludedSources"/> (они удаляются
    /// вместе с целью) не считаются.</summary>
    public List<RelationReference> IncomingReferences(IReadOnlyCollection<int> recordIds, IReadOnlyCollection<int> targetDbIds,
        IReadOnlySet<int>? excludedSources = null)
    {
        var sources = new Dictionary<int, HashSet<int>>();
        var targets = targetDbIds.ToList();
        foreach (var chunk in recordIds.Distinct().Chunk(500).Select(c => c.ToList()))
            foreach (var link in db.DbRecordLinks.AsNoTracking()
                         .Where(l => targets.Contains(l.TargetDbId) && chunk.Contains(l.ToRecordId))
                         .Select(l => new { l.FieldId, l.FromRecordId }).ToList())
                if (excludedSources?.Contains(link.FromRecordId) != true)
                    (sources.TryGetValue(link.FieldId, out var set) ? set : sources[link.FieldId] = []).Add(link.FromRecordId);
        return DescribeReferences(sources);
    }

    /// <summary>Ссылки этой же базы на записи коллекций <paramref name="collectionIds"/> из записей
    /// вне их (внутренние уходят вместе с коллекциями).</summary>
    public List<RelationReference> IncomingReferencesToCollections(IReadOnlyCollection<int> collectionIds)
    {
        var ids = collectionIds.ToList();
        var own = RelationDbResolver.DbIdFor(this);
        var rows = (from link in db.DbRecordLinks.AsNoTracking()
                    join target in db.DbRecords.AsNoTracking() on link.ToRecordId equals target.Id
                    join source in db.DbRecords.AsNoTracking() on link.FromRecordId equals source.Id
                    where (link.TargetDbId == 0 || own > 0 && link.TargetDbId == own)
                          && ids.Contains(target.CollectionId) && !ids.Contains(source.CollectionId)
                    select new { link.FieldId, link.FromRecordId }).Distinct().ToList();
        var sources = new Dictionary<int, HashSet<int>>();
        foreach (var row in rows)
            (sources.TryGetValue(row.FieldId, out var set) ? set : sources[row.FieldId] = []).Add(row.FromRecordId);
        return DescribeReferences(sources);
    }

    private List<RelationReference> DescribeReferences(Dictionary<int, HashSet<int>> sources)
    {
        if (sources.Count == 0) return [];
        var fieldIds = sources.Keys.ToList();
        var fields = db.DbFields.AsNoTracking().Where(f => fieldIds.Contains(f.Id)).ToList();
        var collectionIds = fields.Select(f => f.CollectionId).Distinct().ToList();
        var collections = db.DbCollections.AsNoTracking().Where(c => collectionIds.Contains(c.Id))
            .ToDictionary(c => c.Id, c => c.Name);
        return fields.Select(f => new RelationReference(f.Id, f.Name, f.CollectionId,
                TitleText.FirstLine(collections.GetValueOrDefault(f.CollectionId, "")), sources[f.Id].Count,
                RelationConfig.FromConfig(f.ConfigJson)?.RestrictDelete == true))
            .OrderBy(r => r.Collection, StringComparer.CurrentCulture).ThenBy(r => r.Field, StringComparer.CurrentCulture).ToList();
    }

    /// <summary>Запрет удаления: связь этой базы с настройкой «Запретить удаление» ссылается на
    /// записи из числа удаляемых, а ее источник сам не удаляется.</summary>
    private void EnsureDeleteAllowed(IReadOnlyCollection<int> recordIds)
    {
        if (recordIds.Count == 0) return;
        var deleted = recordIds as IReadOnlySet<int> ?? recordIds.ToHashSet();
        ThrowIfRestricted(IncomingReferences(recordIds, RelationTargetsOfThisBase(), deleted));
    }

    // Ребра этой базы, ведущие в нее же: внутрибазовые (0) и с собственным номером базы.
    private int[] RelationTargetsOfThisBase() =>
        RelationDbResolver.DbIdFor(this) is > 0 and var own ? [0, own] : [0];

    private void EnsureDeleteAllowedForCollections(IReadOnlyCollection<int> collectionIds)
    {
        if (collectionIds.Count > 0) ThrowIfRestricted(IncomingReferencesToCollections(collectionIds));
    }

    // Перенос коллекции или ветки в другую базу удаляет оригинал: ссылки извне на ее записи
    // обрываются, и связь с запретом удаления держит перенос так же, как удаление.
    private static void EnsureMoveAllowed(string sourceDbPath, Func<ConstructorRepository, IReadOnlyCollection<int>> collections)
    {
        using var context = new AppDbContext(sourceDbPath);
        var source = new ConstructorRepository(context);
        source.EnsureDeleteAllowedForCollections(collections(source));
    }

    private static void ThrowIfRestricted(IReadOnlyList<RelationReference> references)
    {
        var restricted = references.Where(r => r.Restrict).ToList();
        if (restricted.Count > 0) throw new RelationDeleteRestrictedException(RestrictedMessage(restricted));
    }

    /// <summary>Текст запрета: какие связи не дают удалить записи.</summary>
    public static string RestrictedMessage(IEnumerable<RelationReference> restricted) =>
        $"Удалить нельзя: на записи ссылаются {DescribeReferenceList(restricted)}, а в этих связях запрещено " +
        "удалять связанные записи. Уберите ссылки или разрешите удаление в окне свойства-связи.";

    /// <summary>«Заказы · Клиент» — 3 записи, …</summary>
    public static string DescribeReferenceList(IEnumerable<RelationReference> references) =>
        string.Join(", ", references.Select(r => $"«{r.Collection} · {r.Field}» — {r.Sources} {RecordsWord(r.Sources)}"));

    internal static string RecordsWord(int n) => (n % 100, n % 10) switch
    {
        ( >= 11 and <= 14, _) => "записей",
        (_, 1) => "запись",
        (_, >= 2 and <= 4) => "записи",
        _ => "записей",
    };
}
