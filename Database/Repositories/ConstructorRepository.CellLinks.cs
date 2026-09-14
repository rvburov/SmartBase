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
/// Только внутрибазовые связи: цель межбазовой лежит в другом файле, найти ее по названию и
/// проверить отсюда нельзя.
/// </summary>
public sealed partial class ConstructorRepository
{
    /// <summary>Набор связей ячейки. <paramref name="TargetIds"/> пустой — связи снять.
    /// <paramref name="ExpectedOldIds"/> — писать, только если сейчас связи ровно такие
    /// (<see cref="LinkIdsKey"/>): так отмена не затирает то, что поправили после.</summary>
    public readonly record struct LinkWrite(int RecordId, int FieldId, IReadOnlyList<int> TargetIds, string? ExpectedOldIds = null);

    /// <summary>Что поменялось в ячейке-связи — для отмены.</summary>
    public sealed record LinkChange(int RecordId, int FieldId, IReadOnlyList<int> OldIds, IReadOnlyList<int> NewIds);

    public sealed record LinkBulkResult(int Changed, int Conflicts, IReadOnlyList<LinkChange> Changes)
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

    /// <summary>
    /// Найти записи целевой коллекции поля-связи по названиям, как их показывает ячейка связи.
    ///
    /// Сначала весь текст ищется как ОДНО название: у записи в названии может быть запятая
    /// («Иванов, Петров и партнеры»), и резать ее нельзя. Не нашлось — текст режется разделителем
    /// показа поля, запятой, точкой с запятой и переводом строки, и каждый кусок обязан найтись
    /// ровно одной записью. Кусок не нашелся — <see cref="TitleMatch.NotFound"/>; нашелся несколькими
    /// записями — <see cref="TitleMatch.Ambiguous"/>: угадывать, какую из тезок имели в виду, нельзя.
    ///
    /// Сравнение — точное, с учетом регистра: без учета регистра SQLite кириллицу не сравнивает, а
    /// читать все названия коллекции в миллионы записей ради регистра — непозволительно. Круг
    /// «скопировали из своей таблицы — вставили» дает ровно те же названия.
    /// </summary>
    public Dictionary<string, TitleResolution> ResolveRelationTitles(int fieldId, IReadOnlyCollection<string> texts)
    {
        var result = new Dictionary<string, TitleResolution>();
        var config = RelationConfig.FromConfig(db.DbFields.AsNoTracking()
            .Where(f => f.Id == fieldId && f.FieldType == "relation")
            .Select(f => f.ConfigJson)
            .FirstOrDefault());

        if (config is null || config.IsCrossDb)
        {
            foreach (var text in texts) result[text] = new TitleResolution(TitleMatch.NotFound, []);
            return result;
        }

        var separators = new HashSet<char>([',', ';', '\n', '\r']);
        foreach (var ch in config.SeparatorString)
            if (!char.IsWhiteSpace(ch)) separators.Add(ch);

        var partsOf = new Dictionary<string, string[]>(StringComparer.Ordinal);
        var names   = new HashSet<string>(StringComparer.Ordinal);
        foreach (var text in texts.Distinct())
        {
            var whole = text.Trim();
            if (whole.Length == 0) continue;

            var parts = whole.Split([.. separators], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            partsOf[text] = parts;
            names.Add(whole);
            names.UnionWith(parts);
        }

        var byTitle = new Dictionary<string, List<int>>(StringComparer.Ordinal);
        foreach (var part in names.Chunk(500))
        {
            // Список, а не массив: Contains у массива в дереве выражений берется в перегрузке для
            // ReadOnlySpan, и EF такой запрос не переводит.
            var chunk = part.ToList();
            var found = db.DbRecords.AsNoTracking()
                .Where(r => r.CollectionId == config.TargetCollectionId && chunk.Contains(r.Title))
                .Select(r => new { r.Id, r.Title })
                .ToList();
            foreach (var record in found)
            {
                var title = record.Title ?? "";
                if (!byTitle.TryGetValue(title, out var ids)) byTitle[title] = ids = [];
                ids.Add(record.Id);
            }
        }

        foreach (var text in texts.Distinct())
        {
            var whole = text.Trim();
            if (whole.Length == 0) { result[text] = new TitleResolution(TitleMatch.Found, []); continue; }

            if (byTitle.TryGetValue(whole, out var exact))
            {
                result[text] = exact.Count == 1
                    ? new TitleResolution(TitleMatch.Found, [exact[0]])
                    : new TitleResolution(TitleMatch.Ambiguous, []);
                continue;
            }

            var ids   = new List<int>();
            var match = TitleMatch.Found;
            foreach (var part in partsOf[text])
            {
                if (!byTitle.TryGetValue(part, out var found)) { match = TitleMatch.NotFound;  break; }
                if (found.Count > 1)                           { match = TitleMatch.Ambiguous; break; }
                ids.Add(found[0]);
            }

            result[text] = match == TitleMatch.Found && ids.Count > 0
                ? new TitleResolution(TitleMatch.Found, ids)
                : new TitleResolution(match == TitleMatch.Found ? TitleMatch.NotFound : match, []);
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

        var changes   = new List<LinkChange>();
        var conflicts = 0;
        var stamp     = DateTime.Now.ToString("o");
        var author    = CurrentUser.DisplayName;

        using (var tx = db.Database.BeginTransaction())
        {
            var alive    = AliveRecordIds(collectionId, writes.Select(w => w.RecordId));
            var fieldIds = writes.Select(w => w.FieldId).Distinct().ToList();
            var fields   = db.DbFields.AsNoTracking()
                .Where(f => fieldIds.Contains(f.Id) && f.CollectionId == collectionId && f.FieldType == "relation")
                .Select(f => new { f.Id, f.Name, f.ConfigJson })
                .ToList()
                .Select(f => (f.Id, f.Name, Config: RelationConfig.FromConfig(f.ConfigJson)))
                .Where(f => f.Config is { IsCrossDb: false })
                .ToDictionary(f => f.Id, f => (f.Name, Config: f.Config!));

            var live = LastWins(writes.Where(w => alive.Contains(w.RecordId) && fields.ContainsKey(w.FieldId)),
                                w => (w.RecordId, w.FieldId));
            if (live.Count == 0) return LinkBulkResult.Nothing;

            var recordIds    = live.Select(w => w.RecordId).Distinct().ToList();
            var liveFieldIds = live.Select(w => w.FieldId).Distinct().ToList();
            var existing = db.DbRecordLinks
                .Where(l => liveFieldIds.Contains(l.FieldId) && recordIds.Contains(l.FromRecordId))
                .ToList()
                .GroupBy(l => (l.FromRecordId, l.FieldId))
                .ToDictionary(g => g.Key, g => g.OrderBy(l => l.Position).ThenBy(l => l.Id).ToList());

            // Цели — только живые записи: запись могли удалить между поиском по названию и записью.
            var wantedIds = live.SelectMany(w => w.TargetIds).Distinct().ToList();
            var targetCollection = wantedIds.Count == 0
                ? []
                : db.DbRecords.AsNoTracking()
                    .Where(r => wantedIds.Contains(r.Id))
                    .Select(r => new { r.Id, r.CollectionId })
                    .ToList()
                    .ToDictionary(r => r.Id, r => r.CollectionId);

            foreach (var write in live)
            {
                var config = fields[write.FieldId].Config;
                var links  = existing.GetValueOrDefault((write.RecordId, write.FieldId)) ?? [];
                var old    = links.Select(l => l.ToRecordId).ToList();

                if (write.ExpectedOldIds is { } expected && expected != LinkIdsKey(old)) { conflicts++; continue; }

                var wanted = new List<int>();
                foreach (var id in write.TargetIds)
                    if (id != write.RecordId && !wanted.Contains(id)
                        && targetCollection.TryGetValue(id, out var collection) && collection == config.TargetCollectionId)
                        wanted.Add(id);
                if (config.Single && wanted.Count > 1) wanted = [wanted[0]];

                if (old.SequenceEqual(wanted)) continue;

                // Точечная разница, как у SetLinks: оставшиеся ребра переставляются, а не удаляются и
                // заводятся заново — иначе удаление и вставка того же ребра в одном сохранении могли бы
                // столкнуться на уникальном индексе.
                var wantedSet = wanted.ToHashSet();
                var kept      = new Dictionary<int, DbRecordLinkEntity>();
                foreach (var link in links)
                {
                    if (wantedSet.Contains(link.ToRecordId) && kept.TryAdd(link.ToRecordId, link)) continue;
                    db.DbRecordLinks.Remove(link);
                }
                for (var i = 0; i < wanted.Count; i++)
                {
                    var position = (i + 1) * 1000;
                    if (kept.TryGetValue(wanted[i], out var link)) link.Position = position;
                    else db.DbRecordLinks.Add(new DbRecordLinkEntity
                    {
                        FieldId = write.FieldId, FromRecordId = write.RecordId, ToRecordId = wanted[i],
                        TargetDbId = 0, Position = position,
                    });
                }

                changes.Add(new LinkChange(write.RecordId, write.FieldId, old, wanted));
            }

            if (changes.Count == 0) return new LinkBulkResult(0, conflicts, []);

            db.SaveChanges();

            // Аудит «последней правки» — как у значений.
            var touched = changes.Select(c => c.RecordId).Distinct().ToList();
            Run(db.Database.GetDbConnection(), tx.GetDbTransaction(),
                "UPDATE db_records SET UpdatedAt = @at, UpdatedBy = @by WHERE Id = @rid",
                touched, (p, id) => { p["@rid"].Value = id; p["@at"].Value = stamp; p["@by"].Value = author; },
                ["@rid", "@at", "@by"]);

            var name = CollectionNameOf(collectionId) ?? "";
            Log("update", "record", name, name, count: changes.Count,
                detail: LinkBatchDetail(operation, changes, fields.ToDictionary(f => f.Key, f => f.Value.Name)));
            db.SaveChanges();

            tx.Commit();
        }

        foreach (var id in changes.Select(c => c.RecordId).Distinct())
            if (db.DbRecords.Local.FirstOrDefault(x => x.Id == id) is { } tracked)
            {
                tracked.UpdatedAt = stamp;
                tracked.UpdatedBy = author;
            }

        return new LinkBulkResult(changes.Count, conflicts, changes);
    }

    // «До → после» первых изменений названиями записей — как строка журнала SetLinks.
    private string LinkBatchDetail(string operation, List<LinkChange> changes, IReadOnlyDictionary<int, string> fieldNames)
    {
        var sample = changes.Take(5).ToList();
        var titles = GetRecordTitles(sample.SelectMany(c => c.OldIds.Concat(c.NewIds).Append(c.RecordId)).Distinct().ToList());

        string Join(IEnumerable<int> ids) => string.Join(", ", ids.Select(id => titles.GetValueOrDefault(id, "")));

        return ChangeDetail.Batch(operation, changes.Count, [.. sample.Select(c => (
            Owner: titles.GetValueOrDefault(c.RecordId, ""),
            Field: fieldNames.GetValueOrDefault(c.FieldId, ""),
            Old:   Join(c.OldIds),
            New:   Join(c.NewIds)))]);
    }
}
