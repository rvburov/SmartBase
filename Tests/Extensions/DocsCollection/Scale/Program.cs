using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using SmartBase.Database;
using SmartBase.Extensions.DocsCollection;

namespace SmartBase.Tests.Extensions.DocsCollection.Scale;

/// <summary>Полный путь на большом дереве: реальные пустые файлы, рекурсивный обход,
/// сортировка, заливка и потоковая сверка каждой строки. Данные стенда остаются на диске.
/// Запускается отдельно, чтобы миллион файлов не создавался при каждом прогоне xUnit.</summary>
internal static class Program
{
    private static readonly string[] Formats = ["PDF", "docx", "xlsx"];

    private static int Main(string[] args)
    {
        if (args.Length != 3 || !int.TryParse(args[2], out var count) || count <= 0)
        {
            Console.Error.WriteLine("Аргументы: <новая папка файлов> <новый файл БД> <число файлов>");
            return 2;
        }

        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(args[0]));
        var dbPath = Path.GetFullPath(args[1]);
        if (Directory.Exists(root) || File.Exists(dbPath) ||
            dbPath.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            Console.Error.WriteLine("Папка и файл БД должны быть новыми; БД должна лежать вне папки файлов.");
            return 2;
        }

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
        try
        {
            Directory.CreateDirectory(root);
            Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
            var watch = Stopwatch.StartNew();
            for (var i = 0; i < count; i++)
            {
                cts.Token.ThrowIfCancellationRequested();
                var path = FilePath(root, i);
                if (i % 1000 == 0) Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write);
            }
            Report("Подготовка файлов (не часть работы расширения)", watch);

            watch.Restart();
            var docs = DocsCollectionBuilder.Scan(root, true, new HashSet<string>(), cts.Token);
            Require(docs.Count == count, "Число найденных файлов не совпало");
            Report("Рекурсивный обход и сортировка", watch);

            using var db = DatabaseManager.Open(1, "scale", dbPath);
            watch.Restart();
            var shell = DocsCollectionBuilder.CreateShell(db.Constructor, "Миллион документов");
            Report("Каркас коллекции", watch);
            var progress = new ScaleProgress(count, watch, dbPath);
            watch.Restart();
            try
            {
                Require(DocsCollectionBuilder.FillRecords(dbPath, shell, docs, progress, cts.Token) == count,
                        "Число записанных документов не совпало");
            }
            catch
            {
                DocsCollectionBuilder.TryDelete(db.Constructor, shell.CollectionId);
                throw;
            }
            Report("Запись документов", watch);
            Console.WriteLine($"Отчетов о сохраненных пакетах: {progress.Reports}; пик WAL: {progress.PeakWal / 1048576.0:F1} МиБ");

            watch.Restart();
            Verify(dbPath, shell, docs, root);
            Report("Потоковая проверка всех строк и целостности БД", watch);
            Console.WriteLine($"Успешно: {count} записей, {3L * count} значений, одна сводная строка истории.");
            Console.WriteLine($"Файлы: {root}\nБД: {dbPath}");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex);
            Console.Error.WriteLine($"Данные стенда оставлены: {root}; {dbPath}");
            return 1;
        }
    }

    private static string FilePath(string root, int i) => Path.Combine(root, $"Раздел{i / 1000:0000}",
        "Вложенная", $"Документ{i:0000000}.{Formats[i % Formats.Length]}");

    private static void Verify(string dbPath, DocsShell shell, IReadOnlyList<DocEntry> docs, string root)
    {
        using var ctx = new AppDbContext(dbPath);
        ctx.Database.OpenConnection();
        using var cmd = ctx.Database.GetDbConnection().CreateCommand();
        // Идентификаторы полей получены из созданного каркаса, пользовательского текста в SQL нет.
        cmd.CommandText = $"""
            SELECT r.Title, r.Position, folder.Value, format.Value, file.Value
            FROM db_records r
            LEFT JOIN db_field_values folder ON folder.RecordId = r.Id AND folder.FieldId = {shell.FolderFieldId}
            LEFT JOIN db_field_values format ON format.RecordId = r.Id AND format.FieldId = {shell.FormatFieldId}
            LEFT JOIN db_field_values file ON file.RecordId = r.Id AND file.FieldId = {shell.FileFieldId}
            WHERE r.CollectionId = {shell.CollectionId} ORDER BY r.Position, r.Id
            """;
        var i = 0;
        var step = docs.Count > 2_000_000 ? 1L : 1000L;
        using (var reader = cmd.ExecuteReader())
            while (reader.Read())
            {
                Require(i < docs.Count, "Лишняя запись");
                var doc = docs[i];
                Require(doc.Name == $"Документ{i:0000000}" && doc.FullPath == FilePath(root, i), "Порядок обхода/сортировки");
                Require(reader.GetString(0) == doc.Name && reader.GetInt64(1) == (i + 1) * step, "Название/позиция");
                Require(reader.GetString(2) == doc.FolderName && reader.GetString(3) == doc.Format &&
                        reader.GetString(4) == doc.FullPath, "Значения полей");
                i++;
            }
        Require(i == docs.Count, "Пропущенные записи");
        Require(ctx.DbFieldValues.LongCount() == 3L * docs.Count, "Число значений полей");
        var log = ctx.ChangeLog.AsNoTracking().Where(l => l.TargetType == "record").ToList();
        Require(log.Count == 1 && log[0].Count == docs.Count, "Сводная история");
        cmd.CommandText = "PRAGMA integrity_check;";
        Require(Equals("ok", cmd.ExecuteScalar()), "Целостность БД");
    }

    private static void Report(string stage, Stopwatch watch)
    {
        using var process = Process.GetCurrentProcess();
        Console.WriteLine($"{stage}: {watch.Elapsed.TotalSeconds:F3} с; " +
            $"управляемая память {GC.GetTotalMemory(false) / 1048576.0:F1} МиБ; " +
            $"пик процесса {process.PeakWorkingSet64 / 1048576.0:F1} МиБ");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed class ScaleProgress(int total, Stopwatch watch, string dbPath) : IProgress<DocsProgress>
    {
        public int Reports { get; private set; }
        public long PeakWal { get; private set; }
        private int _previous;

        public void Report(DocsProgress value)
        {
            Require(value.Current > _previous && value.Total == total, "Прогресс не монотонный");
            _previous = value.Current;
            Reports++;
            var wal = new FileInfo(dbPath + "-wal");
            if (wal.Exists) PeakWal = Math.Max(PeakWal, wal.Length);
            if (value.Current % 100_000 == 0 || value.Current == total)
                Console.WriteLine($"Сохранено {value.Current} из {total}: {watch.Elapsed.TotalSeconds:F3} с");
        }
    }
}
