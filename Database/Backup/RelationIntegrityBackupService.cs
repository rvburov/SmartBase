using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using Npgsql;
using SmartBase.Core.Models;

namespace SmartBase.Database.Backup;

/// <summary>Новая проверенная копия исходной базы перед подтвержденным исправлением связей.
/// Служба не восстанавливает и не перезаписывает пользовательские базы.</summary>
public static class RelationIntegrityBackupService
{
    public static async Task<RelationIntegrityBackup> CreateVerifiedAsync(DatabaseManager source,
        RelationIntegrityBackupRequest request, string backupDirectory, PgConnectionInfo? pgInfo = null,
        Action<string, double?>? stage = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(request);
        ct.ThrowIfCancellationRequested();
        var provider = source.Provider == "postgres" ? "postgres" : "sqlite";
        if (request.SourceDbId != source.DbId || request.Provider != provider
            || request.SourceDatabaseIdentity != source.Constructor.RelationDatabaseIdentity
            || string.IsNullOrWhiteSpace(request.PlanFingerprint))
            throw new InvalidOperationException("План исправления относится к другой базе. Резервная копия не создана.");
        if (provider == "postgres") ValidatePgSource(source, pgInfo);

        var created = DateTime.UtcNow;
        var extension = provider == "postgres" ? ".dump" : ".db";
        var stamp = created.ToString("yyyyMMdd'T'HHmmssfffffff'Z'", CultureInfo.InvariantCulture);
        var directory = Path.GetFullPath(backupDirectory);
        Directory.CreateDirectory(directory);
        var artifact = Path.Combine(directory, $"relation-repair-{source.DbId}-{stamp}-{Guid.NewGuid():N}{extension}");
        var pending = artifact + ".pending";
        try
        {
            if (provider == "postgres")
            {
                if (FindRestoreBinary() is null)
                    throw new InvalidOperationException("Клиент PostgreSQL (pg_restore) не найден. Установите инструменты сервера для проверки архива.");
                Report(stage, "Создаю резервный архив PostgreSQL…", null);
                // Штатный pg_dump берет пароль из выбранного подключения, а не из аргументов процесса.
                if (await PgDump.RunAsync(pgInfo!, source.Name, pending).ConfigureAwait(false) is not null)
                    throw new InvalidOperationException("Не удалось создать резервный архив PostgreSQL. Исправление не начато.");
                ct.ThrowIfCancellationRequested();
                Report(stage, "Проверяю резервный архив PostgreSQL…", null);
                await VerifyPgArchiveAsync(pending, ct).ConfigureAwait(false);
            }
            else
            {
                if (!File.Exists(source.Path)) throw new FileNotFoundException("Исходный файл базы не найден.");
                // BackupTo проверяет страницы, таблицы и индексы. Проверка внешних ключей здесь
                // не требуется: именно подтвержденные поврежденные связи предстоит исправлять.
                // Его синхронную копию дожидаемся даже при отмене, затем удаляем промежуточный файл.
                await Task.Run(() => DatabaseManager.BackupTo(source.Path, source.Name, pending,
                    (text, fraction) => Report(stage, text, fraction)), CancellationToken.None).ConfigureAwait(false);
                ct.ThrowIfCancellationRequested();
            }

            if (!File.Exists(pending) || new FileInfo(pending).Length == 0)
                throw new InvalidDataException("Резервная копия пуста. Исправление не начато.");
            await using (var file = new FileStream(pending, FileMode.Open, FileAccess.ReadWrite, FileShare.Read))
                file.Flush(flushToDisk: true);
            string hash;
            await using (var file = new FileStream(pending, FileMode.Open, FileAccess.Read, FileShare.Read,
                             1 << 20, FileOptions.Asynchronous | FileOptions.SequentialScan))
                hash = Convert.ToHexString(await SHA256.HashDataAsync(file, ct).ConfigureAwait(false));
            ct.ThrowIfCancellationRequested();
            // Новое уникальное имя не участвует в ротации и не заменяет прежние резервные копии.
            File.Move(pending, artifact, overwrite: false);
            return new RelationIntegrityBackup(request.SourceDbId, request.SourceDatabaseIdentity,
                request.PlanFingerprint, provider, artifact, hash, created);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or System.Data.Common.DbException)
        {
            // Исключения драйвера могут содержать параметры подключения. Наружу идет только
            // описание этапа без исходной строки и без вложенного исключения.
            throw new InvalidOperationException("Не удалось создать и проверить резервную копию. Исправление не начато.");
        }
        finally
        {
            DeletePending(pending);
            DeletePending(pending + DatabaseManager.BackupPartSuffix);
        }
    }

    /// <summary>Проверка всего архива без подключения к серверу: pg_restore читает и распаковывает
    /// все записи в поток SQL, который отбрасывается. Это не пробное восстановление в базу.</summary>
    public static async Task VerifyPgArchiveAsync(string artifactPath, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var binary = FindRestoreBinary()
            ?? throw new InvalidOperationException("Клиент PostgreSQL (pg_restore) не найден.");
        var start = new ProcessStartInfo(binary)
        {
            UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true,
        };
        foreach (var argument in new[] { "--no-owner", "--no-privileges", "--file=-", artifactPath })
            start.ArgumentList.Add(argument);
        using var process = Process.Start(start)
            ?? throw new InvalidOperationException("Не удалось запустить проверку резервного архива.");
        try
        {
            // Содержимое копии и сообщения утилиты не попадают в память, интерфейс или журнал.
            await Task.WhenAll(process.StandardOutput.BaseStream.CopyToAsync(Stream.Null, ct),
                process.StandardError.BaseStream.CopyToAsync(Stream.Null, ct), process.WaitForExitAsync(ct)).ConfigureAwait(false);
            if (process.ExitCode != 0)
                throw new InvalidDataException("Резервный архив PostgreSQL не прошел проверку.");
        }
        finally
        {
            if (!process.HasExited)
            {
                try { process.Kill(entireProcessTree: true); }
                catch (InvalidOperationException) { /* процесс уже завершился */ }
                await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            }
        }
    }

    /// <summary>Берет pg_restore из того же набора клиентских инструментов, что и pg_dump.</summary>
    public static string? FindRestoreBinary()
    {
        if (PgDump.FindBinary() is not { } dump) return null;
        var name = OperatingSystem.IsWindows() ? "pg_restore.exe" : "pg_restore";
        var directory = Path.GetDirectoryName(dump);
        if (!string.IsNullOrEmpty(directory))
        {
            var candidate = Path.Combine(directory, name);
            return File.Exists(candidate) ? candidate : null;
        }
        foreach (var entry in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
            if (File.Exists(Path.Combine(entry, name))) return Path.Combine(entry, name);
        return null;
    }

    private static void ValidatePgSource(DatabaseManager source, PgConnectionInfo? info)
    {
        if (info is null) throw new InvalidOperationException("Выберите сохраненное подключение PostgreSQL для резервной копии.");
        var selected = new NpgsqlConnectionStringBuilder(source.Path);
        if (!string.Equals(selected.Host, info.Host, StringComparison.OrdinalIgnoreCase) || selected.Port != info.Port
            || selected.Database != info.Database || selected.Username != info.User)
            throw new InvalidOperationException("Выбранное подключение PostgreSQL относится к другой базе.");
    }

    private static void Report(Action<string, double?>? stage, string text, double? fraction)
    {
        try { stage?.Invoke(text, fraction); }
        catch (Exception) { /* сбой отображения прогресса не отменяет и не публикует непроверенную копию */ }
    }

    private static void DeletePending(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (IOException) { /* при недоступном диске остается только промежуточное имя */ }
        catch (UnauthorizedAccessException) { /* готовым именем такой файл не публикуется */ }
    }
}
