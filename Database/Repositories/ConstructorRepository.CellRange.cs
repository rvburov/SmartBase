using System.Data;
using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using SmartBase.Core.Models;
using SmartBase.Utils;

namespace SmartBase.Database.Repositories;

/// <summary>
/// Массовая правка значений ПРЯМОУГОЛЬНИКА ячеек: вставка из буфера обмена, очистка выделения и
/// отмена этих действий.
///
/// Зачем отдельный путь, а не цикл <c>SetFieldValue</c>: обычная правка ячейки делает до трех
/// SaveChanges, сырой UPDATE аудита, запрос к calendar_index и чтение списка файл-столбцов —
/// около 18 значений в секунду. Диапазон 100×20 ушел бы в минуты работы на потоке интерфейса,
/// в две тысячи коммитов (на сетевой папке каждый берет исключительную блокировку) и в две
/// тысячи строк журнала. Здесь все ложится ОДНОЙ транзакцией — значения, названия, аудит,
/// проекция календаря, уведомления задач и ОДНА строка журнала со счетом — как того требует
/// правило проекта для пакетных путей.
/// </summary>
public sealed partial class ConstructorRepository
{
    /// <summary>Одно значение прямоугольника: куда и что писать. Пустое значение очищает ячейку.
    /// <paramref name="ExpectedOld"/> — писать, только если сейчас в ячейке ровно это («нет
    /// строки» = ""): так отмена не затирает то, что после вставки успел поправить сосед.</summary>
    public readonly record struct CellWrite(int RecordId, int FieldId, string Value, string? ExpectedOld = null);

    /// <summary>Правка системного столбца «Название» (он живет в самой записи, а не в таблице
    /// значений). <paramref name="ExpectedOld"/> — как у <see cref="CellWrite"/>.</summary>
    public readonly record struct TitleWrite(int RecordId, string Title, string? ExpectedOld = null)
    {
        public static implicit operator TitleWrite((int RecordId, string Title) title) => new(title.RecordId, title.Title);
    }

    /// <summary>Что реально поменялось — для отмены. <paramref name="FieldId"/> null — название.</summary>
    public sealed record CellChange(int RecordId, int? FieldId, string OldValue, string NewValue);

    /// <summary>Итог пакетной записи.</summary>
    /// <param name="Changed">Сколько значений и названий реально изменилось.</param>
    /// <param name="Conflicts">Сколько пропущено, потому что в ячейке уже не то, что ждали
    /// (<see cref="CellWrite.ExpectedOld"/>).</param>
    /// <param name="Changes">Старые и новые значения; пусто, если их больше
    /// <see cref="UndoChangesLimit"/> (<paramref name="ChangesTruncated"/>).</param>
    /// <param name="AutoFileChanges">Пересчитанные файл-столбцы с авто-привязкой: правка их
    /// столбца-источника меняет и их.</param>
    public sealed record CellBulkResult(
        int Changed,
        int Conflicts,
        IReadOnlyList<CellChange> Changes,
        bool ChangesTruncated,
        IReadOnlyList<(int RecordId, int FieldId, string Value)> AutoFileChanges)
    {
        public static CellBulkResult Nothing { get; } = new(0, 0, [], false, []);
    }

    /// <summary>Сколько изменений одной операции запоминается для отмены. Больше — операцию не
    /// отменить: старые значения сотен тысяч ячеек держали бы в памяти десятки мегабайт.</summary>
    public const int UndoChangesLimit = 100_000;

    // Уведомления задач пакет пишет, только пока правок исполнителя и стадии немного: вставка
    // исполнителя в двадцать задач — двадцать осмысленных баннеров, а в двадцать тысяч — очередь,
    // которую никто не прочтет (массовые пути уведомлений не дают, см. TaskEvents).
    private const int BulkTaskEventsLimit = 200;

    /// <summary>
    /// Записать значения ячеек пакетом. Значения, совпавшие с текущими, не пишутся и в счет не
    /// идут — иначе повторная вставка того же блока поднимала бы «изменено» у всех записей и
    /// мусорила журналом. Пустое название допустимо: в ячейке его заменит «Без названия».
    /// </summary>
    /// <param name="operation">Как назвать операцию в журнале: «Вставка», «Очистка», «Отмена вставки».</param>
    public CellBulkResult SetCellsBulk(int collectionId,
                                       IReadOnlyList<CellWrite> cells,
                                       IReadOnlyList<TitleWrite>? titles = null,
                                       string operation = "Правка")
    {
        titles ??= [];
        var periodFields = db.DbFields.AsNoTracking().Where(f => f.CollectionId == collectionId && f.FieldType == "date_period")
            .Select(f => f.Id).ToHashSet();
        cells = cells.Where(c => !periodFields.Contains(c.FieldId)).ToArray();
        if (cells.Count == 0 && titles.Count == 0) return CellBulkResult.Nothing;

        // Одна транзакция на все, открытая ДО чтения: текущие значения, отсев удаленных записей и
        // проверка ExpectedOld должны видеть то же состояние, в которое идет запись. На SQLite
        // транзакция EF — BEGIN IMMEDIATE: блокировка записи берется сразу, и сосед не вклинится
        // между чтением и записью.
        List<CellWrite> inserts, updates, deletes;
        List<(int RecordId, string Title)> titleChanges;
        List<CellChange> changes;
        int conflicts;
        string stamp, author;

        using (var tx = db.Database.BeginTransaction())
        {
            // Пишем только в записи, которые ЕЩЕ есть и принадлежат этой коллекции. Сосед мог удалить
            // запись, пока таблица показывала ее строку: без отсева INSERT ее значения упирался бы во
            // внешний ключ и откатывал ВЕСЬ пакет вместе с живыми записями.
            var alive = AliveRecordIds(collectionId, cells.Select(c => c.RecordId).Concat(titles.Select(t => t.RecordId)));
            var liveCells  = LastWins(cells.Where(c => alive.Contains(c.RecordId)), c => (c.RecordId, c.FieldId));
            var liveTitles = LastWins(titles.Where(t => alive.Contains(t.RecordId)), t => t.RecordId);

            (inserts, updates, deletes, changes, conflicts) = DiffCells(liveCells);
            (titleChanges, var titleDiff, var titleConflicts) = DiffTitles(liveTitles);
            changes.AddRange(titleDiff);
            conflicts += titleConflicts;

            if (changes.Count == 0) return new CellBulkResult(0, conflicts, [], false, []);

            var touched = changes.Select(c => c.RecordId).Distinct().ToList();
            var dbTx    = tx.GetDbTransaction();
            var conn    = db.Database.GetDbConnection();

            WriteValues(conn, dbTx, collectionId, inserts, updates, deletes, titleChanges);

            // Аудит «последней правки» — для столбцов «Дата изменения» и «Изменение пользователем».
            stamp  = DateTime.Now.ToString("o");
            author = CurrentUser.DisplayName;
            Run(conn, dbTx, "UPDATE db_records SET UpdatedAt = @at, UpdatedBy = @by WHERE Id = @rid",
                touched, (p, id) => { p["@rid"].Value = id; p["@at"].Value = stamp; p["@by"].Value = author; },
                ["@rid", "@at", "@by"]);

            // Проекция календаря — в той же транзакции: раньше она шла после коммита отдельной
            // записью на каждую ячейку-дату (3000 дат — втрое дольше того же объема текста), и
            // сбой посередине оставлял значения записанными, а календарь — старым.
            SyncCalendarForDateFields(inserts.Concat(updates).Concat(deletes).ToList());

            var names = CollectionNameOf(collectionId) ?? "";
            WriteTaskEvents(collectionId, changes);
            Log("update", "record", names, names, count: changes.Count, detail: BatchDetail(operation, changes));
            db.SaveChanges();

            tx.Commit();
        }

        foreach (var id in changes.Select(c => c.RecordId).Distinct())
            if (db.DbRecords.Local.FirstOrDefault(x => x.Id == id) is { } tracked)
            {
                tracked.UpdatedAt = stamp;
                tracked.UpdatedBy = author;
            }

        // Слияние побуквенных правок держит в памяти недавнюю строку журнала по ячейке. После
        // пакета она уже не про последнюю правку: следующая правка той же ячейки удалила бы ее и
        // записала «до» из времени ПЕРЕД вставкой — вставка пропала бы из истории.
        foreach (var change in changes)
            _editMerges.Remove(change.FieldId is int fid ? ("field", change.RecordId, fid) : ("title", change.RecordId, 0));

        // Локальный кеш EF сырые команды не видят: без синхронизации панель записи и повторное
        // чтение отдали бы прежние значения (та же оговорка, что у аудита в SetFieldValue).
        SyncLocalCache(inserts, updates, deletes, titleChanges);

        var autoFiles = ReactToBulkSourceEdits(collectionId, changes);
        autoFiles.AddRange(DatePeriodBulkSourceChanges(collectionId, changes));
        NotifyRelationDataChanged(collectionId);

        return changes.Count > UndoChangesLimit
            ? new CellBulkResult(changes.Count, conflicts, [], true, autoFiles)
            : new CellBulkResult(changes.Count, conflicts, changes, false, autoFiles);
    }

    // Одна и та же ячейка могла прийти в пакете дважды — побеждает последнее значение, как и при
    // поштучной записи по порядку.
    private static List<T> LastWins<T, TKey>(IEnumerable<T> items, Func<T, TKey> key) where TKey : notnull
    {
        var byKey = new Dictionary<TKey, T>();
        foreach (var item in items) byKey[key(item)] = item;
        return [.. byKey.Values];
    }

    private (List<CellWrite> Inserts, List<CellWrite> Updates, List<CellWrite> Deletes, List<CellChange> Changes, int Conflicts)
        DiffCells(List<CellWrite> cells)
    {
        var inserts   = new List<CellWrite>();
        var updates   = new List<CellWrite>();
        var deletes   = new List<CellWrite>();
        var changes   = new List<CellChange>();
        var conflicts = 0;
        if (cells.Count == 0) return (inserts, updates, deletes, changes, conflicts);

        // Текущие значения затронутых ячеек — одним запросом: без них не отличить настоящую
        // правку от повторной вставки того же блока.
        var recordIds = cells.Select(c => c.RecordId).Distinct().ToList();
        var fieldIds  = cells.Select(c => c.FieldId).Distinct().ToList();
        var current = db.DbFieldValues.AsNoTracking()
            .Where(v => recordIds.Contains(v.RecordId) && fieldIds.Contains(v.FieldId))
            .Select(v => new { v.RecordId, v.FieldId, v.Value })
            .ToList()
            .ToDictionary(v => (v.RecordId, v.FieldId), v => v.Value ?? "");

        foreach (var cell in cells)
        {
            // «Нет строки» и «пустое значение» — одно и то же состояние.
            var old   = current.TryGetValue((cell.RecordId, cell.FieldId), out var existing) ? existing : "";
            var value = cell.Value ?? "";

            if (cell.ExpectedOld is { } expected && expected != old) { conflicts++; continue; }
            if (old == value) continue;                              // уже такое — не трогаем

            if (old.Length == 0)        inserts.Add(cell with { Value = value });
            else if (value.Length == 0) deletes.Add(cell);           // инвариант «нет строки = пусто»
            else                        updates.Add(cell with { Value = value });

            changes.Add(new CellChange(cell.RecordId, cell.FieldId, old, value));
        }

        return (inserts, updates, deletes, changes, conflicts);
    }

    // Названия, которые реально меняются: сравниваем с текущими по той же причине, что и значения.
    private (List<(int RecordId, string Title)> Writes, List<CellChange> Changes, int Conflicts)
        DiffTitles(List<TitleWrite> titles)
    {
        var writes    = new List<(int RecordId, string Title)>();
        var changes   = new List<CellChange>();
        var conflicts = 0;
        if (titles.Count == 0) return (writes, changes, conflicts);

        var ids = titles.Select(t => t.RecordId).ToList();
        var now = db.DbRecords.AsNoTracking()
            .Where(r => ids.Contains(r.Id))
            .Select(r => new { r.Id, r.Title })
            .ToList()
            .ToDictionary(r => r.Id, r => r.Title ?? "");

        foreach (var title in titles)
        {
            var old   = now.GetValueOrDefault(title.RecordId, "");
            var value = title.Title ?? "";

            if (title.ExpectedOld is { } expected && expected != old) { conflicts++; continue; }
            if (old == value) continue;

            writes.Add((title.RecordId, value));
            changes.Add(new CellChange(title.RecordId, null, old, value));
        }

        return (writes, changes, conflicts);
    }

    // Какие из записей еще живы и лежат в этой коллекции. Запись из чужой коллекции сюда попасть
    // не должна вовсе, но проверка стоит той же строки запроса.
    private HashSet<int> AliveRecordIds(int collectionId, IEnumerable<int> recordIds)
    {
        var ids = recordIds.Distinct().ToList();
        if (ids.Count == 0) return [];
        return db.DbRecords.AsNoTracking()
            .Where(r => r.CollectionId == collectionId && ids.Contains(r.Id))
            .Select(r => r.Id)
            .ToHashSet();
    }

    // Значения и названия — подготовленными командами в открытой транзакции.
    private static void WriteValues(DbConnection conn, DbTransaction tx, int collectionId,
                                    List<CellWrite> inserts, List<CellWrite> updates, List<CellWrite> deletes,
                                    List<(int RecordId, string Title)> titles)
    {
        // Вставка — через SELECT ... WHERE EXISTS и ON CONFLICT: запись отсеяна как живая, но на
        // PostgreSQL ее еще могут удалить до коммита, а сосед — завести ту же ячейку («последний
        // побеждает», как у одиночной правки). WHERE у SELECT обязателен и по другой причине: без
        // него SQLite не отличает ON конфликта от ON соединения. Синтаксис один на оба провайдера.
        if (inserts.Count > 0)
            Run(conn, tx,
                "INSERT INTO db_field_values (RecordId, FieldId, Value) " +
                "SELECT @rid, @fid, @val WHERE EXISTS (SELECT 1 FROM db_records WHERE Id = @rid AND CollectionId = @cid) " +
                "ON CONFLICT (RecordId, FieldId) DO UPDATE SET Value = excluded.Value",
                inserts, (p, c) => { p["@rid"].Value = c.RecordId; p["@fid"].Value = c.FieldId; p["@val"].Value = c.Value; p["@cid"].Value = collectionId; },
                ["@rid", "@fid", "@val", "@cid"]);

        if (updates.Count > 0)
            Run(conn, tx, "UPDATE db_field_values SET Value = @val WHERE RecordId = @rid AND FieldId = @fid",
                updates, (p, c) => { p["@rid"].Value = c.RecordId; p["@fid"].Value = c.FieldId; p["@val"].Value = c.Value; },
                ["@rid", "@fid", "@val"]);

        if (deletes.Count > 0)
            Run(conn, tx, "DELETE FROM db_field_values WHERE RecordId = @rid AND FieldId = @fid",
                deletes, (p, c) => { p["@rid"].Value = c.RecordId; p["@fid"].Value = c.FieldId; },
                ["@rid", "@fid"]);

        if (titles.Count > 0)
            Run(conn, tx, "UPDATE db_records SET Title = @title WHERE Id = @rid AND CollectionId = @cid",
                titles, (p, t) => { p["@rid"].Value = t.RecordId; p["@title"].Value = t.Title; p["@cid"].Value = collectionId; },
                ["@rid", "@title", "@cid"]);
    }

    // Prepared-команда, прогнанная по списку: один разбор запроса на весь пакет.
    private static void Run<T>(DbConnection conn, DbTransaction tx, string sql,
                               IReadOnlyList<T> items, Action<Dictionary<string, DbParameter>, T> bind,
                               string[] parameterNames)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;

        var ps = new Dictionary<string, DbParameter>(parameterNames.Length);
        foreach (var name in parameterNames)
            ps[name] = AddParam(cmd, name, name is "@rid" or "@fid" or "@cid" ? DbType.Int32 : DbType.String);
        cmd.Prepare();

        foreach (var item in items)
        {
            bind(ps, item);
            cmd.ExecuteNonQuery();
        }
    }

    private void SyncLocalCache(List<CellWrite> inserts, List<CellWrite> updates, List<CellWrite> deletes,
                                List<(int RecordId, string Title)> titles)
    {
        foreach (var cell in inserts.Concat(updates))
            if (db.DbFieldValues.Local.FirstOrDefault(v => v.RecordId == cell.RecordId && v.FieldId == cell.FieldId)
                is { } tracked)
                tracked.Value = cell.Value;

        foreach (var cell in deletes)
            if (db.DbFieldValues.Local.FirstOrDefault(v => v.RecordId == cell.RecordId && v.FieldId == cell.FieldId)
                is { } tracked)
                db.Entry(tracked).State = EntityState.Detached;

        foreach (var (recordId, title) in titles)
            if (db.DbRecords.Local.FirstOrDefault(r => r.Id == recordId) is { } tracked)
                tracked.Title = title;
    }

    // Поля-даты держат проекцию calendar_index; текстовые к ней отношения не имеют.
    private void SyncCalendarForDateFields(List<CellWrite> written)
    {
        if (written.Count == 0) return;

        var fieldIds     = written.Select(w => w.FieldId).Distinct().ToList();
        var dateFieldIds = db.DbFields.AsNoTracking()
            .Where(f => fieldIds.Contains(f.Id) && f.FieldType == "date")
            .Select(f => f.Id)
            .ToHashSet();
        if (dateFieldIds.Count == 0) return;

        foreach (var cell in written)
            if (dateFieldIds.Contains(cell.FieldId))
                SyncCalendarIndexValue(cell.RecordId, cell.FieldId, cell.Value);
    }

    // Уведомления о смене исполнителя и стадии — те же, что дает правка одной ячейки
    // (TryWriteTaskEvent), в той же транзакции. Больше BulkTaskEventsLimit правок ролей — без
    // уведомлений, как у прочих массовых путей.
    private void WriteTaskEvents(int collectionId, List<CellChange> changes)
    {
        var fieldIds = changes.Where(c => c.FieldId is not null).Select(c => c.FieldId!.Value).Distinct().ToList();
        if (fieldIds.Count == 0) return;

        var roles = db.DbFields.AsNoTracking()
            .Where(f => fieldIds.Contains(f.Id) && (f.IsTaskExec || f.IsTaskStatus))
            .Select(f => new { f.Id, Meta = new EditedFieldMeta(f.Name, f.FieldType, f.IsTaskExec, f.IsTaskStatus) })
            .ToList()
            .ToDictionary(f => f.Id, f => f.Meta);
        if (roles.Count == 0) return;

        var roleChanges = changes.Where(c => c.FieldId is int fid && roles.ContainsKey(fid)).ToList();
        if (roleChanges.Count == 0 || roleChanges.Count > BulkTaskEventsLimit) return;

        // Название задачи — актуальное: если его поменял этот же пакет, в уведомлении новое.
        var recordIds = roleChanges.Select(c => c.RecordId).Distinct().ToList();
        var titles = db.DbRecords.AsNoTracking()
            .Where(r => recordIds.Contains(r.Id))
            .Select(r => new { r.Id, r.Title })
            .ToList()
            .ToDictionary(r => r.Id, r => r.Title ?? "");
        foreach (var renamed in changes.Where(c => c.FieldId is null))
            titles[renamed.RecordId] = renamed.NewValue;

        foreach (var change in roleChanges)
            TryWriteTaskEvent(change.RecordId, titles.GetValueOrDefault(change.RecordId, ""), collectionId,
                              change.FieldId!.Value, roles[change.FieldId.Value], change.OldValue, change.NewValue);
    }

    // Строка журнала пакета: операция, счет и «до → после» первых изменений — сколько влезает
    // в строку журнала. Без них массовую перезапись было не восстановить даже глазами.
    private string BatchDetail(string operation, List<CellChange> changes)
    {
        const int Sample = 5;
        var sample = changes.Take(Sample).ToList();

        var recordIds = sample.Select(c => c.RecordId).Distinct().ToList();
        var fieldIds  = sample.Where(c => c.FieldId is not null).Select(c => c.FieldId!.Value).Distinct().ToList();
        var owners = db.DbRecords.AsNoTracking()
            .Where(r => recordIds.Contains(r.Id)).Select(r => new { r.Id, r.Title })
            .ToList().ToDictionary(r => r.Id, r => r.Title ?? "");
        var fields = db.DbFields.AsNoTracking()
            .Where(f => fieldIds.Contains(f.Id)).Select(f => new { f.Id, f.Name })
            .ToList().ToDictionary(f => f.Id, f => f.Name);

        return ChangeDetail.Batch(operation, changes.Count, [.. sample.Select(c => (
            Owner: owners.GetValueOrDefault(c.RecordId, ""),
            Field: c.FieldId is int fid ? fields.GetValueOrDefault(fid, "") : "Название",
            Old:   c.OldValue,
            New:   c.NewValue))]);
    }

    // Авто-привязка файлов: правка столбца-источника (или «Названия») пересчитывает файл-столбцы
    // записи — как у SetFieldValue. Правила читаются один раз на пакет; записей без правил — ноль
    // обращений к диску.
    private List<(int RecordId, int FieldId, string Value)> ReactToBulkSourceEdits(int collectionId, List<CellChange> changes)
    {
        var result = new List<(int RecordId, int FieldId, string Value)>();

        var rules = db.DbFields.AsNoTracking()
            .Where(f => f.CollectionId == collectionId && f.FieldType == "file")
            .Select(f => new { f.Id, f.ConfigJson })
            .ToList()
            .Select(f => (f.Id, Rule: FileAutoRule.FromConfig(f.ConfigJson)))
            .Where(f => f.Rule is not null)
            .ToList();
        if (rules.Count == 0) return result;

        foreach (var byRecord in changes.GroupBy(c => c.RecordId))
        {
            var titleEdited  = byRecord.Any(c => c.FieldId is null);
            var fieldsEdited = byRecord.Where(c => c.FieldId is not null).Select(c => c.FieldId!.Value).ToHashSet();

            foreach (var (fileFieldId, rule) in rules)
            {
                if (!(rule!.SourceIsTitle && titleEdited) && !fieldsEdited.Contains(rule.SourceFieldId)) continue;

                var matched = ResolveOrQueueAutoFile(fileFieldId, byRecord.Key, rule);
                if (matched is null) continue;
                SetAutoFileValue(fileFieldId, byRecord.Key, matched);
                result.Add((byRecord.Key, fileFieldId, matched));
            }
        }

        return result;
    }
}
