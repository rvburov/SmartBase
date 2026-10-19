using System.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using SmartBase.Database.Entities;

namespace SmartBase.Database.Repositories;

// ── Generic-перенос коллекции/группы между БД РАЗНЫХ провайдеров (SQLite ↔ PG, PG ↔ PG) ──
//
// ATTACH существует только у SQLite, поэтому для пар с PostgreSQL перенос идет на ДВУХ
// независимых подключениях: источник читается keyset-страницами в консистентном снимке
// (одна read-транзакция на все чтение), приемник пишется пакетами — записи prepared-вставкой
// с RETURNING Id (точная карта id без допущения непрерывности), значения/проекция/строчные
// UI-таблицы — multi-row INSERT'ами по карте. Контракт тот же, что у ATTACH-переноса
// (см. MoveCollectionIsolated): копируется все, включая UI-настройки; копия и удаление
// источника раздельны, с компенсацией при сбое удаления.
//
// Отличие от ATTACH-пути: копия коммитится ПО ПАКЕТАМ (а не одной транзакцией), поэтому
// kill посреди копии оставил бы частичную копию в приемнике. Против этого — маркер
// crossmove.pending.* в db_meta приемника: ставится до заливки, снимается по завершении;
// свип при монтировании (SweepUnfinishedCrossMove) доудаляет незавершенную копию.
// Источник не трогается до полного завершения копии — после сбоя он всегда цел.
public partial class ConstructorRepository
{
    private const string PendingCollectionKey = "crossmove.pending.collection";
    private const string PendingGroupKey      = "crossmove.pending.group";

    // Перенос коллекции между провайдерами: копия в приемник (this) → уникализация имени →
    // удаление из источника на отдельном подключении (компенсация при сбое — как у ATTACH).
    private int MoveCollectionGeneric(string sourceDbPath, int srcCollectionId, int? targetGroupId,
                                      IProgress<int>? progress, int batchSize)
    {
        using var targetCtx = new AppDbContext(DbPath);
        targetCtx.ApplyBulkPerformancePragmas();
        var target = new ConstructorRepository(targetCtx);

        using var srcCtx = new AppDbContext(sourceDbPath);
        srcCtx.ApplyBulkPerformancePragmas();

        var (newColId, recCount) = CopyCollectionGeneric(srcCtx, targetCtx, target,
            srcCollectionId, targetGroupId, progress, 0, batchSize);
        if (newColId == 0) return 0;

        target.UniquifyCollectionName(newColId, targetGroupId);

        try
        {
            using var delCtx = new AppDbContext(sourceDbPath);
            delCtx.ApplyBulkPerformancePragmas();
            // batchSize тот же, что у копирования: обе фазы двигают одну полосу прогресса,
            // и разные шаги читались бы как рывок на середине.
            new ConstructorRepository(delCtx).DeleteCollectionData(srcCollectionId,
                new BaseProgress(progress, recCount), batchSize);
        }
        catch
        {
            try { target.DeleteCollectionData(newColId, log: false); }
            catch { /* копию убрать не удалось — данные целы в обеих БД */ }
            throw;
        }
        target.LogNow("move", "collection", target.CollectionNameOf(newColId) ?? "", count: recCount);
        return newColId;
    }

    // Перенос группы со всей веткой между провайдерами: структура групп клонируется через EF
    // (строк мало), коллекции — поколлекционно generic-копией с накоплением прогресса. Один
    // маркер на корневую группу покрывает всю ветку (свип удалит ее целиком).
    private int MoveGroupGeneric(string sourceDbPath, int srcGroupId, int? targetParentId,
                                 IProgress<int>? progress, int batchSize)
    {
        using var targetCtx = new AppDbContext(DbPath);
        targetCtx.ApplyBulkPerformancePragmas();
        var target = new ConstructorRepository(targetCtx);

        using var srcCtx = new AppDbContext(sourceDbPath);
        srcCtx.ApplyBulkPerformancePragmas();

        int newRootId;
        long total = 0;
        var map = new Dictionary<int, int>();   // старый id группы → новый

        try
        {
            // Корень + маркер — одной транзакцией приемника: сбой между ними невозможен.
            using (var tx = targetCtx.Database.BeginTransaction())
            {
                newRootId = CloneOneGroup(srcCtx, targetCtx, srcGroupId, targetParentId, map);
                SetMeta(targetCtx, PendingGroupKey, newRootId.ToString());
                tx.Commit();
            }

            // Остальная структура ветки (EF, строк мало).
            CloneGroupChildren(srcCtx, targetCtx, srcGroupId, newRootId, map);

            // Коллекции ветки — по очереди, с общим % (как CopyGroupFromAttached).
            var srcGroupIds = map.Keys.ToList();
            var cols = srcCtx.DbCollections.AsNoTracking()
                .Where(c => c.GroupId != null && srcGroupIds.Contains(c.GroupId.Value))
                .OrderBy(c => c.GroupId).ThenBy(c => c.Position)
                .Select(c => new { c.Id, GroupId = c.GroupId!.Value })
                .ToList();
            foreach (var col in cols)
            {
                var (_, rc) = CopyCollectionGeneric(srcCtx, targetCtx, target,
                    col.Id, map[col.GroupId], progress, total, batchSize, markPending: false);
                total += rc;
            }

            RemoveMeta(targetCtx, PendingGroupKey);
        }
        catch
        {
            // Сбой на фазе копии: убираем частичную ветку из приемника (или это сделает свип).
            try
            {
                if (map.TryGetValue(srcGroupId, out var rootId))
                {
                    target.DeleteGroupData(rootId, log: false);
                    RemoveMeta(targetCtx, PendingGroupKey);
                }
            }
            catch { /* доудалит свип при следующем монтировании */ }
            throw;
        }

        target.UniquifyGroupName(newRootId, targetParentId);

        try
        {
            using var delCtx = new AppDbContext(sourceDbPath);
            delCtx.ApplyBulkPerformancePragmas();
            new ConstructorRepository(delCtx).DeleteGroupData(srcGroupId, new BaseProgress(progress, total),
                                                             batchSize);   // см. MoveCollectionGeneric
        }
        catch
        {
            try { target.DeleteGroupData(newRootId, log: false); }
            catch { /* копию убрать не удалось — данные целы в обеих БД */ }
            throw;
        }
        var movedName = targetCtx.DbGroups.Where(g => g.Id == newRootId)
                                          .Select(g => g.Name).FirstOrDefault() ?? "";
        target.LogNow("move", "group", movedName, count: total);
        return newRootId;
    }

    // ── Движок generic-копии одной коллекции ────────────────────────────────────

    // Копия одной коллекции src → tgt. Возвращает (id новой коллекции, число записей).
    // markPending=false — при переносе группы ветку целиком покрывает групповой маркер.
    private (int NewColId, long RecCount) CopyCollectionGeneric(AppDbContext srcCtx, AppDbContext tgtCtx,
        ConstructorRepository target, int srcCollectionId, int? targetGroupId,
        IProgress<int>? progress, long progressBase, int batchSize, bool markPending = true)
    {
        // Один консистентный снимок источника на все чтение: конкурентные правки других
        // пользователей PG не дадут «рваную» копию (SQLite-читателю то же дает его
        // транзакция чтения). Держится до конца копии.
        using var srcTx = srcCtx.Database.BeginTransaction(
            srcCtx.IsPostgres ? IsolationLevel.RepeatableRead : IsolationLevel.Serializable);
        var srcConn = srcCtx.Database.GetDbConnection();
        var srcDbTx = srcTx.GetDbTransaction();

        var srcCol = srcCtx.DbCollections.AsNoTracking().FirstOrDefault(c => c.Id == srcCollectionId);
        if (srcCol is null) return (0, 0);

        var srcFields = srcCtx.DbFields.AsNoTracking()
            .Where(f => f.CollectionId == srcCollectionId).OrderBy(f => f.Id).ToList();
        var recCount = srcCtx.DbRecords.LongCount(r => r.CollectionId == srcCollectionId);

        // 1) Шелл в приемнике: коллекция + поля + маркер — одной транзакцией (маркер
        //    гарантированно переживает любой сбой заливки).
        int newColId;
        var fieldMap = new Dictionary<int, int>(srcFields.Count);
        using (var tx = tgtCtx.Database.BeginTransaction())
        {
            var maxPos = tgtCtx.DbCollections.Select(c => (int?)c.Position).Max() ?? 0;
            var copy = new DbCollectionEntity
            {
                Name = srcCol.Name, GroupId = targetGroupId,
                Position = maxPos + 1000, Icon = srcCol.Icon, Color = srcCol.Color,
            };
            tgtCtx.DbCollections.Add(copy);
            tgtCtx.SaveChanges();
            newColId = copy.Id;

            foreach (var f in srcFields)
            {
                var nf = new DbFieldEntity
                {
                    CollectionId = newColId, Name = f.Name, FieldType = f.FieldType,
                    ConfigJson = f.ConfigJson, Position = f.Position,
                    IsCalStart = f.IsCalStart, IsCalEnd = f.IsCalEnd,
                    IsSystem = f.IsSystem, IsRequired = f.IsRequired,
                    IsTaskExec = f.IsTaskExec, IsTaskAssigner = f.IsTaskAssigner,
                    IsTaskStatus = f.IsTaskStatus, IsDescription = f.IsDescription,
                    IsDocFile = f.IsDocFile, IsDocType = f.IsDocType,
                };
                tgtCtx.DbFields.Add(nf);
                tgtCtx.SaveChanges();
                fieldMap[f.Id] = nf.Id;
            }

            if (markPending) SetMeta(tgtCtx, PendingCollectionKey, newColId.ToString());
            tx.Commit();
        }

        try
        {
            // 2) Пакетный цикл: keyset-страница записей из источника (row-value сик по индексу
            //    (CollectionId, Position) — портативен и на SQLite ≥3.15, и на PG), на пакет —
            //    одна транзакция приемника: записи prepared-вставкой с RETURNING Id, затем
            //    значения/проекция/строчные UI-таблицы multi-row INSERT'ами через карту id.
            long processed = 0;
            int prevPos = int.MinValue, prevId = 0;
            while (processed < recCount)
            {
                var page = ReadRecordsPage(srcConn, srcDbTx, srcCollectionId, prevPos, prevId, batchSize);
                if (page.Count == 0) break;

                var oldIds = page.Select(r => r.OldId).ToList();
                var values   = ReadValuesFor(srcConn, srcDbTx, oldIds);
                var calRows  = ReadCalendarFor(srcConn, srcDbTx, oldIds);
                var heights  = ReadRowHeightsFor(srcConn, srcDbTx, srcCollectionId, oldIds);
                var displays = ReadRecordDisplayFor(srcConn, srcDbTx, srcCollectionId, oldIds);
                var cells    = ReadCellStylesFor(srcConn, srcDbTx, srcCollectionId, oldIds);

                using (var tx = tgtCtx.Database.BeginTransaction())
                {
                    var recMap = InsertRecordsBatch(tgtCtx, newColId, page);

                    InsertTriples(tgtCtx, "db_field_values", "RecordId, FieldId, Value",
                        values.Select(v => ((long)recMap[v.RecordId], (object)fieldMap[v.FieldId], (object)v.Value)));

                    InsertRows(tgtCtx,
                        "calendar_index", "RecordId, FieldId, CollectionId, StartIso, EndIso, AllDay",
                        calRows.Select(ci => new object[]
                            { recMap[ci.RecordId], fieldMap[ci.FieldId], newColId, ci.StartIso, ci.EndIso, ci.AllDay }));

                    InsertRows(tgtCtx, "ui_row_heights", "CollectionId, RecordId, Height",
                        heights.Select(h => new object[] { newColId, recMap[h.RecordId], h.Height }));

                    InsertRows(tgtCtx, "record_display",
                        "CollectionId, RecordId, Visible, Level, Bg, Fg, HAlign, VAlign, BgAt, FgAt, HAlignAt, VAlignAt",
                        displays.Select(d => new object?[]
                            { newColId, recMap[d.RecordId], d.Visible, d.Level, d.Bg, d.Fg, d.HAlign, d.VAlign,
                              d.BgAt, d.FgAt, d.HAlignAt, d.VAlignAt }));

                    // Key ремапится на новый id поля — как у column_meta ниже; «__title__» едет как есть.
                    InsertRows(tgtCtx, "cell_style", "CollectionId, RecordId, Key, Bg, Fg, HAlign, VAlign",
                        cells.Select(c => new object?[]
                            { newColId, recMap[c.RecordId], RemapColumnKey(c.Key, fieldMap), c.Bg, c.Fg, c.HAlign, c.VAlign }));

                    tx.Commit();
                }

                processed += page.Count;
                prevPos = page[^1].Position;
                prevId  = (int)page[^1].OldId;
                progress?.Report((int)(progressBase + Math.Min(processed, recCount)));
            }
            if (recCount == 0) progress?.Report((int)progressBase);

            // 3) UI-настройки уровня коллекции (строк мало — EF): режим просмотра,
            //    порядок/видимость/подписи/цвет/ширина колонок (числовые ключи — новый id
            //    поля). Ширины едут аспектом column_meta.Width; копирование легаси-таблицы
            //    ui_col_widths оставлено ради старых баз.
            using (var tx = tgtCtx.Database.BeginTransaction())
            {
                foreach (var w in srcCtx.UiColWidths.AsNoTracking().Where(w => w.CollectionId == srcCollectionId).ToList())
                    if (fieldMap.TryGetValue(w.FieldId, out var nfid))
                        tgtCtx.UiColWidths.Add(new UiColWidthEntity
                            { CollectionId = newColId, FieldId = nfid, Width = w.Width });

                foreach (var s in srcCtx.UiCollectionSettings.AsNoTracking().Where(s => s.CollectionId == srcCollectionId).ToList())
                    tgtCtx.UiCollectionSettings.Add(new UiCollectionSettingEntity
                        { CollectionId = newColId, ColResize = s.ColResize, RowResize = s.RowResize, ViewMode = s.ViewMode });

                foreach (var cm in srcCtx.ColumnMeta.AsNoTracking().Where(m => m.CollectionId == srcCollectionId).ToList())
                    tgtCtx.ColumnMeta.Add(new ColumnMetaEntity
                    {
                        CollectionId = newColId, Key = RemapColumnKey(cm.Key, fieldMap), Position = cm.Position,
                        Visible = cm.Visible, Level = cm.Level, DisplayName = cm.DisplayName,
                        Bg = cm.Bg, Fg = cm.Fg, Width = cm.Width, HAlign = cm.HAlign, VAlign = cm.VAlign,
                        BgAt = cm.BgAt, FgAt = cm.FgAt, HAlignAt = cm.HAlignAt, VAlignAt = cm.VAlignAt,
                    });
                tgtCtx.SaveChanges();
                tx.Commit();
            }

            if (markPending) RemoveMeta(tgtCtx, PendingCollectionKey);
        }
        catch
        {
            // Сбой заливки: убираем частичную копию сразу (иначе ее доудалит свип по маркеру).
            try
            {
                target.DeleteCollectionData(newColId, log: false);
                if (markPending) RemoveMeta(tgtCtx, PendingCollectionKey);
            }
            catch { /* доудалит свип при следующем монтировании */ }
            throw;
        }

        return (newColId, recCount);
    }

    /// <summary>
    /// Свип незавершенного generic-переноса и дубля при монтировании: kill посреди пакетной заливки
    /// оставляет в приемнике частичную копию под маркером crossmove.pending.* (дубль коллекции —
    /// duplicate.pending.collection) — доудаляем ее (источник не тронут, его данные целы). Сбой
    /// свипа не валит монтирование: маркер остается, доудалим в следующий раз.
    /// </summary>
    public void SweepUnfinishedCrossMove()
    {
        try
        {
            if (int.TryParse(GetMeta(db, PendingCollectionKey), out var colId))
            {
                Serilog.Log.Warning("Незавершенный перенос в {Db:l}: найден маркер {Key:l} — частичная копия коллекции {Id} удаляется",
                                    db.DisplayPath, PendingCollectionKey, colId);
                DeleteCollectionData(colId, log: false);
                RemoveMeta(db, PendingCollectionKey);
                Serilog.Log.Information("Незавершенный перенос в {Db:l}: частичная копия коллекции {Id} удалена", db.DisplayPath, colId);
            }
            if (int.TryParse(GetMeta(db, PendingDuplicateKey), out var dupId))
            {
                Serilog.Log.Warning("Незавершенный дубль в {Db:l}: найден маркер {Key:l} — частичная копия коллекции {Id} удаляется",
                                    db.DisplayPath, PendingDuplicateKey, dupId);
                DeleteCollectionData(dupId, log: false);
                RemoveMeta(db, PendingDuplicateKey);
                Serilog.Log.Information("Незавершенный дубль в {Db:l}: частичная копия коллекции {Id} удалена", db.DisplayPath, dupId);
            }
            if (int.TryParse(GetMeta(db, PendingGroupKey), out var grpId))
            {
                Serilog.Log.Warning("Незавершенный перенос в {Db:l}: найден маркер {Key:l} — частичная копия группы {Id} удаляется",
                                    db.DisplayPath, PendingGroupKey, grpId);
                DeleteGroupData(grpId, log: false);
                RemoveMeta(db, PendingGroupKey);
                Serilog.Log.Information("Незавершенный перенос в {Db:l}: частичная копия группы {Id} удалена", db.DisplayPath, grpId);
            }
        }
        catch (Exception ex)
        {
            // Маркер остается — доудалим при следующем монтировании; след в журнале нужен,
            // иначе частичная копия живет в базе молча.
            Serilog.Log.Warning(ex, "Свип незавершенного переноса в {Db:l} не удался — повтор при следующем монтировании", db.DisplayPath);
        }
    }

    // ── Клонирование структуры групп (EF, строк мало) ───────────────────────────

    private static int CloneOneGroup(AppDbContext src, AppDbContext tgt, int srcGroupId,
                                     int? targetParentId, Dictionary<int, int> map)
    {
        var g = src.DbGroups.AsNoTracking().First(x => x.Id == srcGroupId);
        var maxPos = tgt.DbGroups.Where(x => x.ParentId == targetParentId)
                        .Select(x => (int?)x.Position).Max() ?? 0;
        var copy = new DbGroupEntity { Name = g.Name, ParentId = targetParentId, Position = maxPos + 1000 };
        tgt.DbGroups.Add(copy);
        tgt.SaveChanges();
        map[srcGroupId] = copy.Id;
        return copy.Id;
    }

    private static void CloneGroupChildren(AppDbContext src, AppDbContext tgt, int srcGroupId,
                                           int newParentId, Dictionary<int, int> map)
    {
        var children = src.DbGroups.AsNoTracking()
            .Where(x => x.ParentId == srcGroupId).OrderBy(x => x.Position)
            .Select(x => x.Id).ToList();
        foreach (var childId in children)
        {
            var newId = CloneOneGroup(src, tgt, childId, newParentId, map);
            CloneGroupChildren(src, tgt, childId, newId, map);
        }
    }

    // ── Чтение источника (raw, в снимке srcTx) ──────────────────────────────────

    private sealed record RecPage(long OldId, string CreatedAt, int Position,
                                  string? StartDt, string? EndDt, int AllDay,
                                  string Title,
                                  string UpdatedAt, string CreatedBy, string UpdatedBy,
                                  string? Color);

    private static List<RecPage> ReadRecordsPage(System.Data.Common.DbConnection conn,
        System.Data.Common.DbTransaction tx, int collectionId, int afterPos, int afterId, int take)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        // Числа в SQL — int, которые мы формируем сами, поэтому интерполяция безопасна.
        cmd.CommandText = $@"
            SELECT Id, CreatedAt, Position, StartDt, EndDt, AllDay, Title,
                   UpdatedAt, CreatedBy, UpdatedBy, Color
            FROM db_records
            WHERE CollectionId = {collectionId} AND (Position, Id) > ({afterPos}, {afterId})
            ORDER BY Position, Id LIMIT {take}";
        using var r = cmd.ExecuteReader();
        var list = new List<RecPage>(take);
        // Id через Convert: у SQLite INTEGER читается как long, у PG integer — как int.
        while (r.Read())
            list.Add(new RecPage(
                Convert.ToInt64(r.GetValue(0)), r.GetString(1), r.GetInt32(2),
                r.IsDBNull(3) ? null : r.GetString(3),
                r.IsDBNull(4) ? null : r.GetString(4),
                r.GetInt32(5), r.GetString(6),
                r.IsDBNull(7) ? "" : r.GetString(7),
                r.IsDBNull(8) ? "" : r.GetString(8),
                r.IsDBNull(9) ? "" : r.GetString(9),
                r.IsDBNull(10) ? null : r.GetString(10)));
        return list;
    }

    private sealed record ValueRow(long RecordId, int FieldId, string Value);
    private sealed record CalRow(long RecordId, int FieldId, string StartIso, string EndIso, int AllDay);
    private sealed record HeightRow(long RecordId, int Height);
    private sealed record DisplayRow(long RecordId, int Visible, int Level, string? Bg, string? Fg, string? HAlign, string? VAlign,
                                     long? BgAt, long? FgAt, long? HAlignAt, long? VAlignAt);
    private sealed record CellStyleRow(long RecordId, string Key, string? Bg, string? Fg, string? HAlign, string? VAlign);

    // Ключ колонки (column_meta / cell_style) в терминах приемника: числовой ключ — это id
    // поля, его переводим по карте; «__title__» и незнакомые ключи едут как есть.
    private static string RemapColumnKey(string key, Dictionary<int, int> fieldMap) =>
        int.TryParse(key, out var oldFid) && fieldMap.TryGetValue(oldFid, out var nfid)
            ? nfid.ToString()
            : key;

    private static List<ValueRow> ReadValuesFor(System.Data.Common.DbConnection conn,
        System.Data.Common.DbTransaction tx, List<long> recordIds)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "SELECT RecordId, FieldId, Value FROM db_field_values " +
                          $"WHERE RecordId IN ({string.Join(",", recordIds)})";
        using var r = cmd.ExecuteReader();
        var list = new List<ValueRow>();
        while (r.Read()) list.Add(new ValueRow(Convert.ToInt64(r.GetValue(0)), r.GetInt32(1), r.GetString(2)));
        return list;
    }

    private static List<CalRow> ReadCalendarFor(System.Data.Common.DbConnection conn,
        System.Data.Common.DbTransaction tx, List<long> recordIds)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "SELECT RecordId, FieldId, StartIso, EndIso, AllDay FROM calendar_index " +
                          $"WHERE RecordId IN ({string.Join(",", recordIds)})";
        using var r = cmd.ExecuteReader();
        var list = new List<CalRow>();
        while (r.Read())
            list.Add(new CalRow(Convert.ToInt64(r.GetValue(0)), r.GetInt32(1), r.GetString(2), r.GetString(3), r.GetInt32(4)));
        return list;
    }

    private static List<HeightRow> ReadRowHeightsFor(System.Data.Common.DbConnection conn,
        System.Data.Common.DbTransaction tx, int collectionId, List<long> recordIds)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "SELECT RecordId, Height FROM ui_row_heights " +
                          $"WHERE CollectionId = {collectionId} AND RecordId IN ({string.Join(",", recordIds)})";
        using var r = cmd.ExecuteReader();
        var list = new List<HeightRow>();
        while (r.Read()) list.Add(new HeightRow(Convert.ToInt64(r.GetValue(0)), r.GetInt32(1)));
        return list;
    }

    private static List<DisplayRow> ReadRecordDisplayFor(System.Data.Common.DbConnection conn,
        System.Data.Common.DbTransaction tx, int collectionId, List<long> recordIds)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "SELECT RecordId, Visible, Level, Bg, Fg, HAlign, VAlign, BgAt, FgAt, HAlignAt, VAlignAt FROM record_display " +
                          $"WHERE CollectionId = {collectionId} AND RecordId IN ({string.Join(",", recordIds)})";
        using var r = cmd.ExecuteReader();
        var list = new List<DisplayRow>();
        while (r.Read())
            list.Add(new DisplayRow(Convert.ToInt64(r.GetValue(0)), r.GetInt32(1), r.GetInt32(2),
                                    TextOrNull(r, 3), TextOrNull(r, 4), TextOrNull(r, 5), TextOrNull(r, 6),
                                    LongOrNull(r, 7), LongOrNull(r, 8), LongOrNull(r, 9), LongOrNull(r, 10)));
        return list;
    }

    // Оформление ячеек страницы записей. Key — строковый («__title__» либо id поля),
    // поэтому ремап на новые id полей делает вызывающий (как у column_meta).
    private static List<CellStyleRow> ReadCellStylesFor(System.Data.Common.DbConnection conn,
        System.Data.Common.DbTransaction tx, int collectionId, List<long> recordIds)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "SELECT RecordId, Key, Bg, Fg, HAlign, VAlign FROM cell_style " +
                          $"WHERE CollectionId = {collectionId} AND RecordId IN ({string.Join(",", recordIds)})";
        using var r = cmd.ExecuteReader();
        var list = new List<CellStyleRow>();
        while (r.Read())
            list.Add(new CellStyleRow(Convert.ToInt64(r.GetValue(0)), r.GetString(1),
                                      TextOrNull(r, 2), TextOrNull(r, 3), TextOrNull(r, 4), TextOrNull(r, 5)));
        return list;
    }

    private static string? TextOrNull(System.Data.Common.DbDataReader r, int i) => r.IsDBNull(i) ? null : r.GetString(i);
    private static long? LongOrNull(System.Data.Common.DbDataReader r, int i) => r.IsDBNull(i) ? null : Convert.ToInt64(r.GetValue(i));

    // ── Запись в приемник ───────────────────────────────────────────────────────

    // Записи пакета: точная карта старый id → новый, без допущения непрерывности (на PG
    // identity-последовательность делят все клиенты).
    //  • SQLite — prepared-вставка с RETURNING Id.
    //  • PG — id берутся из самой последовательности одним запросом (nextval выданное никому больше
    //    не отдаст; identity BY DEFAULT принимает явные id — так же делает дубль на PG,
    //    CopyCollectionDataPg), записи уходят потоком COPY. По команде на запись перенос платил
    //    сетевой ход и разбор запроса сервером за каждую запись: на стенде 300 тыс. записей в PG
    //    одни записи шли 31 с, на сервере в сети — задержка сети на каждую.
    private static Dictionary<long, long> InsertRecordsBatch(AppDbContext tgt, int newColId, List<RecPage> page)
    {
        var conn = tgt.Database.GetDbConnection();
        var dbTx = tgt.Database.CurrentTransaction!.GetDbTransaction();
        var map  = new Dictionary<long, long>(page.Count);

        if (conn is Npgsql.NpgsqlConnection pg)
        {
            using (var ids = conn.CreateCommand())
            {
                ids.Transaction = dbTx;
                ids.CommandText = "SELECT nextval(pg_get_serial_sequence('db_records', 'id')) " +
                                  $"FROM generate_series(1, {page.Count})";
                using var r = ids.ExecuteReader();
                var at = 0;
                while (r.Read()) map[page[at++].OldId] = r.GetInt64(0);
            }
            CopyRows(pg, "db_records",
                "Id, CollectionId, CreatedAt, Position, StartDt, EndDt, AllDay, Title, UpdatedAt, CreatedBy, UpdatedBy, Color",
                page.Select(rec => new object?[]
                {
                    map[rec.OldId], newColId, rec.CreatedAt, rec.Position, rec.StartDt, rec.EndDt, rec.AllDay,
                    rec.Title, rec.UpdatedAt, rec.CreatedBy, rec.UpdatedBy, rec.Color,
                }));
            return map;
        }

        using var ins = conn.CreateCommand();
        ins.Transaction = dbTx;
        ins.CommandText =
            "INSERT INTO db_records (CollectionId, CreatedAt, Position, StartDt, EndDt, AllDay, Title, UpdatedAt, CreatedBy, UpdatedBy, Color) " +
            "VALUES (@cid, @created, @pos, @s, @e, @a, @t, @ua, @cb, @ub, @clr) RETURNING Id";
        var pCid     = AddParam(ins, "@cid",     DbType.Int32);
        var pCreated = AddParam(ins, "@created", DbType.String);
        var pPos     = AddParam(ins, "@pos",     DbType.Int32);
        var pS       = AddParam(ins, "@s",       DbType.String);
        var pE       = AddParam(ins, "@e",       DbType.String);
        var pA       = AddParam(ins, "@a",       DbType.Int32);
        var pT       = AddParam(ins, "@t",       DbType.String);
        var pUa      = AddParam(ins, "@ua",      DbType.String);   // перенос сохраняет аудит-метаданные
        var pCb      = AddParam(ins, "@cb",      DbType.String);
        var pUb      = AddParam(ins, "@ub",      DbType.String);
        var pClr     = AddParam(ins, "@clr",     DbType.String);   // сквозной цвет записи едет с ней
        ins.Prepare();

        pCid.Value = newColId;
        foreach (var rec in page)
        {
            pCreated.Value = rec.CreatedAt;
            pPos.Value     = rec.Position;
            pS.Value       = (object?)rec.StartDt ?? DBNull.Value;
            pE.Value       = (object?)rec.EndDt ?? DBNull.Value;
            pA.Value       = rec.AllDay;
            pT.Value       = rec.Title;
            pUa.Value      = rec.UpdatedAt;
            pCb.Value      = rec.CreatedBy;
            pUb.Value      = rec.UpdatedBy;
            pClr.Value     = (object?)rec.Color ?? DBNull.Value;
            map[rec.OldId] = Convert.ToInt64(ins.ExecuteScalar()!);
        }
        return map;
    }

    private static void InsertTriples(AppDbContext tgt, string table, string columns,
                                      IEnumerable<(long A, object B, object C)> rows)
        => InsertRows(tgt, table, columns, rows.Select(r => new object[] { r.A, r.B, r.C }));

    // Строки в приемник: на SQLite — одной prepared-командой на строку, на PG — потоком COPY.
    // Прежде шли multi-row INSERT по 500 строк с параметрами @p0…@p1499: каждый кусок
    // разбирался заново, а привязка параметра по имени ищет его среди всех параметров команды —
    // цена куска росла как квадрат его размера, и на 300 тыс. записей значения и проекция
    // переносились больше двух минут в обе стороны.
    private static void InsertRows(AppDbContext tgt, string table, string columns, IEnumerable<object?[]> rows)
    {
        var conn = tgt.Database.GetDbConnection();
        if (conn is Npgsql.NpgsqlConnection pg) { CopyRows(pg, table, columns, rows); return; }

        var width = columns.Split(',').Length;
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tgt.Database.CurrentTransaction!.GetDbTransaction();
        cmd.CommandText = $"INSERT INTO {table} ({columns}) VALUES ({string.Join(", ", Enumerable.Range(0, width).Select(i => $"@p{i}"))})";
        var ps = Enumerable.Range(0, width).Select(i => AddParam(cmd, $"@p{i}")).ToArray();
        var prepared = false;
        foreach (var row in rows)
        {
            for (var i = 0; i < width; i++) ps[i].Value = row[i] ?? DBNull.Value;
            if (!prepared) { cmd.Prepare(); prepared = true; }
            cmd.ExecuteNonQuery();
        }
    }

    // Поток COPY в текстовом формате: тип каждого столбца разбирает сервер, как у INSERT, —
    // значения пишутся строками, null — \N. Идет в открытой транзакции подключения.
    private static void CopyRows(Npgsql.NpgsqlConnection pg, string table, string columns, IEnumerable<object?[]> rows)
    {
        using var w = pg.BeginTextImport($"COPY {table} ({columns}) FROM STDIN");
        var line = new System.Text.StringBuilder();
        foreach (var row in rows)
        {
            line.Clear();
            for (var i = 0; i < row.Length; i++)
            {
                if (i > 0) line.Append('\t');
                AppendCopyValue(line, row[i]);
            }
            line.Append('\n');
            w.Write(line);
        }
    }

    // Значение для текстового COPY: обратная косая, табуляция и переводы строк экранируются
    // (иначе строка значения порвала бы строку потока), null — \N.
    internal static void AppendCopyValue(System.Text.StringBuilder sb, object? value)
    {
        switch (value)
        {
            case null or DBNull:
                sb.Append(@"\N");
                return;
            case string s:
                foreach (var ch in s)
                    switch (ch)
                    {
                        case '\\': sb.Append(@"\\"); break;
                        case '\t':  sb.Append(@"\t");  break;
                        case '\n':  sb.Append(@"\n");  break;
                        case '\r':  sb.Append(@"\r");  break;
                        default:    sb.Append(ch);     break;
                    }
                return;
            case IFormattable f:
                sb.Append(f.ToString(null, System.Globalization.CultureInfo.InvariantCulture));
                return;
            default:
                sb.Append(value);
                return;
        }
    }

    // ── db_meta: маркер незавершенного переноса ─────────────────────────────────

    private static string? GetMeta(AppDbContext ctx, string key) =>
        ctx.DbMeta.AsNoTracking().FirstOrDefault(m => m.Key == key)?.Value;

    private static void SetMeta(AppDbContext ctx, string key, string value)
    {
        var row = ctx.DbMeta.FirstOrDefault(m => m.Key == key);
        if (row is null) ctx.DbMeta.Add(new DbMetaEntity { Key = key, Value = value });
        else             row.Value = value;
        ctx.SaveChanges();
    }

    private static void RemoveMeta(AppDbContext ctx, string key)
    {
        var row = ctx.DbMeta.FirstOrDefault(m => m.Key == key);
        if (row is null) return;
        ctx.DbMeta.Remove(row);
        ctx.SaveChanges();
    }
}
