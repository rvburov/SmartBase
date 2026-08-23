using Microsoft.EntityFrameworkCore;
using SmartBase.Core.Models;
using SmartBase.Database.Entities;
using SmartBase.Utils;

namespace SmartBase.Database.Repositories;

// ── Связи между записями (тип поля «relation») ──────────────────────────────────
// Ребро «из записи в запись по полю-связи» хранится в db_record_links (DbRecordLinkEntity),
// НЕ в db_field_values — так работают каскады (FK ON DELETE CASCADE: удаление поля/записи-конца
// чистит ребра) и обратные ссылки (backlink = запрос по ToRecordId). Показ ячейки — «чипы» с
// названием (Title) связанной записи, резолвятся на лету (в базе дублируется только Id).
// Настройка поля (целевая коллекция, one/many) — RelationConfig в ConfigJson.
public partial class ConstructorRepository
{
    /// <summary>Связанные записи (Id + Title) для ячейки поля-связи одной записи, в порядке
    /// Position. Только ВНУТРИБАЗОВЫЕ ребра (<c>TargetDbId=0</c>) — Title резолвится JOIN'ом к
    /// локальным db_records; межбазовые ребра резолвятся отдельно по своим БД (см.
    /// <c>RelationSelectBox.LoadSelected</c>), иначе их Id совпал бы с чужой локальной записью.</summary>
    public List<(int Id, string Title)> GetLinks(int fieldId, int fromRecordId) =>
        (from l in db.DbRecordLinks.AsNoTracking()
         where l.FieldId == fieldId && l.FromRecordId == fromRecordId && l.TargetDbId == 0
         join r in db.DbRecords on l.ToRecordId equals r.Id
         orderby l.Position, l.Id
         select new { r.Id, r.Title })
        .ToList()
        .Select(x => (x.Id, x.Title ?? ""))
        .ToList();

    /// <summary>Связи для НАБОРА записей-источников (горячий путь виртуализированной таблицы:
    /// тянем чипы только видимых строк): fromRecordId → список (Id, Title) в порядке Position.
    /// Только ВНУТРИБАЗОВЫЕ ребра (<c>TargetDbId=0</c>) — как <see cref="GetLinks"/>; межбазовые в
    /// табличный батч-кеш не попадают (резолвятся ячейкой отдельно по своим БД).</summary>
    public Dictionary<int, List<(int Id, string Title)>> GetLinksBatch(int fieldId, IReadOnlyList<int> fromRecordIds)
    {
        var result = new Dictionary<int, List<(int Id, string Title)>>();
        if (fromRecordIds.Count == 0) return result;

        var rows = (from l in db.DbRecordLinks.AsNoTracking()
                    where l.FieldId == fieldId && fromRecordIds.Contains(l.FromRecordId) && l.TargetDbId == 0
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
    /// вызывается на репозитории ЦЕЛЕВОЙ базы (UI берет его через Workspace/реестр по TargetDbId).
    /// Отсутствующие Id (удаленные цели) в словарь не попадают — при ДОСТУПНОЙ базе вызывающий трактует
    /// это как обрыв (чип пропадает → «связи нет»); недоступная база (repo=null) → «(база недоступна)».</summary>
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

    /// <summary>Ребра ячейки в порядке Position: (ToRecordId, TargetDbId). Нужны переносу записей —
    /// чтобы переписать TargetDbId под целевую базу (см. MoveRecordsCore).</summary>
    public List<(int ToRecordId, int TargetDbId)> GetLinkEdges(int fieldId, int fromRecordId) =>
        db.DbRecordLinks.AsNoTracking()
          .Where(l => l.FieldId == fieldId && l.FromRecordId == fromRecordId)
          .OrderBy(l => l.Position).ThenBy(l => l.Id)
          .Select(l => new { l.ToRecordId, l.TargetDbId })
          .ToList()
          .Select(x => (x.ToRecordId, x.TargetDbId))
          .ToList();

    /// <summary>
    /// Перенести исходящие ребра ОДНОЙ записи на ее новую копию в другой коллекции — то же, что
    /// <c>MoveRecordsCore</c> делает при переносе записей из таблицы, но для «пересоздающих» путей
    /// календаря и задач (<see cref="UpdateCalendarRecord"/>, смена БД в окне записи). Зовется сразу
    /// после <see cref="CopyFieldValuesByName"/>: тот копирует только db_field_values, а ребра живут
    /// в db_record_links и без этого вызова терялись молча. <c>this</c> — репозиторий ЦЕЛИ.
    ///
    /// ВАЖНО: правила обязаны совпадать с <c>MoveRecordsCore</c> — ребро переезжает, только если
    /// исходное и целевое поля одноименные, оба «Связь», и конфиг цели смотрит на ту же коллекцию и ту
    /// же (пересчитанную под целевую базу) целевую БД. Иначе ребро указывало бы в чужую коллекцию.
    /// Недостающие в цели поля здесь не создаются (как и у зовущих: createMissingFields=false).
    ///
    /// <paramref name="sourceDbId"/>/<paramref name="targetDbId"/> — реестровые DbId баз. Равны
    /// (в т.ч. 0,0 — перенос внутри одной базы) → TargetDbId ребер не трогаем. Разные и оба валидные →
    /// пересчитываем по формуле MoveRecordsCore. Разные, но хотя бы один неизвестен (≤0) — ребра НЕ
    /// переносим: без DbId внутрибазовое ребро нельзя честно превратить в межбазовое.
    ///
    /// Входящие связи на исходную запись не сохраняются (у копии новый Id) — за это отвечает окно
    /// предупреждения у вызывающего, как и при переносе из таблицы.
    /// </summary>
    public void CopyRelationLinksByName(
        ConstructorRepository source, int sourceRecordId, int sourceCollectionId,
        int targetRecordId, int targetCollectionId, int sourceDbId = 0, int targetDbId = 0)
    {
        var sameBase = sourceDbId == targetDbId;
        if (!sameBase && (sourceDbId <= 0 || targetDbId <= 0)) return;

        // «Абсолютная» БД цели ребра: 0 означает базу ИСТОЧНИКА. В целевой базе она снова 0, если
        // совпала с назначением, иначе — межбазовая на ту же абсолютную БД (формула MoveRecordsCore).
        int RelTargetDb(int linkDb)
        {
            if (sameBase) return linkDb;
            var abs = linkDb == 0 ? sourceDbId : linkDb;
            return abs == targetDbId ? 0 : abs;
        }

        var srcRel = source.GetFields(sourceCollectionId).Where(f => f.FieldType == "relation").ToList();
        if (srcRel.Count == 0) return;

        var targetByName = new Dictionary<string, DbField>(StringComparer.OrdinalIgnoreCase);
        foreach (var tf in GetFields(targetCollectionId))
            if (tf.FieldType == "relation") targetByName.TryAdd(tf.Name, tf);
        if (targetByName.Count == 0) return;

        var rows = new List<DbRecordLinkEntity>();
        foreach (var sf in srcRel)
        {
            if (!targetByName.TryGetValue(sf.Name, out var tf)) continue;
            var sc = RelationConfig.FromConfig(sf.ConfigJson);
            var tc = RelationConfig.FromConfig(tf.ConfigJson);
            if (sc is null || tc is null) continue;
            if (tc.TargetCollectionId != sc.TargetCollectionId || tc.TargetDbId != RelTargetDb(sc.TargetDbId)) continue;

            var edges = source.GetLinkEdges(sf.Id!.Value, sourceRecordId);
            for (var i = 0; i < edges.Count; i++)
                rows.Add(new DbRecordLinkEntity
                {
                    FieldId    = tf.Id!.Value,                 FromRecordId = targetRecordId,
                    ToRecordId = edges[i].ToRecordId,          Position     = (i + 1) * 1000,
                    TargetDbId = RelTargetDb(edges[i].TargetDbId),
                });
        }
        if (rows.Count == 0) return;
        db.DbRecordLinks.AddRange(rows);
        db.SaveChanges();
    }

    /// <summary>
    /// Заменить ВЕСЬ набор связей ячейки на <paramref name="toRecordIds"/> (основной путь записи из
    /// пикера). Порядок сохраняется (Position), дубли/само-ссылки/чужие коллекции отсеиваются, для
    /// single берется максимум одна связь. Разница применяется точечно (лишние — удалить, новые —
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
        // single: новый выбор замещает прежний (SetLinks сам возьмет первую, но нам нужна именно
        // новая связь — значит передаем ровно ее).
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
    /// Переписать конфиги полей-связей ПЕРЕНЕСЕННОЙ (ATTACH) коллекции под целевую базу: копия полей
    /// идет дословным INSERT…SELECT, поэтому TargetDbId/TargetCollectionId остаются от источника —
    /// правим после. TargetDbId по формуле (абсолютная БД цели = TargetDbId==0?sourceDbId; в целевой
    /// базе → 0 если == targetDbId, иначе межбазовая). Для САМО-связи (цель = сама перенесенная
    /// коллекция) — TargetDbId=0 и TargetCollectionId=новый id (цель уехала вместе с полем). Должно
    /// быть согласовано с переписыванием ребер в CopyOneCollectionFromAttached.
    /// </summary>
    private void RewriteMovedRelationConfigs(int newColId, int srcCollectionId, int sourceDbId, int targetDbId)
    {
        if (sourceDbId <= 0 || targetDbId <= 0) return;
        var fields  = db.DbFields.Where(f => f.CollectionId == newColId && f.FieldType == "relation").ToList();
        var changed = false;
        foreach (var f in fields)
        {
            if (RelationConfig.FromConfig(f.ConfigJson) is not { } c) continue;
            int newDb, newCol;
            if (c.TargetDbId == 0 && c.TargetCollectionId == srcCollectionId)   // само-связь на эту коллекцию
            {
                newDb  = 0;
                newCol = newColId;
            }
            else                                                                // внешняя цель (осталась в исходной/третьей БД)
            {
                var abs = c.TargetDbId == 0 ? sourceDbId : c.TargetDbId;
                newDb  = abs == targetDbId ? 0 : abs;
                newCol = c.TargetCollectionId;
            }
            var updated = new RelationConfig(newCol, c.Single, c.Separator, newDb).ToConfigJson();
            if (updated != f.ConfigJson) { f.ConfigJson = updated; changed = true; }
        }
        if (changed) db.SaveChanges();
    }

    /// <summary>
    /// То же для МЕЖБАЗОВОГО переноса ГРУППЫ (ветки целиком). Ребра ветки не копируются (v1), но
    /// конфиги полей-связей приезжают ДОСЛОВНО, и без правки TargetCollectionId источника читался бы
    /// как id ЛОКАЛЬНОЙ коллекции целевой базы — поле молча показывало бы ЧУЖИЕ записи. Разбор целей:
    /// <list type="bullet">
    /// <item>цель — коллекция ТОЙ ЖЕ перенесенной ветки (есть в <paramref name="colMap"/>): уехала
    /// вместе с полем → внутрибазовая на новый id;</item>
    /// <item>внешняя цель: по формуле MoveCollectionIsolated (абсолютная БД цели = TargetDbId==0 ?
    /// sourceDbId; в целевой базе → 0 если == targetDbId, иначе межбазовая);</item>
    /// <item>база источника неизвестна (<paramref name="sourceDbId"/>/<paramref name="targetDbId"/>
    /// ≤ 0 — цель не восстановить): сбрасываем в «не настроено» (TargetCollectionId=0). Лучше пустое
    /// поле, чем ссылка на чужие записи.</item>
    /// </list>
    /// Сами связи ветки при этом все равно пусты — поле лишь показывает верную цель для нового выбора.
    /// </summary>
    private void RewriteMovedGroupRelationConfigs(IReadOnlyDictionary<int, int> colMap, int sourceDbId, int targetDbId)
    {
        if (colMap.Count == 0) return;
        var newColIds = colMap.Values.ToList();
        var fields    = db.DbFields.Where(f => newColIds.Contains(f.CollectionId) && f.FieldType == "relation").ToList();
        var changed   = false;
        foreach (var f in fields)
        {
            if (RelationConfig.FromConfig(f.ConfigJson) is not { } c) continue;
            int newDb, newCol;
            if (c.TargetDbId == 0 && colMap.TryGetValue(c.TargetCollectionId, out var movedCol))
            {
                newDb  = 0;
                newCol = movedCol;
            }
            else if (sourceDbId > 0 && targetDbId > 0)
            {
                var abs = c.TargetDbId == 0 ? sourceDbId : c.TargetDbId;
                newDb  = abs == targetDbId ? 0 : abs;
                newCol = c.TargetCollectionId;
            }
            else
            {
                newDb  = 0;
                newCol = 0;   // «не настроено» — FromConfig вернет null, поле ничего не связывает
            }
            var updated = new RelationConfig(newCol, c.Single, c.Separator, newDb).ToConfigJson();
            if (updated != f.ConfigJson) { f.ConfigJson = updated; changed = true; }
        }
        if (changed) db.SaveChanges();
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

    // ── Наличие ВХОДЯЩИХ связей перед удалением/переносом (окно «связи будут удалены») ──────
    // Удаление/перенос записи/коллекции/группы обрывает связи, которые НА НИХ ссылаются: внутрибазовые
    // (TargetDbId=0) — чистятся сразу; межбазовые (в ДРУГИХ базах, TargetDbId=нашей базы) — лениво.
    // Нужен лишь ФАКТ наличия (не число) — Any/EXISTS обрывается на первом ребре (без полного скана).

    /// <summary>Есть ли ребро, указывающее на записи <paramref name="toRecordIds"/> с данным
    /// <paramref name="targetDbId"/>. Внутрибазовые входящие → зовут на СВОЕЙ базе с targetDbId=0;
    /// межбазовые → на ЧУЖОЙ базе с targetDbId = реестровый DbId удаляемой базы.</summary>
    public bool HasIncomingLinks(IReadOnlyCollection<int> toRecordIds, int targetDbId) =>
        toRecordIds.Count > 0
        && db.DbRecordLinks.Any(l => l.TargetDbId == targetDbId && toRecordIds.Contains(l.ToRecordId));

    /// <summary>Есть ли ВНУТРИбазовое входящее ребро (TargetDbId=0) на записи коллекций
    /// <paramref name="collectionIds"/> — для удаления коллекции/группы (перечислять все id записей
    /// нельзя). EXISTS обрывается на первом совпадении.</summary>
    public bool HasIncomingLinksToCollections(IReadOnlyCollection<int> collectionIds) =>
        collectionIds.Count > 0
        && db.DbRecordLinks.Any(l => l.TargetDbId == 0
            && db.DbRecords.Any(r => r.Id == l.ToRecordId && collectionIds.Contains(r.CollectionId)));

    /// <summary>Есть ли ВНЕШНЕЕ входящее внутрибазовое ребро (TargetDbId=0) на записи коллекции
    /// <paramref name="collectionId"/>: ToRecordId В коллекции, а FromRecordId — НЕ в ней. Для
    /// межбазового переноса ОДНОЙ коллекции: само-связи внутри переписываются (целы), теряются лишь
    /// ссылки ИЗВНЕ.</summary>
    public bool HasExternalIncomingLinksToCollection(int collectionId) =>
        db.DbRecordLinks.Any(l => l.TargetDbId == 0
            && db.DbRecords.Any(r => r.Id == l.ToRecordId   && r.CollectionId == collectionId)
            && !db.DbRecords.Any(r => r.Id == l.FromRecordId && r.CollectionId == collectionId));

    /// <summary>Есть ли у записей коллекций <paramref name="collectionIds"/> ЛЮБЫЕ ребра — ИСХОДЯЩИЕ
    /// (FromRecordId в них) или ВХОДЯЩИЕ внутрибазовые (ToRecordId в них). Для PG-пути переноса,
    /// который ребра не копирует ВООБЩЕ: проверка «только внешние входящие» пропустила бы коллекцию
    /// с одними само-связями, и они исчезли бы молча. EXISTS обрывается на первом ребре.</summary>
    public bool HasAnyLinksInCollections(IReadOnlyCollection<int> collectionIds) =>
        collectionIds.Count > 0
        && (db.DbRecordLinks.Any(l => db.DbRecords.Any(r => r.Id == l.FromRecordId
                                                            && collectionIds.Contains(r.CollectionId)))
            || HasIncomingLinksToCollections(collectionIds));

    /// <summary>Межбазовые цели ЭТОЙ базы в базу <paramref name="targetDbId"/>: recordId (в той базе) →
    /// число ребер. Множество целей межбазовых связей мало (их не заводят миллионами) — вернуть карту
    /// безопасно; вызывающий пересечет ключи с удаляемыми записями/коллекциями.</summary>
    public Dictionary<int, int> IncomingLinkCountsByTarget(int targetDbId) =>
        db.DbRecordLinks.Where(l => l.TargetDbId == targetDbId)
          .GroupBy(l => l.ToRecordId)
          .Select(g => new { g.Key, C = g.Count() })
          .ToList()
          .ToDictionary(x => x.Key, x => x.C);

    /// <summary>Из <paramref name="recordIds"/> оставить те, что принадлежат коллекциям
    /// <paramref name="collectionIds"/> (пересечение межбазовой карты целей с удаляемой
    /// коллекцией/группой).</summary>
    public List<int> RecordIdsInCollections(IReadOnlyCollection<int> recordIds, IReadOnlyCollection<int> collectionIds) =>
        recordIds.Count == 0 || collectionIds.Count == 0 ? []
        : db.DbRecords.Where(r => recordIds.Contains(r.Id) && collectionIds.Contains(r.CollectionId))
                      .Select(r => r.Id).ToList();

    /// <summary>Id коллекций всей ветки группы (сама группа + вложенные) — цель удаления группы.</summary>
    public List<int> CollectionIdsInGroup(int groupId)
    {
        var groupIds = SubtreeGroupIds(groupId);
        return db.DbCollections.Where(c => c.GroupId != null && groupIds.Contains(c.GroupId.Value))
                               .Select(c => c.Id).ToList();
    }
}
