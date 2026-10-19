using System.Diagnostics;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Serilog;
using SmartBase.Logging;

namespace SmartBase.Database;

// Резервная копия файла SQLite (служба резервного копирования, кнопка «Копировать сейчас»).
public sealed partial class DatabaseManager
{
    /// <summary>Суффикс недописанной копии: файл получает свое имя, только когда копия снята и
    /// проверена. Под именем копии раньше лежал и недописанный файл — оборванная копия (выход из
    /// программы, пропавший диск) считалась ротацией настоящей и вытесняла целую.</summary>
    public const string BackupPartSuffix = ".part";

    // Таблицы, проверку которых запускать первой: самые большие. Параллельная проверка идет
    // столько, сколько самая долгая таблица, и начать с нее — не ждать ее в хвосте.
    private static readonly string[] HeavyTables = ["db_field_values", "calendar_index", "db_records"];

    /// <summary>
    /// Резервная копия базы <paramref name="path"/> в файл <paramref name="targetPath"/>:
    /// компактная (<c>VACUUM INTO</c>, без свободных страниц) и проверенная полностью — копия с
    /// ошибкой целостности не сохраняется (InvalidOperationException, следов на диске нет).
    /// Источник не меняется и не отцепляется: копия снимается с консистентного снимка.
    ///
    /// Почему не просто <c>VACUUM INTO</c> и <c>integrity_check</c>, как раньше. Копия файла
    /// проводником шла в разы быстрее копии программой, и причин две:
    /// <list type="bullet">
    /// <item><c>VACUUM INTO</c> читает базу в порядке деревьев, то есть страницами вразброс. На
    /// своем диске это почти даром, а с сетевого диска каждая такая страница — отдельный запрос к
    /// серверу. Поэтому сетевую базу сначала снимаем целиком подряд, страница за страницей
    /// (Online Backup API — то же последовательное чтение, что у проводника, но с согласованным
    /// снимком), во временный файл на своем диске, и сжимаем уже его.</item>
    /// <item><c>integrity_check</c> работает в один поток и стоит дороже самой копии (на базе
    /// 0,4 ГБ — 4 с против 2 с). Ту же проверку делим на части и ведем параллельно:
    /// <c>quick_check</c> всего файла (структура страниц, свободный список, потерянные страницы)
    /// и <c>integrity_check(таблица)</c> по каждой таблице (согласованность таблицы и ее
    /// индексов — испорченный индекс <c>VACUUM INTO</c> переносит в копию дословно, и
    /// <c>quick_check</c> его не видит). Вместе это та же полная проверка.</item>
    /// </list>
    /// Копия пишется во временный файл и получает свое имя только после проверки. Папка копий на
    /// сетевом диске — копия собирается и проверяется на своем диске и уходит туда одним
    /// последовательным копированием. Не хватает места на своем диске — копия идет прежним путем,
    /// прямо в папку копий.
    /// </summary>
    public static void BackupTo(string path, string name, string targetPath, Action<string, double?>? stage)
    {
        var watch = Stopwatch.StartNew();
        var part  = targetPath + BackupPartSuffix;
        DeleteQuietly(part);

        long fileBytes, liveBytes;
        using (var probe = new AppDbContext(path))
        {
            probe.ApplyPerformancePragmas();
            fileBytes = new FileInfo(path).Length;
            liveBytes = Math.Max(1, fileBytes - SqliteMaintenance.ReadFreeSpace(probe).FreeBytes);
        }
        AppDbContext.ResetPool(path);

        var snapshotNeeded = NetworkPath.IsNetwork(path);
        var remoteTarget   = NetworkPath.IsNetwork(targetPath);
        var scratchDir     = snapshotNeeded || remoteTarget
            ? BackupScratchDir((snapshotNeeded ? fileBytes : 0) + (remoteTarget ? liveBytes : 0))
            : null;

        string? snapshot = snapshotNeeded && scratchDir is not null ? ScratchFile(scratchDir) : null;
        var     work     = remoteTarget && scratchDir is not null ? ScratchFile(scratchDir) : part;
        try
        {
            if (snapshot is not null)
            {
                using (FileGrowth(snapshot, fileBytes, $"Читаю «{name}»", stage))
                    SnapshotTo(path, snapshot);
                using (FileGrowth(work, liveBytes, $"Копирую «{name}»", stage))
                    VacuumIntoFrom(snapshot, work);
                DeleteQuietly(snapshot);
            }
            else
            {
                using var db = new AppDbContext(path);
                db.ApplyPerformancePragmas();
                // Путь — параметром: VACUUM INTO принимает выражение, апострофы в имени файла не ломают SQL.
                using (FileGrowth(work, liveBytes, $"Копирую «{name}»", stage))
                    db.Database.ExecuteSqlRaw("VACUUM INTO {0};", work);
            }
            AppDbContext.ResetPool(path);

            stage?.Invoke($"Проверяю копию «{name}»…", null);
            if (VerifyCopy(work, name, stage) is { } problem)
                throw new InvalidOperationException($"Копия не прошла проверку целостности: {problem}");

            if (work != part) CopyWithProgress(work, part, name, stage);
            File.Move(part, targetPath, overwrite: true);
        }
        finally
        {
            if (snapshot is not null) DeleteQuietly(snapshot);
            if (work != part) DeleteQuietly(work);
            DeleteQuietly(part);
        }
        Log.Information("Резервная копия базы «{Name:l}» сохранена: {Target:l} ({Size:l}) за {Ms:N0} мс{Via:l}",
                        name, targetPath, Diag.Size(new FileInfo(targetPath).Length), watch.Elapsed.TotalMilliseconds,
                        snapshot is not null ? ", через снимок на своем диске" : "");
    }

    /// <summary>Полная проверка копии параллельными частями (см. <see cref="BackupTo"/>).
    /// Возвращает первое найденное нарушение или null, если копия цела.</summary>
    internal static string? VerifyCopy(string file, string name, Action<string, double?>? stage)
    {
        List<string> tables = [];
        using (var conn = OpenCopy(file))
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT name FROM sqlite_schema WHERE type = 'table'";
            using var rdr = cmd.ExecuteReader();
            while (rdr.Read()) tables.Add(rdr.GetString(0));
        }

        var checks = new List<string> { "PRAGMA quick_check;" };
        checks.AddRange(tables.OrderBy(HeavyFirst)
                              .Select(t => $"PRAGMA integrity_check(\"{t.Replace("\"", "\"\"")}\");"));

        string? problem = null;
        var done = 0;
        // По одной части на поток (NoBuffering): иначе ForEach раздает части пачками, и две самые
        // большие таблицы проверялись одним потоком подряд — параллельности не было вовсе.
        Parallel.ForEach(System.Collections.Concurrent.Partitioner.Create(checks, System.Collections.Concurrent.EnumerablePartitionerOptions.NoBuffering),
            new ParallelOptions { MaxDegreeOfParallelism = Math.Clamp(Environment.ProcessorCount / 2, 1, 4) },
            (sql, loop) =>
            {
                if (Volatile.Read(ref problem) is not null) { loop.Stop(); return; }
                using var conn = OpenCopy(file);
                using var cmd  = conn.CreateCommand();
                cmd.CommandText    = sql;
                cmd.CommandTimeout = 24 * 3600;
                using (var rdr = cmd.ExecuteReader())
                    while (rdr.Read())
                    {
                        var line = rdr.GetString(0);
                        if (line == "ok") continue;
                        Interlocked.CompareExchange(ref problem, line, null);
                        loop.Stop();
                        break;
                    }
                var n = Interlocked.Increment(ref done);
                stage?.Invoke($"Проверяю копию «{name}»…", Math.Min(0.99, n / (double)checks.Count));
            });
        return problem;
    }

    private static int HeavyFirst(string table)
    {
        var i = Array.IndexOf(HeavyTables, table);
        return i >= 0 ? i : HeavyTables.Length;
    }

    // Подключение к копии только на чтение, мимо пула: копию никто, кроме проверки, не открывает,
    // и пул держал бы файл, который сразу после проверки переименовывается и удаляется.
    private static SqliteConnection OpenCopy(string file)
    {
        var cs = new SqliteConnectionStringBuilder
        {
            DataSource = file, Mode = SqliteOpenMode.ReadOnly, Pooling = false,
        }.ToString();
        var conn = SqliteGate.NewConnection(cs);
        conn.Open();
        using var cmd = conn.CreateCommand();
        // Проверка читает файл целиком: кеш побольше обычного и mmap (копия на своем диске).
        cmd.CommandText = "PRAGMA cache_size=-65536;"
                        + (NetworkPath.IsNetwork(file) ? "" : "PRAGMA mmap_size=268435456;");
        cmd.ExecuteNonQuery();
        return conn;
    }

    // Снимок сетевой базы подряд, страница за страницей (Online Backup API), — согласованный: на
    // время чтения база держит общий замок, как и прежний VACUUM INTO, только недолго.
    private static void SnapshotTo(string path, string snapshot)
    {
        using var src = new AppDbContext(path);
        var srcConn = (SqliteConnection)src.Database.GetDbConnection();
        src.Database.OpenConnection();
        try
        {
            var cs = new SqliteConnectionStringBuilder { DataSource = snapshot, Pooling = false }.ToString();
            using var dst = SqliteGate.NewConnection(cs);
            dst.Open();
            srcConn.BackupDatabase(dst);
        }
        finally
        {
            src.Database.CloseConnection();
        }
    }

    private static void VacuumIntoFrom(string source, string target)
    {
        var cs = new SqliteConnectionStringBuilder { DataSource = source, Pooling = false }.ToString();
        using var conn = SqliteGate.NewConnection(cs);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText    = "PRAGMA cache_size=-65536; PRAGMA mmap_size=268435456; VACUUM INTO $target;";
        cmd.CommandTimeout = 24 * 3600;
        cmd.Parameters.AddWithValue("$target", target);
        cmd.ExecuteNonQuery();
    }

    // Готовая проверенная копия — в папку копий на сетевом диске одним последовательным потоком.
    private static void CopyWithProgress(string from, string to, string name, Action<string, double?>? stage)
    {
        var total = Math.Max(1, new FileInfo(from).Length);
        using var src = new FileStream(from, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20);
        using var dst = new FileStream(to, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20);
        var buffer = new byte[4 << 20];
        long copied = 0, reportedAt = 0;
        stage?.Invoke($"Сохраняю копию «{name}»\n0 из {total / 1048576.0:N0} МБ", 0);
        int n;
        while ((n = src.Read(buffer, 0, buffer.Length)) > 0)
        {
            dst.Write(buffer, 0, n);
            copied += n;
            if (stage is null || copied - reportedAt < 32 << 20) continue;
            reportedAt = copied;
            stage($"Сохраняю копию «{name}»\n{copied / 1048576.0:N0} из {total / 1048576.0:N0} МБ",
                  Math.Min(0.99, copied / (double)total));
        }
        dst.Flush(flushToDisk: true);
    }

    // Ход этапа по росту файла: раз в полсекунды «N из ≈ M МБ».
    private static IDisposable? FileGrowth(string file, long expected, string phase, Action<string, double?>? stage) =>
        stage is null ? null : new System.Threading.Timer(_ =>
        {
            var done = File.Exists(file) ? new FileInfo(file).Length : 0;
            stage($"{phase}\n{done / 1048576.0:N0} из ≈ {expected / 1048576.0:N0} МБ",
                  Math.Min(0.99, done / (double)Math.Max(1, expected)));
        }, null, 0, 500);

    /// <summary>Папка для промежуточных файлов копии на своем диске — или null, если там не
    /// хватает места (с запасом в 1 ГБ: временная папка общая с системой). Брошенное
    /// оборванными копиями старше суток убирается здесь же.</summary>
    internal static string? BackupScratchDir(long needBytes)
    {
        try
        {
            var dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "SmartBase-backup");
            Directory.CreateDirectory(dir);
            foreach (var stale in Directory.EnumerateFiles(dir))
                if (DateTime.UtcNow - File.GetLastWriteTimeUtc(stale) > TimeSpan.FromDays(1))
                    DeleteQuietly(stale);
            return FreeBytes(dir) >= needBytes + (1L << 30) ? dir : null;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Резервное копирование: временная папка недоступна — копия пойдет прямо в папку копий");
            return null;
        }
    }

    // Свободно на томе папки: том с самым длинным корнем, который является началом ее пути.
    private static long FreeBytes(string dir)
    {
        var full = System.IO.Path.GetFullPath(dir);
        var drive = DriveInfo.GetDrives()
            .Where(d => d.IsReady && full.StartsWith(d.RootDirectory.FullName, StringComparison.OrdinalIgnoreCase))
            .MaxBy(d => d.RootDirectory.FullName.Length);
        return drive?.AvailableFreeSpace ?? 0;
    }

    private static string ScratchFile(string dir) =>
        System.IO.Path.Combine(dir, $"{Guid.NewGuid():N}.db");

    private static void DeleteQuietly(string file)
    {
        try { if (File.Exists(file)) File.Delete(file); }
        catch { /* best-effort: уберет следующая копия (старше суток) */ }
        try { if (File.Exists(file + "-journal")) File.Delete(file + "-journal"); } catch { /* так же */ }
    }
}
