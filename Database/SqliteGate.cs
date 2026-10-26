using System.Collections.Concurrent;
using System.Data;
using System.Data.Common;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace SmartBase.Database;

/// <summary>
/// Подключения SQLite программы — только отсюда: их открытие и очистка пула идут под замком
/// своего пула.
///
/// <b>Зачем.</b> Обход дефекта пула Microsoft.Data.Sqlite (dotnet/efcore#39008, в выпусках 9.0
/// не исправлен). Пул выдает подключение и уже вне своего замка ставит ему признак «занято» —
/// раньше, чем привязку к владельцу. Если в этот миг соседнее открытие того же файла застает
/// пул пустым или пул чистят (<c>ClearPool</c>, <c>ClearAllPools</c>), подключение выглядит
/// брошенным: пул забирает его и отдает следующему. Дальше два <see cref="SqliteConnection"/>
/// работают на одном дескрипторе SQLite — команды одной работы попадают в транзакцию другой,
/// закрытие одного снимает функции с чужого подключения («Collection was modified»), очистка
/// закрывает дескриптор под владельцем. В программе так совпадали открытия основного контекста
/// с фоновыми работами на своих подключениях. Под замком своего пула выдача подключения и
/// поиск брошенных не пересекаются.
///
/// <b>Замок — на пул</b>, то есть на строку подключения: медленное открытие сетевой базы не
/// держит открытия других баз. Держится он только на время <c>Open</c>, а не пока подключение
/// открыто. Подключения без пула (<c>Pooling=False</c>) открываются без замка — гонка их не
/// касается.
///
/// <b>Правило одно: подключение SQLite — только <see cref="NewConnection"/>, очистка пула —
/// только <see cref="ClearPool"/> и <see cref="ClearAllPools"/>.</b> Подключение, открытое мимо
/// замка, снова пускает гонку в свой пул. Сторожа — <c>SqliteGateTests</c> (кто кого ждет и
/// одновременные открытия при очистке пула) и <c>SqliteGateConventionTests</c> (по тексту
/// программы).
/// Исправление у авторов вошло в ветку 10.0 (#39012); когда программа перейдет на выпуск с ним,
/// обход можно снять.
/// </summary>
public static class SqliteGate
{
    private static readonly ConditionalWeakTable<DbTransaction, IDisposable> TransactionAdmissions = new();
    // В EF 9 Dispose транзакции имеет diagnostic event, но не interceptor override.
    // Подписка не хранит контексты/подключения; ключи lease слабы и освобождаются идемпотентно.
    private static readonly IDisposable TransactionEvents = DiagnosticListener.AllListeners.Subscribe(new TransactionListeners());

    internal static void PrepareWriteTransaction(DbConnection connection, int timeoutSeconds, CancellationToken ct = default)
    {
        if (connection is GatedConnection gated) gated.PrepareWrite(timeoutSeconds, ct);
    }

    internal static void ClearPreparedWriteTransaction(DbConnection connection)
    {
        if (connection is GatedConnection gated) gated.ClearPreparedWrite();
    }

    internal static void ReleaseTransactionAdmission(DbTransaction? transaction)
    {
        if (transaction is not null && TransactionAdmissions.TryGetValue(transaction, out var lease)) lease.Dispose();
    }

    internal static void ReleaseCompletedTransactionAdmission(DbTransaction? transaction)
    {
        if (transaction is SqliteTransaction { Connection: null }) ReleaseTransactionAdmission(transaction);
    }

    // Используется в начале commit callbacks: они могут синхронно открыть другой контекст.
    internal static void ReleaseConnectionAdmission(DbConnection connection)
    {
        if (connection is GatedConnection gated) gated.ReleaseAdmission();
    }
    // Замок на строку подключения; null — у подключения без пула.
    private static readonly ConcurrentDictionary<string, object?> Gates = new(StringComparer.Ordinal);

    // Очистки всех пулов идут по одной, и новый замок не появляется посреди очистки: она берет
    // замки всех пулов разом, а пул без замка в этот миг открылся бы мимо нее.
    private static readonly object ClearAllLock = new();

    /// <summary>Новое подключение SQLite (еще не открытое); откроется под замком своего пула.</summary>
    public static SqliteConnection NewConnection(string connectionString) => new GatedConnection(connectionString);

    /// <summary>Сбросить пул той же строки подключения, что у <paramref name="connection"/>, —
    /// под замком этого пула.</summary>
    public static void ClearPool(SqliteConnection connection)
    {
        if (GateFor(connection.ConnectionString) is { } gate)
            lock (gate) SqliteConnection.ClearPool(connection);
        else
            SqliteConnection.ClearPool(connection);
    }

    /// <summary>
    /// Сбросить все пулы процесса — под замками всех пулов сразу. Для тестов и стендов: программа
    /// чистит только пул своей базы (<see cref="ClearPool"/>).
    /// </summary>
    public static void ClearAllPools()
    {
        lock (ClearAllLock)
        {
            // Порядок взятия не важен, пока открытие держит один замок и второго не ждет: внутри
            // Open (обработчики StateChange) других подключений не открывают — иначе такое
            // открытие и эта очистка ждали бы друг друга. Очистки всех пулов идут по одной.
            var gates = Gates.Values.OfType<object>().ToList();
            var taken = 0;
            try
            {
                for (; taken < gates.Count; taken++) Monitor.Enter(gates[taken]);
                SqliteConnection.ClearAllPools();
            }
            finally
            {
                while (taken > 0) Monitor.Exit(gates[--taken]);
            }
        }
    }

    private static object? GateFor(string connectionString)
    {
        if (Gates.TryGetValue(connectionString, out var gate)) return gate;
        lock (ClearAllLock)
            return Gates.GetOrAdd(connectionString,
                cs => new SqliteConnectionStringBuilder(cs).Pooling ? new object() : null);
    }

    /// <summary>Подключение, чье открытие идет под замком своего пула.</summary>
    public sealed class GatedConnection(string connectionString) : SqliteConnection(connectionString)
    {
        private PreparedWrite? _preparedWrite;
        private IDisposable? _activeAdmission;

        internal void PrepareWrite(int seconds, CancellationToken ct) =>
            _preparedWrite = new PreparedWrite(seconds == 0 ? Timeout.InfiniteTimeSpan : TimeSpan.FromSeconds(seconds), ct);
        internal void ClearPreparedWrite() => _preparedWrite = null;
        internal void ReleaseAdmission() => Interlocked.Exchange(ref _activeAdmission, null)?.Dispose();

        private WriteAdmission Admission()
        {
            var source = new SqliteConnectionStringBuilder(ConnectionString).DataSource;
            var key = "sqlite:" + Path.GetFullPath(source.Length == 0 ? DataSource : source);
            return WriteAdmission.ForDatabase(key, 1);
        }

        protected override DbTransaction BeginDbTransaction(IsolationLevel isolationLevel)
        {
            var prepared = Interlocked.Exchange(ref _preparedWrite, null);
            // Только EF поставил marker. Прямые native/deferred read snapshots не попадают
            // в очередь писателей и сохраняют прежнюю семантику SQLite.
            if (prepared is null) return base.BeginDbTransaction(isolationLevel);
            var lease = Admission().Enter(prepared.Timeout, prepared.Cancellation);
            try
            {
                prepared.Cancellation.ThrowIfCancellationRequested();
                return RegisterAdmission(base.BeginDbTransaction(isolationLevel), lease);
            }
            catch { lease.Dispose(); throw; }
        }

        protected override async ValueTask<DbTransaction> BeginDbTransactionAsync(IsolationLevel isolationLevel,
            CancellationToken cancellationToken)
        {
            var prepared = Interlocked.Exchange(ref _preparedWrite, null);
            if (prepared is null) return await base.BeginDbTransactionAsync(isolationLevel, cancellationToken).ConfigureAwait(false);
            var lease = await Admission().EnterAsync(prepared.Timeout, cancellationToken).ConfigureAwait(false);
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                // Microsoft.Data.Sqlite выполняет сам BEGIN синхронно. Очередь до него
                // асинхронна и отменяема, командный deadline провайдера не меняется.
                return RegisterAdmission(base.BeginDbTransaction(isolationLevel), lease);
            }
            catch { lease.Dispose(); throw; }
        }

        private DbTransaction RegisterAdmission(DbTransaction transaction, IDisposable lease)
        {
            var registration = new NativeLease(this, lease);
            try
            {
                TransactionAdmissions.Add(transaction, registration);
                _activeAdmission = registration;
                return transaction;
            }
            catch
            {
                try { transaction.Dispose(); }
                finally { registration.Dispose(); }
                throw;
            }
        }

        // OpenAsync библиотека не переопределяет: базовый DbConnection.OpenAsync зовет этот Open.
        public override void Open()
        {
            if (GateFor(ConnectionString) is { } gate)
                lock (gate) base.Open();
            else
                base.Open();
        }

        public override void Close()
        {
            try { base.Close(); }
            finally { ClearPreparedWrite(); ReleaseAdmission(); }
        }

        protected override void Dispose(bool disposing)
        {
            try { base.Dispose(disposing); }
            finally { if (disposing) { ClearPreparedWrite(); ReleaseAdmission(); } }
        }

        private sealed record PreparedWrite(TimeSpan Timeout, CancellationToken Cancellation);

        private sealed class NativeLease(GatedConnection owner, IDisposable lease) : IDisposable
        {
            private GatedConnection? _owner = owner;
            private IDisposable? _lease = lease;
            public void Dispose()
            {
                var currentOwner = Interlocked.Exchange(ref _owner, null);
                var currentLease = Interlocked.Exchange(ref _lease, null);
                if (currentOwner is not null) Interlocked.CompareExchange(ref currentOwner._activeAdmission, null, this);
                currentLease?.Dispose();
            }
        }
    }

    private sealed class TransactionListeners : IObserver<DiagnosticListener>
    {
        public void OnNext(DiagnosticListener listener)
        {
            if (listener.Name == "Microsoft.EntityFrameworkCore") listener.Subscribe(new TransactionObserver(),
                name => name is "Microsoft.EntityFrameworkCore.Database.Transaction.TransactionDisposed"
                    or "Microsoft.EntityFrameworkCore.Database.Transaction.TransactionCommitted"
                    or "Microsoft.EntityFrameworkCore.Database.Transaction.TransactionRolledBack");
        }
        public void OnCompleted() { }
        public void OnError(Exception error) { }
    }

    private sealed class TransactionObserver : IObserver<KeyValuePair<string, object?>>
    {
        public void OnNext(KeyValuePair<string, object?> item)
        {
            if (item.Value is TransactionEventData data) ReleaseTransactionAdmission(data.Transaction);
        }
        public void OnCompleted() { }
        public void OnError(Exception error) { }
    }
}
