using System.Diagnostics;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using SmartBase.Database;
using SmartBase.Database.Repositories;
using SmartBase.Utils;

namespace SmartBase.Tests.Benchmarks;

/// <summary>
/// Строгий нагрузочный прогон API приложения: отдельный поток, контекст и готовое
/// подключение на участника, общий старт. Это сессии в одном процессе, а не машины/SMB.
/// Ошибки приложения стенд не повторяет и не превращает отказы части клиентов в успех.
/// </summary>
internal static class MultiUserBench
{
    public static int Run(string[] args)
    {
        MultiUserOptions options;
        try { options = MultiUserOptions.Parse(args); }
        catch (ArgumentException ex)
        {
            Console.Error.WriteLine(ex.Message);
            Console.Error.WriteLine("multiuser --provider sqlite|postgres [--profile wal|shared] " +
                "[--scenario lifecycle|hotcell|rollback|planning|library|integrations|access|structure] [--clients 1000] [--rounds 1] " +
                "[--timeout-seconds 180] [--p95-ms N] [--out НОВАЯ_ПАПКА]");
            return 2;
        }

        var directory = options.Output ?? Path.Combine(Path.GetTempPath(), $"sb_multiuser_{Guid.NewGuid():N}");
        if (Directory.Exists(directory) || File.Exists(directory))
        {
            Console.Error.WriteLine("Папка результата уже существует; укажите новую. Существующие данные не изменены.");
            return 2;
        }
        Directory.CreateDirectory(directory);
        var clients = Enumerable.Range(0, options.Clients).Select(i => new MultiUserClient(i)).ToArray();
        var checks = new List<MultiUserCheck>();
        var setup = Stopwatch.StartNew();
        MultiUserFixture? fixture = null;
        try
        {
            fixture = MultiUserFixture.Create(options, directory);
            var scenario = options.Scenario switch
            {
                "planning" => MultiUserPlanningScenario.Create(fixture),
                "library" => MultiUserLibraryScenario.Create(fixture),
                "integrations" => MultiUserIntegrationScenario.Create(fixture),
                "access" => MultiUserAccessScenario.Create(fixture),
                "structure" => MultiUserStructureScenario.Create(fixture),
                _ => (IMultiUserScenario?)null,
            };
            Console.WriteLine($"{options.Provider}/{options.Profile}, {options.Scenario}: " +
                $"{options.Clients} сессий × {options.Rounds} циклов. Данные: {directory}");
            if (fixture.PostgresMaxConnections is not null)
                Console.WriteLine($"PostgreSQL max_connections={fixture.PostgresMaxConnections}; база {fixture.DatabaseName}");

            // CountdownEvent считает и неудачные открытия: отказ не должен подвесить общий барьер.
            // Отдельные потоки нужны, потому что репозитории синхронные, а ThreadPool иначе
            // последовательно добавлял бы заблокированных на барьере участников.
            var ready = new CountdownEvent(options.Clients);
            var finished = new CountdownEvent(options.Clients);
            var start = new ManualResetEventSlim(false);
            var stop = new CancellationTokenSource();
            var active = 0;
            var peakActive = 0;
            var timedOut = false;
            var limit = TimeSpan.FromSeconds(options.TimeoutSeconds);

            void Client(MultiUserClient client)
            {
                var announced = false;
                try
                {
                    using var actor = CurrentUser.ActAs(MultiUserFixture.UserKey(client.Id), $"Участник {client.Id:D4}");
                    client.Stop = stop.Token;
                    using var context = new AppDbContext(fixture.Connection);
                    context.ApplyPerformancePragmas();
                    var open = Stopwatch.StartNew();
                    context.Database.OpenConnection();
                    client.OpenMilliseconds = open.Elapsed.TotalMilliseconds;
                    client.Connected = true;
                    var permissions = new PermissionService(context);
                    permissions.Reload();
                    var repo = new ConstructorRepository(context) { Permissions = permissions };
                    // Компиляция запроса и первая загрузка модели входят в подготовку, а не
                    // теряются у первых клиентов за счет более позднего начала секундомера.
                    repo.GetFields(fixture.SourceCollection);
                    client.Ready = true;
                    ready.Signal();
                    announced = true;
                    start.Wait(stop.Token);
                    if (stop.IsCancellationRequested) return;
                    // После доказанного одновременного открытия соединения живут как в
                    // приложении: EF открывает их для команды и возвращает в пул после нее.
                    context.Database.CloseConnection();
                    client.Started = true;
                    var concurrent = Interlocked.Increment(ref active);
                    int old;
                    do { old = Volatile.Read(ref peakActive); }
                    while (concurrent > old && Interlocked.CompareExchange(ref peakActive, concurrent, old) != old);
                    try
                    {
                        for (var round = 0; round < options.Rounds; round++)
                        {
                            stop.Token.ThrowIfCancellationRequested();
                            if (scenario is not null) scenario.Execute(context, repo, client, round);
                            else MultiUserWorkload.Execute(fixture, context, repo, client, round);
                            client.CompletedRounds++;
                        }
                        client.Completed = true;
                    }
                    finally { Interlocked.Decrement(ref active); }
                }
                catch (Exception ex) { client.Failure = MultiUserFailure.From(ex); }
                finally
                {
                    if (!announced) ready.Signal();
                    finished.Signal();
                }
            }

            var preparation = Stopwatch.StartNew();
            foreach (var client in clients)
            {
                try
                {
                    new Thread(() => Client(client), 512 * 1024)
                    {
                        IsBackground = true,
                        Name = $"multiuser-{client.Id}",
                    }.Start();
                }
                catch (Exception ex)
                {
                    client.Failure = MultiUserFailure.From(ex);
                    ready.Signal();
                    finished.Signal();
                }
            }

            var readyBudget = limit - preparation.Elapsed;
            if (!ready.Wait(readyBudget > TimeSpan.Zero ? readyBudget : TimeSpan.Zero) || preparation.Elapsed > limit)
            {
                timedOut = true;
                stop.Cancel();
                checks.Add(new("Все сессии подготовились за отведенное время", false));
            }
            var prepared = clients.Count(c => c.Ready);
            setup.Stop();
            // В этот момент готовые сессии еще держат все подключения одновременно.
            Console.WriteLine($"Подготовлены: {prepared}/{options.Clients}, подготовка {setup.Elapsed.TotalSeconds:F1} с");
            checks.Add(new("Все запрошенные сессии одновременно готовы к старту", prepared == options.Clients));
            var elapsed = Stopwatch.StartNew();
            start.Set();
            while (!finished.Wait(TimeSpan.FromSeconds(5)))
            {
                Console.WriteLine($"Завершено {clients.Count(c => c.Completed)}/{options.Clients}, " +
                    $"ошибок {clients.Count(c => c.Failure is not null)}, {elapsed.Elapsed.TotalSeconds:F0} с");
                if (elapsed.Elapsed > limit)
                {
                    timedOut = true;
                    stop.Cancel();
                    // Команды имеют собственный таймаут. Ждем их до конца, прежде чем
                    // проверять данные; незавершенный запрос нельзя объявить откатившимся.
                    if (!finished.Wait(TimeSpan.FromSeconds(35)))
                    {
                        Console.Error.WriteLine("Сессии не остановились. Прогон не завершен; проверка данных не выполнена.");
                        File.WriteAllText(Path.Combine(directory, "incomplete.json"),
                            JsonSerializer.Serialize(new { Passed = false, RequestedClients = options.Clients,
                                ReadyClients = prepared, CompletedClients = clients.Count(c => c.Completed),
                                Reason = "Сессии не остановились; сверка данных не выполнена" }));
                        // Потоки фоновые, процесс завершится с ошибкой. Базу сохраняем.
                        // События не закрываем под еще работающими потоками.
                        return 1;
                    }
                    break;
                }
            }
            elapsed.Stop();
            timedOut |= elapsed.Elapsed > limit;
            ready.Dispose();
            finished.Dispose();
            start.Dispose();
            stop.Dispose();
            checks.Add(new("Прогон завершен без таймаута", !timedOut));
            checks.Add(new("Каждая сессия завершила все циклы без ошибок",
                clients.All(c => c.Completed && c.CompletedRounds == options.Rounds && c.Failure is null)));
            var verificationFailures = new List<MultiUserFailure>();
            void Verify(string name, Func<IEnumerable<MultiUserCheck>> verify)
            {
                try { checks.AddRange(verify()); }
                catch (Exception ex)
                {
                    var failure = MultiUserFailure.From(ex);
                    verificationFailures.Add(failure);
                    checks.Add(new(name, false, failure.Code + ": " + failure.Message));
                }
            }
            if (scenario is null) Verify("Сверка результатов команд выполнена", () => MultiUserWorkload.Verify(fixture, clients));
            else
            {
                Verify("Сверка результатов модуля выполнена", () => scenario.Verify(clients));
                Verify("Проверка целостности выполнена", () => MultiUserWorkload.VerifyIntegrity(fixture, clients));
            }
            var operations = Summarize(clients);
            if (options.P95Milliseconds is { } p95)
                checks.Add(new("p95 каждой операции укладывается в заданный предел",
                    operations.Count > 0 && operations.All(o => o.P95Milliseconds <= p95)));

            var report = new
            {
                Provider = options.Provider,
                Profile = options.Profile,
                Scenario = options.Scenario,
                AccessModel = options.Scenario == "access"
                    ? "Четные участники могут писать, нечетные только читают; ожидаемые запреты проверяются отдельно"
                    : "Разные синтетические пользователи с одинаковым полным допуском",
                RepositoryWriteAdmission = new
                {
                    Scope = "Один процесс и одна база; WriteScope приложения, не ограничитель стенда",
                    MaxConcurrentWriteScopeCommands = options.Provider == "postgres" ? 16 : 1,
                    SqliteCallerTransactionsQueued = options.Provider == "sqlite",
                    CallerManagedPostgresTransactionsUseRepositoryLimit = false,
                },
                RequestedClients = options.Clients,
                ConnectedClients = clients.Count(c => c.Connected),
                ReadyClients = prepared,
                StartedClients = clients.Count(c => c.Started),
                ClientsWithSuccessfulOperations = clients.Count(c => c.Samples.Any(s => s.Success)),
                CompletedClients = clients.Count(c => c.Completed),
                PeakActiveSessions = peakActive,
                SetupSeconds = setup.Elapsed.TotalSeconds,
                RunSeconds = elapsed.Elapsed.TotalSeconds,
                SuccessfulOperationsPerSecond = operations.Sum(o => o.Successful) / Math.Max(elapsed.Elapsed.TotalSeconds, .001),
                fixture.DatabaseName,
                fixture.PostgresMaxConnections,
                DataDirectory = directory,
                Passed = checks.All(c => c.Passed),
                Checks = checks,
                Operations = operations,
                VerificationFailures = verificationFailures,
                Clients = clients.Select(c => new
                {
                    c.Id, c.Connected, c.Ready, c.Started, c.Completed, c.CompletedRounds,
                    c.OpenMilliseconds, c.FailedOperation, c.Failure,
                    AcknowledgedRecords = c.Records.Values,
                }),
                Limitations = new[]
                {
                    "Независимые сессии в одном процессе на одной машине; это не 1000 окон/компьютеров.",
                    "1000 готовых сессий входят в обычные очереди приложения: SQLite EF-транзакции пишут по одной, PostgreSQL WriteScope — до 16. Очереди не координируют разные процессы.",
                    "Идентичность задана тестовым scope; вход из разных ОС не моделируется. Маски доступа проверяются отдельным сценарием access.",
                    "Профиль shared на локальном диске не моделирует SMB/NFS, задержку или отказ сети.",
                    "Замеры относятся к этой фикстуре и машине; без --p95-ms задержки не имеют продуктового порога.",
                },
            };
            var reportPath = Path.Combine(directory, "report.json");
            File.WriteAllText(reportPath, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine("Операция              Успех  Ошибка   p50 мс   p95 мс   p99 мс    max мс");
            foreach (var operation in operations)
                Console.WriteLine($"{operation.Name,-22}{operation.Successful,6}{operation.Failed,8}" +
                    $"{operation.P50Milliseconds,9:F1}{operation.P95Milliseconds,9:F1}" +
                    $"{operation.P99Milliseconds,9:F1}{operation.MaxMilliseconds,10:F1}");
            foreach (var check in checks)
                Console.WriteLine($"{(check.Passed ? "PASS" : "FAIL")} {check.Name}" +
                    (check.Passed || check.Detail is null ? "" : ": " + check.Detail));
            foreach (var failure in clients.Where(c => c.Failure is not null)
                         .GroupBy(c => c.Failure!.Code).Take(10))
                Console.WriteLine($"ОШИБКА {failure.Key} ×{failure.Count()}: {failure.First().Failure!.Message}");
            Console.WriteLine($"Отчет: {reportPath}");
            Console.WriteLine("Тестовые данные сохранены для разбора; рабочие базы не использовались.");
            return report.Passed ? 0 : 1;
        }
        catch (Exception ex)
        {
            var failure = MultiUserFailure.From(ex);
            File.WriteAllText(Path.Combine(directory, "setup-error.json"), JsonSerializer.Serialize(failure));
            Console.Error.WriteLine($"Прогон не выполнен: {failure.Code}: {failure.Message}");
            return 1;
        }
        // Фикстуры намеренно не удаляются: имя новой PG-базы и папка записаны в отчете.
    }

    private static List<MultiUserOperation> Summarize(IEnumerable<MultiUserClient> clients) => clients
        .SelectMany(c => c.Samples).GroupBy(s => s.Name).OrderBy(g => g.Key).Select(g =>
        {
            var times = g.Select(s => s.Milliseconds).Order().ToArray();
            double P(double p) => times[Math.Clamp((int)Math.Ceiling(times.Length * p) - 1, 0, times.Length - 1)];
            return new MultiUserOperation(g.Key, g.Count(s => s.Success), g.Count(s => !s.Success),
                P(.50), P(.95), P(.99), times[^1]);
        }).ToList();
}

internal sealed record MultiUserOptions(string Provider, string Profile, string Scenario,
    int Clients, int Rounds, int TimeoutSeconds, double? P95Milliseconds, string? Output)
{
    internal static MultiUserOptions Parse(string[] args)
    {
        var values = new Dictionary<string, string>();
        var known = new HashSet<string> { "--provider", "--profile", "--scenario", "--clients", "--rounds",
            "--timeout-seconds", "--p95-ms", "--out" };
        for (var i = 0; i < args.Length; i += 2)
        {
            if (!known.Contains(args[i]) || i + 1 >= args.Length || !values.TryAdd(args[i], args[i + 1]))
                throw new ArgumentException("Неизвестный, повторенный или незаполненный параметр: " + args[i]);
        }
        string Get(string key, string fallback) => values.GetValueOrDefault(key, fallback);
        int Number(string key, int fallback, int max)
        {
            if (!int.TryParse(Get(key, fallback.ToString()), out var n) || n < 1 || n > max)
                throw new ArgumentException($"{key}: нужно целое число от 1 до {max}");
            return n;
        }
        var provider = Get("--provider", "sqlite");
        var profile = Get("--profile", provider == "postgres" ? "server" : "wal");
        var scenario = Get("--scenario", "lifecycle");
        if (provider is not ("sqlite" or "postgres")) throw new ArgumentException("--provider: sqlite или postgres");
        if (provider == "sqlite" ? profile is not ("wal" or "shared") : profile != "server")
            throw new ArgumentException("--profile: wal/shared только для SQLite; у PostgreSQL профиль server");
        if (scenario is not ("lifecycle" or "hotcell" or "rollback" or "planning" or "library" or "integrations" or "access" or "structure"))
            throw new ArgumentException("--scenario: lifecycle, hotcell, rollback, planning, library, integrations, access или structure");
        double? p95 = null;
        if (values.TryGetValue("--p95-ms", out var threshold))
        {
            if (!double.TryParse(threshold, System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var n) || !double.IsFinite(n) || n <= 0)
                throw new ArgumentException("--p95-ms: нужно положительное число в миллисекундах");
            p95 = n;
        }
        return new(provider, profile, scenario, Number("--clients", 1000, 1000), Number("--rounds", 1, 10000),
            Number("--timeout-seconds", 180, 3600), p95,
            values.TryGetValue("--out", out var path) ? Path.GetFullPath(path) : null);
    }
}

internal sealed class MultiUserClient(int id)
{
    internal int Id { get; } = id;
    internal bool Connected, Ready, Started, Completed;
    internal int CompletedRounds;
    internal double OpenMilliseconds;
    internal MultiUserFailure? Failure;
    internal string? FailedOperation;
    internal CancellationToken Stop;
    internal List<MultiUserSample> Samples { get; } = [];
    internal Dictionary<int, MultiUserExpectedRecord> Records { get; } = [];

    internal T Measure<T>(string name, Func<T> action)
    {
        Stop.ThrowIfCancellationRequested();
        var watch = Stopwatch.StartNew();
        var success = false;
        try { var result = action(); success = true; return result; }
        catch { FailedOperation = name; throw; }
        finally { Samples.Add(new(name, watch.Elapsed.TotalMilliseconds, success)); }
    }
    internal void Measure(string name, Action action) => Measure(name, () => { action(); return true; });
}

internal sealed record MultiUserSample(string Name, double Milliseconds, bool Success);
internal sealed record MultiUserOperation(string Name, int Successful, int Failed,
    double P50Milliseconds, double P95Milliseconds, double P99Milliseconds, double MaxMilliseconds);
internal sealed record MultiUserCheck(string Name, bool Passed, string? Detail = null);
internal sealed record MultiUserExpectedRecord(int Id, int CollectionId, string Title, string Text, string Date, string Author);
internal sealed record MultiUserFailure(string Code, string Message)
{
    internal static MultiUserFailure From(Exception ex)
    {
        var root = ex.GetBaseException();
        var code = root switch
        {
            PostgresException pg => $"PG:{pg.SqlState}",
            Microsoft.Data.Sqlite.SqliteException sqlite => $"SQLite:{sqlite.SqliteErrorCode}/{sqlite.SqliteExtendedErrorCode}",
            _ => root.GetType().Name,
        };
        return new(code, root.Message);
    }
}
