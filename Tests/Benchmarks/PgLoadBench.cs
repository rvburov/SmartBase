using System.Collections.Concurrent;
using System.Diagnostics;
using Npgsql;

namespace SmartBase.Tests.Benchmarks;

/// <summary>
/// Нагрузочный прогон PostgreSQL (перенос режима --load прежнего Tests/PgSmoke). Много
/// одновременных клиентов на одну базу: каждый — отдельное приложение со своим физическим
/// подключением (Pooling=false), вставляет свои строки и конкурирует за инкременты 100 общих
/// строк (автокоммит — транзакция на операцию). Итог: сколько подключилось / отклонено
/// (SqlState 53300 — упёрлись в max_connections), пропускная способность и сверка целостности
/// (COUNT вставок и SUM(hits) обязаны сойтись со счётчиками клиентов). Требует стенд sb-pg.
///
/// Запуск: dotnet run -c Release --project Tests/Benchmarks -- pgload [клиентов] [секунд] [--db ИМЯ]
/// </summary>
internal static class PgLoadBench
{
    public static int Run(string[] args)
    {
        var nums    = args.Where(a => int.TryParse(a, out _)).Select(int.Parse).ToArray();
        var clients = nums.Length > 0 ? nums[0] : 120;
        var seconds = nums.Length > 1 ? nums[1] : 15;
        var db      = BenchUtil.ArgStr(args, "--db") ?? "smartbase";
        var connString = $"Host=localhost;Port=5455;Username=postgres;Password=postgres;Database={db};Timeout=5";

        Console.WriteLine($"Нагрузка: {clients} клиентов × {seconds} с (база {db})");
        try
        {
            using var probe = new NpgsqlConnection(connString);
            probe.Open();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Стенд PostgreSQL sb-pg (порт 5455) недоступен: {ex.Message}");
            Console.WriteLine("Поднимите его — см. Tests/README.md / docs/guides/postgresql-setup.md.");
            return 1;
        }

        return RunLoad(connString, clients, seconds);
    }

    private static int RunLoad(string connString, int clients, int seconds)
    {
        using (var admin = new NpgsqlConnection(connString))
        {
            admin.Open();
            using (var cmd = admin.CreateCommand())
            {
                cmd.CommandText =
                    "DROP TABLE IF EXISTS _load_rows; DROP TABLE IF EXISTS _load_shared; " +
                    "CREATE TABLE _load_rows (id bigserial PRIMARY KEY, client int NOT NULL, " +
                    "  n int NOT NULL, payload text NOT NULL); " +
                    "CREATE TABLE _load_shared (id int PRIMARY KEY, hits bigint NOT NULL DEFAULT 0); " +
                    "INSERT INTO _load_shared SELECT g, 0 FROM generate_series(1, 100) g;";
                cmd.ExecuteNonQuery();
            }
            using (var mx = admin.CreateCommand())
            {
                mx.CommandText = "SHOW max_connections;";
                Console.WriteLine($"max_connections сервера: {mx.ExecuteScalar()}");
            }
        }

        var single = connString + ";Pooling=false";
        long connected = 0, denied = 0, connectTimeouts = 0, unexpected = 0, inserts = 0, updates = 0;
        var errors = new ConcurrentDictionary<string, int>();
        var gate = new ManualResetEventSlim(false);
        var deadlineHolder = new long[1];   // тики UTC-дедлайна; выставляется после старта всех потоков

        void Client(int id)
        {
            try
            {
                gate.Wait();
                using var conn = new NpgsqlConnection(single);
                conn.Open();
                Interlocked.Increment(ref connected);

                using var ins = conn.CreateCommand();
                ins.CommandText = "INSERT INTO _load_rows (client, n, payload) VALUES (@c, @n, @p);";
                var pc = ins.CreateParameter(); pc.ParameterName = "@c"; pc.DbType = System.Data.DbType.Int32;  ins.Parameters.Add(pc);
                var pn = ins.CreateParameter(); pn.ParameterName = "@n"; pn.DbType = System.Data.DbType.Int32;  ins.Parameters.Add(pn);
                var pp = ins.CreateParameter(); pp.ParameterName = "@p"; pp.DbType = System.Data.DbType.String; ins.Parameters.Add(pp);
                ins.Prepare();

                using var upd = conn.CreateCommand();
                upd.CommandText = "UPDATE _load_shared SET hits = hits + 1 WHERE id = @id;";
                var pid = upd.CreateParameter(); pid.ParameterName = "@id"; pid.DbType = System.Data.DbType.Int32; upd.Parameters.Add(pid);
                upd.Prepare();

                var n = 0;
                while (DateTime.UtcNow.Ticks < Interlocked.Read(ref deadlineHolder[0]))
                {
                    n++;
                    pc.Value = id; pn.Value = n; pp.Value = $"клиент {id} операция {n}";
                    ins.ExecuteNonQuery();
                    Interlocked.Increment(ref inserts);

                    pid.Value = Random.Shared.Next(1, 101);
                    upd.ExecuteNonQuery();
                    Interlocked.Increment(ref updates);
                }
            }
            catch (PostgresException pe) when (pe.SqlState == "53300")
            {
                Interlocked.Increment(ref denied);
            }
            catch (NpgsqlException ne) when (ne.InnerException is TimeoutException)
            {
                Interlocked.Increment(ref connectTimeouts);   // шторм подключений не уложился в Timeout=5
            }
            catch (Exception ex)
            {
                Interlocked.Increment(ref unexpected);
                var key = ex is PostgresException p ? $"{p.SqlState}: {p.MessageText}"
                                                    : $"{ex.GetType().Name}: {ex.Message}";
                errors.AddOrUpdate(key, 1, (_, v) => v + 1);
            }
        }

        // Отдельные потоки, не пул: сотня блокирующих клиентов из ThreadPool стартовала бы минуту.
        var threads = Enumerable.Range(1, clients)
            .Select(id => new Thread(() => Client(id)) { IsBackground = true })
            .ToList();
        foreach (var t in threads) t.Start();
        Interlocked.Exchange(ref deadlineHolder[0], DateTime.UtcNow.AddSeconds(seconds).Ticks);
        var sw = Stopwatch.StartNew();
        gate.Set();
        foreach (var t in threads) t.Join();
        sw.Stop();

        var ops = Interlocked.Read(ref inserts) + Interlocked.Read(ref updates);
        Console.WriteLine($"подключилось: {connected}, отклонено сервером (53300): {denied}, " +
                          $"таймаут подключения: {connectTimeouts}");
        Console.WriteLine($"операций: {ops} ({inserts} вставок + {updates} инкрементов), " +
                          $"{ops / Math.Max(sw.Elapsed.TotalSeconds, 0.001):F0} оп/с суммарно");

        var rowCount = Scalar(connString, "SELECT COUNT(*) FROM _load_rows");
        var hitSum   = Scalar(connString, "SELECT COALESCE(SUM(hits), 0) FROM _load_shared");
        var intact   = rowCount == inserts && hitSum == updates;
        Console.WriteLine($"сверка: строк {rowCount} (ожидалось {inserts}), " +
                          $"инкрементов {hitSum} (ожидалось {updates}) — {(intact ? "ok" : "РАСХОЖДЕНИЕ")}");

        foreach (var (key, count) in errors)
            Console.WriteLine($"НЕОЖИДАННАЯ ОШИБКА ×{count}: {key}");

        using (var admin = new NpgsqlConnection(connString))
        {
            admin.Open();
            using var cmd = admin.CreateCommand();
            cmd.CommandText = "DROP TABLE IF EXISTS _load_rows; DROP TABLE IF EXISTS _load_shared;";
            cmd.ExecuteNonQuery();
        }

        var pass = intact && unexpected == 0 && connected > 0;
        Console.WriteLine(pass ? "НАГРУЗКА ПРОЙДЕНА" : "НАГРУЗКА ПРОВАЛЕНА");
        return pass ? 0 : 1;
    }

    private static long Scalar(string connString, string sql)
    {
        using var conn = new NpgsqlConnection(connString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        return Convert.ToInt64(cmd.ExecuteScalar() ?? 0L);
    }
}
