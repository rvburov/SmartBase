using Microsoft.EntityFrameworkCore;
using SmartBase.Core.Models;
using SmartBase.Database.Entities;

namespace SmartBase.Database.Repositories;

/// <summary>Кто ссылается на записи: свойство-связь, его коллекция, число записей-источников и
/// запрещает ли настройка связи удалять цель.</summary>
public sealed record RelationReference(int FieldId, string Field, int CollectionId, string Collection, int Sources,
    bool Restrict);

/// <summary>Удаление или перенос записи запрещены настройкой связи «Запретить удаление».</summary>
public sealed class RelationDeleteRestrictedException(string message) : InvalidOperationException(message);

/// <summary>
/// Входящие ссылки перед удалением и переносом: окно называет, кто ссылается, а связь с
/// настройкой «Запретить удаление» не дает удалить или перенести цель. Проверка в репозитории не
/// дает обойти запрет ни из окна, ни из обмена данными; ссылки из других баз проверяет окно по
/// открытым базам.
///
/// Удаление и перенос различаются ссылками изнутри набора: при удалении источник уходит вместе с
/// целью и не мешает, а перенос записей копирует их с новыми номерами, и ссылка между
/// переносимыми записями обрывается так же, как ссылка извне.
///
/// Ребра в память не выгружаются: один запрос группирует их по свойству и считает источники.
/// Он идет от целей по индексу ToRecordId: фильтр по свойству в SQL или коррелированный EXISTS по
/// записям коллекции увели бы SQLite в перебор всех ребер свойства или базы (без ANALYZE план
/// выбирается по равенству FieldId). Охрана репозитория без свойств с запретом ребер не читает.
/// </summary>
public partial class ConstructorRepository
{
    /// <summary>Ссылки на записи <paramref name="recordIds"/> из ребер с базой цели из
    /// <paramref name="targetDbIds"/>. Источники из <paramref name="excludedSources"/> и из коллекций
    /// <paramref name="excludedSourceCollections"/> (они удаляются вместе с целью) не считаются.</summary>
    public List<RelationReference> IncomingReferences(IReadOnlyCollection<int> recordIds, IReadOnlyCollection<int> targetDbIds,
        IReadOnlyCollection<int>? excludedSources = null, IReadOnlyCollection<int>? excludedSourceCollections = null,
        bool restrictOnly = false)
    {
        if (recordIds.Count == 0) return [];
        var ids = recordIds.Distinct().ToList();
        var targets = targetDbIds.Distinct().ToList();
        var edges = db.DbRecordLinks.AsNoTracking().Where(l => targets.Contains(l.TargetDbId) && ids.Contains(l.ToRecordId));
        if (excludedSources is { Count: > 0 })
        {
            var sources = excludedSources.Distinct().ToList();
            edges = edges.Where(l => !sources.Contains(l.FromRecordId));
        }
        if (excludedSourceCollections is { Count: > 0 })
        {
            var collections = excludedSourceCollections.Distinct().ToList();
            edges = edges.Where(l => !db.DbRecords.Any(s => s.Id == l.FromRecordId && collections.Contains(s.CollectionId)));
        }
        return CountReferences(edges, restrictOnly);
    }

    /// <summary>Ссылки этой же базы на записи коллекций <paramref name="collectionIds"/> из записей вне
    /// их и вне <paramref name="collectionsTogether"/> (удаляемых в той же операции): внутренние
    /// уходят вместе с коллекциями, а при переносе ветки переписываются на копию.</summary>
    public List<RelationReference> IncomingReferencesToCollections(IReadOnlyCollection<int> collectionIds,
        IReadOnlyCollection<int>? collectionsTogether = null, bool restrictOnly = false)
    {
        if (collectionIds.Count == 0) return [];
        var ids = collectionIds.Distinct().ToList();
        var together = ids.Concat(collectionsTogether ?? []).Distinct().ToList();
        var targets = RelationTargetsOfThisBase().ToList();
        // Цели — некоррелированным подзапросом: ToRecordId IN (записи коллекций) идет по индексу
        // от записей, а не перебором всех ребер базы.
        var inCollections = db.DbRecords.Where(t => ids.Contains(t.CollectionId)).Select(t => t.Id);
        var edges = db.DbRecordLinks.AsNoTracking().Where(l => targets.Contains(l.TargetDbId)
            && inCollections.Contains(l.ToRecordId)
            && !db.DbRecords.Any(s => s.Id == l.FromRecordId && together.Contains(s.CollectionId)));
        return CountReferences(edges, restrictOnly);
    }

    // Ссылки по свойствам-связям этой базы: один запрос группирует найденные ребра по свойству и
    // считает разных источников, отбор свойств (запрет, есть ли такое свойство) — в памяти.
    private List<RelationReference> CountReferences(IQueryable<DbRecordLinkEntity> edges, bool restrictOnly)
    {
        var fields = db.DbFields.AsNoTracking().Where(f => f.FieldType == "relation").ToList()
            .Select(f => (Field: f, Config: RelationConfig.FromConfig(f.ConfigJson)))
            .Where(p => !restrictOnly || p.Config?.RestrictDelete == true)
            .ToDictionary(p => p.Field.Id);
        if (fields.Count == 0) return [];
        var counts = edges.GroupBy(l => l.FieldId)
            .Select(g => new { FieldId = g.Key, Sources = g.Select(l => l.FromRecordId).Distinct().Count() })
            .ToList();
        var counted = counts.Where(c => fields.ContainsKey(c.FieldId))
            .Select(c => (fields[c.FieldId].Field, Restrict: fields[c.FieldId].Config?.RestrictDelete == true, c.Sources))
            .ToList();
        if (counted.Count == 0) return [];
        var collectionIds = counted.Select(c => c.Field.CollectionId).Distinct().ToList();
        var collections = db.DbCollections.AsNoTracking().Where(c => collectionIds.Contains(c.Id))
            .ToDictionary(c => c.Id, c => c.Name);
        return counted.Select(c => new RelationReference(c.Field.Id, c.Field.Name, c.Field.CollectionId,
                TitleText.FirstLine(collections.GetValueOrDefault(c.Field.CollectionId, "")), c.Sources, c.Restrict))
            .OrderBy(r => r.Collection, StringComparer.CurrentCulture)
            .ThenBy(r => r.Field, StringComparer.CurrentCulture).ToList();
    }

    /// <summary>Запрет удаления: связь этой базы с настройкой «Запретить удаление» ссылается на
    /// записи из числа удаляемых. При удалении источники из удаляемого набора (и из
    /// <paramref name="deletedTogether"/>) не мешают; при переносе (<paramref name="move"/>) мешают и
    /// они — копии ссылок между переносимыми записями не сохраняют.</summary>
    private void EnsureDeleteAllowed(IReadOnlyCollection<int> recordIds, IReadOnlyCollection<int>? deletedTogether = null,
        bool move = false)
    {
        if (recordIds.Count == 0) return;
        IReadOnlyCollection<int>? excluded = move ? null : recordIds.Concat(deletedTogether ?? []).ToList();
        ThrowIfRestricted(IncomingReferences(recordIds, RelationTargetsOfThisBase(), excluded, restrictOnly: true),
            move, recordIds.Count);
    }

    /// <summary>Перед переносом записей в другую базу: копия создается до удаления оригинала,
    /// поэтому запрет проверяется заранее, до первой записанной строки.</summary>
    public void EnsureRecordsMovable(IReadOnlyCollection<int> recordIds) => EnsureDeleteAllowed(recordIds, move: true);

    private void EnsureDeleteAllowedForCollections(IReadOnlyCollection<int> collectionIds,
        IReadOnlyCollection<int>? collectionsTogether = null, bool move = false)
    {
        if (collectionIds.Count > 0)
            ThrowIfRestricted(IncomingReferencesToCollections(collectionIds, collectionsTogether, restrictOnly: true), move, 0);
    }

    // Ребра этой базы, ведущие в нее же: внутрибазовые (0) и с собственным номером базы.
    private int[] RelationTargetsOfThisBase() =>
        RelationDbResolver.DbIdFor(this) is > 0 and var own ? [0, own] : [0];

    // Перенос коллекции или ветки в другую базу удаляет оригинал: ссылки извне на ее записи
    // обрываются, и связь с запретом удаления держит перенос так же, как удаление.
    private static void EnsureMoveAllowed(string sourceDbPath, Func<ConstructorRepository, IReadOnlyCollection<int>> collections)
    {
        using var context = new AppDbContext(sourceDbPath);
        var source = new ConstructorRepository(context);
        source.EnsureDeleteAllowedForCollections(collections(source), move: true);
    }

    private static void ThrowIfRestricted(IReadOnlyList<RelationReference> references, bool move, int records)
    {
        var restricted = references.Where(r => r.Restrict).ToList();
        if (restricted.Count > 0) throw new RelationDeleteRestrictedException(RestrictedMessage(restricted, move, records));
    }

    /// <summary>Текст запрета: «Удалить нельзя: на запись ссылается «Счета · Клиент» (3 записи), …».
    /// <paramref name="records"/> — сколько записей удаляется (0 — коллекции или ветка).</summary>
    public static string RestrictedMessage(IReadOnlyCollection<RelationReference> restricted, bool move, int records,
        Func<RelationReference, string?>? database = null) =>
        $"{(move ? "Перенести" : "Удалить")} нельзя: на {(records == 1 ? "запись" : "записи")} " +
        $"{(restricted.Count == 1 ? "ссылается" : "ссылаются")} {DescribeReferenceList(restricted, database)}, " +
        $"а {(restricted.Count == 1 ? "в этой связи" : "в этих связях")} запрещено удалять связанные записи. " +
        "Уберите ссылки или выберите «Снять связь» в окне свойства-связи.";

    /// <summary>«Счета · Клиент» (3 записи), «Заказы · Клиент» из базы «Учет» (1 запись). Свойства
    /// закрытых коллекций одной базы — одной фразой, без имен и чисел.</summary>
    public static string DescribeReferenceList(IEnumerable<RelationReference> references,
        Func<RelationReference, string?>? database = null)
    {
        return string.Join(", ", references.Select(r => DescribeReference(r, database?.Invoke(r))));
    }

    /// <summary>Одна ссылка для текста окна.</summary>
    public static string DescribeReference(RelationReference reference, string? database = null)
    {
        var from = database is null ? "" : $" из базы «{database}»";
        return $"«{reference.Collection} · {reference.Field}»{from} ({reference.Sources} {RecordsWord(reference.Sources)})";
    }

    internal static string RecordsWord(int n) => RelationBacklinkText.RecordsWord(n);
}
