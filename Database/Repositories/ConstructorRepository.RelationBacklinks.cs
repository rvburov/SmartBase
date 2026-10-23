using Microsoft.EntityFrameworkCore;
using SmartBase.Core.Models;

namespace SmartBase.Database.Repositories;

/// <summary>
/// Обратные ссылки страницы записи: какие записи этой базы ссылаются на показанную запись — по
/// каждому свойству-связи, нацеленному на ее коллекцию, даже если ссылок нет.
///
/// Чтение в два шага, и оба — точечные поиски, сколько бы ребер ни было. Сначала номера
/// источников по убыванию (сначала новые записи) — только из индекса ребер (ToRecordId,
/// TargetDbId, FieldId, FromRecordId), без чтения таблицы и без сортировки. Затем сверка по
/// первичному ключу db_records: источник лежит в коллекции свойства. Ребро записи, которую
/// перенесли в другую коллекцию, не считается и не называется. План не зависит от ANALYZE и
/// объема — урок поиска входящих перед удалением (см. RelationReferences).
///
/// Своя база хранит ребра на свою запись с базой цели 0 и, у старых ребер, со своим номером —
/// обе формы сливаются без повторов; непосредственная ссылка записи на себя не показывается.
/// Другая база ссылается только своим номером на базу показанной записи. Свойства коллекций,
/// закрытых для пользователя, не показываются вовсе: ни имени, ни числа.
/// </summary>
public partial class ConstructorRepository
{
    // Порция номеров при поиске по названию и при точном подсчете.
    private const int BacklinkScanChunk = 4096;
    private const int BacklinkCountChunk = 16384;

    /// <summary>Карточки ссылок на запись <paramref name="recordId"/> коллекции
    /// <paramref name="collectionId"/>. <paramref name="sourceDbId"/> — 0, если эта база и есть база
    /// показанной записи, иначе постоянный номер этой базы; <paramref name="ownLinkId"/> — номер
    /// базы показанной записи (0 — номера нет, ссылки из других баз ее не находят).</summary>
    public List<RelationBacklinkGroup> RelationBacklinkGroups(int sourceDbId, int recordId, int collectionId, int ownLinkId,
        CancellationToken ct = default)
    {
        var panelBase = sourceDbId == 0;
        if (!panelBase && ownLinkId <= 0) return [];
        int[] targets = panelBase ? ownLinkId > 0 ? [0, ownLinkId] : [0] : [ownLinkId];
        var fields = db.DbFields.AsNoTracking().Where(f => f.FieldType == "relation").ToList()
            .Select(f => (Field: f, Config: RelationConfig.FromConfig(f.ConfigJson)))
            .Where(p => p.Config is { } c && c.TargetCollectionId == collectionId
                        && (panelBase ? c.TargetDbId == 0 || ownLinkId > 0 && c.TargetDbId == ownLinkId : c.TargetDbId == ownLinkId)
                        && CanReadRelationCollection(p.Field.CollectionId))
            .Select(p => p.Field).ToList();
        if (fields.Count == 0) return [];
        var collectionIds = fields.Select(f => f.CollectionId).Distinct().ToList();
        var collections = db.DbCollections.AsNoTracking().Where(c => collectionIds.Contains(c.Id))
            .Select(c => new { c.Id, c.Name }).ToDictionary(c => c.Id, c => c.Name);
        var result = new List<RelationBacklinkGroup>();
        foreach (var field in fields.Where(f => collections.ContainsKey(f.CollectionId)))
        {
            ct.ThrowIfCancellationRequested();
            var sources = new List<int>();
            foreach (var chunk in BacklinkSourceIds(field.Id, recordId, targets, panelBase ? recordId : 0, null,
                         RelationBacklinkText.Cap + 1, ct))
            {
                sources.AddRange(BacklinkVerified(chunk, field.CollectionId, null));
                if (sources.Count > RelationBacklinkText.Cap) break;
            }
            var more = sources.Count > RelationBacklinkText.Cap;
            result.Add(new RelationBacklinkGroup(sourceDbId, field.Id, field.Name, field.CollectionId,
                TitleText.FirstLine(collections[field.CollectionId]), more ? RelationBacklinkText.Cap : sources.Count, more,
                BacklinkTitles(sources.Take(RelationBacklinkText.FirstCount).ToList()), targets));
        }
        return result
            .OrderBy(g => g.Collection, StringComparer.CurrentCulture)
            .ThenBy(g => fields.First(f => f.Id == g.FieldId).Position).ThenBy(g => g.FieldId).ToList();
    }

    /// <summary>Страница списка источников карточки: до <paramref name="take"/> записей с номером
    /// меньше <paramref name="before"/>, сначала новые. <paramref name="query"/> — часть названия без
    /// учета регистра; поиск просматривает источники порциями и проверяет отмену между ними.</summary>
    public RelationBacklinkPage RelationBacklinkSources(RelationBacklinkGroup group, int recordId, string? query, int? before,
        int take, CancellationToken ct = default)
    {
        Demand(PermAction.ViewCollection, group.CollectionId);
        var term = string.IsNullOrWhiteSpace(query) ? null : query.Trim();
        var found = new List<int>();
        foreach (var chunk in BacklinkSourceIds(group.FieldId, recordId, group.Targets, group.SourceDbId == 0 ? recordId : 0,
                     before, term is null ? take + 1 : BacklinkScanChunk, ct))
        {
            found.AddRange(BacklinkVerified(chunk, group.CollectionId, term));
            if (found.Count > take) break;
        }
        var items = found.Take(take).ToList();
        // Следующая страница — ниже последней показанной записи: найденная сверх страницы
        // запись придет первой в следующей.
        return new(BacklinkTitles(items), found.Count > take ? items[^1] : null);
    }

    /// <summary>Точное число источников карточки — для подсказки под открытым списком.</summary>
    public int RelationBacklinkTotal(RelationBacklinkGroup group, int recordId, CancellationToken ct = default)
    {
        Demand(PermAction.ViewCollection, group.CollectionId);
        var total = 0;
        foreach (var chunk in BacklinkSourceIds(group.FieldId, recordId, group.Targets, group.SourceDbId == 0 ? recordId : 0,
                     null, BacklinkCountChunk, ct))
            total += BacklinkVerified(chunk, group.CollectionId, null).Count;
        return total;
    }

    // Номера источников по убыванию без повторов, порциями: по запросу на каждую форму цели, только
    // из индекса ребер. Две формы цели сливаются, и порция кончается на большем из последних
    // номеров полных ответов: ниже него у полного ответа могут быть еще номера, которых другой
    // ответ не видел. Следующая порция идет строго ниже — повторов между порциями нет.
    private IEnumerable<List<int>> BacklinkSourceIds(int fieldId, int recordId, IReadOnlyList<int> targets, int excluded,
        int? before, int take, CancellationToken ct)
    {
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var merged = new HashSet<int>();
            int? cutoff = null;
            foreach (var target in targets)
            {
                var ids = BacklinkEdgeSources(fieldId, recordId, target, before, excluded).Take(take).ToList();
                if (ids.Count == take) cutoff = Math.Max(cutoff ?? int.MinValue, ids[^1]);
                merged.UnionWith(ids);
            }
            var chunk = merged.Where(id => cutoff is null || id >= cutoff).OrderByDescending(id => id).ToList();
            if (chunk.Count > 0) yield return chunk;
            if (cutoff is null) yield break;
            before = chunk[^1];
        }
    }

    /// <summary>Источники ребер свойства на запись с одной формой базы цели, по убыванию номера, ниже
    /// <paramref name="before"/>: только индекс ребер (план проверяет тест).</summary>
    internal IQueryable<int> BacklinkEdgeSources(int fieldId, int recordId, int target, int? before, int excluded)
    {
        var edges = db.DbRecordLinks.AsNoTracking()
            .Where(l => l.ToRecordId == recordId && l.TargetDbId == target && l.FieldId == fieldId);
        if (before is int below) edges = edges.Where(l => l.FromRecordId < below);
        if (excluded > 0) edges = edges.Where(l => l.FromRecordId != excluded);
        return edges.OrderByDescending(l => l.FromRecordId).Select(l => l.FromRecordId);
    }

    // Источники порции, которые лежат в коллекции свойства (и чье название содержит
    // <paramref name="term"/>), — по первичному ключу; порядок порции сохраняется.
    private List<int> BacklinkVerified(List<int> ids, int collectionId, string? term)
    {
        var records = db.DbRecords.AsNoTracking().Where(r => ids.Contains(r.Id) && r.CollectionId == collectionId);
        if (term is not null)
        {
            // Без учета регистра, и у кириллицы тоже, — как поиск в списке связи.
            var pattern = "%" + EscapeLike(term) + "%";
            records = db.IsPostgres ? records.Where(r => EF.Functions.ILike(r.Title, pattern, "\\"))
                : records.Where(r => SqlText.ContainsIgnoreCase(r.Title, term));
        }
        var found = records.Select(r => r.Id).ToHashSet();
        return ids.Where(found.Contains).ToList();
    }

    private List<RelationBacklinkSource> BacklinkTitles(IReadOnlyCollection<int> ids)
    {
        if (ids.Count == 0) return [];
        var titles = db.DbRecords.AsNoTracking().Where(r => ids.Contains(r.Id))
            .Select(r => new { r.Id, r.Title }).ToDictionary(r => r.Id, r => r.Title);
        return ids.Where(titles.ContainsKey).Select(id => new RelationBacklinkSource(id, titles[id] ?? "")).ToList();
    }
}
