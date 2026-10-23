using Microsoft.EntityFrameworkCore;
using SmartBase.Core.Models;
using SmartBase.Database.Entities;
using SmartBase.Utils;

namespace SmartBase.Database.Repositories;

// ── Связи между записями (тип поля «relation») ──────────────────────────────────
// Ребро «из записи в запись по полю-связи» хранится в db_record_links (DbRecordLinkEntity),
// НЕ в db_field_values. Каскады чистят исходящие ребра при удалении свойства/источника;
// локальные входящие чистит код удаления цели. Показ выбранного свойства разрешается на лету.
// Настройка цели, кардинальности, показа/поиска и автосопоставления — RelationConfig в ConfigJson.
public partial class ConstructorRepository
{
    /// <summary>Совместимая локальная проекция: Id и выбранная подпись (старое имя поля Title),
    /// в порядке ребер. Полный межбазовый набор — <see cref="GetRelationLinks"/>.</summary>
    public List<(int Id, string Title)> GetLinks(int fieldId, int fromRecordId) =>
        GetLinksBatch(fieldId, [fromRecordId]).GetValueOrDefault(fromRecordId, []);

    /// <summary>Пакет совместимой локальной проекции (<c>TargetDbId=0</c>), как GetLinks.
    /// Новая таблица/кеш используют GetRelationLinksBatch с полной парой базы и записи.</summary>
    public Dictionary<int, List<(int Id, string Title)>> GetLinksBatch(int fieldId, IReadOnlyList<int> fromRecordIds)
    {
        var result = new Dictionary<int, List<(int Id, string Title)>>();
        if (fromRecordIds.Count == 0) return result;
        var field = db.DbFields.AsNoTracking().FirstOrDefault(f => f.Id == fieldId);
        if (field is null || !CanReadRelationCollection(field.CollectionId)) return result;
        var cfg = RelationConfig.FromConfig(field.ConfigJson);
        var rows = (from l in db.DbRecordLinks.AsNoTracking()
                    where l.FieldId == fieldId && fromRecordIds.Contains(l.FromRecordId) && l.TargetDbId == 0
                    orderby l.Position, l.Id
                    select new { l.FromRecordId, Id = l.ToRecordId })
                   .ToList();
        var labels = ResolveRelationLabels(rows.Select(r => new RelationTarget(0, r.Id)).Distinct().ToList(),
            cfg?.DisplayField ?? RelationConfig.TitleField, cfg?.TargetDbId);
        foreach (var row in rows)
        {
            if (!labels.TryGetValue(new(0, row.Id), out var label)) continue;
            if (!result.TryGetValue(row.FromRecordId, out var list))
                result[row.FromRecordId] = list = [];
            list.Add((row.Id, label));
        }
        return result;
    }

    /// <summary>Названия записей по их Id (Id → Title). Для резолва названий целей МЕЖБАЗОВОЙ связи:
    /// вызывается на репозитории ЦЕЛЕВОЙ базы (UI берет его через Workspace/реестр по TargetDbId).
    /// Отсутствующие Id (удаленные цели) в словарь не попадают — при ДОСТУПНОЙ базе вызывающий трактует
    /// это как обрыв (чип пропадает → «связи нет»); недоступная база (repo=null) → «(база недоступна)».</summary>
    public Dictionary<int, string> GetRecordTitles(IReadOnlyList<int> ids)
    {
        return GetRecordPropertyValues(ids, RelationConfig.TitleField);
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
        var skipped = new Dictionary<int, SkippedLinks>();
        foreach (var sf in srcRel)
        {
            if (!targetByName.TryGetValue(sf.Name, out var tf)) continue;
            var sc = RelationConfig.FromConfig(sf.ConfigJson);
            var tc = RelationConfig.FromConfig(tf.ConfigJson);
            if (sc is null || tc is null) continue;
            if (tc.TargetCollectionId != sc.TargetCollectionId || tc.TargetDbId != RelTargetDb(sc.TargetDbId)) continue;

            var edges = source.GetLinkEdges(sf.Id!.Value, sourceRecordId);
            if (tc.Single) EnsureSingleRelationMove(source, sf.Id.Value, [sourceRecordId], tf.Name);
            // Запись приемника новая: связи вне условия выбора его свойства она не получает — как
            // дубль, иначе перенос стал бы обходом условия.
            var check = tc.HasCondition ? EvaluateConditionOnTarget(tc, edges
                .Where(e => RelTargetDb(e.TargetDbId) == tc.TargetDbId).Select(e => e.ToRecordId).ToHashSet()) : null;
            var position = 0;
            foreach (var edge in edges)
            {
                var targetDb = RelTargetDb(edge.TargetDbId);
                if (check is not null && targetDb == tc.TargetDbId && !check.Allowed.Contains(edge.ToRecordId))
                {
                    skipped[tf.Id!.Value] = new(skipped.GetValueOrDefault(tf.Id.Value).Count + 1, check.Broken);
                    continue;
                }
                rows.Add(new DbRecordLinkEntity
                {
                    FieldId    = tf.Id!.Value,                 FromRecordId = targetRecordId,
                    ToRecordId = edge.ToRecordId,              Position     = ++position * 1000,
                    TargetDbId = targetDb,
                });
            }
        }
        if (rows.Count > 0)
        {
            db.DbRecordLinks.AddRange(rows);
            db.SaveChanges();
        }
        ReportSkippedLinks(skipped, "Запись перенесена.", "перенесено");
    }

    /// <summary>
    /// Совместимый API численных id: база берется из конфига, single сохраняет старое правило первой цели.
    /// Новый код передает полные пары в SetLinkEdges; он отклоняет несколько целей для single.
    /// Запись применяет точечную разницу, сохраняет порядок и пишет журнал выбранными подписями.
    /// </summary>
    public void SetLinks(int fieldId, int fromRecordId, IReadOnlyList<int> toRecordIds)
    {
        var cfg = RelationConfig.FromConfig(db.DbFields.AsNoTracking().Where(f => f.Id == fieldId)
            .Select(f => f.ConfigJson).FirstOrDefault());
        if (cfg is null) return;
        // Старый API доверял межбазовым id; новые UI/IO используют проверяемые полные цели.
        SetLinkEdgesCore(fieldId, fromRecordId,
            toRecordIds.Select(id => new RelationTarget(cfg.TargetDbId, id)).ToList(), allowUnresolved: true);
    }

    public void SetLinkEdges(int fieldId, int fromRecordId, IReadOnlyList<RelationTarget> targets) =>
        SetLinkEdgesCore(fieldId, fromRecordId, targets, allowUnresolved: false);

    private void SetLinkEdgesCore(int fieldId, int fromRecordId, IReadOnlyList<RelationTarget> targets, bool allowUnresolved)
    {
        var source = db.DbRecords.Find(fromRecordId);
        Demand(PermAction.EditRecord, source?.CollectionId);
        if (source is null) return;
        var field = db.DbFields.AsNoTracking().FirstOrDefault(f => f.Id == fieldId);
        if (field is null || field.FieldType != "relation") return;
        if (field.CollectionId != source.CollectionId)
            throw new InvalidOperationException("Свойство связи не принадлежит коллекции записи.");
        var cfg = RelationConfig.FromConfig(field.ConfigJson);
        if (cfg is null) return;
        if (cfg.Auto is not null)
            throw new InvalidOperationException("Автоматическая связь изменяется по правилу сопоставления.");
        var existing = db.DbRecordLinks.Where(l => l.FieldId == fieldId && l.FromRecordId == fromRecordId).ToList();
        var previous = existing.OrderBy(l => l.Position).ThenBy(l => l.Id)
            .Select(l => new RelationTarget(l.TargetDbId, l.ToRecordId)).ToList();
        var wanted = NormalizeRelationTargets(cfg, fromRecordId, targets, previous, allowUnresolved);
        if (previous.SequenceEqual(wanted)) return;

        var labels = ResolveRelationLabels(previous.Concat(wanted).Distinct().ToList(), cfg.DisplayField, cfg.TargetDbId);
        string Join(IEnumerable<RelationTarget> edges) => string.Join(", ", edges.Select(t =>
            labels.GetValueOrDefault(t, $"#{t.RecordId}")));
        Log("update", "record", source.Title, CollectionNameOf(source.CollectionId),
            detail: ChangeDetail.Value(source.Title, field.Name, Join(previous), Join(wanted)));

        var wantedSet = wanted.ToHashSet();
        var byTarget = new Dictionary<RelationTarget, DbRecordLinkEntity>();
        foreach (var edge in existing)
        {
            var target = new RelationTarget(edge.TargetDbId, edge.ToRecordId);
            if (wantedSet.Contains(target) && byTarget.TryAdd(target, edge)) continue;
            db.DbRecordLinks.Remove(edge);
        }
        for (var i = 0; i < wanted.Count; i++)
        {
            var target = wanted[i];
            if (byTarget.TryGetValue(target, out var edge)) edge.Position = (i + 1) * 1000;
            else db.DbRecordLinks.Add(new DbRecordLinkEntity
            {
                FieldId = fieldId, FromRecordId = fromRecordId, ToRecordId = target.RecordId,
                TargetDbId = target.DbId, Position = (i + 1) * 1000,
            });
        }
        TouchUpdated(source);
        db.SaveChanges();
        NotifyRelationDataChanged(source.CollectionId, fieldId, fromRecordId);
    }

    /// <summary>Общие правила выбора; прежние смешанные/временно недоступные ребра сохраняются по полной паре.</summary>
    /// <summary>Ворота новых связей. Цели из <paramref name="allowedExisting"/> (прежние ребра ячейки,
    /// возврат отмены) проходят без проверок; новые обязаны существовать в целевой коллекции и
    /// подходить под условие выбора. Отклоненные условием: при <paramref name="refused"/> = null —
    /// <see cref="RelationConditionException"/>, иначе они складываются в него (вставка пропускает
    /// ячейку, а не отказывает всей операцией). Сломанное условие и недоступная база цели
    /// закрывают новые связи всегда.</summary>
    internal List<RelationTarget> NormalizeRelationTargets(RelationConfig cfg, int sourceId,
        IReadOnlyList<RelationTarget> targets, IReadOnlyCollection<RelationTarget>? allowedExisting = null,
        bool allowUnresolved = false, RelationConditionRefusals? refused = null)
    {
        var previous = allowedExisting?.ToHashSet() ?? [];
        var wanted = new List<RelationTarget>();
        var seen = new HashSet<RelationTarget>();
        var candidates = targets.Where(t => t.RecordId > 0 && t.DbId >= 0
            && !(t.DbId == 0 && t.RecordId == sourceId)).Distinct().ToList();
        var newTargets = candidates.Where(t => !previous.Contains(t) && t.DbId == cfg.TargetDbId).ToList();
        var targetRepo = cfg.TargetDbId == 0 ? this : RelationDbResolver.RepoFor(cfg.TargetDbId);
        HashSet<int> valid = [];
        if (newTargets.Count > 0)
        {
            if (targetRepo is null)
            {
                // Старый API доверял недоступной базе, но условие без нее не проверить: иначе
                // он стал бы обходом условия.
                if (cfg.HasCondition)
                {
                    const string unavailable = "Целевая база связи недоступна: условие выбора проверить нельзя.";
                    if (refused is null) throw new InvalidOperationException(unavailable);
                    refused.Problem ??= unavailable;
                    newTargets.Clear();
                }
                else if (!allowUnresolved) throw new InvalidOperationException("Целевая база связи недоступна.");
                else valid = newTargets.Select(t => t.RecordId).ToHashSet();
            }
            else
            {
                targetRepo.Demand(PermAction.ViewCollection, cfg.TargetCollectionId);
                var ids = newTargets.Select(t => t.RecordId).ToList();
                valid = targetRepo.ReadRelationPropertyValues(cfg.TargetCollectionId, RelationConfig.TitleField, ids)
                    .Select(r => r.RecordId).ToHashSet();
                if (cfg.HasCondition && valid.Count > 0)
                {
                    var verdict = targetRepo.EvaluateRelationCondition(cfg.TargetCollectionId, cfg, valid);
                    if (verdict.Broken is { } broken)
                    {
                        if (refused is null) throw new RelationConditionException(broken);
                        refused.Problem ??= broken;
                        valid = [];
                    }
                    var outside = valid.Where(id => !verdict.Allowed.Contains(id)).ToList();
                    if (outside.Count > 0)
                    {
                        if (refused is null)
                            throw new RelationConditionException(
                                targetRepo.ConditionRefusal(cfg.TargetCollectionId, outside, verdict.Condition));
                        refused.Outside.AddRange(outside.Select(id => new RelationTarget(cfg.TargetDbId, id)));
                        valid = verdict.Allowed.ToHashSet();
                    }
                }
            }
        }
        foreach (var target in candidates)
            if ((previous.Contains(target) || target.DbId == cfg.TargetDbId && valid.Contains(target.RecordId)) && seen.Add(target))
                wanted.Add(target);
        if (cfg.Single && wanted.Count > 1) return [wanted[0]];
        return wanted;
    }

    /// <summary>Добавить одну связь (для single — заменяет прежнюю). Идемпотентно.</summary>
    public void AddLink(int fieldId, int fromRecordId, int toRecordId)
    {
        // single: новый выбор замещает прежний; передаем ровно новую полную цель.
        var cfg = RelationConfig.FromConfig(
            db.DbFields.AsNoTracking().Where(f => f.Id == fieldId).Select(f => f.ConfigJson).FirstOrDefault());
        if (cfg is null) return;
        var target = new RelationTarget(cfg.TargetDbId, toRecordId);
        var cur = cfg.Single ? new List<RelationTarget>() : GetLinkEdges(fieldId, fromRecordId)
            .Select(e => new RelationTarget(e.TargetDbId, e.ToRecordId)).ToList();
        if (cur.Contains(target)) return;
        cur.Add(target);
        SetLinkEdgesCore(fieldId, fromRecordId, cur, allowUnresolved: true);
    }

    /// <summary>Снять одну связь (крестик на чипе). Нет такой связи — no-op.</summary>
    public void RemoveLink(int fieldId, int fromRecordId, int toRecordId)
    {
        var cur = GetLinkEdges(fieldId, fromRecordId).Select(e => new RelationTarget(e.TargetDbId, e.ToRecordId)).ToList();
        var index = cur.FindIndex(t => t.RecordId == toRecordId);
        if (index < 0) return;
        cur.RemoveAt(index);
        SetLinkEdgesCore(fieldId, fromRecordId, cur, allowUnresolved: true);
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
        if (!CanReadRelationCollection(collectionId)) return [];
        var q = db.DbRecords.AsNoTracking().Where(r => r.CollectionId == collectionId);
        if (excludeRecordId > 0) q = q.Where(r => r.Id != excludeRecordId);
        var term = query?.Trim() ?? "";
        if (term.Length > 0)
        {
            // Без учета регистра, и у кириллицы тоже: на SQLite — своей функцией (встроенный LIKE
            // различает регистр всего, кроме латиницы), на PostgreSQL — ILIKE; так же ищет фильтр
            // колонки. Голый LIKE не находил «Договор» по «договор», на PostgreSQL — и «GoSt» по
            // «gost», а «%» и «_» в запросе работали как подстановка. Цена своей функции на
            // промахе — как у четырех регистровых вариантов LIKE (замер: миллион записей, 1,1 с).
            var pattern = "%" + EscapeLike(term) + "%";
            q = db.IsPostgres
                ? q.Where(r => EF.Functions.ILike(r.Title, pattern, "\\"))
                : q.Where(r => SqlText.ContainsIgnoreCase(r.Title, term));
        }
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
