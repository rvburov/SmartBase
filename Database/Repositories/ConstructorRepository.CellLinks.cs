using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using SmartBase.Core.Models;
using SmartBase.Database.Entities;
using SmartBase.Utils;

namespace SmartBase.Database.Repositories;

/// <summary>
/// Пакетная правка ячеек-связей из таблицы: вставка связей по названиям из буфера обмена, их
/// очистка и отмена. Пара к <see cref="SetCellsBulk"/>: связь живет не в db_field_values, а ребрами
/// в db_record_links, и писать ее текстом нельзя.
///
/// Поштучный <see cref="SetLinks"/> на пакет не годится по той же причине, что и поштучная правка
/// значений: коммит и строка журнала на КАЖДУЮ ячейку. Здесь все ребра, аудит и одна строка
/// журнала ложатся одной транзакцией.
///
/// </summary>
public sealed partial class ConstructorRepository
{
    /// <summary>Набор связей ячейки. <paramref name="TargetIds"/> пустой — связи снять.
    /// <paramref name="ExpectedOldIds"/> — писать, только если сейчас связи ровно такие
    /// (<see cref="LinkIdsKey"/>): так отмена не затирает то, что поправили после.</summary>
    public readonly record struct LinkWrite(int RecordId, int FieldId, IReadOnlyList<int> TargetIds, string? ExpectedOldIds = null)
    {
        public IReadOnlyList<RelationTarget>? Targets { get; init; }
        public string? ExpectedTargetsKey { get; init; }
        public string? ExpectedConfig { get; init; }
        internal IReadOnlyList<RelationTarget>? RestoreTargets { get; init; }
        public static LinkWrite FromTargets(int recordId, int fieldId, IReadOnlyList<RelationTarget> targets,
            string? expectedTargetsKey = null, string? expectedConfig = null) =>
            new(recordId, fieldId, []) { Targets = targets, ExpectedTargetsKey = expectedTargetsKey,
                ExpectedConfig = expectedConfig, RestoreTargets = expectedTargetsKey is not null && expectedConfig is not null ? targets : null };
    }

    /// <summary>Что поменялось в ячейке-связи — для отмены.</summary>
    public sealed record LinkChange(int RecordId, int FieldId, IReadOnlyList<int> OldIds, IReadOnlyList<int> NewIds)
    {
        public IReadOnlyList<RelationTarget> OldTargets { get; init; } = [];
        public IReadOnlyList<RelationTarget> NewTargets { get; init; } = [];
        public string? Config { get; init; }
    }

    /// <param name="ConditionRefused">Ячеек, пропущенных целиком: новая связь не подходит под
    /// условие выбора записей.</param>
    public sealed record LinkBulkResult(int Changed, int Conflicts, IReadOnlyList<LinkChange> Changes, bool ChangesTruncated = false,
        int ConditionRefused = 0)
    {
        public static LinkBulkResult Nothing { get; } = new(0, 0, []);
    }

    /// <summary>Нашлись ли записи по названию.</summary>
    public enum TitleMatch { Found, NotFound, Ambiguous }

    /// <summary>Итог поиска по названию: <paramref name="Ids"/> — найденные записи в порядке
    /// названий; пустой при <see cref="TitleMatch.Found"/> — текст пустой, связи снять.</summary>
    public readonly record struct TitleResolution(TitleMatch Match, IReadOnlyList<int> Ids);

    /// <summary>Канон набора связей для сравнения: id в порядке показа через запятую.</summary>
    public static string LinkIdsKey(IEnumerable<int> ids) => string.Join(",", ids);
    public static string LinkTargetsKey(IEnumerable<RelationTarget> targets) =>
        string.Join(",", targets.Select(t => $"{t.DbId}:{t.RecordId}"));

    /// <summary>Разрешить подписи выбранного свойства с точным разделителем. Любое второе
    /// допустимое разбиение или тезки делают текст неоднозначным; идентификаторы не угадываются.</summary>
    public Dictionary<string, TitleResolution> ResolveRelationTitles(int fieldId, IReadOnlyCollection<string> texts)
    {
        var result = new Dictionary<string, TitleResolution>();
        var config = RelationConfig.FromConfig(db.DbFields.AsNoTracking()
            .Where(f => f.Id == fieldId && f.FieldType == "relation")
            .Select(f => f.ConfigJson)
            .FirstOrDefault());

        if (config is null)
        {
            foreach (var text in texts) result[text] = new TitleResolution(TitleMatch.NotFound, []);
            return result;
        }

        var target = config.IsCrossDb ? RelationDbResolver.RepoFor(config.TargetDbId) : this;
        if (target is null)
        {
            foreach (var text in texts) result[text] = new(TitleMatch.NotFound, []);
            return result;
        }
        return target.ResolveRelationTexts(config, texts);
    }

    private Dictionary<string, TitleResolution> ResolveRelationTexts(RelationConfig config, IReadOnlyCollection<string> texts)
    {
        var result = new Dictionary<string, TitleResolution>(StringComparer.Ordinal);
        var separator = config.SeparatorString;
        var partsOf = new Dictionary<string, string[]>(StringComparer.Ordinal);
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var text in texts.Distinct())
        {
            if (string.IsNullOrWhiteSpace(text)) continue;
            var parts = text.Split([separator], StringSplitOptions.None);
            partsOf[text] = parts;
            // Большие текстовые списки нельзя безопасно разобрать за ограниченное время.
            // В собственном буфере SmartBase есть полные идентификаторы, там этот путь не нужен.
            if (parts.Length > 64) continue;
            for (var i = 0; i < parts.Length; i++)
                for (var end = i; end < parts.Length; end++)
                    names.Add(string.Join(separator, parts, i, end - i + 1));
        }
        var unpaddedNames = names.Select(n => n.Trim(' ')).ToHashSet(StringComparer.Ordinal);
        var byTitle = new Dictionary<string, List<int>>(StringComparer.Ordinal);
        if (config.DisplayField == RelationConfig.TitleField)
        {
            Demand(PermAction.ViewCollection, config.TargetCollectionId);
            foreach (var part in names.Chunk(500))
            {
                var chunk = part.ToList();
                var padded = part.Select(s => s.Trim(' ')).Distinct().ToList();
                foreach (var record in db.DbRecords.AsNoTracking()
                    .Where(r => r.CollectionId == config.TargetCollectionId && (chunk.Contains(r.Title) || padded.Contains(r.Title.Trim())))
                    .Select(r => new { r.Id, r.Title }).ToList())
                {
                    var title = record.Title ?? "";
                    if (!byTitle.TryGetValue(title, out var ids)) byTitle[title] = ids = [];
                    if (!ids.Contains(record.Id)) ids.Add(record.Id);
                }
            }
        }
        else
            foreach (var value in ReadRelationPropertyValues(config.TargetCollectionId, config.DisplayField))
                if (names.Contains(value.Display) || unpaddedNames.Contains(value.Display.Trim(' ')))
                {
                    if (!byTitle.TryGetValue(value.Display, out var ids)) byTitle[value.Display] = ids = [];
                    ids.Add(value.RecordId);
                }
        var byUnpaddedTitle = byTitle.SelectMany(p => p.Value.Select(id => (Name: p.Key.Trim(' '), Id: id)))
            .GroupBy(p => p.Name, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Select(p => p.Id).Distinct().ToList(), StringComparer.Ordinal);
        foreach (var text in texts.Distinct())
        {
            if (string.IsNullOrWhiteSpace(text)) { result[text] = new(TitleMatch.Found, []); continue; }
            var parts = partsOf[text];
            if (parts.Length > 64) { result[text] = new(TitleMatch.Ambiguous, []); continue; }
            (int Count, List<int>? Ids) Decode(bool allowPadding)
            {
                var ways = new int[parts.Length + 1];
                var chosen = new List<int>?[parts.Length + 1];
                ways[^1] = 1;
                chosen[^1] = [];
                for (var i = parts.Length - 1; i >= 0; i--)
                    for (var end = i; end < parts.Length; end++)
                    {
                        if (ways[end + 1] == 0) continue;
                        var label = string.Join(separator, parts, i, end - i + 1);
                        if (!byTitle.TryGetValue(label, out var ids)
                            && !(allowPadding && byUnpaddedTitle.TryGetValue(label.Trim(' '), out ids))) continue;
                        chosen[i] ??= [ids[0], .. chosen[end + 1]!];
                        ways[i] = Math.Min(2, ways[i] + Math.Min(2, ids.Count) * ways[end + 1]);
                    }
                return (ways[0], chosen[0]);
            }
            var decoded = Decode(false);
            if (decoded.Count == 0) decoded = Decode(true);
            result[text] = decoded.Count == 0 ? new(TitleMatch.NotFound, [])
                : decoded.Count > 1 || config.Single && decoded.Ids!.Distinct().Count() > 1 ? new(TitleMatch.Ambiguous, [])
                : new(TitleMatch.Found, decoded.Ids!);
        }

        return result;
    }

    /// <summary>
    /// Записать наборы связей ячеек пакетом. Правила набора те же, что у <see cref="SetLinks"/>:
    /// порядок сохраняется, дубли, ссылка на саму себя и записи не из целевой коллекции
    /// отсеиваются, у связи «одна запись» остается первая. Ячейки, у которых набор не изменился, не
    /// пишутся и в счет не идут.
    /// </summary>
    /// <param name="operation">Как назвать операцию в журнале: «Вставка», «Очистка», «Отмена вставки».</param>
    public LinkBulkResult SetLinksBulk(int collectionId, IReadOnlyList<LinkWrite> writes, string operation = "Правка")
    {
        Demand(PermAction.EditRecord, collectionId);
        if (writes.Count == 0) return LinkBulkResult.Nothing;

        var changes = new List<LinkChange>();
        var conflicts = 0;
        var conditionRefused = 0;
        var changed = 0;
        long undoUnits = 0;
        var truncated = false;
        var touched = new HashSet<int>();
        var stamp = DateTime.Now.ToString("o");
        var author = CurrentUser.DisplayName;
        using var ownedTx = db.Database.CurrentTransaction is null ? db.Database.BeginTransaction() : null;
        var tx = db.Database.CurrentTransaction!;
        var alive = AliveRecordIds(collectionId, writes.Select(w => w.RecordId));
        var fieldIds = writes.Select(w => w.FieldId).Distinct().ToList();
        var fields = db.DbFields.AsNoTracking()
            .Where(f => fieldIds.Contains(f.Id) && f.CollectionId == collectionId && f.FieldType == "relation")
            .ToList().Where(f => RelationConfig.FromConfig(f.ConfigJson) is { Auto: null })
            .ToDictionary(f => f.Id);
        var live = LastWins(writes.Where(w => alive.Contains(w.RecordId) && fields.ContainsKey(w.FieldId)),
                            w => (w.RecordId, w.FieldId));
        conflicts += writes.Count(w => w.ExpectedTargetsKey is not null && (!alive.Contains(w.RecordId) || !fields.ContainsKey(w.FieldId)));
        var recordIds = live.Select(w => w.RecordId).Distinct().ToList();
        var existing = db.DbRecordLinks.Where(l => fieldIds.Contains(l.FieldId) && recordIds.Contains(l.FromRecordId))
            .ToList().GroupBy(l => (l.FromRecordId, l.FieldId))
            .ToDictionary(g => g.Key, g => g.OrderBy(l => l.Position).ThenBy(l => l.Id).ToList());
        foreach (var write in live)
        {
            var field = fields[write.FieldId];
            var config = RelationConfig.FromConfig(field.ConfigJson)!;
            var links = existing.GetValueOrDefault((write.RecordId, write.FieldId)) ?? [];
            var old = links.Select(l => new RelationTarget(l.TargetDbId, l.ToRecordId)).ToList();
            if (write.ExpectedOldIds is { } idsKey && idsKey != LinkIdsKey(old.Select(t => t.RecordId))
                || write.ExpectedTargetsKey is { } key && key != LinkTargetsKey(old)
                || write.ExpectedConfig is { } configKey && configKey != field.ConfigJson)
            { conflicts++; continue; }
            var requested = write.Targets ?? write.TargetIds.Select(id => new RelationTarget(config.TargetDbId, id)).ToList();
            // Старый API сохраняет поведение SetLinks. Структурная вставка не теряет лишние цели.
            if (write.Targets is not null && config.Single && requested.Distinct().Count() > 1)
            { conflicts++; continue; }
            // Новая цель вне условия выбора — ячейка пропускается целиком: частично вставленный
            // набор связей человек не просил.
            var refused = new List<RelationTarget>();
            var wanted = NormalizeRelationTargets(config, write.RecordId, requested,
                write.RestoreTargets is null ? old : old.Concat(write.RestoreTargets).Distinct().ToList(), refused: refused);
            if (refused.Count > 0) { conditionRefused++; continue; }
            if (write.Targets is not null && wanted.Count != requested.Distinct().Count())
            { conflicts++; continue; }
            if (old.SequenceEqual(wanted)) continue;
            var wantedSet = wanted.ToHashSet();
            var kept = new Dictionary<RelationTarget, DbRecordLinkEntity>();
            foreach (var link in links)
            {
                var target = new RelationTarget(link.TargetDbId, link.ToRecordId);
                if (wantedSet.Contains(target) && kept.TryAdd(target, link)) continue;
                db.DbRecordLinks.Remove(link);
            }
            for (var i = 0; i < wanted.Count; i++)
            {
                var target = wanted[i];
                if (kept.TryGetValue(target, out var edge)) edge.Position = (i + 1) * 1000;
                else db.DbRecordLinks.Add(new DbRecordLinkEntity
                {
                    FieldId = write.FieldId, FromRecordId = write.RecordId, ToRecordId = target.RecordId,
                    TargetDbId = target.DbId, Position = (i + 1) * 1000,
                });
            }
            changed++;
            touched.Add(write.RecordId);
            undoUnits += Math.Max(1, old.Count + wanted.Count);
            if (undoUnits > UndoChangesLimit) truncated = true;
            if (!truncated)
                changes.Add(new(write.RecordId, write.FieldId, old.Select(t => t.RecordId).ToList(), wanted.Select(t => t.RecordId).ToList())
                { OldTargets = old, NewTargets = wanted, Config = field.ConfigJson });
            else
            {
                if (changes.Count > 5) changes.RemoveRange(5, changes.Count - 5);
                if (changes.Count < 5) changes.Add(new(write.RecordId, write.FieldId,
                    old.Take(5).Select(t => t.RecordId).ToList(), wanted.Take(5).Select(t => t.RecordId).ToList())
                    { OldTargets = old.Take(5).ToList(), NewTargets = wanted.Take(5).ToList(), Config = field.ConfigJson });
            }
        }
        if (changed == 0) return new(0, conflicts, [], ConditionRefused: conditionRefused);
        db.SaveChanges();
        Run(db.Database.GetDbConnection(), tx.GetDbTransaction(),
            "UPDATE db_records SET UpdatedAt = @at, UpdatedBy = @by WHERE Id = @rid",
            touched.ToList(), (p, id) => { p["@rid"].Value = id; p["@at"].Value = stamp; p["@by"].Value = author; },
            ["@rid", "@at", "@by"]);
        var name = CollectionNameOf(collectionId) ?? "";
        Log("update", "record", name, name, count: changed,
            detail: LinkBatchDetail(operation, changes, fields.ToDictionary(f => f.Key, f => f.Value.Name), changed));
        db.SaveChanges();
        NotifyRelationDataChanged(collectionId);
        ownedTx?.Commit();
        foreach (var id in touched)
            if (db.DbRecords.Local.FirstOrDefault(x => x.Id == id) is { } tracked)
            { tracked.UpdatedAt = stamp; tracked.UpdatedBy = author; }
        return truncated ? new(changed, conflicts, [], true, conditionRefused) : new(changed, conflicts, changes, false, conditionRefused);
    }

    // «До → после» первых изменений названиями записей — как строка журнала SetLinks.
    private string LinkBatchDetail(string operation, List<LinkChange> changes, IReadOnlyDictionary<int, string> fieldNames, int changed)
    {
        var sample = changes.Take(5).ToList();
        var titles = GetRecordTitles(sample.SelectMany(c => c.OldIds.Concat(c.NewIds).Append(c.RecordId)).Distinct().ToList());

        string Join(LinkChange change, IReadOnlyList<RelationTarget> targets)
        {
            var cfg = RelationConfig.FromConfig(change.Config);
            var labels = ResolveRelationLabels(targets, cfg?.DisplayField ?? RelationConfig.TitleField, cfg?.TargetDbId);
            return string.Join(", ", targets.Select(t => labels.GetValueOrDefault(t, $"#{t.RecordId}")));
        }

        return ChangeDetail.Batch(operation, changed, [.. sample.Select(c => (
            Owner: titles.GetValueOrDefault(c.RecordId, ""),
            Field: fieldNames.GetValueOrDefault(c.FieldId, ""),
            Old:   Join(c, c.OldTargets),
            New:   Join(c, c.NewTargets)))]);
    }
}
