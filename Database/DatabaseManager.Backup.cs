using System.Diagnostics;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Serilog;
using SmartBase.Logging;
using SmartBase.Database.Backup;

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
    public static void BackupTo(string path, string name, string targetPath, Action<string, double?>? stage,
                                CancellationToken cancellationToken = default)
    {
        try { BackupToCore(path, name, targetPath, stage, cancellationToken); }
        catch (Exception) when (cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException(cancellationToken);
        }
    }

    private static void BackupToCore(string path, string name, string targetPath, Action<string, double?>? stage,
                                    CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!File.Exists(path)) throw new FileNotFoundException("Файл базы не найден.", path);
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (System.IO.Path.GetFullPath(path).Equals(System.IO.Path.GetFullPath(targetPath), comparison))
            throw new ArgumentException("Путь резервной копии совпадает с исходной базой.", nameof(targetPath));
        var watch = Stopwatch.StartNew();
        var part  = targetPath + BackupPartSuffix;
        DeleteQuietly(part);

        long fileBytes, liveBytes;
        using (var probe = new AppDbContext(path))
        {
            probe.ApplyPerformancePragmas();
            probe.Database.OpenConnection();
            using var command = probe.Database.GetDbConnection().CreateCommand();
            using var interruption = new BackupCancellation((SqliteConnection)command.Connection!, cancellationToken);
            command.CommandText = "SELECT page_count * page_size FROM pragma_page_count, pragma_page_size;";
            // page_count включает страницы WAL, которых еще может не быть в основном файле.
            fileBytes = Convert.ToInt64(command.ExecuteScalar());
            liveBytes = Math.Max(1, fileBytes - SqliteMaintenance.ReadFreeSpace(probe).FreeBytes);
        }
        AppDbContext.ResetPool(path);
        cancellationToken.ThrowIfCancellationRequested();
        BackupSpace.RequireAvailable(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(targetPath))!, liveBytes);

        var snapshotNeeded = NetworkPath.IsNetwork(path);
        var remoteTarget   = NetworkPath.IsNetwork(targetPath);
        var scratchDir     = snapshotNeeded || remoteTarget
            // Папка копий и временная папка могут делить диск (включая bind mounts
            // и общие контейнеры APFS). Резервируем также место под конечный .part.
            // На разных дисках оценка консервативна: при нехватке временного места
            // отключается только промежуточная сборка, остается прямая копия.
            ? BackupScratchDir((snapshotNeeded ? fileBytes : 0) + (remoteTarget ? liveBytes : 0) + liveBytes)
            : null;

        string? snapshot = snapshotNeeded && scratchDir is not null ? ScratchFile(scratchDir) : null;
        var     work     = remoteTarget && scratchDir is not null ? ScratchFile(scratchDir) : part;
        try
        {
            if (snapshot is not null)
            {
                using (FileGrowth(snapshot, fileBytes, $"Читаю «{name}»", stage))
                    SnapshotTo(path, snapshot, cancellationToken);
                using (FileGrowth(work, liveBytes, $"Копирую «{name}»", stage))
                    VacuumIntoFrom(snapshot, work, cancellationToken);
                DeleteQuietly(snapshot);
            }
            else
            {
                using (FileGrowth(work, liveBytes, $"Копирую «{name}»", stage))
                    VacuumIntoFrom(path, work, cancellationToken);
            }
            AppDbContext.ResetPool(path);

            cancellationToken.ThrowIfCancellationRequested();
            stage?.Invoke($"Проверяю копию «{name}»…", null);
            if (VerifyCopy(work, name, stage, cancellationToken) is { } problem)
                throw new InvalidOperationException($"Копия не прошла проверку целостности: {problem}");

            if (work != part)
            {
                BackupSpace.RequireAvailable(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(part))!, new FileInfo(work).Length);
                CopyWithProgress(work, part, name, stage, cancellationToken);
            }
            cancellationToken.ThrowIfCancellationRequested();
            // После атомарной публикации отмена уже не превращает готовую копию в неуспех.
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
    internal static string? VerifyCopy(string file, string name, Action<string, double?>? stage,
                                       CancellationToken cancellationToken = default)
    {
        try { return VerifyCopyCore(file, name, stage, cancellationToken); }
        catch (Exception) when (cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException(cancellationToken);
        }
    }

    private static string? VerifyCopyCore(string file, string name, Action<string, double?>? stage,
                                         CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        List<string> tables = [];
        using (var conn = OpenCopy(file))
        using (var cmd = conn.CreateCommand())
        {
            using var interruption = new BackupCancellation(conn, cancellationToken);
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
            new ParallelOptions { MaxDegreeOfParallelism = Math.Clamp(Environment.ProcessorCount / 2, 1, 4), CancellationToken = cancellationToken },
            (sql, loop) =>
            {
                if (Volatile.Read(ref problem) is not null) { loop.Stop(); return; }
                using var conn = OpenCopy(file);
                using var interruption = new BackupCancellation(conn, cancellationToken);
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
        cancellationToken.ThrowIfCancellationRequested();
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
    private static void SnapshotTo(string path, string snapshot, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var src = new AppDbContext(path);
        var srcConn = (SqliteConnection)src.Database.GetDbConnection();
        src.Database.OpenConnection();
        try
        {
            using var interruption = new BackupCancellation(srcConn, cancellationToken);
            using var readTransaction = srcConn.BeginTransaction(deferred: true);
            using (var pin = srcConn.CreateCommand())
            {
                pin.Transaction = readTransaction;
                // BEGIN DEFERRED сам еще не фиксирует снимок: нужно настоящее чтение
                // btree. Без него внешние записи перезапускали бы backup между шагами.
                pin.CommandText = "SELECT name FROM sqlite_schema LIMIT 1;";
                pin.ExecuteScalar();
            }
            var cs = new SqliteConnectionStringBuilder { DataSource = snapshot, Pooling = false }.ToString();
            using var dst = SqliteGate.NewConnection(cs);
            dst.Open();
            var backup = SQLitePCL.raw.sqlite3_backup_init(dst.Handle!, "main", srcConn.Handle!, "main");
            if (backup is null) throw new InvalidOperationException("Не удалось начать согласованный снимок базы.");
            try
            {
                var waiting = Stopwatch.StartNew();
                while (true)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var result = SQLitePCL.raw.sqlite3_backup_step(backup, 256);
                    if (result == SQLitePCL.raw.SQLITE_DONE) break;
                    if (result == SQLitePCL.raw.SQLITE_OK) { waiting.Restart(); continue; }
                    if (result is SQLitePCL.raw.SQLITE_BUSY or SQLitePCL.raw.SQLITE_LOCKED)
                    {
                        if (waiting.Elapsed > TimeSpan.FromSeconds(60))
                            throw new IOException("База занята: согласованный снимок не удалось получить за минуту.");
                        if (cancellationToken.WaitHandle.WaitOne(50)) cancellationToken.ThrowIfCancellationRequested();
                        continue;
                    }
                    throw new SqliteException("Не удалось снять согласованный снимок базы.", result);
                }
            }
            finally { SQLitePCL.raw.sqlite3_backup_finish(backup); }
        }
        finally
        {
            src.Database.CloseConnection();
        }
    }

    private static void VacuumIntoFrom(string source, string target, CancellationToken cancellationToken)
    {
        var cs = new SqliteConnectionStringBuilder { DataSource = source, Pooling = false, Mode = SqliteOpenMode.ReadOnly }.ToString();
        using var conn = SqliteGate.NewConnection(cs);
        conn.Open();
        using var interruption = new BackupCancellation(conn, cancellationToken);
        using var cmd = conn.CreateCommand();
        cmd.CommandText    = "PRAGMA cache_size=-65536; "
                           + (NetworkPath.IsNetwork(source) ? "" : "PRAGMA mmap_size=268435456; ")
                           + "VACUUM INTO $target;";
        cmd.CommandTimeout = 24 * 3600;
        cmd.Parameters.AddWithValue("$target", target);
        cmd.ExecuteNonQuery();
    }

    // Готовая проверенная копия — в папку копий на сетевом диске одним последовательным потоком.
    private static void CopyWithProgress(string from, string to, string name, Action<string, double?>? stage,
                                          CancellationToken cancellationToken)
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
            cancellationToken.ThrowIfCancellationRequested();
            dst.Write(buffer, 0, n);
            copied += n;
            if (stage is null || copied - reportedAt < 32 << 20) continue;
            reportedAt = copied;
            stage($"Сохраняю копию «{name}»\n{copied / 1048576.0:N0} из {total / 1048576.0:N0} МБ",
                  Math.Min(0.99, copied / (double)total));
        }
        dst.Flush(flushToDisk: true);
        cancellationToken.ThrowIfCancellationRequested();
    }

    // Ход этапа по росту файла: раз в полсекунды «N из ≈ M МБ».
    private static IDisposable? FileGrowth(string file, long expected, string phase, Action<string, double?>? stage) =>
        stage is null ? null : new FileGrowthReporter(file, expected, phase, stage);

    private sealed class FileGrowthReporter : IDisposable
    {
        private System.Threading.Timer? _timer;
        private System.Runtime.ExceptionServices.ExceptionDispatchInfo? _failure;

        public FileGrowthReporter(string file, long expected, string phase, Action<string, double?> stage)
        {
            void Report()
            {
                long done = 0;
                try { if (File.Exists(file)) done = new FileInfo(file).Length; }
                catch (IOException) { /* файл мог исчезнуть между Exists и Length */ }
                stage($"{phase}\n{done / 1048576.0:N0} из ≈ {expected / 1048576.0:N0} МБ",
                      Math.Min(0.99, done / (double)Math.Max(1, expected)));
            }
            Report();
            _timer = new System.Threading.Timer(_ =>
            {
                try { Report(); }
                catch (Exception ex)
                {
                    Interlocked.CompareExchange(ref _failure,
                        System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(ex), null);
                }
            }, null, 500, 500);
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _timer, null) is not { } timer) return;
            // Dispose без ожидания оставлял старый отчет после начала следующего этапа
            // и даже после успеха. После этого барьера callbacks этапа уже не работают.
            using var drained = new ManualResetEvent(false);
            if (timer.Dispose(drained)) drained.WaitOne();
            _failure?.Throw();
        }
    }

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
            return BackupSpace.AvailableBytes(dir) >= needBytes + (1L << 30) ? dir : null;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Резервное копирование: временная папка недоступна — копия пойдет прямо в папку копий");
            return null;
        }
    }

    // Свой непуловый дескриптор: отмена не прерывает команды другой работы. Progress
    // handler закрывает гонку «token отменен перед запуском SQL, interrupt уже прошел».
    private sealed class BackupCancellation : IDisposable
    {
        private readonly SQLitePCL.sqlite3 _handle;
        private readonly CancellationTokenRegistration _registration;
        public BackupCancellation(SqliteConnection connection, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _handle = connection.Handle!;
            SQLitePCL.raw.sqlite3_progress_handler(_handle, 10_000,
                _ => cancellationToken.IsCancellationRequested ? 1 : 0, null);
            _registration = cancellationToken.Register(() => SQLitePCL.raw.sqlite3_interrupt(_handle));
        }
        public void Dispose()
        {
            _registration.Dispose();
            SQLitePCL.raw.sqlite3_progress_handler(_handle, 0, null!, null);
        }
    }

    private static string ScratchFile(string dir) =>
        System.IO.Path.Combine(dir, $"{Guid.NewGuid():N}.db");

    private static void DeleteQuietly(string file)
    {
        try { if (File.Exists(file)) File.Delete(file); }
        catch { /* best-effort: уберет следующая копия (старше суток) */ }
        // Только собственные временные файлы: readonly открытие WAL-снимка может
        // оставить пустые -wal/-shm, даже когда все подключения уже закрыты.
        foreach (var suffix in new[] { "-journal", "-wal", "-shm" })
            try { if (File.Exists(file + suffix)) File.Delete(file + suffix); } catch { /* так же */ }
    }
}
