using System.Diagnostics;
using System.Text;
using Microsoft.Data.Sqlite;
using SmartBase.Database;

namespace SmartBase.Tests.Benchmarks;

/// <summary>
/// Замер резервной копии SQLite: копия файла (как проводником) против прежней копии программы
/// (<c>VACUUM INTO</c> и <c>integrity_check</c> в один поток) и нынешней
/// (<see cref="DatabaseManager.BackupTo"/>: сжатие и проверка частями параллельно; у сетевой
/// базы — сначала снимок подряд на своем диске). Источник не меняется; копии пишутся рядом и
/// удаляются.
///
/// Запуск: dotnet run -c Release --project Tests/Benchmarks -- backup --db PATH [--network]
/// --network — источник считается сетевым (снимок через Online Backup API). Скорости сетевого
/// диска стенд не воспроизводит: по нему видна только цена лишнего шага на своем диске.
/// </summary>
internal static class BackupBench
{
    public static int Run(string[] args)
    {
        SQLitePCL.Batteries_V2.Init();
        Console.OutputEncoding = Encoding.UTF8;

        var path = BenchUtil.ArgStr(args, "--db") ?? "";
        if (!File.Exists(path)) { Console.WriteLine($"Нет файла {path}"); return 1; }
        var dir = Path.GetDirectoryName(Path.GetFullPath(path))!;
        Console.WriteLine($"Источник: {path} ({BenchUtil.SizeGb(path):0.00} ГБ)");

        var plain = Path.Combine(dir, "backupbench-copy.db");
        Time("Копия файла (как проводником)", () => File.Copy(path, plain, overwrite: true));
        Delete(plain);

        var old = Path.Combine(dir, "backupbench-old.db");
        Delete(old);
        Time("Прежняя копия: VACUUM INTO", () => Exec(path, "VACUUM INTO $p;", old));
        Time("Прежняя копия: integrity_check в один поток", () => Exec(old, "PRAGMA integrity_check;", null));
        Delete(old);

        if (args.Contains("--network")) NetworkPath.ForceNetwork(path);
        var target = Path.Combine(dir, "backupbench-new.db");
        Delete(target);
        string? last = null;
        var phases = new List<(string Phase, double At)>();
        var sw = Stopwatch.StartNew();
        DatabaseManager.BackupTo(path, "bench", target, (msg, _) =>
        {
            var phase = msg.Split('\n')[0];
            lock (phases)
                if (phase != last) { phases.Add((phase, sw.Elapsed.TotalMilliseconds)); last = phase; }
        });
        var total = sw.Elapsed.TotalMilliseconds;
        for (var i = 0; i < phases.Count; i++)
        {
            var end = i + 1 < phases.Count ? phases[i + 1].At : total;
            Console.WriteLine($"  нынешняя: {phases[i].Phase,-40} {BenchUtil.Fmt(end - phases[i].At)}");
        }
        Console.WriteLine($"Нынешняя копия целиком (BackupTo) … {BenchUtil.Fmt(total)}, копия {BenchUtil.SizeGb(target):0.00} ГБ");
        Delete(target);
        return 0;
    }

    private static void Time(string name, Action act)
    {
        Console.Write($"{name} … ");
        var sw = Stopwatch.StartNew();
        act();
        Console.WriteLine(BenchUtil.Fmt(sw.Elapsed.TotalMilliseconds));
    }

    // Прежний путь: подключение с настройками SQLite по умолчанию, как у проверки копии раньше.
    private static void Exec(string file, string sql, string? param)
    {
        using var c = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = file, Pooling = false }.ToString());
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText    = sql;
        cmd.CommandTimeout = 0;
        if (param is not null) cmd.Parameters.AddWithValue("$p", param);
        cmd.ExecuteNonQuery();
    }

    private static void Delete(string file)
    {
        foreach (var s in new[] { "", "-journal", "-wal", "-shm" })
            if (File.Exists(file + s)) File.Delete(file + s);
    }
}
