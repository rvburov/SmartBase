using System.Data.Common;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using DataBase.Core.Importers;
using DataBase.Core.Models;
using DataBase.Database.Entities;

namespace DataBase.Database.Repositories;

public class ConstructorRepository(AppDbContext db)
{
    // Системное поле «Дата» — единственное, что осталось schema-driven.
    // Название и Описание подняты до колонок Title/Description в db_records.
    private const string FieldDate = "Дата";

    /// <summary>Путь к файлу БД этого репозитория — для переноса коллекции в другую БД
    /// (ATTACH источника на подключении целевой БД, см. MoveCollectionIsolated).</summary>
    public string DbPath => db.DbPath;

    // ── Группы ────────────────────────────────────────────────────────────────

    public List<DbGroup> GetAllGroups() =>
        db.DbGroups
          .OrderBy(g => g.Position)
          .Select(g => new DbGroup { Id = g.Id, Name = g.Name, ParentId = g.ParentId, Position = g.Position })
          .ToList();

    public DbGroup AddGroup(DbGroup group)
    {
        var entity = new DbGroupEntity { Name = group.Name, ParentId = group.ParentId, Position = group.Position };
        db.DbGroups.Add(entity);
        db.SaveChanges();
        group.Id = entity.Id;
        return group;
    }

    public void UpdateGroup(DbGroup group)
    {
        var entity = db.DbGroups.Find(group.Id);
        if (entity is null) return;
        entity.Name     = group.Name;
        entity.ParentId = group.ParentId;
        entity.Position = group.Position;
        db.SaveChanges();
    }

    // Существует ли группа с таким именем на том же уровне (с тем же родителем).
    // excludeId — id исключаемой группы (для переименования самой себя).
    public bool GroupNameExists(string name, int? parentId, int? excludeId = null)
    {
        var q = db.DbGroups.Where(g => g.ParentId == parentId && g.Name == name);
        if (excludeId is int id) q = q.Where(g => g.Id != id);
        return q.Any();
    }

    /// <summary>
    /// Полный дубль группы СО ВСЕМ содержимым (подгруппы + коллекции + записи + значения + UI) в той же
    /// БД. Структуру (группы + шеллы коллекций) создаём через EF, данные коллекций — bulk-SQL
    /// (<see cref="CopyCollectionData"/>), как дубль коллекции. Верхняя группа получает имя «X (копия)»,
    /// вложенные элементы — свои имена. <paramref name="progress"/> — нарастающее число скопированных
    /// записей по всем коллекциям ветки (для %). Возвращает id новой верхней группы.
    /// </summary>
    public int DuplicateGroupCore(int groupId, IProgress<int>? progress = null, int batchSize = 10000)
    {
        var src = db.DbGroups.Find(groupId);
        if (src is null) throw new InvalidOperationException($"Group {groupId} not found");

        // 1) Структура: верхняя группа «(копия)» под тем же родителем; подгруппы — со своими именами.
        var groupMap = new Dictionary<int, int>();   // старый id группы → новый
        var newRootId = CloneGroupTree(groupId, src.ParentId, isRoot: true, groupMap);

        // 2) Коллекции ветки: шелл в новую группу + bulk-копия записей/значений (общий % по ветке).
        var subtree = groupMap.Keys.ToList();
        var cols = db.DbCollections
            .Where(c => c.GroupId != null && subtree.Contains(c.GroupId.Value))
            .OrderBy(c => c.GroupId).ThenBy(c => c.Position)
            .Select(c => new { c.Id, c.GroupId })
            .ToList();

        long basis = 0;
        foreach (var c in cols)
        {
            var rc    = db.DbRecords.Count(r => r.CollectionId == c.Id);
            var shell = CreateCollectionShell(c.Id, overrideGroupId: groupMap[c.GroupId!.Value], addCopySuffix: false);
            if (shell.Collection.Id is int newColId)
                CopyCollectionData(c.Id, newColId, new BaseProgress(progress, basis), batchSize);
            basis += rc;
        }
        progress?.Report((int)basis);
        return newRootId;
    }

    /// <summary>Полный дубль группы на ОТДЕЛЬНОМ подключении (фон + прогресс для больших веток) —
    /// не трогает общий Context (см. правило об общем AppDbContext), безопасно из Task.Run.</summary>
    public int DuplicateGroupIsolated(int groupId, IProgress<int>? progress = null, int batchSize = 10000)
    {
        using var ctx = new AppDbContext(db.DbPath);
        ctx.ApplyPerformancePragmas();
        return new ConstructorRepository(ctx).DuplicateGroupCore(groupId, progress, batchSize);
    }

    // Рекурсивно копирует только СТРУКТУРУ групп (без коллекций): новая группа под targetParentId,
    // затем её подгруппы. Заполняет map (старый id → новый). Верхняя получает «(копия)», вложенные — имена как есть.
    private int CloneGroupTree(int srcGroupId, int? targetParentId, bool isRoot, Dictionary<int, int> map)
    {
        var src    = db.DbGroups.Find(srcGroupId)!;
        var maxPos = db.DbGroups.Where(g => g.ParentId == targetParentId).Select(g => (int?)g.Position).Max() ?? 0;
        var name   = isRoot
            ? NextCopyName(src.Name, cand => db.DbGroups.Any(g => g.ParentId == targetParentId && g.Name == cand))
            : src.Name;

        var copy = new DbGroupEntity { Name = name, ParentId = targetParentId, Position = maxPos + 1000 };
        db.DbGroups.Add(copy);
        db.SaveChanges();
        map[srcGroupId] = copy.Id;

        foreach (var childId in db.DbGroups.Where(g => g.ParentId == srcGroupId).OrderBy(g => g.Position)
                     .Select(g => g.Id).ToList())
            CloneGroupTree(childId, copy.Id, false, map);
        return copy.Id;
    }

    public void DeleteGroup(int groupId)
    {
        // Рекурсивно удаляем все вложенные группы.
        var childIds = db.DbGroups.Where(g => g.ParentId == groupId).Select(g => g.Id).ToList();
        foreach (var childId in childIds)
            DeleteGroup(childId);

        // Удаляем коллекции этой группы; поля/записи/значения уйдут каскадно.
        db.DbCollections.RemoveRange(db.DbCollections.Where(c => c.GroupId == groupId));

        var entity = db.DbGroups.Find(groupId);
        if (entity is not null) db.DbGroups.Remove(entity);
        db.SaveChanges();
    }

    // Id всех групп ветки (сама группа + потомки на любой глубине). Один проход по таблице групп.
    private List<int> SubtreeGroupIds(int rootGroupId)
    {
        var byParent = db.DbGroups.Select(g => new { g.Id, g.ParentId }).ToList()
            .Where(g => g.ParentId.HasValue).ToLookup(g => g.ParentId!.Value, g => g.Id);
        var result = new List<int>();
        var stack  = new Stack<int>();
        stack.Push(rootGroupId);
        while (stack.Count > 0)
        {
            var id = stack.Pop();
            result.Add(id);
            foreach (var child in byParent[id]) stack.Push(child);
        }
        return result;
    }

    /// <summary>Число коллекций во всей ветке группы и суммарное число их записей — для индикатора
    /// (дубль/удаление/перенос группы показывают «Коллекций: X · Записей: Y»).</summary>
    public (int Collections, long Records) CountGroupSubtree(int groupId)
    {
        var groupIds = SubtreeGroupIds(groupId);
        var colIds   = db.DbCollections.Where(c => c.GroupId != null && groupIds.Contains(c.GroupId.Value))
                         .Select(c => c.Id).ToList();
        long records = colIds.Count == 0 ? 0 : db.DbRecords.LongCount(r => colIds.Contains(r.CollectionId));
        return (colIds.Count, records);
    }

    /// <summary>Удалить группу со всей веткой на ОТДЕЛЬНОМ подключении (фон + прогресс) — для больших
    /// веток. Не трогает общий Context (см. правило об общем AppDbContext), безопасно из Task.Run.</summary>
    public void DeleteGroupIsolated(int groupId, IProgress<int>? progress = null)
    {
        using var ctx = new AppDbContext(db.DbPath);
        ctx.ApplyPerformancePragmas();
        new ConstructorRepository(ctx).DeleteGroupData(groupId, progress);
    }

    /// <summary>
    /// Удалить группу со всей веткой ПАКЕТАМИ: записи всех коллекций ветки по 10 000 (значения — каскадом
    /// по FK), затем строки коллекций (каскад уберёт поля), затем строки групп ветки. Всё в одной
    /// транзакции. <paramref name="progress"/> — нарастающее число удалённых записей (для %). Каскад
    /// держится на PRAGMA foreign_keys=ON (см. ApplyPerformancePragmas).
    /// </summary>
    public void DeleteGroupData(int groupId, IProgress<int>? progress = null, int batchSize = 10000)
    {
        var groupIds = SubtreeGroupIds(groupId);
        var colIds   = db.DbCollections.Where(c => c.GroupId != null && groupIds.Contains(c.GroupId.Value))
                         .Select(c => c.Id).ToList();
        var total    = colIds.Count == 0 ? 0L : db.DbRecords.LongCount(r => colIds.Contains(r.CollectionId));

        using var tx = db.Database.BeginTransaction();

        long deleted = 0;
        foreach (var cid in colIds)
        {
            int prevId = 0;
            while (true)
            {
                var end = DeleteBatchEndId(cid, prevId, batchSize);
                if (end is int e)
                {
                    db.Database.ExecuteSqlInterpolated(
                        $"DELETE FROM db_records WHERE CollectionId = {cid} AND Id > {prevId} AND Id <= {e}");
                    deleted += batchSize; prevId = e;
                    progress?.Report((int)deleted);
                }
                else
                {
                    db.Database.ExecuteSqlInterpolated(
                        $"DELETE FROM db_records WHERE CollectionId = {cid} AND Id > {prevId}");
                    break;
                }
            }
            db.Database.ExecuteSqlInterpolated($"DELETE FROM db_collections WHERE Id = {cid}");
        }

        // Строки групп ветки (ParentId — SET NULL, не каскад, поэтому удаляем явно весь набор).
        foreach (var gid in groupIds)
            db.Database.ExecuteSqlInterpolated($"DELETE FROM db_groups WHERE Id = {gid}");

        tx.Commit();
        progress?.Report((int)total);   // финально 100% для этой ветки
    }

    // ── Коллекции ─────────────────────────────────────────────────────────────

    public List<DbCollection> GetAllCollections() =>
        db.DbCollections
          .OrderBy(c => c.Position)
          .Select(c => new DbCollection
          {
              Id = c.Id, Name = c.Name, GroupId = c.GroupId,
              Position = c.Position, Icon = c.Icon, Color = c.Color,
          })
          .ToList();

    public List<DbCollection> GetCollectionsByGroup(int? groupId)
    {
        var query = groupId is null
            ? db.DbCollections.Where(c => c.GroupId == null)
            : db.DbCollections.Where(c => c.GroupId == groupId);

        return query.OrderBy(c => c.Position)
                    .Select(c => new DbCollection
                    {
                        Id = c.Id, Name = c.Name, GroupId = c.GroupId,
                        Position = c.Position, Icon = c.Icon, Color = c.Color,
                    })
                    .ToList();
    }

    // Существует ли коллекция с таким именем на том же уровне (в той же группе).
    // excludeId — id исключаемой коллекции (для переименования самой себя).
    public bool CollectionNameExists(string name, int? groupId, int? excludeId = null)
    {
        var q = db.DbCollections.Where(c => c.GroupId == groupId && c.Name == name);
        if (excludeId is int id) q = q.Where(c => c.Id != id);
        return q.Any();
    }

    public DbCollection AddCollection(DbCollection col)
    {
        var entity = new DbCollectionEntity
        {
            Name = col.Name, GroupId = col.GroupId,
            Position = col.Position, Icon = col.Icon, Color = col.Color,
        };
        db.DbCollections.Add(entity);
        db.SaveChanges();
        col.Id = entity.Id;

        // Title и Description теперь являются колонками таблицы db_records.
        // В схеме DbFields их больше не создаём.
        return col;
    }

    // Ключи системных колонок (см. TableRowVm.TitleKey/DescKey). Дублируем строками,
    // чтобы слой БД не зависел от ViewModels.
    private const string TitleKey = "__title__";
    private const string DescKey  = "__desc__";

    /// <summary>
    /// Создать новую коллекцию из распарсенного листа Excel: текстовые свойства по столбцам,
    /// записи со значениями, подпись системной колонки «Название» из заголовка. Колонка
    /// «Описание» создаётся пустой и ставится в конец (данные в неё не импортируются).
    /// Всё в одной транзакции. Возвращает id созданной коллекции.
    /// </summary>
    public int ImportExcelCollection(string collectionName, int? groupId, ExcelImportData data,
                                     IProgress<double>? progress = null)
    {
        using var tx = db.Database.BeginTransaction();

        var maxPos = db.DbCollections.Select(c => (int?)c.Position).Max() ?? 0;
        var col = new DbCollectionEntity
        {
            Name = collectionName, GroupId = groupId, Position = maxPos + 1000,
            Icon = "table", Color = "#007AFF",
        };
        db.DbCollections.Add(col);
        db.SaveChanges();

        // Свойства по столбцам — текстовые, в порядке столбцов. Имена уже уникальны.
        var fields = data.FieldNames
            .Select((name, i) => new DbFieldEntity
            {
                CollectionId = col.Id, Name = name, FieldType = "text", Position = (i + 1) * 1000,
            })
            .ToList();
        db.DbFields.AddRange(fields);
        db.SaveChanges();
        var fieldIds = fields.Select(f => f.Id).ToList();

        // Подпись колонки «Название» из заголовка (пусто/дефолт → имя по умолчанию).
        SetSystemFieldName(col.Id, TitleKey, data.TitleHeader, "Название");

        // Порядок колонок: «Название», затем свойства, «Описание» — в конце.
        var order = new List<string> { TitleKey };
        order.AddRange(fieldIds.Select(id => id.ToString()));
        order.Add(DescKey);
        SaveColumnOrder(col.Id, order);

        // Записи + значения свойств (Описание не импортируется). Пишем СЫРЫМ ADO.NET через
        // подготовленные команды, а НЕ через EF: EF на каждую из миллионов сущностей тратит
        // построение графа и чтение сгенерированного Id — импорт 425k×N колонок деградирует до
        // минут (≈60× медленнее сырого пути). Подготовленные INSERT в одной транзакции —
        // секунды (как bulk-копия дубля). Мы единственный писатель в транзакции → id новых
        // записей идут непрерывным блоком, поэтому RecordId значения = firstNew + индекс строки.
        var now   = DateTime.Now.ToString("o");
        var total = Math.Max(1, data.Rows.Count);
        var conn  = db.Database.GetDbConnection();
        var dbTx  = db.Database.CurrentTransaction!.GetDbTransaction();

        long firstNew = 0;
        using (var insRec = conn.CreateCommand())
        {
            insRec.Transaction = dbTx;
            insRec.CommandText =
                "INSERT INTO db_records (CollectionId, CreatedAt, Position, AllDay, Title, Description) " +
                "VALUES ($cid, $created, $pos, 0, $title, NULL)";
            var pCid     = AddParam(insRec, "$cid");
            var pCreated = AddParam(insRec, "$created");
            var pPos     = AddParam(insRec, "$pos");
            var pTitle   = AddParam(insRec, "$title");
            pCid.Value     = col.Id;
            pCreated.Value = now;
            insRec.Prepare();

            for (var idx = 0; idx < data.Rows.Count; idx++)
            {
                pPos.Value   = (idx + 1) * 1000;
                pTitle.Value = data.Rows[idx].Title ?? "";
                insRec.ExecuteNonQuery();
                if (progress is not null && idx % 2000 == 0) progress.Report((idx + 1) / (double)total * 0.3);
            }
            if (data.Rows.Count > 0) firstNew = LastInsertRowid() - data.Rows.Count + 1;
        }

        using (var insVal = conn.CreateCommand())
        {
            insVal.Transaction = dbTx;
            insVal.CommandText = "INSERT INTO db_field_values (RecordId, FieldId, Value) VALUES ($rid, $fid, $val)";
            var pRid = AddParam(insVal, "$rid");
            var pFid = AddParam(insVal, "$fid");
            var pVal = AddParam(insVal, "$val");
            insVal.Prepare();

            for (var idx = 0; idx < data.Rows.Count; idx++)
            {
                var row = data.Rows[idx];
                pRid.Value = firstNew + idx;
                for (var i = 0; i < fieldIds.Count; i++)
                {
                    pFid.Value = fieldIds[i];
                    pVal.Value = (i < row.FieldValues.Count ? row.FieldValues[i] : "") ?? "";
                    insVal.ExecuteNonQuery();
                }
                if (progress is not null && idx % 2000 == 0) progress.Report(0.3 + (idx + 1) / (double)total * 0.7);
            }
        }
        progress?.Report(1.0);

        tx.Commit();
        return col.Id;
    }

    public void UpdateCollection(DbCollection col)
    {
        var entity = db.DbCollections.Find(col.Id);
        if (entity is null) return;
        entity.Name     = col.Name;
        entity.GroupId  = col.GroupId;
        entity.Position = col.Position;
        entity.Icon     = col.Icon;
        entity.Color    = col.Color;
        db.SaveChanges();
    }

    public void DeleteCollection(int collectionId)
    {
        var entity = db.DbCollections.Find(collectionId);
        if (entity is not null) db.DbCollections.Remove(entity);
        db.SaveChanges();
    }

    /// <summary>
    /// Удалить коллекцию на ОТДЕЛЬНОМ подключении (фон + прогресс) — для больших коллекций.
    /// Не трогает общий Context (см. правило об общем AppDbContext), поэтому безопасно из Task.Run.
    /// </summary>
    public void DeleteCollectionIsolated(int collectionId, IProgress<int>? progress = null)
    {
        using var ctx = new AppDbContext(db.DbPath);
        ctx.ApplyPerformancePragmas();
        new ConstructorRepository(ctx).DeleteCollectionData(collectionId, progress);
    }

    /// <summary>
    /// Удалить записи коллекции ПАКЕТАМИ (значения уходят каскадом по FK ON DELETE CASCADE — его
    /// включает провайдер EF), затем строку коллекции (каскад уберёт поля и UI-настройки). Всё в
    /// одной транзакции. Пакеты — только ради гранулярности прогресса (<paramref name="progress"/> —
    /// нарастающее число удалённых записей); по SQL это та же работа, что один каскадный DELETE.
    /// </summary>
    public void DeleteCollectionData(int collectionId, IProgress<int>? progress = null, int batchSize = 10000)
    {
        var recCount = db.DbRecords.Count(r => r.CollectionId == collectionId);

        using var tx = db.Database.BeginTransaction();

        // Записи пакетами по Id (порядок не важен): DELETE по диапазону каскадит на значения.
        int prevId = 0, deleted = 0;
        while (true)
        {
            var end = DeleteBatchEndId(collectionId, prevId, batchSize);
            if (end is int e)
            {
                db.Database.ExecuteSqlInterpolated(
                    $"DELETE FROM db_records WHERE CollectionId = {collectionId} AND Id > {prevId} AND Id <= {e}");
                deleted += batchSize; prevId = e;
                progress?.Report(deleted);
            }
            else
            {
                // Последний (неполный) пакет: без верхней границы.
                db.Database.ExecuteSqlInterpolated(
                    $"DELETE FROM db_records WHERE CollectionId = {collectionId} AND Id > {prevId}");
                break;
            }
        }

        // Строка коллекции: каскад уберёт поля (и UI-настройки, если их FK каскадные).
        db.Database.ExecuteSqlInterpolated($"DELETE FROM db_collections WHERE Id = {collectionId}");
        tx.Commit();
        progress?.Report(recCount);   // финально 100% для этой коллекции
    }

    // Id записи на позиции offset−1 в срезе коллекции после prevId (порядок Id) — граница пакета.
    // null, если записей меньше (последний неполный пакет). Параметры — int, не строки.
    private int? DeleteBatchEndId(int collectionId, int prevId, int offset)
    {
        using var c = db.Database.GetDbConnection().CreateCommand();
        c.Transaction = db.Database.CurrentTransaction!.GetDbTransaction();
        c.CommandText = $@"SELECT Id FROM db_records
            WHERE CollectionId = {collectionId} AND Id > {prevId}
            ORDER BY Id LIMIT 1 OFFSET {offset - 1}";
        using var rdr = c.ExecuteReader();
        return rdr.Read() ? rdr.GetInt32(0) : null;
    }

    // «Скелет» дубля: новая коллекция + число записей источника (для строк-заглушек таблицы),
    // пока сами записи/значения копируются отдельно (см. CopyCollectionData).
    public sealed record CollectionShell(DbCollection Collection, int SourceRecordCount);

    public DbCollection DuplicateCollection(int collectionId)
    {
        var shell = CreateCollectionShell(collectionId);
        if (shell.Collection.Id is int newId)
            CopyCollectionData(collectionId, newId);
        return shell.Collection;
    }

    /// <summary>
    /// Создать «скелет» дубля коллекции: строка коллекции + копии полей (колонок), без записей.
    /// Записи/значения копирует затем <see cref="CopyCollectionData"/> (sync) или
    /// <see cref="DuplicateCollectionIsolated"/> (фон + прогресс). Возвращает новую коллекцию
    /// и число записей источника.
    /// </summary>
    /// <param name="overrideGroupId">Если задан — коллекция-копия кладётся в ЭТУ группу (для дубля
    /// группы: копии коллекций идут в новые подгруппы), иначе в группу источника.</param>
    /// <param name="addCopySuffix">true — имя «X (копия)» (дубль одной коллекции); false — имя как у
    /// источника (дубль группы: суффикс «(копия)» получает только верхняя группа, вложенное — нет).</param>
    public CollectionShell CreateCollectionShell(int collectionId, int? overrideGroupId = null, bool addCopySuffix = true)
    {
        var src = db.DbCollections.Find(collectionId);
        if (src is null) throw new InvalidOperationException($"Collection {collectionId} not found");

        var maxPos     = db.DbCollections.Select(c => (int?)c.Position).Max() ?? 0;
        var targetGroup = overrideGroupId ?? src.GroupId;

        // Уникальное имя на целевом уровне: «X (копия)», «X (копия) 2» … (без стакания «копия»).
        // При дубле группы (addCopySuffix=false) имя коллекции сохраняем — новая подгруппа пуста, конфликта нет.
        var name = addCopySuffix
            ? NextCopyName(src.Name, candidate => db.DbCollections.Any(c => c.GroupId == targetGroup && c.Name == candidate))
            : src.Name;

        var copy = new DbCollectionEntity
        {
            Name     = name,
            GroupId  = targetGroup,
            Position = maxPos + 1000,
            Icon     = src.Icon,
            Color    = src.Color,
        };
        db.DbCollections.Add(copy);
        db.SaveChanges();

        // Поля (колонки) немногочисленны — копируем через EF в порядке Id; этот же порядок
        // позволит сопоставить старые и новые поля при копировании значений (CopyCollectionData).
        var fields = db.DbFields.AsNoTracking()
            .Where(f => f.CollectionId == collectionId)
            .OrderBy(f => f.Id)
            .ToList();
        foreach (var f in fields)
        {
            db.DbFields.Add(new DbFieldEntity
            {
                CollectionId = copy.Id, Name = f.Name, FieldType = f.FieldType,
                ConfigJson = f.ConfigJson, Position = f.Position,
                IsCalStart = f.IsCalStart, IsCalEnd = f.IsCalEnd,
                IsSystem = f.IsSystem, IsRequired = f.IsRequired,
            });
        }
        db.SaveChanges();

        var recCount = db.DbRecords.Count(r => r.CollectionId == collectionId);
        var dto = new DbCollection
        {
            Id = copy.Id, Name = copy.Name, GroupId = copy.GroupId,
            Position = copy.Position, Icon = copy.Icon, Color = copy.Color,
        };
        return new CollectionShell(dto, recCount);
    }

    /// <summary>
    /// Скопировать записи и значения из исходной коллекции в уже созданный «скелет» (newCollectionId).
    /// Только bulk-SQL без материализации в EF. Карта полей old→new — по Position (скелет копирует
    /// Position дословно). Всё в ОДНОЙ транзакции (атомарно, без частичной копии при ошибке):
    ///  1) ВСЕ записи одним `INSERT … SELECT … ORDER BY Position, Id` (id новых записей — непрерывный
    ///     блок в этом порядке: firstNew = last_insert_rowid − recCount + 1);
    ///  2) значения — КРУПНЫМИ пакетами (по batchSize записей), но ВСЕ поля за один `INSERT` через
    ///     временную карту полей `_dup_fmap` (а не по одному полю с `ROW_NUMBER` на каждом — это было
    ///     в ~3.4× медленнее). Пакеты нужны лишь для гранулярности прогресса (окно с %); внутри
    ///     пакета новый RecordId = (firstNew + processed) + локальный ранг − 1.
    /// <paramref name="progress"/> — нарастающее число скопированных записей (для процентов в окне).
    /// </summary>
    public void CopyCollectionData(int srcCollectionId, int newCollectionId,
                                   IProgress<int>? progress = null, int batchSize = 10000)
    {
        var recCount = db.DbRecords.Count(r => r.CollectionId == srcCollectionId);
        if (recCount == 0) { progress?.Report(0); return; }

        // old FieldId → new FieldId по Position: скелет копирует Position полей дословно, а Position
        // уникален в пределах коллекции — надёжнее, чем полагаться на порядок вставки/нумерации Id.
        var oldFields = db.DbFields.AsNoTracking().Where(f => f.CollectionId == srcCollectionId)
            .Select(f => new { f.Id, f.Position }).ToList();
        var newByPos = new Dictionary<int, int>();
        foreach (var nf in db.DbFields.AsNoTracking().Where(f => f.CollectionId == newCollectionId)
                     .Select(f => new { f.Id, f.Position }))
            newByPos[nf.Position] = nf.Id;
        var fieldMap = new Dictionary<int, int>(oldFields.Count);
        foreach (var of in oldFields)
            if (newByPos.TryGetValue(of.Position, out var newFid)) fieldMap[of.Id] = newFid;

        using var tx = db.Database.BeginTransaction();
        var createdAt = DateTime.Now.ToString("o");

        // 1) Все записи одним INSERT … SELECT в порядке (Position, Id). Новые id — непрерывный блок
        //    в этом порядке (SQLite присваивает rowid по порядку SELECT, мы единственный писатель).
        db.Database.ExecuteSqlInterpolated($@"
            INSERT INTO db_records (CollectionId, CreatedAt, Position, StartDt, EndDt, AllDay, Title, Description)
            SELECT {newCollectionId}, {createdAt}, Position, StartDt, EndDt, AllDay, Title, Description
            FROM db_records WHERE CollectionId = {srcCollectionId} ORDER BY Position, Id");
        var firstNew = LastInsertRowid() - recCount + 1;

        // Временная карта полей (old_fid → new_fid) — чтобы копировать значения ВСЕХ полей за один
        // INSERT (JOIN _dup_fmap), а не по одному полю. Живёт на этом подключении; пересоздаём.
        db.Database.ExecuteSqlRaw("DROP TABLE IF EXISTS temp._dup_fmap");
        db.Database.ExecuteSqlRaw("CREATE TEMP TABLE _dup_fmap(old_fid INTEGER PRIMARY KEY, new_fid INTEGER)");
        foreach (var (oldFid, newFid) in fieldMap)
            db.Database.ExecuteSqlInterpolated($"INSERT INTO _dup_fmap(old_fid, new_fid) VALUES ({oldFid}, {newFid})");

        // 2) Значения крупными пакетами по (Position, Id). На каждый пакет — один INSERT по всем полям.
        int prevPos = int.MinValue, prevId = 0, processed = 0;
        while (processed < recCount)
        {
            var bbase = firstNew + processed;   // новый RecordId первой записи пакета = bbase
            var end   = BatchEndCursor(srcCollectionId, prevPos, prevId, batchSize);
            if (end is { } e)
            {
                db.Database.ExecuteSqlInterpolated($@"
                    INSERT INTO db_field_values (RecordId, FieldId, Value)
                    SELECT {bbase} + r.rn - 1, fm.new_fid, fv.Value
                    FROM db_field_values fv
                    JOIN (SELECT Id AS old_id, ROW_NUMBER() OVER (ORDER BY Position, Id) AS rn
                          FROM db_records
                          WHERE CollectionId = {srcCollectionId}
                            AND (Position > {prevPos} OR (Position = {prevPos} AND Id > {prevId}))
                            AND (Position < {e.Pos} OR (Position = {e.Pos} AND Id <= {e.Id}))) r
                      ON r.old_id = fv.RecordId
                    JOIN _dup_fmap fm ON fm.old_fid = fv.FieldId");
                processed += batchSize;
                prevPos = e.Pos; prevId = e.Id;
            }
            else
            {
                // Последний (неполный) пакет: без верхней границы.
                db.Database.ExecuteSqlInterpolated($@"
                    INSERT INTO db_field_values (RecordId, FieldId, Value)
                    SELECT {bbase} + r.rn - 1, fm.new_fid, fv.Value
                    FROM db_field_values fv
                    JOIN (SELECT Id AS old_id, ROW_NUMBER() OVER (ORDER BY Position, Id) AS rn
                          FROM db_records
                          WHERE CollectionId = {srcCollectionId}
                            AND (Position > {prevPos} OR (Position = {prevPos} AND Id > {prevId}))) r
                      ON r.old_id = fv.RecordId
                    JOIN _dup_fmap fm ON fm.old_fid = fv.FieldId");
                processed = recCount;
            }
            progress?.Report(Math.Min(processed, recCount));
        }

        db.Database.ExecuteSqlRaw("DROP TABLE IF EXISTS temp._dup_fmap");
        tx.Commit();
    }

    // id последней вставленной строки на текущем подключении (в открытой транзакции).
    private long LastInsertRowid()
    {
        using var c = db.Database.GetDbConnection().CreateCommand();
        c.Transaction = db.Database.CurrentTransaction!.GetDbTransaction();
        c.CommandText = "SELECT last_insert_rowid()";
        return (long)c.ExecuteScalar()!;
    }

    // Именованный параметр для сырых ADO.NET-команд (bulk-импорт). Имя — как плейсхолдер ($name).
    private static System.Data.Common.DbParameter AddParam(System.Data.Common.DbCommand cmd, string name)
    {
        var p = cmd.CreateParameter();
        p.ParameterName = name;
        cmd.Parameters.Add(p);
        return p;
    }

    // (Position, Id) записи на позиции offset−1 в keyset-срезе источника после курсора — т.е.
    // граница пакета из batchSize записей. null, если записей меньше (последний неполный пакет).
    // Параметры — int, не строки.
    private (int Pos, int Id)? BatchEndCursor(int srcCollectionId, int prevPos, int prevId, int offset)
    {
        using var c = db.Database.GetDbConnection().CreateCommand();
        c.Transaction = db.Database.CurrentTransaction!.GetDbTransaction();
        c.CommandText = $@"
            SELECT Position, Id FROM db_records
            WHERE CollectionId = {srcCollectionId}
              AND (Position > {prevPos} OR (Position = {prevPos} AND Id > {prevId}))
            ORDER BY Position, Id
            LIMIT 1 OFFSET {offset - 1}";
        using var rdr = c.ExecuteReader();
        return rdr.Read() ? (rdr.GetInt32(0), rdr.GetInt32(1)) : null;
    }

    /// <summary>
    /// Полный дубль коллекции (коллекция + колонки + записи + значения) на ОТДЕЛЬНОМ подключении.
    /// Для фонового дублирования больших коллекций с окном прогресса: НЕ трогает общий Context
    /// (см. правило об общем AppDbContext — фоновая работа только на свежем подключении), поэтому
    /// безопасно вызывать из Task.Run. <paramref name="progress"/> сообщает нарастающее число
    /// скопированных записей (для процентов в окне). Возвращает id новой коллекции (0 при ошибке).
    /// </summary>
    public int DuplicateCollectionIsolated(int srcCollectionId, IProgress<int>? progress = null)
    {
        using var ctx = new AppDbContext(db.DbPath);
        ctx.ApplyPerformancePragmas();
        var repo  = new ConstructorRepository(ctx);
        var shell = repo.CreateCollectionShell(srcCollectionId);
        if (shell.Collection.Id is not int newId) return 0;
        repo.CopyCollectionData(srcCollectionId, newId, progress);
        return newId;
    }

    /// <summary>
    /// Импорт Excel-листа в новую коллекцию на ОТДЕЛЬНОМ подключении (не общий <c>Context</c>).
    /// Вызывается уже с фонового потока (после `ExcelImporter.Parse`), поэтому работает синхронно.
    /// Главное — НЕ трогать общий контекст с фонового потока: иначе параллельное обращение к нему
    /// из UI (дерево/календарь) или фоновое копирование дубля валит EF («A second operation was
    /// started on this context instance»). Свежий контекст + WAL + busy_timeout делают импорт
    /// безопасным рядом с любыми другими операциями над той же БД.
    /// </summary>
    public int ImportExcelCollectionIsolated(string collectionName, int? groupId, ExcelImportData data,
                                             IProgress<double>? progress = null)
    {
        using var ctx = new AppDbContext(db.DbPath);
        ctx.ApplyPerformancePragmas();   // в т.ч. mmap_size — без него вставка в глобальные индексы db_field_values деградирует
        return new ConstructorRepository(ctx).ImportExcelCollection(collectionName, groupId, data, progress);
    }

    // ── Перенос между БД (мульти-маунт) ───────────────────────────────────────
    //
    // Из-за изоляции id (DbId, Id) коллекция/группа физически живут в своём .db-файле,
    // поэтому «перенос в другую БД» = глубокая копия снимка в целевой репозиторий (с
    // перенумерацией id) + удаление из исходного. Переносится всё: поля, записи, значения
    // и UI-настройки коллекции (ширины/порядок/видимость колонок, высоты строк, отображение
    // записей, подписи системных колонок) — ключи-id (поле/запись) перемэппливаются на новые.

    // Снимок коллекции: метаданные + поля + записи со значениями + UI-настройки.
    // Ключи значений и UI — СТАРЫЕ id поля/записи; при импорте перемэппливаются.
    public sealed record CollectionSnapshot(DbCollection Collection, List<DbField> Fields,
                                            List<RecordSnapshot> Records, UiSnapshot Ui);
    public sealed record RecordSnapshot(DbRecord Record, Dictionary<int, string> ValuesByOldFieldId);

    // UI-настройки коллекции для переноса (отвязанные от контекста-источника значения).
    public sealed record UiSnapshot(
        List<(int FieldId, int Width)>                        ColWidths,
        List<(int RecordId, int Height)>                      RowHeights,
        (bool ColResize, bool RowResize, string ViewMode)?    Settings,
        List<(string Key, string? DisplayName, int Position)> SystemMeta,
        List<(string Key, int Position)>                      ColumnOrder,
        List<(string Key, bool Visible, int Level)>           ColumnDisplay,
        List<(int RecordId, bool Visible, int Level)>         RecordDisplay);

    // Снимок группы со всей вложенной веткой (подгруппы + коллекции).
    public sealed record GroupSnapshot(DbGroup Group, List<GroupSnapshot> Subgroups, List<CollectionSnapshot> Collections);

    /// <summary>Снять коллекцию (с полями, записями и значениями) для переноса в другую БД.</summary>
    public CollectionSnapshot ExportCollection(int collectionId)
    {
        var col     = GetAllCollections().First(c => c.Id == collectionId);
        var fields  = GetFields(collectionId);
        var records = GetRecords(collectionId);
        var byRec   = GetFieldValuesBatch(records.Where(r => r.Id.HasValue).Select(r => r.Id!.Value).ToList());

        var recSnaps = new List<RecordSnapshot>(records.Count);
        foreach (var r in records)
        {
            Dictionary<int, string> vals = new();
            if (r.Id is int rid && byRec.TryGetValue(rid, out var v)) vals = v;
            recSnaps.Add(new RecordSnapshot(r, vals));
        }

        return new CollectionSnapshot(col, fields, recSnaps, ExportUi(collectionId));
    }

    // Снять UI-настройки коллекции в отвязанные значения (AsNoTracking — детач от источника).
    private UiSnapshot ExportUi(int collectionId)
    {
        var settings = db.UiCollectionSettings.AsNoTracking().FirstOrDefault(s => s.CollectionId == collectionId);
        return new UiSnapshot(
            ColWidths:     db.UiColWidths.AsNoTracking().Where(w => w.CollectionId == collectionId).ToList()
                             .Select(w => (w.FieldId, w.Width)).ToList(),
            RowHeights:    db.UiRowHeights.AsNoTracking().Where(h => h.CollectionId == collectionId).ToList()
                             .Select(h => (h.RecordId, h.Height)).ToList(),
            Settings:      settings is null ? null : (settings.ColResize, settings.RowResize, settings.ViewMode),
            SystemMeta:    db.SystemFieldMeta.AsNoTracking().Where(m => m.CollectionId == collectionId).ToList()
                             .Select(m => (m.Key, m.DisplayName, m.Position)).ToList(),
            ColumnOrder:   db.ColumnOrder.AsNoTracking().Where(o => o.CollectionId == collectionId).ToList()
                             .Select(o => (o.Key, o.Position)).ToList(),
            ColumnDisplay: db.ColumnDisplay.AsNoTracking().Where(d => d.CollectionId == collectionId).ToList()
                             .Select(d => (d.Key, d.Visible, d.Level)).ToList(),
            RecordDisplay: db.RecordDisplay.AsNoTracking().Where(d => d.CollectionId == collectionId).ToList()
                             .Select(d => (d.RecordId, d.Visible, d.Level)).ToList());
    }

    /// <summary>Импортировать снимок коллекции под группу targetGroupId — атомарно (одна транзакция). Возвращает новую коллекцию.</summary>
    public DbCollection ImportCollection(CollectionSnapshot snap, int? targetGroupId)
    {
        using var tx = db.Database.BeginTransaction();
        var result = ImportCollectionCore(snap, targetGroupId);
        tx.Commit();
        return result;
    }

    private DbCollection ImportCollectionCore(CollectionSnapshot snap, int? targetGroupId)
    {
        var maxPos = db.DbCollections.Select(c => (int?)c.Position).Max() ?? 0;
        var copy = new DbCollectionEntity
        {
            Name = snap.Collection.Name, GroupId = targetGroupId,
            Position = maxPos + 1000, Icon = snap.Collection.Icon, Color = snap.Collection.Color,
        };
        db.DbCollections.Add(copy);
        db.SaveChanges();

        var fieldMap = new Dictionary<int, int>();   // старый id поля → новый
        foreach (var f in snap.Fields)
        {
            var nf = new DbFieldEntity
            {
                CollectionId = copy.Id, Name = f.Name, FieldType = f.FieldType,
                ConfigJson = f.ConfigJson, Position = f.Position,
                IsCalStart = f.IsCalStart, IsCalEnd = f.IsCalEnd,
                IsSystem = f.IsSystem, IsRequired = f.IsRequired,
            };
            db.DbFields.Add(nf);
            db.SaveChanges();
            if (f.Id is int oldFid) fieldMap[oldFid] = nf.Id;
        }

        var recordMap = new Dictionary<int, int>();   // старый id записи → новый
        foreach (var rs in snap.Records)
        {
            var r = rs.Record;
            var nr = new DbRecordEntity
            {
                CollectionId = copy.Id, CreatedAt = r.CreatedAt ?? DateTime.Now.ToString("o"),
                Position = r.Position, StartDt = r.StartDt, EndDt = r.EndDt,
                AllDay = r.AllDay, Title = r.Title, Description = r.Description,
            };
            db.DbRecords.Add(nr);
            db.SaveChanges();
            if (r.Id is int oldRid) recordMap[oldRid] = nr.Id;

            foreach (var (oldFid, val) in rs.ValuesByOldFieldId)
                if (fieldMap.TryGetValue(oldFid, out var nfid))
                    db.DbFieldValues.Add(new DbFieldValueEntity { RecordId = nr.Id, FieldId = nfid, Value = val });
            db.SaveChanges();
        }

        ImportUi(snap.Ui, copy.Id, fieldMap, recordMap);

        return new DbCollection
        {
            Id = copy.Id, Name = copy.Name, GroupId = copy.GroupId,
            Position = copy.Position, Icon = copy.Icon, Color = copy.Color,
        };
    }

    // Восстановить UI-настройки в новой коллекции: id полей/записей — через карты,
    // системные ключи (__title__/__desc__) и настройки коллекции — как есть.
    private void ImportUi(UiSnapshot ui, int newCollectionId,
                          Dictionary<int, int> fieldMap, Dictionary<int, int> recordMap)
    {
        foreach (var (oldFid, width) in ui.ColWidths)
            if (fieldMap.TryGetValue(oldFid, out var nfid))
                db.UiColWidths.Add(new UiColWidthEntity { CollectionId = newCollectionId, FieldId = nfid, Width = width });

        foreach (var (oldRid, height) in ui.RowHeights)
            if (recordMap.TryGetValue(oldRid, out var nrid))
                db.UiRowHeights.Add(new UiRowHeightEntity { CollectionId = newCollectionId, RecordId = nrid, Height = height });

        if (ui.Settings is { } st)
            db.UiCollectionSettings.Add(new UiCollectionSettingEntity
            {
                CollectionId = newCollectionId, ColResize = st.ColResize, RowResize = st.RowResize, ViewMode = st.ViewMode,
            });

        foreach (var (key, name, pos) in ui.SystemMeta)
            db.SystemFieldMeta.Add(new SystemFieldMetaEntity
            {
                CollectionId = newCollectionId, Key = key, DisplayName = name, Position = pos,
            });

        foreach (var (key, pos) in ui.ColumnOrder)
            db.ColumnOrder.Add(new ColumnOrderEntity
            {
                CollectionId = newCollectionId, Key = RemapColKey(key, fieldMap), Position = pos,
            });

        foreach (var (key, vis, lvl) in ui.ColumnDisplay)
            db.ColumnDisplay.Add(new ColumnDisplayEntity
            {
                CollectionId = newCollectionId, Key = RemapColKey(key, fieldMap), Visible = vis, Level = lvl,
            });

        foreach (var (oldRid, vis, lvl) in ui.RecordDisplay)
            if (recordMap.TryGetValue(oldRid, out var nrid))
                db.RecordDisplay.Add(new RecordDisplayEntity
                {
                    CollectionId = newCollectionId, RecordId = nrid, Visible = vis, Level = lvl,
                });

        db.SaveChanges();
    }

    // Ключ колонки: системные (__title__/__desc__) оставляем как есть; числовой id поля — на новый.
    private static string RemapColKey(string key, Dictionary<int, int> fieldMap) =>
        int.TryParse(key, out var oldFid) && fieldMap.TryGetValue(oldFid, out var nfid) ? nfid.ToString() : key;

    /// <summary>Снять группу со всей вложенной веткой (подгруппы + коллекции) для переноса в другую БД.</summary>
    public GroupSnapshot ExportGroup(int groupId)
    {
        var groups = GetAllGroups();
        var g      = groups.First(x => x.Id == groupId);
        var subs   = groups.Where(x => x.ParentId == groupId)
                           .Select(x => ExportGroup(x.Id!.Value)).ToList();
        var cols   = GetCollectionsByGroup(groupId)
                           .Select(c => ExportCollection(c.Id!.Value)).ToList();
        return new GroupSnapshot(g, subs, cols);
    }

    /// <summary>Импортировать снимок группы (с веткой) под targetParentId — атомарно (одна транзакция). Возвращает новую группу.</summary>
    public DbGroup ImportGroup(GroupSnapshot snap, int? targetParentId)
    {
        using var tx = db.Database.BeginTransaction();
        var result = ImportGroupCore(snap, targetParentId);
        tx.Commit();
        return result;
    }

    private DbGroup ImportGroupCore(GroupSnapshot snap, int? targetParentId)
    {
        var maxPos = db.DbGroups.Where(x => x.ParentId == targetParentId).Select(x => (int?)x.Position).Max() ?? 0;
        var copy = new DbGroupEntity { Name = snap.Group.Name, ParentId = targetParentId, Position = maxPos + 1000 };
        db.DbGroups.Add(copy);
        db.SaveChanges();

        // Внутри уже открытой транзакции — зовём *Core (без вложенной транзакции).
        foreach (var sub in snap.Subgroups)   ImportGroupCore(sub, copy.Id);
        foreach (var col in snap.Collections) ImportCollectionCore(col, copy.Id);

        return new DbGroup { Id = copy.Id, Name = copy.Name, ParentId = copy.ParentId, Position = copy.Position };
    }

    // ── Перенос целиком: export → import (в транзакции) → delete; откат копии при сбое ──
    //
    // import уже атомарен (транзакция целевой БД) — частичной копии не бывает. Если удаление
    // из исходной упадёт после копии, удаляем копию из целевой (компенсация) и возвращаемся к
    // исходному состоянию. Дубль возможен лишь при двойном сбое (исчезающе редко) — данные целы.

    /// <summary>Перенести коллекцию из source в target (с именем newName). Бросает при сбое — после отката копии.</summary>
    public static DbCollection MoveCollection(ConstructorRepository source, ConstructorRepository target,
                                              int collectionId, int? targetGroupId, string newName)
    {
        var snap = source.ExportCollection(collectionId);
        snap.Collection.Name = newName;
        var moved = target.ImportCollection(snap, targetGroupId);
        try
        {
            source.DeleteCollection(collectionId);
        }
        catch
        {
            if (moved.Id is int newId) target.DeleteCollection(newId);   // откат копии — без дубля
            throw;
        }
        return moved;
    }

    /// <summary>Перенести группу (с веткой) из source в target. Бросает при сбое — после отката копии.</summary>
    public static DbGroup MoveGroup(ConstructorRepository source, ConstructorRepository target,
                                    int groupId, int? targetParentId, string newName)
    {
        var snap = source.ExportGroup(groupId);
        snap.Group.Name = newName;
        var moved = target.ImportGroup(snap, targetParentId);
        try
        {
            source.DeleteGroup(groupId);
        }
        catch
        {
            if (moved.Id is int newId) target.DeleteGroup(newId);   // откат копии — без дубля
            throw;
        }
        return moved;
    }

    // ── Быстрый перенос коллекции в другую БД (bulk-SQL через ATTACH) ──────────
    //
    // Старый MoveCollection материализует ВСЮ коллекцию в память (ExportCollection) и пишет её в
    // целевую БД через EF по одной записи (ImportCollection) — на больших коллекциях это минуты
    // (как был импорт до перевода на bulk). Здесь то же, что дубль, только источник — присоединённый
    // файл: на подключении ЦЕЛЕВОЙ БД делаем `ATTACH src`, копируем шелл/записи/значения/UI bulk-ом
    // (id полей и записей перемэппливаются), затем удаляем коллекцию из источника на ОТДЕЛЬНОМ
    // подключении. Копия и удаление раздельны (в WAL межбазовая транзакция не атомарна), с той же
    // компенсацией, что и MoveCollection: при сбое удаления откатываем копию.

    /// <summary>
    /// Перенести коллекцию в ДРУГУЮ БД быстро (bulk-SQL через ATTACH) на ОТДЕЛЬНЫХ подключениях —
    /// не трогает общий Context (см. правило об общем AppDbContext), поэтому безопасно из Task.Run.
    /// <c>this</c> — репозиторий ЦЕЛЕВОЙ БД (куда переносим); источник задаётся путём к его .db-файлу.
    /// <paramref name="progress"/> — нарастающее число перенесённых записей (для % в окне). Возвращает
    /// id новой коллекции в целевой БД (0 — если копировать нечего). Бросает при сбое (после отката копии).
    /// </summary>
    public int MoveCollectionIsolated(string sourceDbPath, int srcCollectionId, int? targetGroupId,
                                      IProgress<int>? progress = null, int batchSize = 10000)
    {
        using var targetCtx = new AppDbContext(db.DbPath);
        targetCtx.ApplyPerformancePragmas();
        var target = new ConstructorRepository(targetCtx);

        var newColId = target.CopyCollectionFromAttached(sourceDbPath, srcCollectionId, targetGroupId, progress, batchSize);
        if (newColId == 0) return 0;

        // Источник чистим на отдельном подключении (каскад: записи→значения, коллекция→поля).
        try
        {
            using var srcCtx = new AppDbContext(sourceDbPath);
            srcCtx.ApplyPerformancePragmas();
            new ConstructorRepository(srcCtx).DeleteCollectionData(srcCollectionId);
        }
        catch
        {
            try { target.DeleteCollectionData(newColId); } catch { /* копию убрать не удалось — данные целы в обеих БД */ }
            throw;
        }
        return newColId;
    }

    /// <summary>
    /// Перенести группу СО ВСЕЙ веткой в ДРУГУЮ БД быстро (bulk-SQL через ATTACH). <c>this</c> —
    /// репозиторий ЦЕЛЕВОЙ БД; источник задаётся путём к его .db-файлу. Структуру групп и все коллекции
    /// ветки копируем в ОДНОЙ транзакции целевой БД, затем удаляем ветку из источника на ОТДЕЛЬНОМ
    /// подключении (с компенсацией как в MoveCollectionIsolated). <paramref name="progress"/> —
    /// нарастающее число перенесённых записей. Возвращает id новой верхней группы (0 — нечего копировать).
    /// </summary>
    public int MoveGroupIsolated(string sourceDbPath, int srcGroupId, int? targetParentId,
                                 IProgress<int>? progress = null, int batchSize = 10000)
    {
        using var targetCtx = new AppDbContext(db.DbPath);
        targetCtx.ApplyPerformancePragmas();
        var target = new ConstructorRepository(targetCtx);

        var newRootId = target.CopyGroupFromAttached(sourceDbPath, srcGroupId, targetParentId, progress, batchSize);
        if (newRootId == 0) return 0;

        try
        {
            using var srcCtx = new AppDbContext(sourceDbPath);
            srcCtx.ApplyPerformancePragmas();
            new ConstructorRepository(srcCtx).DeleteGroupData(srcGroupId);
        }
        catch
        {
            try { target.DeleteGroupData(newRootId); } catch { /* копию убрать не удалось — данные целы в обеих БД */ }
            throw;
        }
        return newRootId;
    }

    // Перенос группы из присоединённой БД: одна транзакция целевой БД — сначала структура групп
    // (CloneGroupTreeAttached, имена через INSERT…SELECT, без материализации в C#), затем все коллекции
    // ветки по очереди с общим % (progressBase накапливает перенесённые записи).
    private int CopyGroupFromAttached(string sourceDbPath, int srcGroupId, int? targetParentId,
                                      IProgress<int>? progress, int batchSize) =>
        WithAttachedSource(sourceDbPath, conn =>
        {
            using var tx = conn.BeginTransaction();

            var map     = new Dictionary<int, int>();   // старый id группы → новый
            var newRoot = CloneGroupTreeAttached(conn, tx, srcGroupId, targetParentId, map);

            // Коллекции ветки (GroupId в наборе старых id) — по очереди, с накоплением прогресса.
            var oldGroupIds = string.Join(",", map.Keys);
            var cols = ReadIntPairs(conn, tx,
                $"SELECT Id, GroupId FROM src.db_collections WHERE GroupId IN ({oldGroupIds}) ORDER BY GroupId, Position");

            long basis = 0;
            foreach (var (colId, oldGroup) in cols)
            {
                var (_, rc) = CopyOneCollectionFromAttached(conn, tx, colId, map[oldGroup], progress, basis, batchSize);
                basis += rc;
            }

            tx.Commit();
            return newRoot;
        });

    // Рекурсивно копирует структуру групп из src в main (INSERT…SELECT — имя остаётся в SQL, без
    // инъекции). Заполняет map (старый id → новый). Возвращает id новой верхней группы.
    private int CloneGroupTreeAttached(DbConnection conn, DbTransaction tx, int srcGroupId,
                                       int? targetParentId, Dictionary<int, int> map)
    {
        var parentVal  = targetParentId is int pid ? pid.ToString() : "NULL";
        var parentCond = targetParentId is int p2 ? $"ParentId = {p2}" : "ParentId IS NULL";
        var maxPos     = ScalarLong(conn, tx, $"SELECT COALESCE(MAX(Position), 0) FROM main.db_groups WHERE {parentCond}");

        Exec(conn, tx, $@"
            INSERT INTO main.db_groups (Name, ParentId, Position)
            SELECT Name, {parentVal}, {maxPos + 1000} FROM src.db_groups WHERE Id = {srcGroupId}");
        var newId = (int)ScalarLong(conn, tx, "SELECT last_insert_rowid()");
        map[srcGroupId] = newId;

        foreach (var childId in ReadIntList(conn, tx, $"SELECT Id FROM src.db_groups WHERE ParentId = {srcGroupId} ORDER BY Position"))
            CloneGroupTreeAttached(conn, tx, childId, newId, map);
        return newId;
    }

    // Прогресс со сдвигом: пробрасывает baseValue + value (общий % по нескольким коллекциям ветки,
    // когда CopyCollectionData/CopyOneCollection сообщает 0..recCount для каждой по отдельности).
    private sealed class BaseProgress(IProgress<int>? inner, long baseValue) : IProgress<int>
    {
        public void Report(int value) => inner?.Report((int)(baseValue + value));
    }

    // ATTACH источника (src) на подключение целевой БД, выполнить body, DETACH. Подключение держим
    // открытым на всё время: ATTACH живёт на подключении. Путь источника — через параметр (в пути
    // iCloud пробелы и спецсимволы). Используется и переносом коллекции, и переносом группы.
    private T WithAttachedSource<T>(string sourceDbPath, Func<DbConnection, T> body)
    {
        var conn = db.Database.GetDbConnection();
        db.Database.OpenConnection();
        try
        {
            using (var att = conn.CreateCommand())
            {
                att.CommandText = "ATTACH DATABASE $src AS src";
                var p = att.CreateParameter(); p.ParameterName = "$src"; p.Value = sourceDbPath; att.Parameters.Add(p);
                att.ExecuteNonQuery();
            }
            try { return body(conn); }
            finally
            {
                using var det = conn.CreateCommand();
                det.CommandText = "DETACH src";
                det.ExecuteNonQuery();
            }
        }
        finally
        {
            db.Database.CloseConnection();
        }
    }

    // Перенос одной коллекции из присоединённой БД: одна транзакция целевой БД.
    private int CopyCollectionFromAttached(string sourceDbPath, int srcCollectionId, int? targetGroupId,
                                           IProgress<int>? progress, int batchSize) =>
        WithAttachedSource(sourceDbPath, conn =>
        {
            using var tx = conn.BeginTransaction();
            var (newColId, _) = CopyOneCollectionFromAttached(conn, tx, srcCollectionId, targetGroupId, progress, 0, batchSize);
            tx.Commit();
            return newColId;
        });

    // Копия ОДНОЙ коллекции из src в main в УЖЕ открытой транзакции (для переноса группы — много
    // коллекций в одной транзакции). Шелл (коллекция + поля) bulk-ом из src, карта полей old→new по
    // Position (temp _move_fmap), записи одним INSERT…SELECT (новые id — непрерывный блок в порядке
    // Position,Id), значения крупными пакетами (один INSERT по всем полям через _move_fmap; пакеты —
    // ради гранулярности %), затем UI-настройки. progressBase — сколько записей уже перенесено ранее
    // (для общего % по группе). Возвращает (id новой коллекции, число её записей). Числа в SQL — int/
    // long, которые мы формируем сами (не пользовательский ввод), поэтому интерполяция безопасна.
    private (int NewColId, long RecCount) CopyOneCollectionFromAttached(DbConnection conn, DbTransaction tx,
        int srcCollectionId, int? targetGroupId, IProgress<int>? progress, long progressBase, int batchSize)
    {
        var recCount = ScalarLong(conn, tx, $"SELECT COUNT(*) FROM src.db_records WHERE CollectionId = {srcCollectionId}");

        var maxPos   = ScalarLong(conn, tx, "SELECT COALESCE(MAX(Position), 0) FROM main.db_collections");
        var groupSql = targetGroupId is int gid ? gid.ToString() : "NULL";

        // 1) Строка коллекции (имя/иконку/цвет переносим как есть; группа — целевая).
        Exec(conn, tx, $@"
            INSERT INTO main.db_collections (Name, GroupId, Position, Icon, Color)
            SELECT Name, {groupSql}, {maxPos + 1000}, Icon, Color FROM src.db_collections WHERE Id = {srcCollectionId}");
        var newColId = (int)ScalarLong(conn, tx, "SELECT last_insert_rowid()");

        // 2) Поля (Position копируем дословно — по нему строим карту old→new).
        Exec(conn, tx, $@"
            INSERT INTO main.db_fields (CollectionId, Name, FieldType, ConfigJson, Position, IsCalStart, IsCalEnd, IsSystem, IsRequired)
            SELECT {newColId}, Name, FieldType, ConfigJson, Position, IsCalStart, IsCalEnd, IsSystem, IsRequired
            FROM src.db_fields WHERE CollectionId = {srcCollectionId} ORDER BY Id");

        Exec(conn, tx, "DROP TABLE IF EXISTS temp._move_fmap");
        Exec(conn, tx, "CREATE TEMP TABLE _move_fmap(old_fid INTEGER PRIMARY KEY, new_fid INTEGER)");
        Exec(conn, tx, $@"
            INSERT INTO _move_fmap(old_fid, new_fid)
            SELECT s.Id, n.Id FROM src.db_fields s
            JOIN main.db_fields n ON n.CollectionId = {newColId} AND n.Position = s.Position
            WHERE s.CollectionId = {srcCollectionId}");

        if (recCount > 0)
        {
            // 3) Записи одним INSERT … SELECT в порядке (Position, Id) — новые id непрерывным блоком.
            Exec(conn, tx, $@"
                INSERT INTO main.db_records (CollectionId, CreatedAt, Position, StartDt, EndDt, AllDay, Title, Description)
                SELECT {newColId}, CreatedAt, Position, StartDt, EndDt, AllDay, Title, Description
                FROM src.db_records WHERE CollectionId = {srcCollectionId} ORDER BY Position, Id");
            var firstNew = ScalarLong(conn, tx, "SELECT last_insert_rowid()") - recCount + 1;

            // 4) Значения крупными пакетами по (Position, Id): на пакет — один INSERT по всем полям.
            long prevPos = long.MinValue, prevId = 0, processed = 0;
            while (processed < recCount)
            {
                var bbase = firstNew + processed;
                var end   = AttachedBatchEnd(conn, tx, srcCollectionId, prevPos, prevId, batchSize);
                if (end is { } e)
                {
                    Exec(conn, tx, $@"
                        INSERT INTO main.db_field_values (RecordId, FieldId, Value)
                        SELECT {bbase} + r.rn - 1, fm.new_fid, fv.Value
                        FROM src.db_field_values fv
                        JOIN (SELECT Id AS old_id, ROW_NUMBER() OVER (ORDER BY Position, Id) AS rn
                              FROM src.db_records
                              WHERE CollectionId = {srcCollectionId}
                                AND (Position > {prevPos} OR (Position = {prevPos} AND Id > {prevId}))
                                AND (Position < {e.Pos} OR (Position = {e.Pos} AND Id <= {e.Id}))) r
                          ON r.old_id = fv.RecordId
                        JOIN _move_fmap fm ON fm.old_fid = fv.FieldId");
                    processed += batchSize; prevPos = e.Pos; prevId = e.Id;
                }
                else
                {
                    Exec(conn, tx, $@"
                        INSERT INTO main.db_field_values (RecordId, FieldId, Value)
                        SELECT {bbase} + r.rn - 1, fm.new_fid, fv.Value
                        FROM src.db_field_values fv
                        JOIN (SELECT Id AS old_id, ROW_NUMBER() OVER (ORDER BY Position, Id) AS rn
                              FROM src.db_records
                              WHERE CollectionId = {srcCollectionId}
                                AND (Position > {prevPos} OR (Position = {prevPos} AND Id > {prevId}))) r
                          ON r.old_id = fv.RecordId
                        JOIN _move_fmap fm ON fm.old_fid = fv.FieldId");
                    processed = recCount;
                }
                progress?.Report((int)(progressBase + Math.Min(processed, recCount)));
            }

            CopyMovedUi(conn, tx, srcCollectionId, newColId, firstNew, recCount);
        }
        else
        {
            CopyMovedUi(conn, tx, srcCollectionId, newColId, 0, 0);
            progress?.Report((int)progressBase);
        }

        Exec(conn, tx, "DROP TABLE IF EXISTS temp._move_fmap");
        return (newColId, recCount);
    }

    // UI-настройки переносимой коллекции: ключи-id полей/записей перемэппливаются (поля — через
    // _move_fmap; записи — по рангу в порядке Position,Id → firstNew + ранг − 1). Ключи колонок
    // (column_order/display) числовые → новый id поля, системные (__title__/__desc__) — как есть.
    private static void CopyMovedUi(DbConnection conn, DbTransaction tx, int srcCol, int newCol, long firstNew, long recCount)
    {
        Exec(conn, tx, $@"
            INSERT INTO main.ui_col_widths (CollectionId, FieldId, Width)
            SELECT {newCol}, fm.new_fid, w.Width FROM src.ui_col_widths w
            JOIN _move_fmap fm ON fm.old_fid = w.FieldId WHERE w.CollectionId = {srcCol}");

        Exec(conn, tx, $@"
            INSERT INTO main.ui_collection_settings (CollectionId, ColResize, RowResize, ViewMode)
            SELECT {newCol}, ColResize, RowResize, ViewMode FROM src.ui_collection_settings WHERE CollectionId = {srcCol}");

        Exec(conn, tx, $@"
            INSERT INTO main.system_field_meta (CollectionId, Key, DisplayName, Position)
            SELECT {newCol}, Key, DisplayName, Position FROM src.system_field_meta WHERE CollectionId = {srcCol}");

        Exec(conn, tx, $@"
            INSERT INTO main.column_order (CollectionId, Key, Position)
            SELECT {newCol},
                   CASE WHEN fm.new_fid IS NOT NULL THEN CAST(fm.new_fid AS TEXT) ELSE co.Key END,
                   co.Position
            FROM src.column_order co
            LEFT JOIN _move_fmap fm ON co.Key GLOB '[0-9]*' AND CAST(co.Key AS INTEGER) = fm.old_fid
            WHERE co.CollectionId = {srcCol}");

        Exec(conn, tx, $@"
            INSERT INTO main.column_display (CollectionId, Key, Visible, Level)
            SELECT {newCol},
                   CASE WHEN fm.new_fid IS NOT NULL THEN CAST(fm.new_fid AS TEXT) ELSE cd.Key END,
                   cd.Visible, cd.Level
            FROM src.column_display cd
            LEFT JOIN _move_fmap fm ON cd.Key GLOB '[0-9]*' AND CAST(cd.Key AS INTEGER) = fm.old_fid
            WHERE cd.CollectionId = {srcCol}");

        if (recCount > 0)
        {
            var rank = $@"(SELECT Id AS old_id, ROW_NUMBER() OVER (ORDER BY Position, Id) AS rn
                          FROM src.db_records WHERE CollectionId = {srcCol})";
            Exec(conn, tx, $@"
                INSERT INTO main.ui_row_heights (CollectionId, RecordId, Height)
                SELECT {newCol}, {firstNew} + r.rn - 1, h.Height FROM src.ui_row_heights h
                JOIN {rank} r ON r.old_id = h.RecordId WHERE h.CollectionId = {srcCol}");
            Exec(conn, tx, $@"
                INSERT INTO main.record_display (CollectionId, RecordId, Visible, Level)
                SELECT {newCol}, {firstNew} + r.rn - 1, d.Visible, d.Level FROM src.record_display d
                JOIN {rank} r ON r.old_id = d.RecordId WHERE d.CollectionId = {srcCol}");
        }
    }

    // (Position, Id) записи источника на позиции offset−1 после курсора — граница пакета значений.
    // null — записей меньше (последний неполный пакет). Источник — присоединённая схема src.
    private static (long Pos, long Id)? AttachedBatchEnd(DbConnection conn, DbTransaction tx,
        int srcCollectionId, long prevPos, long prevId, int offset)
    {
        using var c = conn.CreateCommand();
        c.Transaction = tx;
        c.CommandText = $@"
            SELECT Position, Id FROM src.db_records
            WHERE CollectionId = {srcCollectionId}
              AND (Position > {prevPos} OR (Position = {prevPos} AND Id > {prevId}))
            ORDER BY Position, Id LIMIT 1 OFFSET {offset - 1}";
        using var rdr = c.ExecuteReader();
        return rdr.Read() ? (rdr.GetInt64(0), rdr.GetInt64(1)) : null;
    }

    private static void Exec(DbConnection conn, DbTransaction tx, string sql)
    {
        using var c = conn.CreateCommand();
        c.Transaction = tx; c.CommandText = sql; c.ExecuteNonQuery();
    }

    private static long ScalarLong(DbConnection conn, DbTransaction? tx, string sql)
    {
        using var c = conn.CreateCommand();
        if (tx is not null) c.Transaction = tx;
        c.CommandText = sql;
        return Convert.ToInt64(c.ExecuteScalar());
    }

    private static List<int> ReadIntList(DbConnection conn, DbTransaction tx, string sql)
    {
        using var c = conn.CreateCommand();
        c.Transaction = tx; c.CommandText = sql;
        using var rdr = c.ExecuteReader();
        var list = new List<int>();
        while (rdr.Read()) list.Add(rdr.GetInt32(0));
        return list;
    }

    // Пары (col0, col1) из запроса — для (Id, GroupId) коллекций поддерева при переносе группы.
    private static List<(int A, int B)> ReadIntPairs(DbConnection conn, DbTransaction tx, string sql)
    {
        using var c = conn.CreateCommand();
        c.Transaction = tx; c.CommandText = sql;
        using var rdr = c.ExecuteReader();
        var list = new List<(int, int)>();
        while (rdr.Read()) list.Add((rdr.GetInt32(0), rdr.GetInt32(1)));
        return list;
    }

    // ── Поля ──────────────────────────────────────────────────────────────────

    public List<DbField> GetFields(int collectionId) =>
        db.DbFields
          .Where(f => f.CollectionId == collectionId)
          .OrderBy(f => f.Position)
          .Select(f => new DbField
          {
              Id = f.Id, CollectionId = f.CollectionId, Name = f.Name,
              FieldType = f.FieldType, ConfigJson = f.ConfigJson, Position = f.Position,
              IsCalStart = f.IsCalStart, IsCalEnd = f.IsCalEnd,
              IsSystem = f.IsSystem, IsRequired = f.IsRequired,
          })
          .ToList();

    public DbField AddField(DbField field)
    {
        if (db.DbFields.Any(f => f.CollectionId == field.CollectionId && f.Name == field.Name))
            throw new InvalidOperationException("Поле с таким именем уже существует.");

        // Первое поле-дата в коллекции становится источником календаря: его значение
        // проецируется на db_records.StartDt/EndDt (см. SetFieldValue → ProjectDateToRecord).
        if (field.FieldType == "date" &&
            !db.DbFields.Any(f => f.CollectionId == field.CollectionId && f.FieldType == "date" && f.IsCalStart))
            field.IsCalStart = true;

        var entity = new DbFieldEntity
        {
            CollectionId = field.CollectionId, Name = field.Name,
            FieldType    = field.FieldType,    ConfigJson = field.ConfigJson,
            Position     = field.Position,     IsCalStart = field.IsCalStart,
            IsCalEnd     = field.IsCalEnd,     IsSystem = field.IsSystem,
            IsRequired   = field.IsRequired,
        };
        db.DbFields.Add(entity);
        db.SaveChanges();
        field.Id = entity.Id;
        return field;
    }

    public void UpdateField(DbField field)
    {
        var entity = db.DbFields.Find(field.Id);
        if (entity is null) return;
        if (entity.Name != field.Name &&
            db.DbFields.Any(f => f.CollectionId == entity.CollectionId && f.Name == field.Name && f.Id != field.Id))
            throw new InvalidOperationException("Поле с таким именем уже существует.");
        entity.Name       = field.Name;
        entity.FieldType  = field.FieldType;
        entity.ConfigJson = field.ConfigJson;
        entity.Position   = field.Position;
        entity.IsCalStart = field.IsCalStart;
        entity.IsCalEnd   = field.IsCalEnd;
        entity.IsSystem   = field.IsSystem;
        entity.IsRequired = field.IsRequired;
        db.SaveChanges();
    }

    public DbField DuplicateField(int fieldId)
    {
        var src = db.DbFields.Find(fieldId);
        if (src is null) throw new InvalidOperationException($"Field {fieldId} not found");

        var name = NextCopyName(src.Name,
            candidate => db.DbFields.Any(f => f.CollectionId == src.CollectionId && f.Name == candidate));

        // Сдвигаем все поля правее исходного, освобождая место под копию.
        var following = db.DbFields
            .Where(f => f.CollectionId == src.CollectionId && f.Position > src.Position)
            .ToList();
        foreach (var f in following)
            f.Position += 1000;

        var copy = new DbFieldEntity
        {
            CollectionId = src.CollectionId,     Name = name,
            FieldType    = src.FieldType,        ConfigJson = src.ConfigJson,
            Position     = src.Position + 1000,  IsCalStart = false,
            IsCalEnd     = false,                IsSystem = false,
            IsRequired   = false,
        };
        db.DbFields.Add(copy);
        db.SaveChanges();

        // Копируем значения ячеек исходного свойства во всех записях ОДНИМ bulk-SQL —
        // на больших коллекциях это сотни тысяч строк, поэлементная вставка через EF
        // подвешивала бы приложение. Конфликтов по (RecordId, FieldId) нет: поле новое.
        db.Database.ExecuteSqlInterpolated($@"
            INSERT INTO db_field_values (RecordId, FieldId, Value)
            SELECT RecordId, {copy.Id}, Value FROM db_field_values WHERE FieldId = {fieldId}");

        return new DbField
        {
            Id = copy.Id, CollectionId = copy.CollectionId, Name = copy.Name,
            FieldType = copy.FieldType, ConfigJson = copy.ConfigJson, Position = copy.Position,
        };
    }

    // Имя копии в стиле macOS (без стакания слова «копия»). Отрезаем уже имеющийся
    // суффикс «(копия)»/«(копия) N» и подбираем первый свободный: «X (копия)»,
    // «X (копия) 2», «X (копия) 3» … exists — проверка занятости имени.
    private static string NextCopyName(string sourceName, Func<string, bool> exists)
    {
        var stem = StripCopySuffix(sourceName);
        if (string.IsNullOrEmpty(stem)) stem = sourceName;

        string Make(int n) => n <= 1 ? $"{stem} (копия)" : $"{stem} (копия) {n}";

        var name = Make(1);
        var k = 2;
        while (exists(name)) name = Make(k++);
        return name;
    }

    // Убирает хвостовые суффиксы копий — «(копия)» и «(копия) N», один или несколько:
    // «8888 (копия) 2» → «8888», «8888 (копия) (копия)» → «8888».
    private static string StripCopySuffix(string name) =>
        Regex.Replace(name, @"(\s+\(копия\)(\s+\d+)?)+$", "").TrimEnd();

    public void DeleteField(int fieldId)
    {
        var entity = db.DbFields.Find(fieldId);
        if (entity is null) { db.SaveChanges(); return; }

        var colId = entity.CollectionId;
        db.DbFields.Remove(entity);

        // Чистим строку порядка для этого поля, чтобы в column_order не копились
        // мёртвые ключи (иначе перемещение колонок начинает «не работать»).
        var key      = fieldId.ToString();
        var orderRow = db.ColumnOrder.FirstOrDefault(o => o.CollectionId == colId && o.Key == key);
        if (orderRow is not null) db.ColumnOrder.Remove(orderRow);

        db.SaveChanges();
    }

    public void ReorderFields(List<int> orderedIds)
    {
        for (var i = 0; i < orderedIds.Count; i++)
        {
            var entity = db.DbFields.Find(orderedIds[i]);
            if (entity is not null) entity.Position = (i + 1) * 1000;
        }
        db.SaveChanges();
    }

    // ── Записи (низкоуровневые DbRecord) ──────────────────────────────────────

    public List<DbRecord> GetRecords(int collectionId) =>
        db.DbRecords
          .Where(r => r.CollectionId == collectionId)
          .OrderBy(r => r.Position)
          .Select(r => new DbRecord
          {
              Id = r.Id, CollectionId = r.CollectionId, CreatedAt = r.CreatedAt,
              Position = r.Position, StartDt = r.StartDt, EndDt = r.EndDt, AllDay = r.AllDay,
              Title = r.Title, Description = r.Description,
          })
          .ToList();

    /// <summary>
    /// Лёгкая выборка (Id, Title) всех записей коллекции в порядке (Position, Id) — для окна
    /// «Отображение записей». Только две колонки через проекцию + AsNoTracking: не материализует
    /// полные сущности и не засоряет трекер (на коллекциях в сотни тысяч строк это заметно).
    /// </summary>
    public List<(int Id, string Title)> GetRecordTitlesOrdered(int collectionId) =>
        db.DbRecords.AsNoTracking()
          .Where(r => r.CollectionId == collectionId)
          .OrderBy(r => r.Position).ThenBy(r => r.Id)
          .Select(r => new { r.Id, r.Title })
          .ToList()
          .Select(r => (r.Id, r.Title ?? ""))
          .ToList();

    // excludeHidden=true исключает записи, скрытые в окне «Отображение записей»
    // (record_display.Visible=false). Фильтр уходит в SQL до Count/Skip/Take, поэтому
    // виртуализация и пагинация таблицы сохраняются (см. VirtualRowList).
    public int CountRecords(int collectionId, bool excludeHidden = false) =>
        db.DbRecords.Count(r => r.CollectionId == collectionId
            && (!excludeHidden || !db.RecordDisplay.Any(d =>
                   d.CollectionId == collectionId && d.RecordId == r.Id && !d.Visible)));

    public List<DbRecord> GetRecordsPage(int collectionId, int skip, int take, bool excludeHidden = false) =>
        db.DbRecords
          .Where(r => r.CollectionId == collectionId
              && (!excludeHidden || !db.RecordDisplay.Any(d =>
                     d.CollectionId == collectionId && d.RecordId == r.Id && !d.Visible)))
          .OrderBy(r => r.Position).ThenBy(r => r.Id)
          .Skip(skip)
          .Take(take)
          .Select(r => new DbRecord
          {
              Id = r.Id, CollectionId = r.CollectionId, CreatedAt = r.CreatedAt,
              Position = r.Position, StartDt = r.StartDt, EndDt = r.EndDt, AllDay = r.AllDay,
              Title = r.Title, Description = r.Description,
          })
          .ToList();

    /// <summary>
    /// Keyset-пагинация: следующая страница ПОСЛЕ курсора (afterPosition, afterId), упорядочение
    /// по <c>(Position, Id)</c> — тот же ключ, что и составной индекс db_records(CollectionId, Position).
    /// В отличие от <see cref="GetRecordsPage"/> со <c>Skip(N)</c> (O(offset) — перечитывает N строк),
    /// здесь стоимость = O(take): нет смещения, индекс сразу позиционируется на курсор. Используется
    /// при последовательном скролле, когда известна последняя строка предыдущей страницы.
    /// Курсор null/null — с самого начала. Двойной ключ (Position, Id) устойчив к дублям Position
    /// (после серий вставок/свопов), иначе строки с равной Position могли бы потеряться/повториться.
    /// </summary>
    public List<DbRecord> GetRecordsPageAfter(int collectionId, int? afterPosition, int? afterId,
                                              int take, bool excludeHidden = false)
    {
        var q = db.DbRecords
            .Where(r => r.CollectionId == collectionId
                && (!excludeHidden || !db.RecordDisplay.Any(d =>
                       d.CollectionId == collectionId && d.RecordId == r.Id && !d.Visible)));

        if (afterPosition is int p && afterId is int aid)
            q = q.Where(r => r.Position > p || (r.Position == p && r.Id > aid));

        return q.OrderBy(r => r.Position).ThenBy(r => r.Id)
                .Take(take)
                .Select(r => new DbRecord
                {
                    Id = r.Id, CollectionId = r.CollectionId, CreatedAt = r.CreatedAt,
                    Position = r.Position, StartDt = r.StartDt, EndDt = r.EndDt, AllDay = r.AllDay,
                    Title = r.Title, Description = r.Description,
                })
                .ToList();
    }

    public Dictionary<int, Dictionary<int, string>> GetFieldValuesBatch(IReadOnlyList<int> recordIds)
    {
        if (recordIds.Count == 0) return [];
        return db.DbFieldValues
            .Where(fv => recordIds.Contains(fv.RecordId))
            .GroupBy(fv => fv.RecordId)
            .ToDictionary(g => g.Key, g => g.ToDictionary(fv => fv.FieldId, fv => fv.Value));
    }

    public int GetRecordPosition(int collectionId, int recordId)
    {
        var pos = db.DbRecords
            .Where(r => r.CollectionId == collectionId && r.Id == recordId)
            .Select(r => (int?)r.Position)
            .FirstOrDefault();
        if (pos is null) return -1;
        return db.DbRecords.Count(r => r.CollectionId == collectionId && r.Position < pos);
    }

    public DbRecord? GetRecord(int recordId)
    {
        var r = db.DbRecords.Find(recordId);
        if (r is null) return null;
        return new DbRecord
        {
            Id = r.Id, CollectionId = r.CollectionId, CreatedAt = r.CreatedAt,
            Position = r.Position, StartDt = r.StartDt, EndDt = r.EndDt, AllDay = r.AllDay,
            Title = r.Title, Description = r.Description,
        };
    }

    public void SetRecordTitle(int recordId, string title)
    {
        var rec = db.DbRecords.Find(recordId);
        if (rec is null) return;
        rec.Title = title;
        db.SaveChanges();
    }

    public void SetRecordDescription(int recordId, string? description)
    {
        var rec = db.DbRecords.Find(recordId);
        if (rec is null) return;
        rec.Description = string.IsNullOrEmpty(description) ? null : description;
        db.SaveChanges();
    }

    public DbRecord AddRecord(DbRecord record)
    {
        var entity = new DbRecordEntity
        {
            CollectionId = record.CollectionId,
            CreatedAt    = record.CreatedAt ?? DateTime.Now.ToString("o"),
            Position     = record.Position,
            StartDt      = record.StartDt,
            EndDt        = record.EndDt,
            AllDay       = record.AllDay,
            Title        = record.Title,
            Description  = record.Description,
        };
        db.DbRecords.Add(entity);
        db.SaveChanges();
        record.Id = entity.Id;
        return record;
    }

    // Создаёт запись сразу после afterRecordId (по позиции).
    public DbRecord AddRecordAfter(DbRecord record, int afterRecordId)
    {
        record.Position = PositionAfterRecord(record.CollectionId, afterRecordId);
        return AddRecord(record);
    }

    // Позиция для вставки записи сразу после afterRecordId (между ней и следующей).
    // Соседи берутся запросами по индексу (CollectionId, Position) — без загрузки всей
    // коллекции в память (иначе на 100k+ зависание). Инициатор не найден — позиция в конце.
    // Если зазора между соседями нет — редкая перенумерация одним bulk-SQL (RenumberPositions),
    // тоже без материализации в EF.
    private int PositionAfterRecord(int collectionId, int afterRecordId)
    {
        var after = db.DbRecords.AsNoTracking()
            .Where(r => r.Id == afterRecordId && r.CollectionId == collectionId)
            .Select(r => new { r.Position, r.Id })
            .FirstOrDefault();

        if (after is null)   // инициатор не найден — в конец
            return (db.DbRecords.Where(r => r.CollectionId == collectionId)
                        .Select(r => (int?)r.Position).Max() ?? 0) + 1000;

        var nextPos = db.DbRecords.AsNoTracking()
            .Where(r => r.CollectionId == collectionId
                && (r.Position > after.Position || (r.Position == after.Position && r.Id > after.Id)))
            .OrderBy(r => r.Position).ThenBy(r => r.Id)
            .Select(r => (int?)r.Position)
            .FirstOrDefault();

        if (nextPos is not int np) return after.Position + 1000;          // после последней
        if (np - after.Position >= 2) return after.Position + (np - after.Position) / 2;

        // Зазора нет — перенумеровываем всю коллекцию одним SQL-запросом, затем считаем середину.
        RenumberPositions(collectionId);
        var ap = db.DbRecords.AsNoTracking().Where(r => r.Id == afterRecordId).Select(r => r.Position).First();
        var n2 = db.DbRecords.AsNoTracking()
            .Where(r => r.CollectionId == collectionId && r.Position > ap)
            .OrderBy(r => r.Position).Select(r => (int?)r.Position).FirstOrDefault();
        return n2 is int n ? ap + (n - ap) / 2 : ap + 1000;
    }

    // Перенумеровать Position всех записей коллекции с шагом 1000 ОДНИМ bulk-SQL-запросом
    // (оконная функция ROW_NUMBER), не загружая записи в EF. Используется как редкий
    // «ребаланс», когда между соседями не осталось зазора для вставки.
    private void RenumberPositions(int collectionId)
    {
        db.Database.ExecuteSqlInterpolated($@"
            UPDATE db_records
            SET Position = sub.rn * 1000
            FROM (SELECT Id, ROW_NUMBER() OVER (ORDER BY Position, Id) AS rn
                  FROM db_records WHERE CollectionId = {collectionId}) AS sub
            WHERE db_records.Id = sub.Id");
        // Сбрасываем закешированные EF позиции, чтобы последующие чтения видели свежие значения.
        foreach (var e in db.ChangeTracker.Entries<DbRecordEntity>().ToList())
            if (e.Entity.CollectionId == collectionId) e.Reload();
    }

    public void UpdateRecord(DbRecord record)
    {
        if (record.Id is null) return;
        var entity = db.DbRecords.Find(record.Id.Value);
        if (entity is null) return;

        entity.CollectionId = record.CollectionId;
        entity.Position     = record.Position;
        entity.StartDt      = record.StartDt;
        entity.EndDt        = record.EndDt;
        entity.AllDay       = record.AllDay;
        entity.Title        = record.Title;
        entity.Description  = record.Description;
        db.SaveChanges();
    }

    public void ReorderRecords(List<int> orderedIds)
    {
        for (var i = 0; i < orderedIds.Count; i++)
        {
            var entity = db.DbRecords.Find(orderedIds[i]);
            if (entity is not null) entity.Position = (i + 1) * 1000;
        }
        db.SaveChanges();
    }

    // Переместить запись на одну позицию вверх/вниз в порядке коллекции (по (Position, Id)).
    // Сосед ищется ОДНИМ индексным запросом (не загрузкой всей коллекции — иначе на 100k+
    // зависание), затем меняем местами Position двух записей. Крайняя запись — no-op.
    public void MoveRecord(int collectionId, int recordId, bool up)
    {
        var rec = db.DbRecords.Find(recordId);
        if (rec is null || rec.CollectionId != collectionId) return;
        var p = rec.Position;
        var id = rec.Id;

        // Ближайший сосед в нужную сторону по упорядочению (Position, Id).
        var neighbor = up
            ? db.DbRecords.Where(r => r.CollectionId == collectionId
                  && (r.Position < p || (r.Position == p && r.Id < id)))
                .OrderByDescending(r => r.Position).ThenByDescending(r => r.Id).FirstOrDefault()
            : db.DbRecords.Where(r => r.CollectionId == collectionId
                  && (r.Position > p || (r.Position == p && r.Id > id)))
                .OrderBy(r => r.Position).ThenBy(r => r.Id).FirstOrDefault();
        if (neighbor is null) return;   // крайняя запись — no-op

        if (neighbor.Position != rec.Position)
            (rec.Position, neighbor.Position) = (neighbor.Position, rec.Position);
        else
            // Позиции слиплись (ничью решал Id) — сдвигаем текущую на 1, чтобы реально
            // переставить относительно соседа. Без глобальной перенумерации.
            rec.Position = up ? rec.Position - 1 : rec.Position + 1;

        db.SaveChanges();
    }

    public void DeleteRecord(int recordId)
    {
        var entity = db.DbRecords.Find(recordId);
        if (entity is not null) db.DbRecords.Remove(entity);
        db.SaveChanges();
    }

    public DbRecord DuplicateRecord(int recordId)
    {
        var src = db.DbRecords.Find(recordId);
        if (src is null) throw new InvalidOperationException($"Record {recordId} not found");

        var copy = new DbRecordEntity
        {
            CollectionId = src.CollectionId,
            CreatedAt    = DateTime.Now.ToString("o"),
            Position     = PositionAfterRecord(src.CollectionId, recordId),   // сразу после исходной
            StartDt      = src.StartDt,
            EndDt        = src.EndDt,
            AllDay       = src.AllDay,
            Title        = src.Title,
            Description  = src.Description,
        };
        db.DbRecords.Add(copy);
        db.SaveChanges();

        var fieldValues = db.DbFieldValues.Where(fv => fv.RecordId == recordId).ToList();
        foreach (var fv in fieldValues)
            db.DbFieldValues.Add(new DbFieldValueEntity { RecordId = copy.Id, FieldId = fv.FieldId, Value = fv.Value });
        db.SaveChanges();

        return new DbRecord
        {
            Id           = copy.Id,
            CollectionId = copy.CollectionId,
            Position     = copy.Position,
            Title        = copy.Title,
            Description  = copy.Description,
        };
    }

    // ── Значения полей ────────────────────────────────────────────────────────

    public Dictionary<int, string> GetFieldValues(int recordId) =>
        db.DbFieldValues
          .Where(fv => fv.RecordId == recordId)
          .ToDictionary(fv => fv.FieldId, fv => fv.Value);

    /// <summary>Все значения полей коллекции одним запросом (для фильтрации/сортировки).</summary>
    public Dictionary<int, Dictionary<int, string>> GetFieldValuesByCollection(int collectionId) =>
        db.DbFieldValues
          .Where(fv => fv.Record.CollectionId == collectionId)
          .Select(fv => new { fv.RecordId, fv.FieldId, fv.Value })
          .ToList()
          .GroupBy(x => x.RecordId)
          .ToDictionary(g => g.Key, g => g.ToDictionary(x => x.FieldId, x => x.Value));

    // ── Фильтр/сортировка в SQL (для виртуализации отфильтрованного представления) ──
    //
    // Вместо загрузки ВСЕХ строк в память (старый LoadAllRows — зависание на больших
    // коллекциях) фильтр уходит в SQL и возвращает только id совпавших записей. Показ
    // затем виртуализируется постранично по этому списку id (см. VirtualRowList в режиме
    // списка id). Сортировка — отдельным шагом в UI (тот же компаратор, что и раньше),
    // по значениям одной колонки (GetColumnValues), а не по всем полям.

    /// <summary>
    /// id записей коллекции, прошедших фильтры (key → набор допустимых значений), в порядке
    /// (Position, Id). Пустое значение "" в наборе допускает записи без значения/с пустым.
    /// excludeHidden — исключить скрытые в окне «Отображение записей».
    /// </summary>
    public List<int> GetFilteredRecordIds(int collectionId,
        IReadOnlyDictionary<string, HashSet<string>> filters, bool excludeHidden = false)
    {
        var q = db.DbRecords.AsNoTracking()
            .Where(r => r.CollectionId == collectionId
                && (!excludeHidden || !db.RecordDisplay.Any(d =>
                       d.CollectionId == collectionId && d.RecordId == r.Id && !d.Visible)));

        foreach (var (key, allowed) in filters)
        {
            var allow = allowed.ToList();   // EF переведёт Contains → SQL IN (...)
            if (key == TitleKey)
                q = q.Where(r => allow.Contains(r.Title));
            else if (key == DescKey)
                q = q.Where(r => allow.Contains(r.Description ?? ""));
            else if (int.TryParse(key, out var fid))
            {
                var hasEmpty = allowed.Contains("");
                q = q.Where(r =>
                    db.DbFieldValues.Any(fv => fv.RecordId == r.Id && fv.FieldId == fid && allow.Contains(fv.Value))
                    || (hasEmpty && !db.DbFieldValues.Any(fv => fv.RecordId == r.Id && fv.FieldId == fid && fv.Value != "")));
            }
        }

        return q.OrderBy(r => r.Position).ThenBy(r => r.Id).Select(r => r.Id).ToList();
    }

    /// <summary>
    /// Значения одной колонки по всем записям коллекции (id → значение) — для сортировки
    /// отфильтрованного списка в UI тем же компаратором, что и раньше. Грузит ровно одну
    /// колонку, а не все поля (в отличие от GetFieldValuesByCollection).
    /// </summary>
    public Dictionary<int, string> GetColumnValues(int collectionId, string key)
    {
        if (key == TitleKey)
            return db.DbRecords.AsNoTracking().Where(r => r.CollectionId == collectionId)
                     .Select(r => new { r.Id, r.Title }).ToList()
                     .ToDictionary(x => x.Id, x => x.Title ?? "");
        if (key == DescKey)
            return db.DbRecords.AsNoTracking().Where(r => r.CollectionId == collectionId)
                     .Select(r => new { r.Id, r.Description }).ToList()
                     .ToDictionary(x => x.Id, x => x.Description ?? "");
        if (int.TryParse(key, out var fid))
            return db.DbFieldValues.AsNoTracking()
                     .Where(fv => fv.FieldId == fid && fv.Record.CollectionId == collectionId)
                     .Select(fv => new { fv.RecordId, fv.Value }).ToList()
                     .ToDictionary(x => x.RecordId, x => x.Value);
        return new();
    }

    /// <summary>Записи по списку id в ТОМ ЖЕ порядке, что и переданный список (для постраничной
    /// подгрузки отфильтрованного/отсортированного представления). ids — одна страница (≤ PageSize).</summary>
    public List<DbRecord> GetRecordsByIdsOrdered(IReadOnlyList<int> ids)
    {
        if (ids.Count == 0) return [];
        var byId = db.DbRecords.AsNoTracking()
            .Where(r => ids.Contains(r.Id))
            .Select(r => new DbRecord
            {
                Id = r.Id, CollectionId = r.CollectionId, CreatedAt = r.CreatedAt,
                Position = r.Position, StartDt = r.StartDt, EndDt = r.EndDt, AllDay = r.AllDay,
                Title = r.Title, Description = r.Description,
            })
            .ToList()
            .ToDictionary(r => r.Id!.Value);

        var result = new List<DbRecord>(ids.Count);
        foreach (var id in ids)
            if (byId.TryGetValue(id, out var r)) result.Add(r);
        return result;
    }

    /// <summary>
    /// Асинхронно (на фоновом потоке, отдельное read-only подключение) грузит страницу записей
    /// и их значения. Нужно виртуализации таблицы, чтобы подгрузка строк при скролле НЕ блокировала
    /// UI-поток (ноль микроподвисаний даже при экстремально быстром скролле). WAL → фоновое чтение
    /// не конфликтует с основным контекстом. Режим выбирается параметрами: idSlice (фильтр/сортировка),
    /// иначе keyset по (afterPos, afterId), иначе offset по skip.
    /// </summary>
    public Task<(List<DbRecord> Records, Dictionary<int, Dictionary<int, string>> Values)> LoadPageAsync(
        int collectionId, bool excludeHidden, IReadOnlyList<int>? idSlice,
        int skip, int take, int? afterPos, int? afterId) =>
        Task.Run(() =>
        {
            using var ctx = new AppDbContext(db.DbPath);
            ctx.ApplyPerformancePragmas();
            var repo = new ConstructorRepository(ctx);

            var records = idSlice is not null
                ? repo.GetRecordsByIdsOrdered(idSlice)
                : afterPos is int p && afterId is int aid
                    ? repo.GetRecordsPageAfter(collectionId, p, aid, take, excludeHidden)
                    : repo.GetRecordsPage(collectionId, skip, take, excludeHidden);

            var ids    = records.Select(r => r.Id!.Value).ToList();
            var values = repo.GetFieldValuesBatch(ids);
            return (records, values);
        });

    // ── Фильтр колонок (Excel-стиль): уникальные значения с количеством ───────
    //
    // Порядок значений — как в таблице: по первой позиции записи с этим значением
    // (MIN(Position)); «(Пусто)» всегда последним. Это нужно панели фильтра — при открытии
    // значения идут в том же порядке, что строки таблицы, а сортировку пользователь выбирает сам.

    public List<(string Value, int Count)> GetTitleCounts(int collectionId)
    {
        var groups = db.DbRecords
            .Where(r => r.CollectionId == collectionId)
            .GroupBy(r => r.Title)
            .Select(g => new { Value = g.Key, Cnt = g.Count(), FirstPos = g.Min(r => r.Position) })
            .ToList();
        return OrderValueGroups(groups.Select(g => (g.Value ?? "", g.Cnt, g.FirstPos)));
    }

    public List<(string Value, int Count)> GetDescriptionCounts(int collectionId)
    {
        var groups = db.DbRecords
            .Where(r => r.CollectionId == collectionId)
            .GroupBy(r => r.Description ?? "")
            .Select(g => new { Value = g.Key, Cnt = g.Count(), FirstPos = g.Min(r => r.Position) })
            .ToList();
        return OrderValueGroups(groups.Select(g => (g.Value, g.Cnt, g.FirstPos)));
    }

    /// <summary>
    /// Значения поля с количеством в порядке таблицы (по первой позиции). Записи без строки
    /// значения учитываются в пустом значении "" — в UI оно показывается как «(Пусто)» и идёт в конец.
    /// </summary>
    public List<(string Value, int Count)> GetFieldValueCounts(int collectionId, int fieldId)
    {
        var groups = db.DbFieldValues
            .Where(fv => fv.FieldId == fieldId && fv.Record.CollectionId == collectionId)
            .GroupBy(fv => fv.Value)
            .Select(g => new { Value = g.Key, Cnt = g.Count(), FirstPos = g.Min(fv => fv.Record.Position) })
            .ToList();

        // Непустые — по первой позиции (порядок таблицы).
        var result = groups.Where(g => g.Value != "")
            .OrderBy(g => g.FirstPos)
            .Select(g => (Value: g.Value ?? "", Count: g.Cnt))
            .ToList();

        // «(Пусто)» = записи без строки значения + с пустым значением — последним.
        var total   = db.DbRecords.Count(r => r.CollectionId == collectionId);
        var emptied = total - groups.Where(g => g.Value != "").Sum(g => g.Cnt);
        if (emptied > 0) result.Add(("", emptied));
        return result;
    }

    // Счётчики значений колонки (Title/Desc/поле) по ключу — диспетчер для фона.
    private List<(string Value, int Count)> ColumnCounts(int collectionId, string key) =>
        key == TitleKey ? GetTitleCounts(collectionId)
        : key == DescKey ? GetDescriptionCounts(collectionId)
        : int.TryParse(key, out var fid) ? GetFieldValueCounts(collectionId, fid)
        : [];

    /// <summary>
    /// Асинхронно (на фоновом потоке, отдельное read-only подключение) считает значения колонки
    /// с количеством — чтобы окно фильтра открывалось мгновенно, а UI не подвисал на подсчёте.
    /// WAL допускает одновременное чтение, поэтому фоновое подключение не конфликтует с основным.
    /// </summary>
    public Task<List<(string Value, int Count)>> GetColumnCountsAsync(int collectionId, string key) =>
        Task.Run(() =>
        {
            using var ctx = new AppDbContext(db.DbPath);
            ctx.ApplyPerformancePragmas();
            return new ConstructorRepository(ctx).ColumnCounts(collectionId, key);
        });

    // Упорядочить группы значений по первой позиции (порядок таблицы); пустое — последним.
    private static List<(string Value, int Count)> OrderValueGroups(
        IEnumerable<(string Value, int Count, int FirstPos)> groups)
    {
        var list   = groups.ToList();
        var result = list.Where(g => g.Value != "")
            .OrderBy(g => g.FirstPos)
            .Select(g => (g.Value, g.Count))
            .ToList();
        var empty = list.Where(g => g.Value == "").Sum(g => g.Count);
        if (empty > 0) result.Add(("", empty));
        return result;
    }

    public void SetFieldValue(int recordId, int fieldId, string value)
    {
        var fv = db.DbFieldValues.FirstOrDefault(x => x.RecordId == recordId && x.FieldId == fieldId);
        if (fv is null)
            db.DbFieldValues.Add(new DbFieldValueEntity { RecordId = recordId, FieldId = fieldId, Value = value });
        else
            fv.Value = value;

        // Календарное поле-дата проецируем на колонки записи, которые читает календарь.
        var field = db.DbFields.Find(fieldId);
        if (field is { FieldType: "date", IsCalStart: true })
            ProjectDateToRecord(recordId, value);

        db.SaveChanges();
    }

    // Записывает значение поля-даты в db_records.StartDt/EndDt/AllDay (источник правды
    // для календаря). Пустое/нераспознанное значение убирает запись из календаря.
    private void ProjectDateToRecord(int recordId, string value)
    {
        var rec = db.DbRecords.Find(recordId);
        if (rec is null) return;

        if (DateValue.TryParse(value, out var dv))
        {
            var (startIso, endIso) = dv.ToRecordColumns();
            rec.StartDt = startIso;
            rec.EndDt   = endIso;
            rec.AllDay  = dv.AllDay;
        }
        else
        {
            rec.StartDt = null;
            rec.EndDt   = null;
            rec.AllDay  = false;
        }
    }

    // ── UI настройки ──────────────────────────────────────────────────────────

    public (bool ColResize, bool RowResize, string ViewMode) GetUiSettings(int collectionId)
    {
        var s = db.UiCollectionSettings.Find(collectionId);
        return s is null ? (false, false, "table") : (s.ColResize, s.RowResize, s.ViewMode);
    }

    public void SaveUiSettings(int collectionId, bool colResize, bool rowResize, string viewMode)
    {
        var s = db.UiCollectionSettings.Find(collectionId);
        if (s is null)
        {
            db.UiCollectionSettings.Add(new UiCollectionSettingEntity
            {
                CollectionId = collectionId, ColResize = colResize,
                RowResize = rowResize, ViewMode = viewMode,
            });
        }
        else
        {
            s.ColResize = colResize;
            s.RowResize = rowResize;
            s.ViewMode  = viewMode;
        }
        db.SaveChanges();
    }

    // Подписи системных колонок «Название»/«Описание» (только заданные пользователем).
    // Ключ — TableRowVm.TitleKey / DescKey; значение — отображаемое имя.
    public Dictionary<string, string> GetSystemFieldNames(int collectionId) =>
        db.SystemFieldMeta
          .Where(m => m.CollectionId == collectionId && m.DisplayName != null)
          .ToDictionary(m => m.Key, m => m.DisplayName!);

    // Upsert подписи. Пустое имя или совпадение с дефолтом — удаляем строку
    // (колонка возвращается к имени по умолчанию).
    public void SetSystemFieldName(int collectionId, string key, string? displayName, string defaultName)
    {
        var trimmed = displayName?.Trim();
        var reset   = string.IsNullOrEmpty(trimmed) || trimmed == defaultName;

        var row = db.SystemFieldMeta.FirstOrDefault(m => m.CollectionId == collectionId && m.Key == key);

        if (reset)
        {
            if (row is not null) db.SystemFieldMeta.Remove(row);
        }
        else if (row is null)
        {
            db.SystemFieldMeta.Add(new SystemFieldMetaEntity
            {
                CollectionId = collectionId, Key = key, DisplayName = trimmed,
            });
        }
        else
        {
            row.DisplayName = trimmed;
        }

        db.SaveChanges();
    }

    // ── Порядок колонок (единый для Title/Desc и пользовательских) ─────────────

    // Сохранённый порядок (key -> position); пусто, если в коллекции ещё не двигали.
    public Dictionary<string, int> GetColumnOrder(int collectionId) =>
        db.ColumnOrder
          .Where(o => o.CollectionId == collectionId)
          .ToDictionary(o => o.Key, o => o.Position);

    // Переместить колонку key на одну позицию влево/вправо в общем ряду движимых
    // колонок. movableKeys — текущий порядок всех движимых колонок (как их строит
    // TableView); нужен для ленивой материализации дефолтного порядка.
    public void MoveColumn(int collectionId, string key, bool left, IReadOnlyList<string> movableKeys)
    {
        // Синхронизируем column_order с актуальным набором колонок (movableKeys):
        // удаляем строки с устаревшими ключами (поля могли быть удалены) и
        // перезаписываем порядок целиком в текущей раскладке. Это убирает рассинхрон,
        // из-за которого своп не находил ключ и молча ничего не делал.
        var existing = db.ColumnOrder.Where(o => o.CollectionId == collectionId).ToList();
        db.ColumnOrder.RemoveRange(existing);
        for (var i = 0; i < movableKeys.Count; i++)
            db.ColumnOrder.Add(new ColumnOrderEntity
            {
                CollectionId = collectionId, Key = movableKeys[i], Position = (i + 1) * 1000,
            });
        db.SaveChanges();

        var rows = db.ColumnOrder
            .Where(o => o.CollectionId == collectionId)
            .OrderBy(o => o.Position)
            .ToList();

        var idx = rows.FindIndex(o => o.Key == key);
        if (idx < 0) return;

        var swapIdx = left ? idx - 1 : idx + 1;
        if (swapIdx < 0 || swapIdx >= rows.Count) return;   // крайняя колонка — no-op

        (rows[idx].Position, rows[swapIdx].Position) = (rows[swapIdx].Position, rows[idx].Position);
        db.SaveChanges();
    }

    // Поставить колонку newKey сразу после afterKey в общем порядке. movableKeys —
    // актуальный порядок колонок ДО вставки (newKey там в конце, как создаёт AddField).
    // Перезаписываем column_order из movableKeys, затем перемещаем newKey за afterKey.
    public void MoveColumnAfter(int collectionId, string newKey, string afterKey, IReadOnlyList<string> movableKeys)
    {
        // Целевой порядок: как movableKeys, но newKey вынут и вставлен после afterKey.
        var order = movableKeys.Where(k => k != newKey).ToList();
        var at    = order.IndexOf(afterKey);
        if (at < 0) order.Add(newKey);              // afterKey не найден — оставляем в конце
        else        order.Insert(at + 1, newKey);

        var existing = db.ColumnOrder.Where(o => o.CollectionId == collectionId).ToList();
        db.ColumnOrder.RemoveRange(existing);
        for (var i = 0; i < order.Count; i++)
            db.ColumnOrder.Add(new ColumnOrderEntity
            {
                CollectionId = collectionId, Key = order[i], Position = (i + 1) * 1000,
            });
        db.SaveChanges();
    }

    // Перезаписать порядок колонок целиком из заданной последовательности ключей
    // (как они идут слева-направо). Используется окном «Отображение» при сохранении.
    public void SaveColumnOrder(int collectionId, IReadOnlyList<string> orderedKeys)
    {
        var existing = db.ColumnOrder.Where(o => o.CollectionId == collectionId).ToList();
        db.ColumnOrder.RemoveRange(existing);
        for (var i = 0; i < orderedKeys.Count; i++)
            db.ColumnOrder.Add(new ColumnOrderEntity
            {
                CollectionId = collectionId, Key = orderedKeys[i], Position = (i + 1) * 1000,
            });
        db.SaveChanges();
    }

    public Dictionary<int, int> GetColWidths(int collectionId) =>
        db.UiColWidths
          .Where(w => w.CollectionId == collectionId)
          .ToDictionary(w => w.FieldId, w => w.Width);

    public void SaveColWidths(int collectionId, Dictionary<int, int> widths)
    {
        db.UiColWidths.RemoveRange(db.UiColWidths.Where(w => w.CollectionId == collectionId));
        db.UiColWidths.AddRange(widths.Select(kv => new UiColWidthEntity
        {
            CollectionId = collectionId, FieldId = kv.Key, Width = kv.Value,
        }));
        db.SaveChanges();
    }

    // ── Отображение колонок (видимость + уровень; окно «Отображение») ──────────

    // key → (Visible, Level). Только сохранённые строки; отсутствие ключа = (видима, 1).
    public Dictionary<string, (bool Visible, int Level)> GetColumnDisplay(int collectionId) =>
        db.ColumnDisplay
          .Where(d => d.CollectionId == collectionId)
          .ToDictionary(d => d.Key, d => (d.Visible, d.Level));

    // Перезаписываем отображение колонок целиком. Дефолтные строки (видима, уровень 1)
    // не храним — так таблица не разрастается, а новые колонки по умолчанию видимы.
    public void SaveColumnDisplay(int collectionId, IEnumerable<(string Key, bool Visible, int Level)> entries)
    {
        db.ColumnDisplay.RemoveRange(db.ColumnDisplay.Where(d => d.CollectionId == collectionId));
        foreach (var (key, visible, level) in entries)
        {
            if (visible && level <= 1) continue;   // дефолт — не храним
            db.ColumnDisplay.Add(new ColumnDisplayEntity
            {
                CollectionId = collectionId, Key = key, Visible = visible, Level = Math.Max(1, level),
            });
        }
        db.SaveChanges();
    }

    // ── Отображение записей (видимость + уровень; окно «Отображение записей») ────

    // recordId → (Visible, Level). Только сохранённые строки; отсутствие ключа = (видима, 1).
    public Dictionary<int, (bool Visible, int Level)> GetRecordDisplay(int collectionId) =>
        db.RecordDisplay
          .Where(d => d.CollectionId == collectionId)
          .ToDictionary(d => d.RecordId, d => (d.Visible, d.Level));

    // Перезаписываем отображение записей целиком. Дефолт (видима, уровень 1) не храним —
    // так таблица не разрастается, а новые записи по умолчанию видимы.
    public void SaveRecordDisplay(int collectionId, IEnumerable<(int RecordId, bool Visible, int Level)> entries)
    {
        db.RecordDisplay.RemoveRange(db.RecordDisplay.Where(d => d.CollectionId == collectionId));
        foreach (var (recordId, visible, level) in entries)
        {
            if (visible && level <= 1) continue;   // дефолт — не храним
            db.RecordDisplay.Add(new RecordDisplayEntity
            {
                CollectionId = collectionId, RecordId = recordId, Visible = visible, Level = Math.Max(1, level),
            });
        }
        db.SaveChanges();
    }

    // Пакетная перестановка порядка записей (окно «Отображение записей»). Переписывает
    // Position по порядку списка через bulk-SQL (временная таблица id→Pos + один UPDATE FROM),
    // а не загрузкой всех записей в EF — иначе на коллекциях в сотни тысяч строк зависание.
    public void SaveRecordOrder(int collectionId, IReadOnlyList<int> orderedRecordIds)
    {
        if (orderedRecordIds.Count == 0) return;

        var conn   = db.Database.GetDbConnection();
        var wasOpen = conn.State == System.Data.ConnectionState.Open;
        if (!wasOpen) conn.Open();
        using (var tx = conn.BeginTransaction())
        {
            void Exec(string sql, System.Data.Common.DbTransaction t)
            {
                using var c = conn.CreateCommand();
                c.Transaction = t; c.CommandText = sql; c.ExecuteNonQuery();
            }

            Exec("CREATE TEMP TABLE _ord (Id INTEGER PRIMARY KEY, Pos INTEGER)", tx);

            using (var ins = conn.CreateCommand())
            {
                ins.Transaction = tx;
                ins.CommandText = "INSERT INTO _ord (Id, Pos) VALUES ($id, $pos)";
                var pId  = ins.CreateParameter(); pId.ParameterName  = "$id";  ins.Parameters.Add(pId);
                var pPos = ins.CreateParameter(); pPos.ParameterName = "$pos"; ins.Parameters.Add(pPos);
                for (var i = 0; i < orderedRecordIds.Count; i++)
                {
                    pId.Value  = orderedRecordIds[i];
                    pPos.Value = (i + 1) * 1000;
                    ins.ExecuteNonQuery();
                }
            }

            using (var upd = conn.CreateCommand())
            {
                upd.Transaction = tx;
                upd.CommandText =
                    "UPDATE db_records SET Position = (SELECT Pos FROM _ord WHERE _ord.Id = db_records.Id) " +
                    "WHERE CollectionId = $c AND Id IN (SELECT Id FROM _ord)";
                var pC = upd.CreateParameter(); pC.ParameterName = "$c"; pC.Value = collectionId; upd.Parameters.Add(pC);
                upd.ExecuteNonQuery();
            }

            Exec("DROP TABLE _ord", tx);
            tx.Commit();
        }
        if (!wasOpen) conn.Close();

        // Освежаем позиции у отслеживаемых записей этой коллекции (правка шла мимо EF).
        foreach (var e in db.ChangeTracker.Entries<DbRecordEntity>().ToList())
            if (e.Entity.CollectionId == collectionId) e.Reload();
    }

    public Dictionary<int, int> GetRowHeights(int collectionId) =>
        db.UiRowHeights
          .Where(h => h.CollectionId == collectionId)
          .ToDictionary(h => h.RecordId, h => h.Height);

    public void SaveRowHeights(int collectionId, Dictionary<int, int> heights)
    {
        db.UiRowHeights.RemoveRange(db.UiRowHeights.Where(h => h.CollectionId == collectionId));
        db.UiRowHeights.AddRange(heights.Select(kv => new UiRowHeightEntity
        {
            CollectionId = collectionId, RecordId = kv.Key, Height = kv.Value,
        }));
        db.SaveChanges();
    }

    // ══════════════════════════════════════════════════════════════════════════
    // ─── CalendarRecord API (для календарных представлений) ───────────────────
    // ══════════════════════════════════════════════════════════════════════════

    public CalendarRecord AddCalendarRecord(CalendarRecord r)
    {
        var colId = r.CollectionId ?? GetOrCreateDefaultCollection();
        var fieldIds = EnsureSystemFields(colId);

        var maxPos = db.DbRecords
            .Where(rec => rec.CollectionId == colId)
            .Select(rec => (int?)rec.Position)
            .Max() ?? 0;

        var rec = new DbRecordEntity
        {
            CollectionId = colId,
            StartDt      = DateTime.SpecifyKind(r.StartDt, DateTimeKind.Unspecified).ToString("o"),
            EndDt        = DateTime.SpecifyKind(r.EndDt,   DateTimeKind.Unspecified).ToString("o"),
            AllDay       = r.AllDay,
            CreatedAt    = DateTime.Now.ToString("o"),
            Position     = maxPos + 1000,
            Title        = r.Title,
            Description  = string.IsNullOrEmpty(r.Description) ? null : r.Description,
        };
        db.DbRecords.Add(rec);
        db.SaveChanges();

        SetCalendarFieldValues(rec.Id, fieldIds, r);

        r.Id           = rec.Id;
        r.CollectionId = colId;
        return r;
    }

    public void UpdateCalendarRecord(CalendarRecord r)
    {
        if (r.Id is null) return;

        var rec = db.DbRecords.Find(r.Id.Value);
        if (rec is null) return;

        var oldColId = rec.CollectionId;
        var newColId = r.CollectionId ?? oldColId;

        if (oldColId != newColId)
        {
            db.DbRecords.Remove(rec);
            db.SaveChanges();
            r.CollectionId = newColId;
            r.Id = null;
            AddCalendarRecord(r);
            return;
        }

        rec.StartDt     = DateTime.SpecifyKind(r.StartDt, DateTimeKind.Unspecified).ToString("o");
        rec.EndDt       = DateTime.SpecifyKind(r.EndDt,   DateTimeKind.Unspecified).ToString("o");
        rec.AllDay      = r.AllDay;
        rec.Title       = r.Title;
        rec.Description = string.IsNullOrEmpty(r.Description) ? null : r.Description;
        db.SaveChanges();

        var fieldIds = EnsureSystemFields(newColId);
        SetCalendarFieldValues(rec.Id, fieldIds, r);
    }

    public void DeleteCalendarRecord(int id)
    {
        var rec = db.DbRecords.Find(id);
        if (rec is not null)
        {
            db.DbRecords.Remove(rec);
            db.SaveChanges();
        }
    }

    public List<CalendarRecord> GetCalendarRecordsByRange(DateTime start, DateTime end, ISet<int>? activeIds = null)
    {
        // нормализуем Kind → Unspecified, чтобы строковое сравнение ISO было корректным
        // независимо от того, в каком Kind пришли границы диапазона
        var startIso = DateTime.SpecifyKind(start, DateTimeKind.Unspecified).ToString("o");
        var endIso   = DateTime.SpecifyKind(end,   DateTimeKind.Unspecified).ToString("o");

        var query = db.DbRecords
            .Where(r => r.StartDt != null && r.StartDt.CompareTo(endIso) < 0
                                          && r.EndDt!.CompareTo(startIso) >= 0)
            .OrderBy(r => r.StartDt);

        return RowsToCalendarRecords(query, activeIds);
    }

    public List<CalendarRecord> GetAllCalendarRecords(ISet<int>? activeIds = null)
    {
        var query = db.DbRecords
            .Where(r => r.StartDt != null)
            .OrderBy(r => r.StartDt);

        return RowsToCalendarRecords(query, activeIds);
    }

    public int CountCalendarRecords() =>
        db.DbRecords.Count(r => r.StartDt != null);

    // ── Вспомогательные методы для CalendarRecord ─────────────────────────────

    private int GetOrCreateDefaultCollection()
    {
        var col = db.DbCollections.FirstOrDefault(c => c.Name == "Без названия");
        if (col is not null) return col.Id;

        var maxPos = db.DbCollections.Select(c => (int?)c.Position).Max() ?? 0;
        col = new DbCollectionEntity { Name = "Без названия", Position = maxPos + 1000, Color = "#007AFF" };
        db.DbCollections.Add(col);
        db.SaveChanges();

        EnsureSystemFields(col.Id);
        return col.Id;
    }

    private Dictionary<string, int> EnsureSystemFields(int collectionId)
    {
        var existing = db.DbFields
            .Where(f => f.CollectionId == collectionId)
            .GroupBy(f => f.Name)
            .ToDictionary(g => g.Key, g => g.Min(f => f.Id));

        if (existing.ContainsKey(FieldDate)) return existing;

        var maxPos = db.DbFields
            .Where(f => f.CollectionId == collectionId)
            .Select(f => (int?)f.Position)
            .Max() ?? 0;

        // Системное поле «Дата» — источник календаря (если другой ещё не назначен).
        var hasCalField = db.DbFields.Any(f =>
            f.CollectionId == collectionId && f.FieldType == "date" && f.IsCalStart);

        var field = new DbFieldEntity
        {
            CollectionId = collectionId,
            Name         = FieldDate,
            FieldType    = "date",
            Position     = maxPos + 1000,
            IsSystem     = true,
            IsCalStart   = !hasCalField,
            IsRequired   = false,
        };
        db.DbFields.Add(field);
        db.SaveChanges();
        existing[FieldDate] = field.Id;

        return existing;
    }

    private void SetCalendarFieldValues(int recordId, Dictionary<string, int> fieldIds, CalendarRecord r)
    {
        // В DbFieldValues пишем только системное поле «Дата» — title/description
        // живут в колонках db_records (Title / Description).
        if (!fieldIds.TryGetValue(FieldDate, out var fid)) return;

        var dateStr = new DateValue(r.StartDt, r.EndDt, r.AllDay).ToStorage();

        var fv = db.DbFieldValues.FirstOrDefault(x => x.RecordId == recordId && x.FieldId == fid);
        if (fv is null)
        {
            db.DbFieldValues.Add(new DbFieldValueEntity { RecordId = recordId, FieldId = fid, Value = dateStr });
        }
        else
        {
            fv.Value = dateStr;
        }
        db.SaveChanges();
    }

    private List<CalendarRecord> RowsToCalendarRecords(IQueryable<DbRecordEntity> query, ISet<int>? activeIds)
    {
        var records = query.ToList();
        if (activeIds is not null)
            records = records.Where(r => activeIds.Contains(r.CollectionId)).ToList();

        if (records.Count == 0) return [];

        var colMap = db.DbCollections.ToDictionary(c => c.Id);

        var result = new List<CalendarRecord>();
        foreach (var rec in records)
        {
            if (!DateTime.TryParse(rec.StartDt, out var startDt)) continue;
            if (!DateTime.TryParse(rec.EndDt,   out var endDt))   continue;

            colMap.TryGetValue(rec.CollectionId, out var col);

            result.Add(new CalendarRecord
            {
                Id             = rec.Id,
                Title          = rec.Title ?? "",
                Description    = rec.Description ?? "",
                StartDt        = startDt,
                EndDt          = endDt,
                AllDay         = rec.AllDay,
                CollectionId   = rec.CollectionId,
                CollectionName = col?.Name ?? "",
                Color          = col?.Color ?? "#007AFF",
            });
        }

        return result;
    }

    // ── Разовая миграция/бэкафилл календарных дат (вызывается при старте) ──────

    /// <summary>
    /// 1) Системные поля «Дата» из эпохи, когда они были типа text, переводит в date
    ///    и помечает источником календаря. 2) Бэкафилл: для записей с заполненным
    ///    календарным полем-датой, но пустыми StartDt — проецирует значение на колонки,
    ///    чтобы такие записи появились в календаре. Идемпотентно и дёшево.
    /// </summary>
    public void MigrateAndBackfillCalendarDates()
    {
        var legacy = db.DbFields.Where(f => f.Name == FieldDate && f.FieldType == "text").ToList();
        foreach (var f in legacy)
        {
            f.FieldType = "date";
            var hasOther = db.DbFields.Any(o =>
                o.CollectionId == f.CollectionId && o.Id != f.Id && o.FieldType == "date" && o.IsCalStart);
            if (!hasOther) f.IsCalStart = true;
        }
        if (legacy.Count > 0) db.SaveChanges();

        // Коллекции, где есть поля-даты, но ни одно не назначено календарным: первое
        // (по позиции) становится источником календаря — чтобы существующие даты тоже
        // попали в календарь.
        var calCollections = db.DbFields
            .Where(f => f.FieldType == "date" && f.IsCalStart)
            .Select(f => f.CollectionId)
            .Distinct()
            .ToHashSet();

        var orphanDateFields = db.DbFields
            .Where(f => f.FieldType == "date" && !calCollections.Contains(f.CollectionId))
            .OrderBy(f => f.CollectionId).ThenBy(f => f.Position)
            .ToList();

        var assigned = new HashSet<int>();
        foreach (var f in orphanDateFields)
            if (assigned.Add(f.CollectionId)) f.IsCalStart = true;
        if (assigned.Count > 0) db.SaveChanges();

        var calFieldIds = db.DbFields
            .Where(f => f.FieldType == "date" && f.IsCalStart)
            .Select(f => f.Id)
            .ToList();
        if (calFieldIds.Count == 0) return;

        var pending = db.DbFieldValues
            .Where(v => calFieldIds.Contains(v.FieldId) && v.Value != "" && v.Record.StartDt == null)
            .Select(v => new { v.RecordId, v.Value })
            .ToList();

        var touched = false;
        foreach (var x in pending)
        {
            if (!DateValue.TryParse(x.Value, out var dv)) continue;
            var rec = db.DbRecords.Find(x.RecordId);
            if (rec is null) continue;
            var (startIso, endIso) = dv.ToRecordColumns();
            rec.StartDt = startIso;
            rec.EndDt   = endIso;
            rec.AllDay  = dv.AllDay;
            touched = true;
        }
        if (touched) db.SaveChanges();
    }
}
