using System.Diagnostics;
using System.Text;
using SmartBase.Core.Importers;
using SmartBase.Core.Importers.TableSources;
using SmartBase.Database;

namespace SmartBase.Tests.Benchmarks;

/// <summary>Замер импорта большого CSV (перенос Tests/ImportPerf --perf). Функциональная
/// матрица импорта — в Tests/SmartBase.Tests (ImportMatrixTests).</summary>
internal static class ImportBench
{
    public static int Run(string[] args)
    {
        SQLitePCL.Batteries_V2.Init();
        EncodingSetup.Ensure();

        var dir = BenchUtil.ArgStr(args, "--dir") ?? Path.Combine(Path.GetTempPath(), "import-perf");
        if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        Directory.CreateDirectory(dir);
        var rows   = BenchUtil.ArgInt(args, "--rows") ?? 1_000_000;
        var dbPath = Path.Combine(dir, "import_test.db");

        var f = Path.Combine(dir, $"perf_{rows}.csv");
        Console.WriteLine($"Генерация CSV {rows:N0} строк × 8 столбцов…");
        using (var w = new StreamWriter(f, false, new UTF8Encoding(false), 1 << 20))
        {
            w.WriteLine("Название,Код,Цена,Количество,Дата,Категория,Статус,Комментарий");
            for (var i = 0; i < rows; i++)
                w.WriteLine($"Позиция {i},K-{i},{i % 1000}.5,{i % 50},{1 + i % 28:00}.0{1 + i % 9}.2026,Кат {i % 20},OK,строка с текстом {i}");
        }
        Console.WriteLine($"Файл: {new FileInfo(f).Length / 1024.0 / 1024.0:0.0} МБ");

        using var m = DatabaseManager.Open(0, "import-perf", dbPath);
        var repo = m.Constructor;

        var sw = Stopwatch.StartNew();
        var units  = ImportPlanner.Plan(new ImportSpec(f, false, false, null, 1, 2, "1", SheetMode.Single, $"Перф {rows}"));
        var src    = units[0].OpenSource();
        var schema = src.Analyze(null, CancellationToken.None);
        var tAnalyze = sw.Elapsed.TotalSeconds;

        sw.Restart();
        var data  = SourceHelpers.WithTitles(src.ReadRows(CancellationToken.None), schema.EmptyTitleCount);
        var colId = repo.ImportStreamedCollectionIsolated(units[0].CollectionName, null, schema, data, CancellationToken.None);
        var tWrite = sw.Elapsed.TotalSeconds;

        Console.WriteLine($"Analyze: {tAnalyze:0.0} с; чтение+запись: {tWrite:0.0} с " +
                          $"({rows / Math.Max(0.001, tWrite):N0} строк/с)");
        Console.WriteLine($"Записей в БД: {repo.CountRecords(colId):N0}; БД: {new FileInfo(dbPath).Length / 1024.0 / 1024.0:0.0} МБ");
        return 0;
    }
}
