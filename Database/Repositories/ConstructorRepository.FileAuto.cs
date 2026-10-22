using System.Data;
using System.Data.Common;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using SmartBase.Core.Models;

namespace SmartBase.Database.Repositories;

public partial class ConstructorRepository
{
    public enum FileAutoResolveResult
    {
        Ok, FolderInaccessible, NoRule, Cancelled, Conflict, NoAccess, TooManyMatches, Unavailable,
    }

    public static string FileAutoFailureMessage(FileAutoResolveResult result) => result switch
    {
        FileAutoResolveResult.FolderInaccessible => "Папка недоступна. Прежние привязки сохранены.",
        FileAutoResolveResult.Conflict => "Данные или настройки изменились во время поиска. Повторите обновление привязки.",
        FileAutoResolveResult.NoAccess => "Нет прав на обновление привязки файлов. Прежние значения сохранены.",
        FileAutoResolveResult.TooManyMatches => "Слишком много подходящих файлов для одной записи. Уточните имя, формат или папку. Прежние привязки сохранены.",
        FileAutoResolveResult.Unavailable => "Не удалось завершить поиск файлов. Проверьте доступность базы и свободное место на диске. Прежние привязки сохранены.",
        _ => "",
    };

    public bool FieldHasValues(int fieldId) =>
        db.DbFieldValues.AsNoTracking().Any(v => v.FieldId == fieldId && v.Value != "");

    /// <summary>Явный перескан. Каталог и результаты потоково складываются на локальный диск;
    /// общая база меняется только после проверки снимка, в одной транзакции.</summary>
    public FileAutoResolveResult ResolveAutoFileField(int fieldId)
    {
        var field = db.DbFields.AsNoTracking().FirstOrDefault(f => f.Id == fieldId);
        if (field?.FieldType != "file") return FileAutoResolveResult.NoRule;
        Demand(PermAction.EditRecord, field.CollectionId);
        var result = ResolveAutoFileFieldCore(fieldId, null, default, refresh: true);
        if (result == FileAutoResolveResult.Ok) DetachResolvedFileValues(fieldId);
        return result;
    }

    /// <summary>Скан, индексирование и запросы выполняются на своем контексте в фоне.
    /// NaN в progress означает фазу с неизвестным числом файлов (бегущая полоса).</summary>
    public async Task<FileAutoResolveResult> ResolveAutoFileFieldAsync(int fieldId,
        IProgress<double>? progress = null, CancellationToken ct = default)
    {
        var field = db.DbFields.AsNoTracking().FirstOrDefault(f => f.Id == fieldId);
        if (field?.FieldType != "file") return FileAutoResolveResult.NoRule;
        Demand(PermAction.EditRecord, field.CollectionId);
        var path = DbPath;
        var permissions = AutoFilePermissions;
        var config = field.ConfigJson;
        var result = await Task.Run(() =>
        {
            using var context = NewIntegrityContext(path, writable: true);
            return new ConstructorRepository(context) { Permissions = permissions }
                .ResolveAutoFileFieldCore(fieldId, progress, ct, refresh: true, expectedConfig: config);
        });
        if (result == FileAutoResolveResult.Ok)
        {
            try { DetachResolvedFileValues(fieldId); }
            catch (ObjectDisposedException) { /* окно/базу закрыли после успешного коммита */ }
        }
        return result;
    }

    private void DetachResolvedFileValues(int fieldId)
    {
        foreach (var entry in db.ChangeTracker.Entries<Entities.DbFieldValueEntity>()
                     .Where(e => e.Entity.FieldId == fieldId).ToList()) entry.State = EntityState.Detached;
    }

    /// <summary>После фоновой записи UI забывает только устаревшие отслеживаемые file-значения.</summary>
    public void ForgetAutoFileValues(int fieldId) => DetachResolvedFileValues(fieldId);

    private FileAutoResolveResult ResolveAutoFileFieldCore(int fieldId, IProgress<double>? progress,
        CancellationToken ct, bool refresh, int? recordId = null, string? expectedConfig = null)
    {
        try
        {
            ct.ThrowIfCancellationRequested();
            if (db.Database.CurrentTransaction is not null) return FileAutoResolveResult.Conflict;
            var field = db.DbFields.AsNoTracking().FirstOrDefault(f => f.Id == fieldId);
            if (field?.FieldType != "file") return FileAutoResolveResult.NoRule;
            if (expectedConfig is not null && expectedConfig != field.ConfigJson) return FileAutoResolveResult.Conflict;
            var rule = FileAutoRule.FromConfig(field.ConfigJson);
            if (rule is null) return FileAutoResolveResult.NoRule;
            DemandFreshFileAccess(field.CollectionId);
            if (!ValidFileSource(rule, field.CollectionId)) return FileAutoResolveResult.NoRule;

            using var stage = new AutoFileStage(ct);
            var fingerprint = ReadFileSourceSnapshot(fieldId, field.CollectionId, rule, recordId,
                (id, source, previous) => stage.Add(id, source, previous), ct);
            stage.FinishSources();
            progress?.Report(double.NaN);
            FileAutoCatalogCache.Lease catalog;
            try
            {
                catalog = FileAutoCatalogCache.Acquire(rule, refresh, ct,
                    _ => progress?.Report(double.NaN));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            { return FileAutoResolveResult.FolderInaccessible; }
            using (catalog)
            {
                progress?.Report(.45);
                stage.Match(source => catalog.Match(rule, source, ct), ct, progress);
            }
            ct.ThrowIfCancellationRequested();

            // До этого момента общая база не блокировалась для обхода сетевого каталога.
            using var transaction = db.Database.BeginTransaction(IsolationLevel.Serializable);
            if (db.IsPostgres)
            {
                using var guard = db.Database.GetDbConnection().CreateCommand();
                guard.Transaction = transaction.GetDbTransaction();
                guard.CommandText = "LOCK TABLE db_fields, db_records, db_field_values, db_users, db_permissions IN SHARE ROW EXCLUSIVE MODE";
                guard.ExecuteNonQuery();
            }
            var current = db.DbFields.AsNoTracking().FirstOrDefault(f => f.Id == fieldId);
            if (current?.FieldType != "file" || current.CollectionId != field.CollectionId
                || current.ConfigJson != field.ConfigJson) return FileAutoResolveResult.Conflict;
            DemandFreshFileAccess(field.CollectionId);
            if (!ValidFileSource(rule, field.CollectionId)
                || fingerprint != ReadFileSourceSnapshot(fieldId, field.CollectionId, rule, recordId, null, ct))
                return FileAutoResolveResult.Conflict;

            var changed = ApplyFileStage(fieldId, stage, ct, progress);
            ct.ThrowIfCancellationRequested();
            if (changed > 0)
            {
                WriteFileAutoAudit(field.CollectionId, changed);
            }
            transaction.Commit();
            transaction.Dispose();
            try
            {
                if (changed > 0)
                {
                    ChangeBus.Publish(DbPath, [new("update", "records", CollectionNameOf(field.CollectionId))]);
                    NotifyRelationDataChanged(field.CollectionId, fieldId, recordId);
                }
                progress?.Report(1);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            { Serilog.Log.Warning("Файлы обновлены, обновление интерфейса отложено"); }
            return FileAutoResolveResult.Ok;
        }
        catch (OperationCanceledException) { return FileAutoResolveResult.Cancelled; }
        catch (Exception ex) when (ct.IsCancellationRequested && ex is DbException or DbUpdateException)
        { return FileAutoResolveResult.Cancelled; }
        catch (FileAutoMatchLimitException) { return FileAutoResolveResult.TooManyMatches; }
        catch (PermissionDeniedException) { return FileAutoResolveResult.NoAccess; }
        catch (Exception ex) when (IsWriteConflict(ex)) { return FileAutoResolveResult.Conflict; }
        catch (Exception ex) when (ex is DbException or DbUpdateException or IOException or UnauthorizedAccessException)
        { return FileAutoResolveResult.Unavailable; }
    }

    // Сырой аудит в той же транзакции: SaveChanges общего контекста мог бы заодно
    // сохранить не относящиеся к перескану незавершенные изменения вызывающего кода.
    private void WriteFileAutoAudit(int collectionId, long changed)
    {
        using var command = db.Database.GetDbConnection().CreateCommand();
        command.Transaction = db.Database.CurrentTransaction!.GetDbTransaction();
        command.CommandText = "INSERT INTO change_log (Ts, UserKey, UserName, Action, TargetType, TargetName, CollectionName, Count, Detail) "
            + "VALUES (@ts,@user,@name,'update','records','Автоподстановка файлов',@collection,@count,NULL)";
        AddParam(command, "@ts", DbType.String).Value = DateTime.UtcNow.ToString("o");
        AddParam(command, "@user", DbType.String).Value = Utils.CurrentUser.Key;
        AddParam(command, "@name", DbType.String).Value = Utils.CurrentUser.DisplayName;
        AddParam(command, "@collection", DbType.String).Value = CollectionNameOf(collectionId);
        AddParam(command, "@count", DbType.Int64).Value = changed;
        command.ExecuteNonQuery();
    }

    private bool ValidFileSource(FileAutoRule rule, int collectionId) => rule.SourceIsTitle
        || db.DbFields.AsNoTracking().Any(f => f.Id == rule.SourceFieldId
            && f.CollectionId == collectionId && f.FieldType == "text");

    private void DemandFreshFileAccess(int collectionId)
    {
        if (AutoFilePermissions is not { } permissions) return;
        permissions.Demand(PermAction.EditRecord, collectionId);
        permissions.Demand(PermAction.ViewCollection, collectionId);
        var fresh = new PermissionService(db);
        fresh.Reload(notify: false);
        fresh.Demand(PermAction.EditRecord, collectionId);
        fresh.Demand(PermAction.ViewCollection, collectionId);
    }

    private bool CanWriteAutoFiles(int collectionId) => AutoFilePermissions is not { } permissions
        || permissions.Can(PermAction.EditRecord, collectionId) && permissions.Can(PermAction.ViewCollection, collectionId);

    private string ReadFileSourceSnapshot(int fieldId, int collectionId, FileAutoRule rule, int? recordId,
        Action<int, string, string>? accept, CancellationToken ct)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var connection = db.Database.GetDbConnection();
        var wasOpen = connection.State == ConnectionState.Open;
        if (!wasOpen) connection.Open();
        try
        {
            using var command = connection.CreateCommand();
            command.Transaction = db.Database.CurrentTransaction?.GetDbTransaction();
            var source = rule.SourceIsTitle ? "r.Title" : "s.Value";
            var join = rule.SourceIsTitle ? "" : $" LEFT JOIN db_field_values s ON s.RecordId = r.Id AND s.FieldId = {rule.SourceFieldId}";
            command.CommandText = $"SELECT r.Id, COALESCE({source}, ''), COALESCE(v.Value, '') FROM db_records r{join} "
                + $"LEFT JOIN db_field_values v ON v.RecordId = r.Id AND v.FieldId = {fieldId} "
                + $"WHERE r.CollectionId = {collectionId}"
                + (recordId is int selectedRecord ? $" AND r.Id = {selectedRecord}" : "") + " ORDER BY r.Id";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                ct.ThrowIfCancellationRequested();
                var id = reader.GetInt32(0);
                var value = reader.GetString(1);
                var previous = reader.GetString(2);
                AppendSnapshot(hash, id.ToString(System.Globalization.CultureInfo.InvariantCulture));
                AppendSnapshot(hash, value);
                AppendSnapshot(hash, previous);
                accept?.Invoke(id, value, previous);
            }
            return Convert.ToHexString(hash.GetHashAndReset());
        }
        finally { if (!wasOpen) connection.Close(); }
    }

    private static void AppendSnapshot(IncrementalHash hash, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        Span<byte> length = stackalloc byte[4];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(length, bytes.Length);
        hash.AppendData(length);
        hash.AppendData(bytes);
    }

    private long ApplyFileStage(int fieldId, AutoFileStage stage, CancellationToken ct, IProgress<double>? progress)
    {
        var connection = db.Database.GetDbConnection();
        using var delete = connection.CreateCommand();
        using var insert = connection.CreateCommand();
        delete.Transaction = insert.Transaction = db.Database.CurrentTransaction!.GetDbTransaction();
        delete.CommandText = $"DELETE FROM db_field_values WHERE FieldId = {fieldId} AND RecordId = @rid";
        insert.CommandText = $"INSERT INTO db_field_values (RecordId, FieldId, Value) VALUES (@rid, {fieldId}, @value)";
        var deleteId = AddParam(delete, "@rid", DbType.Int32);
        var insertId = AddParam(insert, "@rid", DbType.Int32);
        var value = AddParam(insert, "@value", DbType.String);
        long count = 0;
        foreach (var (record, next) in stage.ReadChanges())
        {
            ct.ThrowIfCancellationRequested();
            deleteId.Value = insertId.Value = record;
            delete.ExecuteNonQuery();
            if (next.Length > 0) { value.Value = next; insert.ExecuteNonQuery(); }
            if (++count % 1000 == 0) progress?.Report(.85 + .14 * count / Math.Max(1, stage.Count));
        }
        return count;
    }

    private string ReadSourceValue(FileAutoRule rule, int recordId) => rule.SourceIsTitle
        ? db.DbRecords.AsNoTracking().Where(r => r.Id == recordId).Select(r => r.Title).FirstOrDefault() ?? ""
        : db.DbFieldValues.AsNoTracking().Where(v => v.FieldId == rule.SourceFieldId && v.RecordId == recordId)
            .Select(v => v.Value).FirstOrDefault() ?? "";

    // API/стенды без главного окна сохраняют синхронный контракт. В приложении запрос
    // передается планировщику после коммита и никогда не сканирует диск на UI-потоке.
    private string? ResolveOrQueueAutoFile(int fieldId, int recordId, FileAutoRule rule)
    {
        var collectionId = CollectionOfRecord(recordId);
        if (collectionId is not int collection || !CanWriteAutoFiles(collection)
            || !ValidFileSource(rule, collection)) return null;
        if (QueueAutoFileWork(fieldId, recordId)) return null;
        try
        {
            DemandFreshFileAccess(collection);
            var source = ReadSourceValue(rule, recordId);
            if (source.Length == 0 || rule.PartialMatch && string.IsNullOrWhiteSpace(source)) return "";
            using var catalog = FileAutoCatalogCache.Acquire(rule, refresh: false, default);
            var matched = catalog.Match(rule, source, default);
            DemandFreshFileAccess(collection);
            return matched;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DbException
            or FileAutoMatchLimitException or PermissionDeniedException)
        { return null; } // недоступность/слишком широкий поиск не стирают прежнее значение
    }

    private void ResolveAutoFileForRecord(int collectionId, int recordId)
    {
        // Создание разрешено отдельно от последующего автозаполнения: отсутствие EditRecord
        // не отменяет новую запись. Пустой источник дождется обычной правки значения.
        if (!CanWriteAutoFiles(collectionId)) return;
        foreach (var field in db.DbFields.AsNoTracking().Where(f => f.CollectionId == collectionId && f.FieldType == "file")
                     .Select(f => new { f.Id, f.ConfigJson }).ToList())
            if (FileAutoRule.FromConfig(field.ConfigJson) is { } rule
                && ValidFileSource(rule, collectionId)
                && ReadSourceValue(rule, recordId) is { Length: > 0 } source
                && (!rule.PartialMatch || !string.IsNullOrWhiteSpace(source))
                && ResolveOrQueueAutoFile(field.Id, recordId, rule) is { } matched)
                SetAutoFileValue(field.Id, recordId, matched);
    }

    private sealed class AutoFileStage : IDisposable
    {
        private readonly SqliteConnection _connection;
        private readonly SqliteCommand _insert;
        private readonly CancellationTokenRegistration _cancellation;
        private SqliteTransaction? _transaction;
        public long Count { get; private set; }

        public AutoFileStage(CancellationToken ct)
        {
            // Пустой DataSource — приватная временная база SQLite НА ДИСКЕ, удаляется
            // библиотекой после закрытия. Она не попадает в пользовательскую базу/backup.
            _connection = SqliteGate.NewConnection("Data Source=;Mode=ReadWriteCreate;Pooling=False");
            try
            {
                _connection.Open();
                _cancellation = ct.Register(() =>
                {
                    if (_connection.Handle is { } handle) SQLitePCL.raw.sqlite3_interrupt(handle);
                });
                using var setup = _connection.CreateCommand();
                setup.CommandText = "PRAGMA cache_size=-8192; PRAGMA temp_store=FILE; PRAGMA journal_mode=OFF; PRAGMA synchronous=OFF; "
                    + "CREATE TABLE sources (record_id INTEGER PRIMARY KEY, source TEXT NOT NULL, previous TEXT NOT NULL, next TEXT NOT NULL DEFAULT '');";
                setup.ExecuteNonQuery();
                _transaction = _connection.BeginTransaction();
                _insert = _connection.CreateCommand();
                _insert.CommandText = "INSERT INTO sources (record_id, source, previous) VALUES ($id,$source,$previous)";
                _insert.Parameters.AddWithValue("$id", 0);
                _insert.Parameters.AddWithValue("$source", "");
                _insert.Parameters.AddWithValue("$previous", "");
                _insert.Transaction = _transaction;
            }
            catch
            {
                _cancellation.Dispose();
                _transaction?.Dispose();
                _connection.Dispose();
                throw;
            }
        }

        public void Add(int id, string source, string previous)
        {
            _insert.Parameters[0].Value = id;
            _insert.Parameters[1].Value = source;
            _insert.Parameters[2].Value = previous;
            _insert.ExecuteNonQuery();
            if (++Count % 4096 != 0) return;
            _transaction!.Commit();
            _transaction.Dispose();
            _transaction = _connection.BeginTransaction();
            _insert.Transaction = _transaction;
        }

        public void FinishSources()
        {
            _transaction!.Commit();
            _transaction.Dispose();
            _transaction = null;
            _insert.Transaction = null;
            using var index = _connection.CreateCommand();
            index.CommandText = "CREATE INDEX sources_value ON sources(source)";
            index.ExecuteNonQuery();
        }

        public void Match(Func<string, string> match, CancellationToken ct, IProgress<double>? progress)
        {
            using var count = _connection.CreateCommand();
            count.CommandText = "SELECT COUNT(DISTINCT source) FROM sources";
            var total = Convert.ToInt64(count.ExecuteScalar());
            using var select = _connection.CreateCommand();
            select.CommandText = "SELECT DISTINCT source FROM sources ORDER BY source";
            using var update = _connection.CreateCommand();
            update.CommandText = "UPDATE sources SET next=$next WHERE source=$source";
            update.Parameters.AddWithValue("$source", "");
            update.Parameters.AddWithValue("$next", "");
            using var transaction = _connection.BeginTransaction();
            select.Transaction = update.Transaction = transaction;
            using var rows = select.ExecuteReader();
            long done = 0;
            while (rows.Read())
            {
                ct.ThrowIfCancellationRequested();
                var source = rows.GetString(0);
                var next = match(source);
                update.Parameters[0].Value = source;
                update.Parameters[1].Value = next;
                update.ExecuteNonQuery();
                if (++done % 100 == 0) progress?.Report(.45 + .39 * done / Math.Max(1, total));
            }
            rows.Close();
            transaction.Commit();
        }

        public IEnumerable<(int RecordId, string Value)> ReadChanges()
        {
            using var command = _connection.CreateCommand();
            command.CommandText = "SELECT record_id,next FROM sources WHERE previous<>next ORDER BY record_id";
            using var reader = command.ExecuteReader();
            while (reader.Read()) yield return (reader.GetInt32(0), reader.GetString(1));
        }

        public void Dispose()
        {
            _cancellation.Dispose();
            _insert.Dispose();
            _transaction?.Dispose();
            _connection.Dispose();
        }
    }
}
