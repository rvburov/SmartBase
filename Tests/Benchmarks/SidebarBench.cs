using System.Diagnostics;
using SmartBase.Core.Models;
using SmartBase.Database;
using SmartBase.Database.Repositories;

namespace SmartBase.Tests.Benchmarks;

/// <summary>
/// Замер операций сайдбара (SidebarTree) на большом стенде — перенос Tests/SidebarPerf.
/// Вызывает ровно те методы ConstructorRepository и в той же последовательности, что и UI.
/// Стенд создает Tests/DbGenerator (см. docs/testing/test-database.md):
///   dotnet run -c Release --project Tests/DbGenerator -- --count 10000000 --out ~/TestData/perf_main.db
/// </summary>
internal static class SidebarBench
{
    private sealed record Row(string Op, double Ms, string Note);
    private static readonly List<Row> Rows = [];

    public static int Run(string[] args)
    {
        SQLitePCL.Batteries_V2.Init();

        var home       = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var mainPath   = BenchUtil.ArgStr(args, "--db")     ?? Path.Combine(home, "TestData", "perf_main.db");
        var targetPath = BenchUtil.ArgStr(args, "--target") ?? Path.Combine(home, "TestData", "perf_target.db");
        var light      = args.Contains("--light");

        if (!File.Exists(mainPath))
        {
            Console.WriteLine($"Нет стенда: {mainPath}");
            Console.WriteLine("Сгенерируйте: dotnet run -c Release --project Tests/DbGenerator -- --count 10000000 --out <путь> --force");
            return 1;
        }

        foreach (var suffix in new[] { "", "-wal", "-shm", "-journal" })
            if (File.Exists(targetPath + suffix)) File.Delete(targetPath + suffix);

        Console.WriteLine($"Стенд:   {mainPath} ({BenchUtil.SizeGb(mainPath):0.00} ГБ)");
        Console.WriteLine($"Целевая: {targetPath} (пересоздается)");
        Console.WriteLine($"Режим:   {(light ? "light (без дубля/переноса группы)" : "полный")}");
        Console.WriteLine();

        var total = Stopwatch.StartNew();

        DatabaseManager main = null!, target = null!;
        Time("Монтирование основной БД (DatabaseManager.Open)",
            () => main = DatabaseManager.Open(0, "perf-main", mainPath));
        Time("Создание целевой БД (DatabaseManager.Open, Migrate с нуля)",
            () => target = DatabaseManager.Open(1, "perf-target", targetPath));
        var repo = main.Constructor;

        List<DbGroup>      groups = [];
        List<DbCollection> cols   = [];
        Time("Refresh: GetAllGroups (холодное)",      () => groups = repo.GetAllGroups());
        Time("Refresh: GetAllCollections (холодное)", () => cols   = repo.GetAllCollections());
        Time("Refresh: GetAllGroups+GetAllCollections (теплое)",
            () => { groups = repo.GetAllGroups(); cols = repo.GetAllCollections(); });

        var taskCol = cols.FirstOrDefault(c => c.Name == "Задачи")
            ?? throw new InvalidOperationException("В стенде нет коллекции «Задачи» — это БД от Tests/DbGenerator?");
        var taskId = taskCol.Id!.Value;

        List<DbField> fields = [];
        Time("Меню «Отображение свойств даты»: GetFields(Задачи)", () => fields = repo.GetFields(taskId));
        var fRem = fields.First(f => f.Name == "Напоминание").Id!.Value;
        var fDl  = fields.First(f => f.Name == "Дедлайн").Id!.Value;

        var rc = 0;
        Time("CountRecords(Задачи) — UI-поток перед дублем/удалением/переносом",
            () => rc = repo.CountRecords(taskId), () => $"{rc:N0} записей");

        var normStart  = new DateTime(2023, 5, 1);
        var giantStart = new DateTime(2026, 6, 15);
        Time("Календарь: окно Месяца, обычный период (42 дня, cap 2000/день)",
            () => repo.GetCalendarRecordsByRangeCapped(normStart, normStart.AddDays(42), null, 2000));
        Time("Календарь: окно Месяца с днями-гигантами (42 дня, cap 2000/день)",
            () => repo.GetCalendarRecordsByRangeCapped(giantStart, giantStart.AddDays(42), null, 2000));
        var dayCount = 0;
        Time("Календарь: COUNT событий дня-гиганта (для «+N еще»)",
            () => dayCount = repo.CountCalendarDayEvents(new DateTime(2026, 7, 1), new DateTime(2026, 7, 2), null),
            () => $"{dayCount:N0} событий");

        Time("SetDateFieldVisible(Напоминание → выкл)", () => repo.SetDateFieldVisible(fRem, false));
        Time("SetDateFieldVisible(Дедлайн → выкл)",     () => repo.SetDateFieldVisible(fDl, false));
        Time("Календарь: то же окно-гигант при 2 скрытых полях",
            () => repo.GetCalendarRecordsByRangeCapped(giantStart, giantStart.AddDays(42), null, 2000));
        Time("SetDateFieldVisible(Напоминание → вкл)", () => repo.SetDateFieldVisible(fRem, true));
        Time("SetDateFieldVisible(Дедлайн → вкл)",     () => repo.SetDateFieldVisible(fDl, true));

        DbGroup g1 = null!;
        Time("Создание группы", () =>
        {
            var pos = repo.GetAllGroups().Select(g => g.Position).DefaultIfEmpty(0).Max() + 1000;
            g1 = repo.AddGroup(new DbGroup { Name = "Перф-группа", ParentId = null, Position = pos });
        });
        Time("Создание коллекции (+первая запись)", () =>
        {
            var pos = repo.GetAllCollections().Select(c => c.Position).DefaultIfEmpty(0).Max() + 1000;
            var col = repo.AddCollection(new DbCollection { Name = "Новая коллекция", GroupId = null, Position = pos });
            repo.AddRecord(new DbRecord { CollectionId = col.Id!.Value, Title = "Без названия" });
        });

        Time("Переименование большой коллекции", () =>
        {
            var col = repo.GetAllCollections().First(c => c.Id == taskId);
            col.Name = "Задачи";
            repo.UpdateCollection(col);
        });
        Time("Изменение цвета большой коллекции", () =>
        {
            var col = repo.GetAllCollections().First(c => c.Id == taskId);
            col.Color = "#34C759";
            repo.UpdateCollection(col);
        });
        Time("Переименование группы", () => { g1.Name = "Перф-группа"; repo.UpdateGroup(g1); });

        Time("Расположение: большая коллекция → в группу (та же БД)", () =>
        {
            var col = repo.GetAllCollections().First(c => c.Id == taskId);
            col.GroupId = g1.Id;
            col.Name    = repo.UniqueCollectionName(col.Name, g1.Id, excludeId: taskId);
            repo.UpdateCollection(col);
        });

        int dupId;
        {
            using var ctx = new AppDbContext(mainPath);
            ctx.ApplyBulkPerformancePragmas();
            var iso = new ConstructorRepository(ctx);

            ConstructorRepository.CollectionShell shell = null!;
            Time("Дубль большой коллекции: шелл (строка коллекции + поля + COUNT)",
                () => shell = iso.CreateCollectionShell(taskId));
            dupId = shell.Collection.Id!.Value;

            var probe = new Probe();
            var t = Time("Дубль большой коллекции: записи + значения + UI + проекция календаря",
                () => iso.CopyCollectionData(taskId, dupId, probe));
            PhaseNote(t, probe,
                tail => $"хвост после последнего процента (commit + пересборка calendar_index): {BenchUtil.Fmt(tail)} — окно прогресса в это время висит на 100%");
        }

        (int Collections, long Records) sub = default;
        Time("CountGroupSubtree(группа с 2 большими коллекциями)",
            () => sub = repo.CountGroupSubtree(g1.Id!.Value), () => $"{sub.Collections} коллекции, {sub.Records:N0} записей");

        var dupRootId = 0;
        if (!light)
        {
            var probeG = new Probe();
            var tG = Time("Дубль группы с 2 большими коллекциями (DuplicateGroupIsolated)",
                () => dupRootId = repo.DuplicateGroupIsolated(g1.Id!.Value, probeG));
            PhaseNote(tG, probeG, tail => $"хвост после последнего процента: {BenchUtil.Fmt(tail)}");
        }

        {
            int moveColId, moveGroupId = 0;
            if (!light)
            {
                var allCols = repo.GetAllCollections();
                moveColId   = allCols.First(c => c.GroupId == dupRootId && c.Name == "Задачи").Id!.Value;
                moveGroupId = dupRootId;
            }
            else moveColId = dupId;

            var probeM1 = new Probe();
            var movedId = 0;
            var tM1 = Time("Перенос большой коллекции в другую БД (MoveCollectionIsolated)",
                () => movedId = target.Constructor.MoveCollectionIsolated(mainPath, moveColId, null, probeM1));
            MovePhases(tM1, probeM1, rc);

            if (!light)
            {
                var probeM2 = new Probe();
                var tM2 = Time("Перенос группы с большой коллекцией в другую БД (MoveGroupIsolated)",
                    () => moveGroupId = target.Constructor.MoveGroupIsolated(mainPath, moveGroupId, null, probeM2));
                MovePhases(tM2, probeM2, rc);

                var probeD1 = new Probe();
                Time("Удаление большой коллекции (DeleteCollectionIsolated)",
                    () => repo.DeleteCollectionIsolated(dupId, probeD1));
                var probeD2 = new Probe();
                Time("Удаление группы с большой коллекцией в целевой БД (DeleteGroupIsolated)",
                    () => target.Constructor.DeleteGroupIsolated(moveGroupId, probeD2));
            }
            else
            {
                var probeD1 = new Probe();
                Time("Удаление большой коллекции в целевой БД (DeleteCollectionIsolated)",
                    () => target.Constructor.DeleteCollectionIsolated(movedId, probeD1));
            }
        }

        Time("Refresh после операций: GetAllGroups+GetAllCollections",
            () => { repo.GetAllGroups(); repo.GetAllCollections(); });

        total.Stop();

        Console.WriteLine();
        Console.WriteLine($"Прогон целиком: {BenchUtil.Fmt(total.Elapsed.TotalMilliseconds)}");
        Console.WriteLine($"Файлы: main {BenchUtil.SizeGb(mainPath):0.00} ГБ (+wal {BenchUtil.SizeGb(mainPath + "-wal"):0.00} ГБ), " +
                          $"target {BenchUtil.SizeGb(targetPath):0.00} ГБ (+wal {BenchUtil.SizeGb(targetPath + "-wal"):0.00} ГБ)");
        Console.WriteLine();
        Console.WriteLine("── Сводная таблица ──────────────────────────────────────────────────────────");
        var w = Rows.Max(r => r.Op.Length);
        foreach (var r in Rows)
            Console.WriteLine($"{r.Op.PadRight(w)}  {BenchUtil.Fmt(r.Ms),12}  {r.Note}");

        main.Dispose();
        target.Dispose();
        return 0;
    }

    private static double Time(string op, Action action, Func<string>? note = null)
    {
        Console.Write($"{op} … ");
        var sw = Stopwatch.StartNew();
        action();
        sw.Stop();
        var ms   = sw.Elapsed.TotalMilliseconds;
        var text = note?.Invoke() ?? "";
        Console.WriteLine($"{BenchUtil.Fmt(ms)}{(text.Length > 0 ? $"  ({text})" : "")}");
        Rows.Add(new Row(op, ms, text));
        return ms;
    }

    private static void PhaseNote(double totalMs, Probe probe, Func<double, string> tailText)
    {
        if (probe.Points.Count == 0) return;
        var tail = totalMs - probe.Points[^1].Ms;
        var text = tailText(tail);
        Console.WriteLine($"    {text}");
        Rows[^1] = Rows[^1] with { Note = text };
    }

    private static void MovePhases(double totalMs, Probe probe, long recCount)
    {
        var cross = probe.Points.FirstOrDefault(p => p.Value > recCount);
        if (cross == default) return;
        var note = $"копия ≈{BenchUtil.Fmt(cross.Ms)}, удаление источника ≈{BenchUtil.Fmt(totalMs - cross.Ms)}";
        Console.WriteLine($"    {note}");
        Rows[^1] = Rows[^1] with { Note = note };
    }

    private sealed class Probe : IProgress<int>
    {
        private readonly Stopwatch _sw = Stopwatch.StartNew();
        public readonly List<(double Ms, int Value)> Points = [];
        public void Report(int value) { lock (Points) Points.Add((_sw.Elapsed.TotalMilliseconds, value)); }
    }
}
