using SmartBase.Core.Exporters;
using SmartBase.Core.Importers;
using SmartBase.Core.Importers.TableSources;
using SmartBase.Database;
using SmartBase.Database.Repositories;

namespace SmartBase.Tests.Integration;

/// <summary>
/// Импорт/экспорт ровно тем конвейером, что и приложение (зеркало
/// SidebarTree.OpenImportExport / RunExport) — перенос обвязки харнессов
/// Tests/ImportPerf и Tests/ExportPerf. Читатели результата открывают свежий контекст.
/// </summary>
public static class ImportExport
{
    // ── Импорт ─────────────────────────────────────────────────────────────────

    /// <summary>Заявка на импорт (стандарт: заголовки в 1-й строке, данные со 2-й,
    /// столбец названия — первый). Manual-параметры действуют только на Excel.</summary>
    public static ImportSpec Std(string path, string name, bool folder = false, bool manual = false,
        string? sheetName = null, int headerRow = 1, int dataRow = 2, string titleCol = "1",
        SheetMode sheets = SheetMode.Single,
        string? excludeSheets = null, string? excludeRows = null, string? excludeCols = null) =>
        new(path, folder, manual || sheetName is not null, sheetName,
            headerRow, dataRow, titleCol, sheets, name, excludeSheets, excludeRows, excludeCols);

    /// <summary>Импорт по заявке (как цикл SidebarTree.OpenImportExport): юниты по очереди,
    /// ошибка юнита — дружелюбный текст с именем файла, готовые коллекции остаются.</summary>
    public static List<int> Run(ConstructorRepository repo, ImportSpec spec, List<int>? created = null)
    {
        created ??= [];
        foreach (var unit in ImportPlanner.Plan(spec))
        {
            try
            {
                var src    = unit.OpenSource();
                var schema = src.Analyze(null, CancellationToken.None);
                var rows   = SourceHelpers.WithTitles(src.ReadRows(CancellationToken.None), schema.EmptyTitleCount);
                created.Add(repo.ImportStreamedCollectionIsolated(
                    unit.CollectionName, null, schema, rows, CancellationToken.None));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                var friendly = ImportErrors.Map(ex, unit.FilePath);
                throw new ImportException($"{Path.GetFileName(unit.FilePath)}: {friendly.Message}");
            }
        }
        return created;
    }

    /// <summary>Импорт одиночного файла по стандартным правилам. Возвращает id коллекции.</summary>
    public static int Import(ConstructorRepository repo, string path, string name) =>
        Run(repo, Std(path, name))[0];

    // ── Экспорт ────────────────────────────────────────────────────────────────

    public static ExportSpec ExportSpecFor(string dbPath, IReadOnlyList<int> colIds, string? fileName,
        ExportFormat fmt, string folder, bool manual = false, string? sheetName = null,
        string excludeColumns = "", bool noHeader = false, bool includeHidden = false, bool singleFile = false)
    {
        var refs = colIds.Select(id => new ExportCollectionRef(0, id, CollectionName(dbPath, id), "БД")).ToList();
        return new ExportSpec(folder, fmt, refs, fileName,
            manual || sheetName is not null, sheetName, excludeColumns, noHeader, includeHidden, singleFile);
    }

    /// <summary>Экспорт по заявке (как цикл SidebarTree.RunExport): по юниту создаётся файл,
    /// столбцы/строки читаются isolated-методами; ошибка юнита удаляет недописанный файл.</summary>
    public static List<string> Export(ConstructorRepository repo, ExportSpec spec,
        CancellationToken ct = default, Action<long>? onRow = null, List<string>? completed = null)
    {
        completed ??= [];
        var excl = ExportPlanner.ParseColumnExclusions(spec.ExcludeColumns);
        foreach (var unit in ExportPlanner.Plan(spec))
        {
            try
            {
                var fileCreated = false;
                try
                {
                    using var writer = unit.OpenWriter();
                    fileCreated = true;
                    foreach (var tbl in unit.Tables)
                    {
                        var cols = repo.GetExportColumnsIsolated(tbl.Collection.CollectionId, spec.IncludeHidden)
                                       .Where(c => !excl.Contains(c.Header)).ToList();
                        if (cols.Count == 0)
                            throw new ExportException(
                                $"Все столбцы коллекции «{tbl.Collection.CollectionName}» исключены — нет данных для экспорта.");
                        var total = repo.CountRecordsIsolated(tbl.Collection.CollectionId, excludeHidden: !spec.IncludeHidden);
                        writer.BeginTable(tbl.TableName, cols, !spec.NoHeaderRow);
                        foreach (var row in repo.ReadExportRowsIsolated(tbl.Collection.CollectionId, cols, !spec.IncludeHidden, ct))
                        {
                            writer.WriteRow(row);
                            onRow?.Invoke(1);
                        }
                        writer.EndTable();
                        repo.LogExportIsolated(tbl.Collection.CollectionName, total);
                    }
                    writer.Finish();
                    completed.Add(unit.FilePath);
                }
                catch
                {
                    if (fileCreated) try { File.Delete(unit.FilePath); } catch { /* best-effort */ }
                    throw;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                var friendly = ExportErrors.Map(ex, unit.FilePath);
                throw new ExportException($"{Path.GetFileName(unit.FilePath)}: {friendly.Message}");
            }
        }
        return completed;
    }

    /// <summary>Экспорт одной коллекции в файл заданного формата.</summary>
    public static List<string> Export(ConstructorRepository repo, string dbPath, int colId,
                                      string fileName, ExportFormat fmt, string folder) =>
        Export(repo, ExportSpecFor(dbPath, [colId], fileName, fmt, folder));

    /// <summary>Строки экспорта без записи в файл — для проверок потока.</summary>
    public static List<string[]> ReadExportRows(ConstructorRepository repo, int colId, bool includeHidden)
    {
        var cols = repo.GetExportColumnsIsolated(colId, includeHidden);
        return repo.ReadExportRowsIsolated(colId, cols, !includeHidden, CancellationToken.None).ToList();
    }

    // ── Чтение результата (свежий контекст) ──────────────────────────────────────

    public static (List<string> Fields, int Count, List<string> Titles) Snapshot(string dbPath, int colId)
    {
        using var ctx = new AppDbContext(dbPath);
        var fields = ctx.DbFields.Where(f => f.CollectionId == colId)
                                 .OrderBy(f => f.Position).Select(f => f.Name).ToList();
        var titles = ctx.DbRecords.Where(r => r.CollectionId == colId)
                                  .OrderBy(r => r.Position).ThenBy(r => r.Id)
                                  .Select(r => r.Title).ToList();
        return (fields, titles.Count, titles);
    }

    public static Dictionary<string, string> Values(string dbPath, int colId, int recIndex)
    {
        using var ctx = new AppDbContext(dbPath);
        var fields = ctx.DbFields.Where(f => f.CollectionId == colId).OrderBy(f => f.Position).ToList();
        var rec = ctx.DbRecords.Where(r => r.CollectionId == colId)
                               .OrderBy(r => r.Position).ThenBy(r => r.Id)
                               .Skip(recIndex).First();
        var vals = ctx.DbFieldValues.Where(v => v.RecordId == rec.Id).ToList();
        return fields.ToDictionary(f => f.Name, f => vals.FirstOrDefault(v => v.FieldId == f.Id)?.Value ?? "");
    }

    public static int RecordIdAt(string dbPath, int colId, int recIndex)
    {
        using var ctx = new AppDbContext(dbPath);
        return ctx.DbRecords.Where(r => r.CollectionId == colId)
                            .OrderBy(r => r.Position).ThenBy(r => r.Id)
                            .Skip(recIndex).First().Id;
    }

    public static string CollectionName(string dbPath, int colId)
    {
        using var ctx = new AppDbContext(dbPath);
        return ctx.DbCollections.Find(colId)?.Name ?? "";
    }

    public static int CollectionCount(string dbPath)
    {
        using var ctx = new AppDbContext(dbPath);
        return ctx.DbCollections.Count();
    }

    public static List<string> AllCollectionNames(string dbPath)
    {
        using var ctx = new AppDbContext(dbPath);
        return ctx.DbCollections.Select(c => c.Name).ToList();
    }

    /// <summary>Текст ImportException, если действие бросает его; иначе null.</summary>
    public static string? ImportFails(Action action)
    {
        try { action(); return null; } catch (ImportException ex) { return ex.Message; }
    }

    /// <summary>Текст ExportException, если действие бросает его; иначе null.</summary>
    public static string? ExportFails(Action action)
    {
        try { action(); return null; } catch (ExportException ex) { return ex.Message; }
    }
}
