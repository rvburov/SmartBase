using System.Collections.Concurrent;
using Microsoft.Data.Sqlite;

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
        // OpenAsync библиотека не переопределяет: базовый DbConnection.OpenAsync зовет этот Open.
        public override void Open()
        {
            if (GateFor(ConnectionString) is { } gate)
                lock (gate) base.Open();
            else
                base.Open();
        }
    }
}
