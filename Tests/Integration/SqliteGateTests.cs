using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using SmartBase.Database;

namespace SmartBase.Tests.Integration;

/// <summary>
/// Сторож обхода гонки пула Microsoft.Data.Sqlite (<see cref="SqliteGate"/>,
/// dotnet/efcore#39008). Пул изредка отдает одно подключение двум владельцам, когда выдача
/// подключения совпадает с поиском брошенных, — а ищет он их при открытии на пустом пуле и при
/// очистке пула. Здесь подключения к одному файлу открываются и закрываются из нескольких
/// потоков, а отдельный поток чистит пул: без замка такой прогон дает тысячи общих дескрипторов
/// в секунду и «Collection was modified» при закрытии (замер 05.10.2026: от 66 до 122 тысяч на
/// миллион открытий), с замком — ни одного.
///
/// Темпов очистки два. С паузами у пула остаются свободные подключения, и гонка идет между
/// самими открытиями: без замка на открытии она выпадает сотнями раз за две секунды. Без пауз
/// пул почти всегда пуст, и очистка изредка застает открытие посреди выдачи — без замка на
/// очистке закрывает дескриптор под владельцем (<c>ObjectDisposedException</c> на
/// <c>sqlite3</c>). Это окно — наносекунды, и за две секунды оно выпадает не каждый раз (замер
/// 08.10.2026 на SQLitePCLRaw 2.1.12, как в программе: то ни одного случая, то 178), поэтому
/// само правило замка — кто кого ждет — проверяют отдельные тесты ниже, без случайности.
/// </summary>
public sealed class SqliteGateTests
{
    private static readonly TimeSpan Duration = TimeSpan.FromSeconds(2);
    private const int Workers = 8;

    // Сколько ждать того, что обязано случиться, и сколько — того, что случиться не должно.
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan Pause    = TimeSpan.FromMilliseconds(300);

    [Theory]
    [InlineData(false, false)]   // своя очистка с паузами — так чистит AppDbContext.ResetPool
    [InlineData(true,  false)]   // очистка всех пулов с паузами — так чистят тесты
    [InlineData(false, true)]    // очистка без пауз — застает открытие посреди выдачи
    [InlineData(true,  true)]
    public void Concurrent_opens_never_share_one_connection(bool clearAll, bool nonstop)
    {
        var path = TempDatabase.NewTempPath();
        var cs   = ConnectionString(path);
        try
        {
            using (var setup = SqliteGate.NewConnection(cs))
            {
                setup.Open();
                using var cmd = setup.CreateCommand();
                cmd.CommandText = "PRAGMA journal_mode=WAL; CREATE TABLE t(x);";
                cmd.ExecuteNonQuery();
            }

            // Живые владельцы каждого дескриптора SQLite: второй — это и есть гонка.
            var owners = new ConcurrentDictionary<IntPtr, int>();
            long opens = 0, shared = 0;
            var errors = new ConcurrentQueue<Exception>();
            var watch  = Stopwatch.StartNew();

            var workers = Enumerable.Range(0, Workers).Select(_ => new Thread(() =>
            {
                while (watch.Elapsed < Duration)
                {
                    try
                    {
                        using var conn = SqliteGate.NewConnection(cs);
                        conn.Open();
                        Interlocked.Increment(ref opens);
                        var handle = conn.Handle!.DangerousGetHandle();
                        if (owners.AddOrUpdate(handle, 1, (_, n) => n + 1) > 1) Interlocked.Increment(ref shared);
                        // Своя функция на подключении — как у программы на каждом открытии.
                        conn.CreateFunction("sb_probe", (string? s) => s);
                        Thread.SpinWait(50);
                        owners.AddOrUpdate(handle, 0, (_, n) => n - 1);
                    }
                    catch (Exception ex) { errors.Enqueue(ex); }
                }
            })).ToList();

            var clearer = new Thread(() =>
            {
                using var probe = SqliteGate.NewConnection(cs);
                while (watch.Elapsed < Duration)
                {
                    if (clearAll) SqliteGate.ClearAllPools();
                    else          SqliteGate.ClearPool(probe);
                    if (nonstop) Thread.Yield();
                    else         Thread.Sleep(1);
                }
            });

            workers.ForEach(t => t.Start());
            clearer.Start();
            workers.ForEach(t => t.Join());
            clearer.Join();

            Assert.True(errors.IsEmpty, $"Сбоев: {errors.Count}, первый: {errors.FirstOrDefault()}");
            Assert.Equal(0, Interlocked.Read(ref shared));
            Assert.True(opens > 1000, $"Открытий всего {opens} — прогон ничего не проверил");
        }
        finally
        {
            TempDatabase.Cleanup(path);
        }
    }

    // ── Кто кого ждет ────────────────────────────────────────────────────────────────────────
    // Открытие задерживается внутри Open (см. HeldOpen), и видно, кто ждет его конца, — без
    // случайности стресса.

    /// <summary>
    /// Очистка пула ждет открытия этого пула: посреди выдачи она нашла бы подключение
    /// «брошенным» и закрыла бы его под владельцем. Очистка всех пулов ждет открытия любого.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Clearing_waits_for_an_open_in_progress(bool clearAll)
    {
        var path = TempDatabase.NewTempPath();
        try
        {
            var cs = ConnectionString(path);
            using var probe = SqliteGate.NewConnection(cs);
            using var held  = new HeldOpen(cs);   // последним: при выходе отпускается первым

            var clear = Task.Run(() =>
            {
                if (clearAll) SqliteGate.ClearAllPools();
                else          SqliteGate.ClearPool(probe);
            });

            Assert.False(clear.Wait(Pause), "Очистка пула прошла посреди открытия — подключение могли закрыть под владельцем");
            held.Release();
            Assert.True(clear.Wait(Patience), "Очистка пула не дождалась конца открытия");
        }
        finally
        {
            TempDatabase.Cleanup(path);
        }
    }

    /// <summary>
    /// Открытия одного пула идут по одному, а другие пулы и подключения без пула их не ждут:
    /// медленное открытие сетевой базы не держит открытия других баз.
    /// </summary>
    [Fact]
    public void Opens_wait_only_for_their_own_pool()
    {
        var path  = TempDatabase.NewTempPath();
        var other = TempDatabase.NewTempPath();
        try
        {
            var cs       = ConnectionString(path);
            var unpooled = new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString();
            using var same         = SqliteGate.NewConnection(cs);
            using var otherPool    = SqliteGate.NewConnection(ConnectionString(other));
            using var unpooledToo  = SqliteGate.NewConnection(unpooled);
            using var held         = new HeldOpen(cs);         // последними: при выходе
            using var heldUnpooled = new HeldOpen(unpooled);   // отпускаются первыми

            var sameOpen = Task.Run(same.Open);
            Assert.True(Task.Run(otherPool.Open).Wait(Patience), "Открытие другой базы ждало чужого пула");
            Assert.True(Task.Run(unpooledToo.Open).Wait(Patience), "Открытие без пула ждало другого такого же");

            Assert.False(sameOpen.Wait(Pause), "Второе открытие того же пула прошло посреди первого");
            held.Release();
            Assert.True(sameOpen.Wait(Patience), "Второе открытие того же пула не дождалось первого");
        }
        finally
        {
            TempDatabase.Cleanup(path);
            TempDatabase.Cleanup(other);
        }
    }

    /// <summary>
    /// Контексты EF открывают подключения тоже под замком: EF зовет <c>Open</c> сам, и
    /// подключение, созданное им из строки, шло бы мимо <see cref="SqliteGate"/>.
    /// </summary>
    [Fact]
    public void Contexts_connect_through_the_gate()
    {
        using var db = new TempDatabase();
        Assert.IsType<SqliteGate.GatedConnection>(db.Context.Database.GetDbConnection());

        using var background = new AppDbContext(db.Path);
        Assert.IsType<SqliteGate.GatedConnection>(background.Database.GetDbConnection());

        // Контекст не открывается — файл не создается.
        using var system = new SystemDbContext(TempDatabase.NewTempPath());
        Assert.IsType<SqliteGate.GatedConnection>(system.Database.GetDbConnection());
    }

    private static string ConnectionString(string path) =>
        new SqliteConnectionStringBuilder { DataSource = path }.ToString();

    /// <summary>
    /// Подключение, чье открытие идет в фоне и стоит внутри <c>Open</c>, пока его не отпустят:
    /// обработчик <c>StateChange</c> библиотека зовет до выхода из <c>Open</c> — еще под замком
    /// пула.
    /// </summary>
    private sealed class HeldOpen : IDisposable
    {
        private readonly ManualResetEventSlim _inside  = new();
        private readonly ManualResetEventSlim _release = new();
        private readonly SqliteConnection _connection;
        private readonly Task _open;

        public HeldOpen(string connectionString)
        {
            _connection = SqliteGate.NewConnection(connectionString);
            _connection.StateChange += (_, e) =>
            {
                if (e.CurrentState != System.Data.ConnectionState.Open) return;
                _inside.Set();
                _release.Wait();
            };
            _open = Task.Run(_connection.Open);
            Assert.True(_inside.Wait(Patience), "Открытие не дошло до обработчика");
        }

        public void Release()
        {
            _release.Set();
            Assert.True(_open.Wait(Patience), "Задержанное открытие не закончилось");
        }

        public void Dispose()
        {
            _release.Set();
            try { _open.Wait(Patience); } catch { /* сбой открытия уже показан проверкой */ }
            _connection.Dispose();
        }
    }
}
