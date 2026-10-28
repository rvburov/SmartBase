using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using SmartBase.Database;

namespace SmartBase.Tests.Benchmarks;

/// <summary>
/// Замер резервной копии SQLite: обычная копия файла с Flush(true), прежние VACUUM INTO и
/// integrity_check в один поток, нынешний BackupTo с полной проверкой. Только для отдельной
/// неиспользуемой базы: SHA256 исходника сверяется до и после, вне времени замера.
///
/// Запуск: dotnet run -c Release --project Tests/Benchmarks -- backup --db PATH [--network] [--network-target] [--repeat 3]
/// --network / --network-target выбирают сетевой путь алгоритма, но не воспроизводят задержки SMB/NFS.
/// Порядок трех способов меняется между повторами; кеш ОС намеренно не сбрасывается.
/// </summary>
internal static class BackupBench
{
    public static int Run(string[] args)
    {
        SQLitePCL.Batteries_V2.Init();
        Console.OutputEncoding = Encoding.UTF8;

        var path = BenchUtil.ArgStr(args, "--db") ?? "";
        if (!File.Exists(path)) { Console.WriteLine($"Нет файла {path}"); return 1; }
        var repeats = BenchUtil.ArgInt(args, "--repeat") ?? 1;
        if ((args.Contains("--repeat") && BenchUtil.ArgInt(args, "--repeat") is null) || repeats is < 1 or > 5)
        {
            Console.WriteLine("--repeat: целое число от 1 до 5.");
            return 1;
        }
        if (File.Exists(path + "-wal") && new FileInfo(path + "-wal").Length > 0)
        {
            Console.WriteLine("У исходника есть непустой WAL. Замер запускается на отдельной закрытой копии базы.");
            return 1;
        }

        path = Path.GetFullPath(path);
        var sourceBytes = new FileInfo(path).Length;
        var before = Hash(path);
        var directory = Path.Combine(Path.GetDirectoryName(path)!, "backupbench-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        Console.WriteLine($"Источник: {path} ({sourceBytes / 1048576.0:N1} МиБ), SHA256 {before}");
        Console.WriteLine("Отдельная неиспользуемая база; кеш ОС прогрет чтением SHA256 и не сбрасывается.");
        Console.WriteLine("File.Copy включает Flush(true); резервная копия дополнительно уплотняет и проверяет базу.");
        Console.WriteLine("МиБ/с рассчитаны по размеру файла: File.Copy может использовать ускорение файловой системы.");
        if (args.Contains("--network"))
        {
            NetworkPath.ForceNetwork(path);
            Console.WriteLine("Выбран алгоритм сетевого источника. Задержки и пропускная способность реальной сети здесь не моделируются.");
        }
        if (args.Contains("--network-target"))
            Console.WriteLine("Выбран алгоритм сетевой папки копий: проверка на своем диске, затем перенос готового файла.");

        var measurements = new Dictionary<string, List<double>>();
        try
        {
            for (var round = 0; round < repeats; round++)
            {
                Console.WriteLine($"Повтор {round + 1} из {repeats}:");
                // В трех повторах каждый способ ровно один раз оказывается первым.
                for (var position = 0; position < 3; position++)
                {
                    var method = (round + position) % 3;
                    var target = Path.Combine(directory, $"{round}-{method}.db");
                    if (method == 0)
                    {
                        Measure("Копия файла + Flush(true)", sourceBytes, () =>
                        {
                            File.Copy(path, target);
                            Flush(target);
                        });
                    }
                    else if (method == 1)
                    {
                        var vacuum = Measure("Прежняя: VACUUM INTO", sourceBytes,
                            () => Exec(path, "VACUUM INTO $p;", target));
                        var check = Measure("Прежняя: integrity_check", new FileInfo(target).Length,
                            () => Exec(target, "PRAGMA integrity_check;", null));
                        Add("Прежняя: всего", vacuum + check);
                        Print("Прежняя: всего", vacuum + check, sourceBytes);
                    }
                    else
                    {
                        if (args.Contains("--network-target")) NetworkPath.ForceNetwork(target);
                        var phases = new List<(string Phase, double At)> { ("Подготовка", 0) };
                        var watch = Stopwatch.StartNew();
                        DatabaseManager.BackupTo(path, "bench", target, (message, _) =>
                        {
                            var phase = message.Split('\n')[0];
                            lock (phases)
                                if (phase != phases[^1].Phase) phases.Add((phase, watch.Elapsed.TotalMilliseconds));
                        });
                        var total = watch.Elapsed.TotalMilliseconds;
                        lock (phases)
                            for (var i = 0; i < phases.Count; i++)
                            {
                                var end = i + 1 < phases.Count ? phases[i + 1].At : total;
                                Console.WriteLine($"    {phases[i].Phase}: {BenchUtil.Fmt(end - phases[i].At)}");
                            }
                        Add("Нынешняя: BackupTo целиком", total);
                        Print("Нынешняя: BackupTo целиком", total, sourceBytes);
                    }
                    Console.WriteLine($"    Размер результата: {new FileInfo(target).Length / 1048576.0:N1} МиБ");
                    Delete(target);
                }
            }

            Console.WriteLine("Медианы длительности:");
            foreach (var (name, times) in measurements)
            {
                var sorted = times.Order().ToArray();
                var median = sorted.Length % 2 == 1 ? sorted[sorted.Length / 2]
                    : (sorted[sorted.Length / 2 - 1] + sorted[sorted.Length / 2]) / 2;
                Console.WriteLine($"  {name}: {BenchUtil.Fmt(median)} (мин. {BenchUtil.Fmt(sorted[0])}, макс. {BenchUtil.Fmt(sorted[^1])})");
            }

            var after = Hash(path);
            if (before != after)
            {
                Console.WriteLine($"ОШИБКА: исходник изменился, замер недействителен. SHA256 после: {after}");
                return 1;
            }
            Console.WriteLine("SHA256 исходника после замера совпадает.");
            return 0;
        }
        finally
        {
            // Папка создана этим запуском; существующие backupbench-*.db рядом с исходником не трогаем.
            Directory.Delete(directory, recursive: true);
        }

        double Measure(string name, long bytes, Action action)
        {
            var watch = Stopwatch.StartNew();
            action();
            var elapsed = watch.Elapsed.TotalMilliseconds;
            Add(name, elapsed);
            Print(name, elapsed, bytes);
            return elapsed;
        }
        void Add(string name, double elapsed)
        {
            if (!measurements.TryGetValue(name, out var times)) measurements.Add(name, times = []);
            times.Add(elapsed);
        }
    }

    private static void Print(string name, double milliseconds, long bytes) =>
        Console.WriteLine($"  {name}: {BenchUtil.Fmt(milliseconds)}, {bytes / 1048576.0 / Math.Max(0.001, milliseconds / 1000):N1} МиБ/с");

    private static string Hash(string file)
    {
        using var input = File.OpenRead(file);
        return Convert.ToHexString(SHA256.HashData(input));
    }

    private static void Flush(string file)
    {
        using var stream = new FileStream(file, FileMode.Open, FileAccess.Write, FileShare.None);
        stream.Flush(flushToDisk: true);
    }

    // Прежний путь: подключение с настройками SQLite по умолчанию, как у проверки копии раньше.
    private static void Exec(string file, string sql, string? param)
    {
        using var c = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = file, Pooling = false }.ToString());
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText    = sql;
        cmd.CommandTimeout = 0;
        if (param is not null)
        {
            cmd.Parameters.AddWithValue("$p", param);
            cmd.ExecuteNonQuery();
        }
        else
        {
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
                if (reader.GetString(0) is { } problem && problem != "ok")
                    throw new InvalidOperationException("Прежняя копия не прошла integrity_check: " + problem);
        }
    }

    private static void Delete(string file)
    {
        foreach (var s in new[] { "", "-journal", "-wal", "-shm" })
            if (File.Exists(file + s)) File.Delete(file + s);
    }
}
