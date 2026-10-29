using System.Diagnostics;
using Serilog;
using SmartBase.Logging;
using SmartBase.Utils;
using Npgsql;

namespace SmartBase.Database.Backup;

/// <summary>
/// Резервная копия базы PostgreSQL штатным pg_dump (формат custom, «-Fc»: сжатый,
/// восстанавливается pg_restore — целиком или выборочно). Приложение не носит клиент PG
/// с собой: бинарник ищется по тем же типовым путям установщиков, что и сервер в
/// PgServerControl/PgServerProbe, плюс PATH; не найден — честная русская ошибка
/// (появится в «Диагностике»), а не тихий пропуск. Пароль уходит переменной окружения
/// PGPASSWORD — в аргументах процесса (видны в списке процессов) его нет; в журнал
/// пароль и строка подключения не пишутся никогда (та же дисциплина, что DbErrors.Display).
/// </summary>
public static class PgDump
{
    // Кеш на процесс: установка PG между тиками службы не появляется, а обход путей —
    // это обращения к диску. Lazy без потокобезопасности не нужен: гонка даст тот же путь.
    private static string? _binary;
    private static bool _searched;

    /// <summary>Полный путь к pg_dump или null — клиент PostgreSQL не найден.</summary>
    public static string? FindBinary()
    {
        if (!_searched) { _binary = Locate(); _searched = true; }
        return _binary;
    }

    /// <summary>Сбросить кеш поиска (для тестов).</summary>
    public static void ResetForTests() { _binary = null; _searched = false; }

    /// <summary>
    /// Снять дамп базы в <paramref name="targetPath"/>. null — успех (строка об успехе уже
    /// в журнале), иначе русский текст ошибки; недописанный файл-цель убирается.
    /// </summary>
    public static async Task<string?> RunAsync(PgConnectionInfo info, string name, string targetPath,
                                              CancellationToken cancellationToken = default,
                                              Action<string, double?>? stage = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var binary = FindBinary();
        if (binary is null)
            return "Клиент PostgreSQL (pg_dump) не найден — установите инструменты сервера " +
                   "или добавьте pg_dump в PATH.";

        var watch = Stopwatch.StartNew();
        var part = targetPath + DatabaseManager.BackupPartSuffix;
        var applicationName = "SmartBase.backup." + Guid.NewGuid().ToString("N");
        var writingStarted = false;
        try
        {
            stage?.Invoke($"Проверяю свободное место для PostgreSQL «{name}»…", null);
            // Оценка консервативная: pg_dump не переносит индексы и сжимает данные.
            // Точный размер custom-архива заранее неизвестен, свободное место не резервируется.
            await using (var connection = new NpgsqlConnection(info.ToConnectionString()))
            {
                await connection.OpenAsync(cancellationToken);
                await using var command = connection.CreateCommand();
                command.CommandText = "SELECT pg_database_size(current_database());";
                var estimatedBytes = Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken));
                BackupSpace.RequireAvailable(Path.GetDirectoryName(Path.GetFullPath(targetPath))!, estimatedBytes);
            }
            cancellationToken.ThrowIfCancellationRequested();
            stage?.Invoke($"Сохраняю PostgreSQL «{name}»…", null);
            cancellationToken.ThrowIfCancellationRequested();
            writingStarted = true;
            TryDeletePartial(part);
            var psi = new ProcessStartInfo(binary)
            {
                RedirectStandardError = true,
                UseShellExecute       = false,
            };
            foreach (var a in new[]
            {
                "--format=custom", "--no-password",
                "--host", info.Host,
                "--port", info.Port.ToString(System.Globalization.CultureInfo.InvariantCulture),
                "--username", info.User,
                "--dbname", info.Database,
                "--file", part,
            })
                psi.ArgumentList.Add(a);
            // Секреты и режим SSL — через окружение: PGPASSWORD в аргументы не попадает.
            psi.Environment["PGPASSWORD"] = info.Password;
            psi.Environment["PGSSLMODE"]  = info.SslMode.ToLowerInvariant();
            psi.Environment["PGAPPNAME"] = applicationName;

            using var process = Process.Start(psi);
            if (process is null) return "pg_dump не запустился.";

            var errorRead = process.StandardError.ReadToEndAsync();
            using var cancellation = cancellationToken.Register(() =>
            {
                try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
                catch (InvalidOperationException) { /* процесс уже завершился */ }
                catch (System.ComponentModel.Win32Exception ex)
                { Log.Warning(ex, "Резервное копирование: не удалось остановить pg_dump"); }
            });
            // Даже при отмене дожидаемся закрытия файлов процессом до удаления .part.
            await process.WaitForExitAsync();
            var stderr = await errorRead;
            if (cancellationToken.IsCancellationRequested)
                await CancelOwnServerQueryAsync(info, applicationName);
            cancellationToken.ThrowIfCancellationRequested();

            if (process.ExitCode != 0)
            {
                var reason = stderr.Trim();
                return reason.Length > 0 ? reason : $"pg_dump завершился с кодом {process.ExitCode}.";
            }

            // Закрытый архив публикуется одним переименованием, как SQLite-копия.
            using (var file = new FileStream(part, FileMode.Open, FileAccess.ReadWrite, FileShare.Read))
            {
                Span<byte> magic = stackalloc byte[5];
                if (file.Read(magic) != 5 || !magic.SequenceEqual("PGDMP"u8))
                    return "pg_dump не создал корректный архив PostgreSQL.";
                file.Flush(flushToDisk: true);
            }
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(part, targetPath, overwrite: true);

            // Зеркало строки успеха SQLite-копии (DatabaseManager.BackupTo) — чтобы в
            // «Диагностике» оба провайдера читались одинаково.
            Log.Information("Резервная копия базы «{Name:l}» сохранена: {Target:l} ({Size:l}) за {Ms:N0} мс",
                            name, targetPath, Diag.Size(new FileInfo(targetPath).Length),
                            watch.Elapsed.TotalMilliseconds);
            return null;
        }
        catch (Exception) when (cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException(cancellationToken);
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
        finally
        {
            if (writingStarted) TryDeletePartial(part);
        }
    }

    private static async Task CancelOwnServerQueryAsync(PgConnectionInfo info, string applicationName)
    {
        // Сервер с client_connection_check_interval=0 не замечает закрытый клиентский
        // сокет, пока запрос ждет блокировку. Убитый pg_dump оставлял этот запрос жить
        // до снятия чужого lock. Отменяем только собственную уникальную сессию, тем же
        // пользователем; права администратора для pg_cancel_backend своего запроса не нужны.
        try
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var builder = new NpgsqlConnectionStringBuilder(info.ToConnectionString())
            {
                Pooling = false, Timeout = 3, CommandTimeout = 3,
            };
            await using var connection = new NpgsqlConnection(builder.ConnectionString);
            await connection.OpenAsync(deadline.Token);
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT pg_cancel_backend(pid) FROM pg_stat_activity " +
                "WHERE application_name=$1 AND usename=current_user AND datname=current_database() AND pid<>pg_backend_pid();";
            command.Parameters.AddWithValue(applicationName);
            await command.ExecuteNonQueryAsync(deadline.Token);
        }
        catch (Exception ex)
        {
            // Клиентский процесс уже остановлен; сервер может быть недоступен. Это
            // отдельный диагностический след, но отмену не выдаем за успешную копию.
            Log.Warning(ex, "Резервное копирование: не удалось отменить собственный запрос pg_dump на сервере");
        }
    }

    private static void TryDeletePartial(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch { /* best-effort: недописанный дамп не годен, но и мешает только месту на диске */ }
    }

    // ── Поиск бинарника ───────────────────────────────────────────────────────

    private static string? Locate()
    {
        try
        {
            var candidates = OperatingSystem.IsMacOS()   ? MacCandidates()
                           : OperatingSystem.IsWindows() ? WindowsCandidates()
                           : OperatingSystem.IsLinux()   ? LinuxCandidates()
                           : [];
            foreach (var path in candidates)
                if (File.Exists(path))
                    return path;

            // PATH — последним: явные установки новее и предсказуемее случайного клиента.
            return FoundInPath() ? DumpExe : null;
        }
        catch
        {
            return null;   // детект — best-effort: сломался — считаем, что клиента нет
        }
    }

    private static string DumpExe => OperatingSystem.IsWindows() ? "pg_dump.exe" : "pg_dump";

    // Homebrew (Apple Silicon и Intel) и Postgres.app — те же корни, что в PgServerControl.
    private static IEnumerable<string> MacCandidates()
    {
        foreach (var optDir in new[] { "/opt/homebrew/opt", "/usr/local/opt" })
        {
            if (!Directory.Exists(optDir)) continue;
            foreach (var formula in Directory.EnumerateDirectories(optDir, "postgresql*")
                                             .OrderDescending())   // при нескольких — самая новая
                yield return Path.Combine(formula, "bin", "pg_dump");
        }

        const string versions = "/Applications/Postgres.app/Contents/Versions";
        if (Directory.Exists(versions))
        {
            yield return Path.Combine(versions, "latest", "bin", "pg_dump");
            foreach (var v in Directory.EnumerateDirectories(versions).OrderDescending())
                yield return Path.Combine(v, "bin", "pg_dump");
        }
    }

    // Установки — по реестру установщика, службам Windows и «Program Files», самая новая первой:
    // тот же поиск, что у PgServerControl и PgServerProbe (PgWindowsInstalls). Пока смотрели
    // только «Program Files», сервер в другой папке резервной копии не доставался вовсе.
    private static IEnumerable<string> WindowsCandidates() =>
        PgWindowsInstalls.Find().Select(i => Path.Combine(i.BaseDir, "bin", "pg_dump.exe"));

    // Debian/Ubuntu кладут версионные bin-папки, RHEL — /usr/pgsql-N; обычно есть и /usr/bin.
    private static IEnumerable<string> LinuxCandidates()
    {
        yield return "/usr/bin/pg_dump";

        const string debianRoot = "/usr/lib/postgresql";
        if (Directory.Exists(debianRoot))
            foreach (var v in Directory.EnumerateDirectories(debianRoot).OrderDescending())
                yield return Path.Combine(v, "bin", "pg_dump");

        foreach (var root in Directory.Exists("/usr") ? Directory.EnumerateDirectories("/usr", "pgsql-*") : [])
            yield return Path.Combine(root, "bin", "pg_dump");
    }

    // «which pg_dump» / «where pg_dump» — как проба psql в PgServerProbe.
    private static bool FoundInPath()
    {
        try
        {
            var (file, arg) = OperatingSystem.IsWindows() ? ("where", "pg_dump") : ("which", "pg_dump");
            var psi = new ProcessStartInfo(file)
            {
                RedirectStandardOutput = true,
                RedirectStandardError  = true,
                UseShellExecute        = false,
            };
            psi.ArgumentList.Add(arg);
            using var p = Process.Start(psi);
            if (p is null) return false;
            p.WaitForExit(3000);
            return p.HasExited && p.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }
}
