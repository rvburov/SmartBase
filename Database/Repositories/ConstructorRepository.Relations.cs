using Microsoft.EntityFrameworkCore;
using SmartBase.Core.Models;
using SmartBase.Database.Entities;
using SmartBase.Utils;

namespace SmartBase.Database.Repositories;

// ── Связи между записями (тип поля «relation») ──────────────────────────────────
// Ребро «из записи в запись по полю-связи» хранится в db_record_links (DbRecordLinkEntity),
// НЕ в db_field_values — так работают каскады (FK ON DELETE CASCADE: удаление поля/записи-конца
// чистит рёбра) и обратные ссылки (backlink = запрос по ToRecordId). Показ ячейки — «чипы» с
// названием (Title) связанной записи, резолвятся на лету (в базе дублируется только Id).
// Настройка поля (целевая коллекция, one/many) — RelationConfig в ConfigJson.
public partial class ConstructorRepository
{
    /// <summary>Связанные записи (Id + Title) для ячейки поля-связи одной записи, в порядке
    /// Position. Title резолвится из db_records (в самой связи хранится только Id).</summary>
    public List<(int Id, string Title)> GetLinks(int fieldId, int fromRecordId) =>
        (from l in db.DbRecordLinks.AsNoTracking()
         where l.FieldId == fieldId && l.FromRecordId == fromRecordId
         join r in db.DbRecords on l.ToRecordId equals r.Id
         orderby l.Position, l.Id
         select new { r.Id, r.Title })
        .ToList()
        .Select(x => (x.Id, x.Title ?? ""))
        .ToList();

    /// <summary>Связи для НАБОРА записей-источников (горячий путь виртуализированной таблицы:
    /// тянем чипы только видимых строк): fromRecordId → список (Id, Title) в порядке Position.</summary>
    public Dictionary<int, List<(int Id, string Title)>> GetLinksBatch(int fieldId, IReadOnlyList<int> fromRecordIds)
    {
        var result = new Dictionary<int, List<(int Id, string Title)>>();
        if (fromRecordIds.Count == 0) return result;

        var rows = (from l in db.DbRecordLinks.AsNoTracking()
                    where l.FieldId == fieldId && fromRecordIds.Contains(l.FromRecordId)
                    join r in db.DbRecords on l.ToRecordId equals r.Id
                    orderby l.Position, l.Id
                    select new { l.FromRecordId, r.Id, r.Title })
                   .ToList();
        foreach (var row in rows)
        {
            if (!result.TryGetValue(row.FromRecordId, out var list))
                result[row.FromRecordId] = list = [];
            list.Add((row.Id, row.Title ?? ""));
        }
        return result;
    }

    /// <summary>Названия записей по их Id (Id → Title). Для резолва названий целей МЕЖБАЗОВОЙ связи:
    /// вызывается на репозитории ЦЕЛЕВОЙ базы (UI берёт его через Workspace/реестр по TargetDbId).
    /// Отсутствующие Id (удалённые цели) в словарь не попадают — вызывающий покажет «(запись удалена)».</summary>
    public Dictionary<int, string> GetRecordTitles(IReadOnlyList<int> ids)
    {
        if (ids.Count == 0) return new();
        return db.DbRecords.AsNoTracking().Where(r => ids.Contains(r.Id))
                 .Select(r => new { r.Id, r.Title }).ToList()
                 .ToDictionary(x => x.Id, x => x.Title ?? "");
    }

    /// <summary>Id связанных записей ячейки в порядке Position (без резолва Title).</summary>
    public List<int> GetLinkTargetIds(int fieldId, int fromRecordId) =>
        db.DbRecordLinks.AsNoTracking()
          .Where(l => l.FieldId == fieldId && l.FromRecordId == fromRecordId)
          .OrderBy(l => l.Position).ThenBy(l => l.Id)
          .Select(l => l.ToRecordId)
          .ToList();

    /// <summary>
    /// Заменить ВЕСЬ набор связей ячейки на <paramref name="toRecordIds"/> (основной путь записи из
    /// пикера). Порядок сохраняется (Position), дубли/само-ссылки/чужие коллекции отсеиваются, для
    /// single берётся максимум одна связь. Разница применяется точечно (лишние — удалить, новые —
    /// добавить, оставшиеся — переставить), чтобы не задваивать unique-индекс и не плодить Id.
    /// Пишет строку журнала «до → после» именами записей. Ничего не изменилось → выходим молча.
    /// </summary>
    public void SetLinks(int fieldId, int fromRecordId, IReadOnlyList<int> toRecordIds)
    {
        Demand(PermAction.EditRecord, CollectionOfRecord(fromRecordId));

        var field = db.DbFields.AsNoTracking().FirstOrDefault(f => f.Id == fieldId);
        if (field is null || field.FieldType != "relation") return;
        var cfg = RelationConfig.FromConfig(field.ConfigJson);
        if (cfg is null) return;
        var targetDbId = cfg.TargetDbId;   // 0 = та же БД; >0 = цель в другом .db (реестровый DbId)

        // Нормализуем желаемый набор: без дублей, порядок выбора сохраняем. Само-ссылку отсеиваем
        // только для внутрибазовой связи (в другой БД тот же Id — уже иная запись).
        var wanted = new List<int>();
        var seen   = new HashSet<int>();
        foreach (var to in toRecordIds)
            if ((targetDbId != 0 || to != fromRecordId) && seen.Add(to)) wanted.Add(to);

        // Внутрибазовая связь: оставляем только существующие записи целевой коллекции (страховка от
        // гонок/битых Id). Межбазовую проверить отсюда нельзя (цель в другом файле) — доверяем пикеру.
        if (targetDbId == 0 && wanted.Count > 0)
        {
            var valid = db.DbRecords.AsNoTracking()
                .Where(r => r.CollectionId == cfg.TargetCollectionId && wanted.Contains(r.Id))
                .Select(r => r.Id)
                .ToHashSet();
            wanted = wanted.Where(valid.Contains).ToList();
        }
        if (cfg.Single && wanted.Count > 1) wanted = [wanted[0]];

        var existing = db.DbRecordLinks
            .Where(l => l.FieldId == fieldId && l.FromRecordId == fromRecordId)
            .ToList();
        var existingOrdered = existing.OrderBy(l => l.Position).ThenBy(l => l.Id)
                                      .Select(l => l.ToRecordId).ToList();
        if (existingOrdered.SequenceEqual(wanted)) return;   // ничего не изменилось

        // Журнал «до → после» именами записей (владелец — сама запись-источник).
        var fromRec = db.DbRecords.Find(fromRecordId);
        if (fromRec is not null)
        {
            var titleIds = existingOrdered.Concat(wanted).Distinct().ToList();
            var titles   = db.DbRecords.AsNoTracking()
                .Where(r => titleIds.Contains(r.Id))
                .ToDictionary(r => r.Id, r => r.Title ?? "");
            // Межбазовые цели локально не резолвятся (другой файл) — в журнале показываем «#id».
            string Join(IEnumerable<int> ids) => string.Join(", ", ids.Select(id => titles.GetValueOrDefault(id, targetDbId == 0 ? "" : $"#{id}")));
            Log("update", "record", fromRec.Title, CollectionNameOf(fromRec.CollectionId),
                detail: ChangeDetail.Value(fromRec.Title, field.Name, Join(existingOrdered), Join(wanted)));
        }

        // Точечная разница: убрать лишние, оставшиеся переставить, недостающие добавить.
        var wantedSet = wanted.ToHashSet();
        var byTo      = new Dictionary<int, DbRecordLinkEntity>();
        foreach (var l in existing)
        {
            if (wantedSet.Contains(l.ToRecordId)) byTo[l.ToRecordId] = l;
            else db.DbRecordLinks.Remove(l);
        }
        for (var i = 0; i < wanted.Count; i++)
        {
            var pos = (i + 1) * 1000;
            if (byTo.TryGetValue(wanted[i], out var l)) l.Position = pos;
            else db.DbRecordLinks.Add(new DbRecordLinkEntity
            {
                FieldId = fieldId, FromRecordId = fromRecordId, ToRecordId = wanted[i],
                TargetDbId = targetDbId, Position = pos,
            });
        }

        if (fromRec is not null) TouchUpdated(fromRec);
        db.SaveChanges();
    }

    /// <summary>Добавить одну связь (для single — заменяет прежнюю). Идемпотентно.</summary>
    public void AddLink(int fieldId, int fromRecordId, int toRecordId)
    {
        // single: новый выбор замещает прежний (SetLinks сам возьмёт первую, но нам нужна именно
        // новая связь — значит передаём ровно её).
        var cfg = RelationConfig.FromConfig(
            db.DbFields.AsNoTracking().Where(f => f.Id == fieldId).Select(f => f.ConfigJson).FirstOrDefault());
        if (cfg is { Single: true }) { SetLinks(fieldId, fromRecordId, [toRecordId]); return; }

        var cur = GetLinkTargetIds(fieldId, fromRecordId);
        if (cur.Contains(toRecordId)) return;
        cur.Add(toRecordId);
        SetLinks(fieldId, fromRecordId, cur);
    }

    /// <summary>Снять одну связь (крестик на чипе). Нет такой связи — no-op.</summary>
    public void RemoveLink(int fieldId, int fromRecordId, int toRecordId)
    {
        var cur = GetLinkTargetIds(fieldId, fromRecordId);
        if (!cur.Remove(toRecordId)) return;
        SetLinks(fieldId, fromRecordId, cur);
    }

    /// <summary>
    /// Кандидаты для пикера связи: записи коллекции с непустым/любым Title, отсортированные по
    /// Position, с поиском по подстроке Title (регистронезависимо) и ограничением количества.
    /// excludeRecordId — убрать саму текущую запись (для self-relation, чтобы не связать с собой).
    /// Для очень больших коллекций результат ограничен <paramref name="limit"/> (без FTS — скан
    /// в пределах коллекции; для v1 достаточно).
    /// </summary>
    public List<(int Id, string Title)> SearchRecordTitles(
        int collectionId, string? query, int limit = 100, int excludeRecordId = 0)
    {
        var q = db.DbRecords.AsNoTracking().Where(r => r.CollectionId == collectionId);
        if (excludeRecordId > 0) q = q.Where(r => r.Id != excludeRecordId);
        var term = query?.Trim() ?? "";
        if (term.Length > 0)
            q = q.Where(r => EF.Functions.Like(r.Title, "%" + term + "%"));
        return q.OrderBy(r => r.Position).ThenBy(r => r.Id)
                .Take(Math.Max(1, limit))
                .Select(r => new { r.Id, r.Title })
                .ToList()
                .Select(x => (x.Id, x.Title ?? ""))
                .ToList();
    }
}
