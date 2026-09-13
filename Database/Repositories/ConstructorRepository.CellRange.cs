using System.Data;
using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using SmartBase.Core.Models;
using SmartBase.Utils;

namespace SmartBase.Database.Repositories;

/// <summary>
/// Массовая правка значений ПРЯМОУГОЛЬНИКА ячеек: вставка из буфера обмена и очистка выделения.
///
/// Зачем отдельный путь, а не цикл <c>SetFieldValue</c>: обычная правка ячейки делает до трех
/// SaveChanges, сырой UPDATE аудита, запрос к calendar_index и чтение списка файл-столбцов —
/// около 18 значений в секунду. Диапазон 100×20 ушел бы в минуты работы на потоке интерфейса,
/// в две тысячи коммитов (на сетевой папке каждый берет исключительную блокировку) и в две
/// тысячи строк журнала. Здесь все ложится одной транзакцией и ОДНОЙ строкой журнала со счетом —
/// как того требует правило проекта для пакетных путей.
/// </summary>
public sealed partial class ConstructorRepository
{
    /// <summary>Одно значение прямоугольника: куда и что писать. Пустое значение очищает ячейку.</summary>
    public readonly record struct CellWrite(int RecordId, int FieldId, string Value);

    /// <summary>
    /// Записать значения ячеек пакетом. Возвращает, сколько ячеек ДЕЙСТВИТЕЛЬНО изменилось:
    /// значения, совпавшие с текущими, не пишутся и в счет не идут — иначе повторная вставка
    /// того же блока поднимала бы «изменено» у всех записей и мусорила журналом.
    ///
    /// <paramref name="titles"/> — правки системного столбца «Название» (он живет в самой записи,
    /// а не в таблице значений). Пустое название допустимо: в ячейке его заменит «Без названия».
    /// </summary>
    public int SetCellsBulk(int collectionId,
                            IReadOnlyList<CellWrite> cells,
                            IReadOnlyList<(int RecordId, string Title)>? titles = null)
    {
        Demand(PermAction.EditRecord, collectionId);
        if (cells.Count == 0 && (titles is null || titles.Count == 0)) return 0;

        // Текущие значения затронутых ячеек — одним запросом: без них не отличить настоящую
        // правку от повторной вставки того же блока.
        var recordIds = cells.Select(c => c.RecordId).Distinct().ToList();
        var fieldIds  = cells.Select(c => c.FieldId).Distinct().ToList();
        var current = db.DbFieldValues.AsNoTracking()
            .Where(v => recordIds.Contains(v.RecordId) && fieldIds.Contains(v.FieldId))
            .Select(v => new { v.RecordId, v.FieldId, v.Value })
            .ToList()
            .ToDictionary(v => (v.RecordId, v.FieldId), v => v.Value);

        var inserts = new List<CellWrite>();
        var updates = new List<CellWrite>();
        var deletes = new List<CellWrite>();

        foreach (var cell in cells)
        {
            var has = current.TryGetValue((cell.RecordId, cell.FieldId), out var old);
            var value = cell.Value ?? "";

            if (!has)
            {
                // «Нет строки» и «пустое значение» — одно и то же состояние, писать нечего.
                if (value.Length > 0) inserts.Add(cell with { Value = value });
            }
            else if (old == value) continue;                       // уже такое — не трогаем
            else if (value.Length == 0) deletes.Add(cell);          // инвариант «нет строки = пусто»
            else updates.Add(cell with { Value = value });
        }

        var titleChanges = TitleChanges(titles);
        var changed = inserts.Count + updates.Count + deletes.Count + titleChanges.Count;
        if (changed == 0) return 0;

        var touched = inserts.Concat(updates).Concat(deletes).Select(c => c.RecordId)
            .Concat(titleChanges.Select(t => t.RecordId))
            .Distinct().ToList();

        WriteCells(inserts, updates, deletes, titleChanges, touched);

        // Локальный кеш EF сырые команды не видят: без синхронизации панель записи и повторное
        // чтение отдали бы прежние значения (та же оговорка, что у аудита в SetFieldValue).
        SyncLocalCache(inserts, updates, deletes, titleChanges);

        // Проекция календаря — после коммита и только для полей-дат: их в диапазоне единицы,
        // а дергать ее на каждое текстовое значение незачем.
        SyncCalendarForDateFields(inserts.Concat(updates).Concat(deletes).ToList());

        var name = CollectionNameOf(collectionId) ?? "";
        LogNow("update", "record", name, name, count: changed);
        return changed;
    }

    /// <summary>
    /// Отметить в журнале, что значения ячеек ушли в буфер обмена.
    ///
    /// Копирование — такой же вынос данных наружу, как выгрузка в файл, только без файла:
    /// скопированное уходит в любую другую программу. Выгрузка в этом проекте намеренно
    /// оставляет след (см. ExportTrace), и оставлять копирование единственным бесследным
    /// каналом нельзя — поэтому строка журнала пишется на КАЖДОЕ копирование, включая одну
    /// ячейку. Строка одна на операцию, со счетом ячеек.
    /// </summary>
    public void LogCellsCopied(int collectionId, int cellCount)
    {
        if (cellCount <= 0) return;
        var name = CollectionNameOf(collectionId) ?? "";
        LogNow("copy", "records", name, name, count: cellCount);
    }

    /// <summary>Значения ячеек прямоугольника одним запросом — для копирования в буфер обмена.
    /// Ключ — (запись, поле); ячейки без значения в словарь не попадают.</summary>
    public Dictionary<(int RecordId, int FieldId), string> GetCellValues(
        IReadOnlyList<int> recordIds, IReadOnlyList<int> fieldIds)
    {
        if (recordIds.Count == 0 || fieldIds.Count == 0) return [];
        return db.DbFieldValues.AsNoTracking()
            .Where(v => recordIds.Contains(v.RecordId) && fieldIds.Contains(v.FieldId))
            .Select(v => new { v.RecordId, v.FieldId, v.Value })
            .ToList()
            .ToDictionary(v => (v.RecordId, v.FieldId), v => v.Value);
    }

    // Названия, которые реально меняются: сравниваем с текущими по той же причине, что и значения.
    private List<(int RecordId, string Title)> TitleChanges(IReadOnlyList<(int RecordId, string Title)>? titles)
    {
        if (titles is null || titles.Count == 0) return [];

        var ids = titles.Select(t => t.RecordId).Distinct().ToList();
        var now = db.DbRecords.AsNoTracking()
            .Where(r => ids.Contains(r.Id))
            .Select(r => new { r.Id, r.Title })
            .ToList()
            .ToDictionary(r => r.Id, r => r.Title);

        return [.. titles.Where(t => now.TryGetValue(t.RecordId, out var old) && old != (t.Title ?? ""))
                         .Select(t => (t.RecordId, Title: t.Title ?? ""))];
    }

    // Одна транзакция на все: значения, названия и аудит «последней правки».
    private void WriteCells(List<CellWrite> inserts, List<CellWrite> updates, List<CellWrite> deletes,
                            List<(int RecordId, string Title)> titles, List<int> touched)
    {
        var conn    = db.Database.GetDbConnection();
        var wasOpen = conn.State == ConnectionState.Open;
        if (!wasOpen) conn.Open();

        try
        {
            using var tx = conn.BeginTransaction();

            if (inserts.Count > 0)
                Run(conn, tx, "INSERT INTO db_field_values (RecordId, FieldId, Value) VALUES (@rid, @fid, @val)",
                    inserts, (p, c) => { p["@rid"].Value = c.RecordId; p["@fid"].Value = c.FieldId; p["@val"].Value = c.Value; },
                    ["@rid", "@fid", "@val"]);

            if (updates.Count > 0)
                Run(conn, tx, "UPDATE db_field_values SET Value = @val WHERE RecordId = @rid AND FieldId = @fid",
                    updates, (p, c) => { p["@rid"].Value = c.RecordId; p["@fid"].Value = c.FieldId; p["@val"].Value = c.Value; },
                    ["@rid", "@fid", "@val"]);

            if (deletes.Count > 0)
                Run(conn, tx, "DELETE FROM db_field_values WHERE RecordId = @rid AND FieldId = @fid",
                    deletes, (p, c) => { p["@rid"].Value = c.RecordId; p["@fid"].Value = c.FieldId; },
                    ["@rid", "@fid"]);

            if (titles.Count > 0)
                Run(conn, tx, "UPDATE db_records SET Title = @title WHERE Id = @rid",
                    titles, (p, t) => { p["@rid"].Value = t.RecordId; p["@title"].Value = t.Title; },
                    ["@rid", "@title"]);

            // Аудит «последней правки» — для столбцов «Дата изменения» и «Изменение пользователем».
            var stamp  = DateTime.Now.ToString("o");
            var author = CurrentUser.DisplayName;
            Run(conn, tx, "UPDATE db_records SET UpdatedAt = @at, UpdatedBy = @by WHERE Id = @rid",
                touched, (p, id) => { p["@rid"].Value = id; p["@at"].Value = stamp; p["@by"].Value = author; },
                ["@rid", "@at", "@by"]);

            tx.Commit();

            foreach (var id in touched)
                if (db.DbRecords.Local.FirstOrDefault(x => x.Id == id) is { } tracked)
                {
                    tracked.UpdatedAt = stamp;
                    tracked.UpdatedBy = author;
                }
        }
        finally
        {
            if (!wasOpen) conn.Close();
        }
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
            ps[name] = AddParam(cmd, name, name is "@rid" or "@fid" ? DbType.Int32 : DbType.String);
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

        var dateFieldIds = db.DbFields.AsNoTracking()
            .Where(f => written.Select(w => w.FieldId).Distinct().Contains(f.Id) && f.FieldType == "date")
            .Select(f => f.Id)
            .ToHashSet();
        if (dateFieldIds.Count == 0) return;

        foreach (var cell in written)
            if (dateFieldIds.Contains(cell.FieldId))
                SyncCalendarIndexValue(cell.RecordId, cell.FieldId, cell.Value);
    }
}
