using System.Diagnostics;
using DataBase.Core.Models;
using DataBase.Database;
using DataBase.Database.Repositories;

namespace DataBase.Tests.SidebarPerf;

/// <summary>
/// Замер производительности операций сайдбара (SidebarTree) на большом стенде.
///
/// Вызывает РОВНО те методы <see cref="ConstructorRepository"/>, что вызывает сайдбар,
/// в той же последовательности (включая подсчёты CountRecords/CountGroupSubtree,
/// которые в приложении идут на UI-потоке перед каждой массовой операцией), и печатает
/// таблицу времён. Стенд «строит сам себя»: дубль коллекции 10 млн даёт вторую такую же,
/// дубль группы — ветку 2×10 млн, перенос в другую БД гоняет данные между файлами,
/// удаления в конце сносят копии (исходная коллекция «Задачи» остаётся).
///
/// Стенд создаёт Tests/DbGenerator (см. docs/guides/test-database.md):
///   dotnet run -c Release --project Tests/DbGenerator -- --count 10000000 --out ~/TestData/perf_main.db
///
/// Запуск (из корня репозитория):
///   dotnet run -c Release --project Tests/SidebarPerf                       → ~/TestData/perf_main.db
///   dotnet run -c Release --project Tests/SidebarPerf -- --db /path/main.db --target /path/target.db
///   dotnet run -c Release --project Tests/SidebarPerf -- --light            → без дубля/переноса группы
///
/// Целевая БД переноса (--target) пересоздаётся при каждом запуске. После полного прогона:
/// в основной БД остаются «Задачи» (10 млн) в группе + «Новая коллекция» (1 запись),
/// в целевой — копия «Задач» (10 млн) в корне. Файлы после удалений НЕ сжимаются
/// (auto_vacuum выключен) — на диске остаются свободные страницы.
/// </summary>
internal static class Program
{
    private sealed record Row(string Op, double Ms, string Note);
    private static readonly List<Row> Rows = [];

    public static int Main(string[] args)
    {
        SQLitePCL.Batteries_V2.Init();

        var home       = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var mainPath   = ArgStr(args, "--db")     ?? Path.Combine(home, "TestData", "perf_main.db");
        var targetPath = ArgStr(args, "--target") ?? Path.Combine(home, "TestData", "perf_target.db");
        var light      = args.Contains("--light");

        if (!File.Exists(mainPath))
        {
            Console.WriteLine($"Нет стенда: {mainPath}");
            Console.WriteLine("Сгенерируйте: dotnet run -c Release --project Tests/DbGenerator -- --count 10000000 --out <путь> --force");
            return 1;
        }

        // Целевая БД переноса — всегда свежая (прогон воспроизводим).
        foreach (var suffix in new[] { "", "-wal", "-shm", "-journal" })
            if (File.Exists(targetPath + suffix)) File.Delete(targetPath + suffix);

        Console.WriteLine($"Стенд:   {mainPath} ({SizeGb(mainPath):0.00} ГБ)");
        Console.WriteLine($"Целевая: {targetPath} (пересоздаётся)");
        Console.WriteLine($"Режим:   {(light ? "light (без дубля/переноса группы)" : "полный")}");
        Console.WriteLine();

        var total = Stopwatch.StartNew();

        // ── 0. Монтирование (как подключение БД в приложении) ────────────────────
        DatabaseManager main = null!, target = null!;
        Time("Монтирование основной БД (DatabaseManager.Open)",
            () => main = DatabaseManager.Open(0, "perf-main", mainPath));
        Time("Создание целевой БД (DatabaseManager.Open, Migrate с нуля)",
            () => target = DatabaseManager.Open(1, "perf-target", targetPath));
        var repo = main.Constructor;

        // ── 1. Чтения дерева (Refresh сайдбара) ───────────────────────────────────
        List<DbGroup>      groups = [];
        List<DbCollection> cols   = [];
        Time("Refresh: GetAllGroups (холодное)",      () => groups = repo.GetAllGroups());
        Time("Refresh: GetAllCollections (холодное)", () => cols   = repo.GetAllCollections());
        Time("Refresh: GetAllGroups+GetAllCollections (тёплое)",
            () => { groups = repo.GetAllGroups(); cols = repo.GetAllCollections(); });

        var taskCol = cols.FirstOrDefault(c => c.Name == "Задачи")
            ?? throw new InvalidOperationException("В стенде нет коллекции «Задачи» — это БД от Tests/DbGenerator?");
        var taskId = taskCol.Id!.Value;

        List<DbField> fields = [];
        Time("Меню «Отображение свойств даты»: GetFields(Задачи)", () => fields = repo.GetFields(taskId));
        var fRem = fields.First(f => f.Name == "Напоминание").Id!.Value;
        var fDl  = fields.First(f => f.Name == "Дедлайн").Id!.Value;

        // ── 2. Подсчёт записей — в приложении идёт на UI-потоке ПЕРЕД операцией ──
        var rc = 0;
        Time("CountRecords(Задачи) — UI-поток перед дублем/удалением/переносом",
            () => rc = repo.CountRecords(taskId), () => $"{rc:N0} записей");

        // ── 3. Чтения календаря (перечитка окна после клика по чекбоксу) ─────────
        var normStart  = new DateTime(2023, 5, 1);
        var giantStart = new DateTime(2026, 6, 15);   // 42-дневное окно с 3 днями-гигантами
        Time("Календарь: окно Месяца, обычный период (42 дня, cap 2000/день)",
            () => repo.GetCalendarRecordsByRangeCapped(normStart, normStart.AddDays(42), null, 2000));
        Time("Календарь: окно Месяца с днями-гигантами (42 дня, cap 2000/день)",
            () => repo.GetCalendarRecordsByRangeCapped(giantStart, giantStart.AddDays(42), null, 2000));
        var dayCount = 0;
        Time("Календарь: COUNT событий дня-гиганта (для «+N ещё»)",
            () => dayCount = repo.CountCalendarDayEvents(new DateTime(2026, 7, 1), new DateTime(2026, 7, 2), null),
            () => $"{dayCount:N0} событий");

        // ── 4. «Отображение свойств даты» — флип флага, проекция не пересобирается ─
        Time("SetDateFieldVisible(Напоминание → выкл)", () => repo.SetDateFieldVisible(fRem, false));
        Time("SetDateFieldVisible(Дедлайн → выкл)",     () => repo.SetDateFieldVisible(fDl, false));
        Time("Календарь: то же окно-гигант при 2 скрытых полях",
            () => repo.GetCalendarRecordsByRangeCapped(giantStart, giantStart.AddDays(42), null, 2000));
        Time("SetDateFieldVisible(Напоминание → вкл)", () => repo.SetDateFieldVisible(fRem, true));
        Time("SetDateFieldVisible(Дедлайн → вкл)",     () => repo.SetDateFieldVisible(fDl, true));

        // ── 5. Создание (контекстное меню / кнопка «+») ───────────────────────────
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

        // ── 6. Изменения (диалог «Изменить», подменю цвета) ───────────────────────
        Time("Переименование большой коллекции", () =>
        {
            var col = repo.GetAllCollections().First(c => c.Id == taskId);
            col.Name = "Задачи";   // как есть — стоимость Update не зависит от имени
            repo.UpdateCollection(col);
        });
        Time("Изменение цвета большой коллекции", () =>
        {
            var col = repo.GetAllCollections().First(c => c.Id == taskId);
            col.Color = "#34C759";
            repo.UpdateCollection(col);
        });
        Time("Переименование группы", () => { g1.Name = "Перф-группа"; repo.UpdateGroup(g1); });

        // ── 7. «Расположение» в той же БД — только метаданные ────────────────────
        Time("Расположение: большая коллекция → в группу (та же БД)", () =>
        {
            var col = repo.GetAllCollections().First(c => c.Id == taskId);
            col.GroupId = g1.Id;
            col.Name    = repo.UniqueCollectionName(col.Name, g1.Id, excludeId: taskId);
            repo.UpdateCollection(col);
        });

        // ── 8. Дубль коллекции 10 млн (как DuplicateCollectionIsolated, фазы врозь) ─
        int dupId;
        {
            using var ctx = new AppDbContext(mainPath);
            ctx.ApplyBulkPerformancePragmas();   // как DuplicateCollectionIsolated
            var iso = new ConstructorRepository(ctx);

            ConstructorRepository.CollectionShell shell = null!;
            Time("Дубль большой коллекции: шелл (строка коллекции + поля + COUNT)",
                () => shell = iso.CreateCollectionShell(taskId));
            dupId = shell.Collection.Id!.Value;

            var probe = new Probe();
            var t = Time("Дубль большой коллекции: записи + значения + UI + проекция календаря",
                () => iso.CopyCollectionData(taskId, dupId, probe));
            PhaseNote(t, probe, rc,
                tail => $"хвост после последнего процента (commit + пересборка calendar_index): {Fmt(tail)} — окно прогресса в это время висит на 100%");
        }

        // ── 9. Подсчёт ветки (UI-поток): группа с 2×10 млн ────────────────────────
        (int Collections, long Records) sub = default;
        Time("CountGroupSubtree(группа с 2 большими коллекциями)",
            () => sub = repo.CountGroupSubtree(g1.Id!.Value), () => $"{sub.Collections} коллекции, {sub.Records:N0} записей");

        var dupRootId = 0;
        if (!light)
        {
            // ── 10. Дубль группы со всей веткой (2 коллекции × 10 млн) ────────────
            var probeG = new Probe();
            var tG = Time("Дубль группы с 2 большими коллекциями (DuplicateGroupIsolated)",
                () => dupRootId = repo.DuplicateGroupIsolated(g1.Id!.Value, probeG));
            PhaseNote(tG, probeG, sub.Records,
                tail => $"хвост после последнего процента: {Fmt(tail)}");
        }

        // ── 11. Перенос в другую БД (ATTACH + bulk, затем удаление источника) ─────
        {
            // Что переносим: в полном режиме — коллекции копии-ветки, в light — дубль B.
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

                // ── 12. Удаления ────────────────────────────────────────────────────
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

        // ── 13. Refresh после массовых операций ───────────────────────────────────
        Time("Refresh после операций: GetAllGroups+GetAllCollections",
            () => { repo.GetAllGroups(); repo.GetAllCollections(); });

        total.Stop();

        // ── Сводка ────────────────────────────────────────────────────────────────
        Console.WriteLine();
        Console.WriteLine($"Прогон целиком: {Fmt(total.Elapsed.TotalMilliseconds)}");
        Console.WriteLine($"Файлы: main {SizeGb(mainPath):0.00} ГБ (+wal {SizeGb(mainPath + "-wal"):0.00} ГБ), " +
                          $"target {SizeGb(targetPath):0.00} ГБ (+wal {SizeGb(targetPath + "-wal"):0.00} ГБ)");
        Console.WriteLine();
        Console.WriteLine("── Сводная таблица ──────────────────────────────────────────────────────────");
        var w = Rows.Max(r => r.Op.Length);
        foreach (var r in Rows)
            Console.WriteLine($"{r.Op.PadRight(w)}  {Fmt(r.Ms),12}  {r.Note}");

        main.Dispose();
        target.Dispose();
        return 0;
    }

    // ── Замер и печать ────────────────────────────────────────────────────────────

    // note — ленивая: вычисляется ПОСЛЕ действия (счётчики заполняются внутри него).
    private static double Time(string op, Action action, Func<string>? note = null)
    {
        Console.Write($"{op} … ");
        var sw = Stopwatch.StartNew();
        action();
        sw.Stop();
        var ms   = sw.Elapsed.TotalMilliseconds;
        var text = note?.Invoke() ?? "";
        Console.WriteLine($"{Fmt(ms)}{(text.Length > 0 ? $"  ({text})" : "")}");
        Rows.Add(new Row(op, ms, text));
        return ms;
    }

    // Хвост операции после последнего отчёта прогресса (commit/пересборка проекции):
    // ровно то время, что окно прогресса в приложении показывает 100% без движения.
    private static void PhaseNote(double totalMs, Probe probe, long recCount, Func<double, string> tailText)
    {
        if (probe.Points.Count == 0) return;
        var tail = totalMs - probe.Points[^1].Ms;
        var text = tailText(tail);
        Console.WriteLine($"    {text}");
        Rows[^1] = Rows[^1] with { Note = text };
    }

    // Фазы переноса: прогресс 0..rec — копия в целевую БД, rec..2·rec — удаление источника.
    private static void MovePhases(double totalMs, Probe probe, long recCount)
    {
        var cross = probe.Points.FirstOrDefault(p => p.Value > recCount);
        if (cross == default) return;
        var copyMs = cross.Ms;
        var note = $"копия ≈{Fmt(copyMs)}, удаление источника ≈{Fmt(totalMs - copyMs)}";
        Console.WriteLine($"    {note}");
        Rows[^1] = Rows[^1] with { Note = note };
    }

    /// <summary>Собирает точки прогресса (мс от старта, значение) — для разбивки на фазы.</summary>
    private sealed class Probe : IProgress<int>
    {
        private readonly Stopwatch _sw = Stopwatch.StartNew();
        public readonly List<(double Ms, int Value)> Points = [];
        public void Report(int value) { lock (Points) Points.Add((_sw.Elapsed.TotalMilliseconds, value)); }
    }

    private static string Fmt(double ms) => ms switch
    {
        < 1000   => $"{ms:0} мс",
        < 60_000 => $"{ms / 1000:0.0} с",
        _        => $"{(int)(ms / 60_000)} мин {ms % 60_000 / 1000:0} с",
    };

    private static double SizeGb(string path) =>
        File.Exists(path) ? new FileInfo(path).Length / 1024.0 / 1024.0 / 1024.0 : 0;

    private static string? ArgStr(string[] args, string name)
    {
        var i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }
}
