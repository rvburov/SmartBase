using System.Diagnostics;
using System.Text;
using SmartBase.Core.Exporters;
using SmartBase.Core.Importers;
using SmartBase.Core.Importers.TableSources;
using SmartBase.Database;
using SmartBase.Database.Repositories;

namespace SmartBase.Tests.Benchmarks;

/// <summary>Замер экспорта большой коллекции во все форматы (перенос Tests/ExportPerf --perf).
/// Функциональная матрица экспорта — в Tests/SmartBase.Tests (ExportMatrixTests).</summary>
internal static class ExportBench
{
    public static int Run(string[] args)
    {
        SQLitePCL.Batteries_V2.Init();
        EncodingSetup.Ensure();

        var dir = BenchUtil.ArgStr(args, "--dir") ?? Path.Combine(Path.GetTempPath(), "export-perf");
        if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        Directory.CreateDirectory(dir);
        var outDir = Path.Combine(dir, "out");
        Directory.CreateDirectory(outDir);
        var rows   = BenchUtil.ArgInt(args, "--rows") ?? 1_000_000;
        var dbPath = Path.Combine(dir, "export_test.db");

        using var m = DatabaseManager.Open(0, "export-perf", dbPath);
        var repo = m.Constructor;

        // Коллекцию строим тем же импортом, что и приложение.
        var f = Path.Combine(dir, $"perf_{rows}.csv");
        Console.WriteLine($"Генерация коллекции {rows:N0} строк (через импорт CSV)…");
        using (var w = new StreamWriter(f, false, new UTF8Encoding(false), 1 << 20))
        {
            w.WriteLine("Название,Код,Цена,Количество,Дата,Категория,Статус,Комментарий");
            for (var i = 0; i < rows; i++)
                w.WriteLine($"Позиция {i},K-{i},{i % 1000}.5,{i % 50},2026-0{1 + i % 9:0}-{1 + i % 28:00},Кат {i % 20},OK,строка с текстом {i}");
        }
        var units  = ImportPlanner.Plan(new ImportSpec(f, false, false, null, 1, 2, "1", SheetMode.Single, $"Перф {rows}"));
        var src    = units[0].OpenSource();
        var schema = src.Analyze(null, CancellationToken.None);
        var data   = SourceHelpers.WithTitles(src.ReadRows(CancellationToken.None), schema.EmptyTitleCount);
        var colId  = repo.ImportStreamedCollectionIsolated(units[0].CollectionName, null, schema, data, CancellationToken.None);
        Console.WriteLine($"Коллекция готова: {repo.CountRecords(colId):N0} записей");
        Console.WriteLine();

        foreach (var fmt in new[] { ExportFormat.Csv, ExportFormat.Json, ExportFormat.Xlsx, ExportFormat.Xlsm })
        {
            var sw = Stopwatch.StartNew();
            var file = ExportOne(repo, outDir, colId, $"перф-{fmt}", fmt);
            sw.Stop();
            var size = new FileInfo(file).Length / 1024.0 / 1024.0;
            Console.WriteLine($"{fmt,-5}: {sw.Elapsed.TotalSeconds,7:0.0} с " +
                              $"({rows / Math.Max(0.001, sw.Elapsed.TotalSeconds),10:N0} строк/с), файл {size,8:0.0} МБ");
        }
        return 0;
    }

    // Экспорт одной коллекции (тот же конвейер, что SidebarTree.RunExport, без UI).
    private static string ExportOne(ConstructorRepository repo, string folder, int colId, string fileName, ExportFormat fmt)
    {
        var name = repo.GetAllCollections().First(c => c.Id == colId).Name;
        var spec = new ExportSpec(folder, fmt, [new ExportCollectionRef(0, colId, name, "БД")],
                                  fileName, false, null, "", false, false, false);
        var unit = ExportPlanner.Plan(spec)[0];
        using var writer = unit.OpenWriter();
        foreach (var tbl in unit.Tables)
        {
            var cols = repo.GetExportColumnsIsolated(tbl.Collection.CollectionId, includeHidden: false);
            writer.BeginTable(tbl.TableName, cols, writeHeader: true);
            foreach (var row in repo.ReadExportRowsIsolated(tbl.Collection.CollectionId, cols, true, CancellationToken.None))
                writer.WriteRow(row);
            writer.EndTable();
        }
        writer.Finish();
        return unit.FilePath;
    }
}
