using System.Collections;
using System.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Storage;

namespace SmartBase.Database.Repositories;

public partial class ConstructorRepository
{
    /// <summary>
    /// Составная операция репозитория: проверка свежих данных и все записи выполняются вместе.
    /// Serializable не дает двум клиентам подтвердить правку одного снимка поверх друг друга.
    /// Исключение откатывает и данные, и журнал изменений.
    ///
    /// На PostgreSQL эта защита срабатывает отказом: сосед тронул прочитанное, и сервер
    /// отвечает 40001 (сериализация) или 40P01 (взаимная блокировка) — на любой команде или на
    /// самом коммите. Тогда операция повторяется целиком, на свежих данных: ее же проверки и
    /// решают, писать ли теперь. Повторить можно только свою транзакцию — внешнюю повторяет
    /// ее владелец.
    /// </summary>
    public T InWriteTransaction<T>(Func<T> operation) =>
        ExecuteWriteOperation(operation, IsolationLevel.Serializable);

    /// <summary>Свежая проверка ключа/номера и запись в одной коллекции. PostgreSQL сначала
    /// запирает коллекцию, затем читает свежие строки в ReadCommitted; SQLite берет writer lock
    /// до чтения. Работает и между отдельными процессами, без кеша выданных номеров.</summary>
    public T InCollectionWriteTransaction<T>(int collectionId, Func<T> operation) =>
        ExecuteWriteOperation(() =>
        {
            if (db.IsPostgres)
            {
                using var command = db.Database.GetDbConnection().CreateCommand();
                command.Transaction = db.Database.CurrentTransaction!.GetDbTransaction();
                command.CommandTimeout = db.Database.GetCommandTimeout() ?? 30;
                command.CommandText = "SELECT id FROM db_collections WHERE id = @id FOR UPDATE";
                AddParam(command, "@id").Value = collectionId;
                if (command.ExecuteScalar() is null)
                    throw new InvalidOperationException("Коллекция уже удалена.");
            }
            else if (!db.DbCollections.Any(c => c.Id == collectionId))
                throw new InvalidOperationException("Коллекция уже удалена.");
            return operation();
        }, db.IsPostgres ? IsolationLevel.ReadCommitted : IsolationLevel.Serializable);

    private T ExecuteAtomicWrite<T>(Func<T> operation) =>
        ExecuteWriteOperation(operation, db.IsPostgres ? IsolationLevel.ReadCommitted : IsolationLevel.Serializable,
            privateCommand: true);

    private int _atomicCommandDepth;

    private T InvokePublicWriteCallback<T>(Func<T> callback)
    {
        var previous = _atomicCommandDepth;
        _atomicCommandDepth = 0;
        try { return callback(); }
        finally { _atomicCommandDepth = previous; }
    }

    private T ExecuteWriteOperation<T>(Func<T> operation, IsolationLevel isolation, bool privateCommand = false)
    {
        // Внутренние шаги одной закрытой команды откатываются вместе с ее внешним scope.
        // Не копируем весь трекер и не создаем savepoint на каждое поле задачи. Публичные
        // callback API эту привилегию не получают: внутри них можно поймать ошибку и продолжить.
        if (privateCommand && _atomicCommandDepth > 0) return operation();
        var own = db.Database.CurrentTransaction is null;
        for (var attempt = 1; ; attempt++)
        {
            var merges = own ? null : _editMerges.ToArray();
            WriteScope? scope = null;
            try
            {
                using var lifetime = scope = new WriteScope(db, isolation, savepoint: !own);
                var previousDepth = _atomicCommandDepth;
                T result;
                _atomicCommandDepth = privateCommand ? previousDepth + 1 : 0;
                try { result = operation(); }
                finally { _atomicCommandDepth = previousDepth; }
                scope.Commit();
                return result;
            }
            catch (Exception error)
            {
                // Dispose/уведомление после подтвержденного коммита не дает права еще раз
                // выполнить команду, даже если его ошибка похожа на конфликт записи.
                if (scope?.Committed == true) throw;
                // Даже не повторяемый отказ не должен оставить ссылку на откаченный журнал.
                _editMerges.Clear();
                if (merges is not null)
                    foreach (var merge in merges) _editMerges[merge.Key] = merge.Value;
                if (!own || attempt >= WriteConflictAttempts || !IsWriteConflict(error)) throw;
                Thread.Sleep(Random.Shared.Next(5, 15) * attempt);
            }
        }
    }

    /// <summary>Сколько раз пробовать составную операцию, пока соседи перебивают ее снимок.</summary>
    private const int WriteConflictAttempts = 5;

    /// <summary>Отказ сериализации или взаимная блокировка PostgreSQL, на любой глубине вложения.</summary>
    private static bool IsWriteConflict(Exception error) =>
        error is Npgsql.PostgresException { SqlState: "40001" or "40P01" }
        or Microsoft.Data.Sqlite.SqliteException { SqliteErrorCode: 5 or 6 }
        || (error.InnerException is { } inner && IsWriteConflict(inner));

    // Детерминированный порядок нужен переносам и связям, запирающим несколько записей.
    // SQLite уже держит RESERVED writer lock. PG FOR UPDATE также не дает удалить parent
    // посреди проверки/вставки дочернего значения или локальной ссылки.
    private void LockRecordsForWrite(IEnumerable<int> ids)
    {
        if (!db.IsPostgres) return;
        foreach (var id in ids.Distinct().Order())
        {
            using var command = db.Database.GetDbConnection().CreateCommand();
            command.Transaction = db.Database.CurrentTransaction!.GetDbTransaction();
            command.CommandTimeout = db.Database.GetCommandTimeout() ?? 30;
            command.CommandText = "SELECT id FROM db_records WHERE id = @id FOR UPDATE";
            AddParam(command, "@id").Value = id;
            command.ExecuteScalar();
        }
    }

    /// <summary>Версия внешних изменений SQLite только на уже открытом подключении.
    /// Не переносится между подключениями и не используется как идентичность базы.</summary>
    public long? ReadExternalWriteVersion()
    {
        var connection = db.Database.GetDbConnection();
        if (db.IsPostgres || connection.State != ConnectionState.Open) return null;
        using var command = connection.CreateCommand();
        command.Transaction = db.Database.CurrentTransaction?.GetDbTransaction();
        command.CommandText = "PRAGMA data_version";
        return Convert.ToInt64(command.ExecuteScalar());
    }

    /// <summary>
    /// Открывает транзакцию записи — но только если своей еще нет. Нужна там, где составная
    /// операция собрана из шагов, каждый из которых умеет коммитить сам.
    ///
    /// Зачем. Создание ОДНОЙ задачи шло 9–11 независимыми автокоммитами (а на первой задаче
    /// в коллекции — до 15–20): EnsureTaskCollection, EnsureRoles, AddField, AddRecord или
    /// AddCalendarRecord, три SetFieldValue, и каждый SetFieldValue внутри себя писал еще
    /// 3–4 раза (значение, аудит db_records, calendar_index). Каждый коммит — свой цикл
    /// захвата и снятия файловой блокировки; на сетевой папке (журнал DELETE) писатель на
    /// коммите берет PENDING+EXCLUSIVE и НЕ пускает новых читателей. То есть один клик по
    /// «добавить задачу» открывал полтора десятка окон, в любое из которых чужое чтение
    /// попадало с «database is locked» — и наоборот. Плюс задача была неатомарной: обрыв
    /// посередине оставлял запись без исполнителя или коллекцию наполовину размеченной.
    ///
    /// Почему нельзя просто обернуть все в BeginTransaction. SQLite вложенных транзакций не
    /// поддерживает, а Microsoft.Data.Sqlite на второй BeginTransaction кидает
    /// InvalidOperationException. Шаги при этом вызываются и по отдельности (тумблер роли в
    /// диалоге свойства, правка одной ячейки), где транзакция нужна своя. Поэтому scope
    /// смотрит на <c>Database.CurrentTransaction</c>: своей нет — начинает и отвечает за
    /// коммит/откат; чужая есть — не делает ничего и оставляет решение владельцу.
    /// </summary>
    private WriteScope BeginWrite() => new(db);

    private sealed class WriteScope : IDisposable
    {
        private readonly AppDbContext          _ctx;
        private readonly IDbContextTransaction? _own;   // null — транзакцию держит внешний вызов
        private readonly IDbContextTransaction? _outer;
        private readonly string? _savepoint;
        private readonly Action? _restore;
        private readonly IDisposable? _admission;
        private bool _committed;
        internal bool Committed => _committed;

        // Очередь локальных фоновых работ не создает тысячи одновременно ожидающих SQLite
        // writer locks. Защита других процессов остается настоящей транзакцией в СУБД.
        public WriteScope(AppDbContext ctx, IsolationLevel? isolation = null, bool savepoint = false)
        {
            _ctx = ctx;
            if (ctx.Database.CurrentTransaction is { } outer)
            {
                if (!savepoint) return;
                _outer = outer;
                _savepoint = "write_" + Guid.NewGuid().ToString("N");
                _restore = CaptureSavepointState(ctx);
                outer.CreateSavepoint(_savepoint);
                return;
            }

            if (ctx.IsPostgres)
            {
                var settings = new Npgsql.NpgsqlConnectionStringBuilder(ctx.DbPath);
                var key = "pg:" + settings.Host + ":" + settings.Port + "/" + settings.Database;
                var seconds = ctx.Database.GetCommandTimeout() ?? 30;
                var wait = seconds == 0 ? Timeout.InfiniteTimeSpan : TimeSpan.FromSeconds(seconds);
                _admission = WriteAdmission.ForDatabase(key, 16).Enter(wait);
            }
            // SQLite admission живет у подготовленного EF native BEGIN в SqliteGate:
            // один lease и для этих scope, и для caller-owned BeginTransaction.
            try
            {
                _own = isolation is { } level ? ctx.Database.BeginTransaction(level)
                    : ctx.Database.BeginTransaction();
            }
            catch { _admission?.Dispose(); throw; }
        }

        public void Commit()
        {
            // Флаг — только после коммита: упавший COMMIT (PostgreSQL отдает отказ сериализации
            // и здесь) должен откатиться и очистить трекер, как любой другой сбой.
            _own?.Commit();
            if (_savepoint is not null) _outer!.ReleaseSavepoint(_savepoint);
            _committed = true;
        }

        public void Dispose()
        {
            if (_own is null)
            {
                if (!_committed && _savepoint is not null)
                {
                    try
                    {
                        _outer!.RollbackToSavepoint(_savepoint);
                        _outer.ReleaseSavepoint(_savepoint);
                    }
                    finally { _restore!(); }
                }
                return;
            }

            try
            {
                if (!_committed)
                {
                    // Откат вернул строки в БД, но трекер держит правки примененными: следующее
                    // чтение отдало бы, например, «размеченную» роль, которой в базе нет.
                    // Чистим — перечитается. Сам Rollback может упасть, если соединение уже
                    // умерло; исходное исключение важнее, поэтому глушим.
                    try { _own.Rollback(); } catch { /* исходная ошибка уже летит наверх */ }
                    _ctx.ChangeTracker.Clear();
                }
            }
            finally
            {
                try { _own.Dispose(); }
                catch (Exception error) when (_committed && error is not OutOfMemoryException)
                {
                    // Данные подтверждены коммитом: отказ освобождения ресурса не означает
                    // отказ записи. Тип ошибки безопасен, путь/строка подключения сюда не идут.
                    Serilog.Log.Warning("Не удалось освободить транзакцию после сохранения ({ErrorType})",
                        error.GetType().Name);
                }
                finally { _admission?.Dispose(); }
            }
        }

        private static Action CaptureSavepointState(AppDbContext context)
        {
            var entries = context.ChangeTracker.Entries().Select(entry => new TrackedState(entry.Entity,
                entry.State, entry.CurrentValues.Clone(), entry.OriginalValues.Clone(),
                entry.Properties.ToDictionary(p => p.Metadata.Name, p => p.IsTemporary),
                entry.Properties.ToDictionary(p => p.Metadata.Name, p => p.IsModified),
                entry.Navigations.Select(navigation => new NavigationState(navigation.Metadata.Name,
                    navigation.Metadata.IsCollection, navigation.CurrentValue,
                    navigation.Metadata.IsCollection && navigation.CurrentValue is IEnumerable collection
                        ? collection.Cast<object>().ToArray() : null,
                    navigation.Metadata.TargetEntityType.ClrType, navigation.IsLoaded)).ToArray())).ToArray();
            var restoreChanges = context.CaptureTransactionChanges();
            var fileWork = PendingFiles.TryGetValue(context, out var files)
                ? files.Fields.ToDictionary(f => f.Key, f => f.Value.ToDictionary(r => r.Key, r => r.Value)) : null;
            var relationWork = PendingRelationChanges.TryGetValue(context, out var relations)
                ? relations.Changes.ToDictionary(r => r.Key, r => r.Value) : null;
            return () =>
            {
                context.ChangeTracker.Clear();
                foreach (var state in entries)
                {
                    var entry = context.Entry(state.Entity);
                    entry.CurrentValues.SetValues(state.Current);
                    entry.OriginalValues.SetValues(state.Original);
                    foreach (var navigation in state.Navigations)
                    {
                        var target = entry.Navigation(navigation.Name);
                        if (navigation.Collection && navigation.Value is { } collection)
                        {
                            // Clear не откатывает CLR navigation fixup. Убираем из прежней
                            // коллекции новых детей до повторного присоединения графа.
                            if (collection is IList list)
                            {
                                list.Clear();
                                foreach (var item in navigation.Items!) list.Add(item);
                            }
                            else
                            {
                                var contract = typeof(ICollection<>).MakeGenericType(navigation.TargetType);
                                contract.GetMethod("Clear")!.Invoke(collection, null);
                                var add = contract.GetMethod("Add")!;
                                foreach (var item in navigation.Items!) add.Invoke(collection, [item]);
                            }
                        }
                        target.CurrentValue = navigation.Value;
                        target.IsLoaded = navigation.Loaded;
                    }
                }
                // Навигации всех объектов уже восстановлены: EF fixup не увидит ребенка
                // откаченной команды и не сохранит его позднее через DetectChanges.
                foreach (var state in entries)
                {
                    var entry = context.Entry(state.Entity);
                    entry.State = state.State;
                    foreach (var property in state.Temporary)
                        entry.Property(property.Key).IsTemporary = property.Value;
                    foreach (var property in state.Modified)
                        entry.Property(property.Key).IsModified = property.Value;
                }
                if (PendingFiles.TryGetValue(context, out var pendingFiles)) pendingFiles.Fields.Clear();
                if (fileWork is not null)
                    foreach (var field in fileWork) PendingFiles.GetValue(context, _ => new()).Fields[field.Key] = field.Value;
                if (PendingRelationChanges.TryGetValue(context, out var pendingRelations)) pendingRelations.Changes.Clear();
                if (relationWork is not null)
                    foreach (var item in relationWork) PendingRelationChanges.GetValue(context, _ => new()).Changes[item.Key] = item.Value;
                restoreChanges();
            };
        }

        private sealed record TrackedState(object Entity, EntityState State, PropertyValues Current,
            PropertyValues Original, Dictionary<string, bool> Temporary, Dictionary<string, bool> Modified,
            NavigationState[] Navigations);

        private sealed record NavigationState(string Name, bool Collection, object? Value,
            object[]? Items, Type TargetType, bool Loaded);
    }
}
