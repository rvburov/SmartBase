using SmartBase.Core.Models;
using SmartBase.Database.Repositories;
using SmartBase.Views.Tasks;

namespace SmartBase.Tests.Integration;

/// <summary>
/// Счетчики быстрого фильтра, посчитанные В БАЗЕ
/// (<c>ConstructorRepository.CountTaskView</c> / <c>CountTaskViews</c>).
///
/// На маленьком разделе их считает проход по памяти (<c>TaskFilters.Counts</c>), на большом —
/// SQL: прочитанной части там мало, и счет по ней занижал бы цифру в пилюле. Значит, два
/// счетчика обязаны давать ОДНО И ТО ЖЕ на одних и тех же данных — иначе у человека, чья база
/// подросла, цифры поедут сами собой.
///
/// Каждый тест ниже строит набор задач и сверяет ВСЕ пункты разом — стандартные и все стадии
/// коллекций: правила определены парой (SQL здесь, C# в TaskFilters), и разъехаться они могут
/// в любом. Правила показа (скрытые выполненные, момент просрочки) сверяются во всех сочетаниях.
/// </summary>
public sealed class TaskFilterCountsTests
{
    private const string Me = "Иванов Иван";

    private static int EnableTasks(ConstructorRepository repo, int collectionId)
    {
        var exec = SampleData.AddField(repo, collectionId, "Исполнитель", "user", position: 1000);
        repo.SetUserFieldTaskExec(exec.Id!.Value, true);
        return exec.Id!.Value;
    }

    private static int DueFieldOf(ConstructorRepository repo, int collectionId) =>
        repo.GetFields(collectionId).First(f => f.FieldType == "date" && f.IsCalStart).Id!.Value;

    private static ConstructorRepository.TaskView ViewOf(TaskFilter f, string search, DateTime now, TaskRules rules) =>
        new(f.Kind.ToString(), search, [Me], now, f.StageName,
            HideDone: !rules.ShowDone, OverdueNextDay: rules.OverdueNextDay);

    private static readonly TaskRules[] AllRules =
    [
        TaskRules.Default,
        new(OverdueNextDay: true,  ShowDone: true),
        new(OverdueNextDay: false, ShowDone: false),
        new(OverdueNextDay: true,  ShowDone: false),
    ];

    /// <summary>
    /// Сверяет все пункты (стандартные и стадии) при всех сочетаниях правил: точный счет в SQL
    /// против прохода по памяти. Поиск участвует на равных — в SQL он уходит тем же условием,
    /// что и фильтр.
    /// </summary>
    private static void AssertCountsAgree(ConstructorRepository repo, DateTime now, string search = "")
    {
        IEnumerable<TaskRecord> all = repo.GetTaskRecords(limit: 1_000_000);
        if (search.Length > 0)
            all = all.Where(t => t.Title.Contains(search, StringComparison.CurrentCultureIgnoreCase));

        var tasks   = all.ToList();
        var filters = TaskFilters.WithStages(repo.GetTaskStages());
        var stats   = repo.GetTaskCollectionStats();

        foreach (var rules in AllRules)
        {
            var inMemory = TaskFilters.Counts(tasks, filters, now, Me, rules);
            foreach (var f in filters)
            {
                var view = ViewOf(f, search, now, rules);
                var sql  = stats.Sum(kv => repo.CountTaskView(kv.Key, kv.Value, view).Total);
                Assert.True(inMemory[f] == sql,
                            $"{f.Key} при {rules}: в памяти {inMemory[f]}, в SQL {sql}");
            }
        }
    }

    // Набор, в котором есть каждое из состояний, различаемых фильтрами.
    private static void SeedMixed(ConstructorRepository repo, int col, DateTime now)
    {
        var exec   = repo.TaskExecFieldId(col)!.Value;
        var due    = DueFieldOf(repo, col);
        var status = repo.TaskRoleFields(col).StatusId!.Value;

        void Task(string title, string executor, DateTime? start, DateTime? end, string state)
        {
            var id = SampleData.AddRecord(repo, col, title).Id!.Value;
            if (executor.Length > 0) repo.SetFieldValue(id, exec, executor);
            if (state.Length > 0)    repo.SetFieldValue(id, status, state);
            if (start is DateTime s)
                repo.SetFieldValue(id, due, new DateValue(s, end ?? s, false).ToStorage());
        }

        var planned = ConstructorRepository.TaskStatusPlanned;
        var active  = ConstructorRepository.TaskStatusActive;
        var done    = ConstructorRepository.TaskStatusDone;

        // Сегодня: однодневная и многодневная, идущая ЧЕРЕЗ сегодня; и та, чье время уже
        // прошло — просрочена с момента срока, но не «со следующего дня».
        Task("Сегодня, мне",        Me,           now,                 now.AddHours(2),  planned);
        Task("Через сегодня",       "Петров",     now.AddDays(-2),     now.AddDays(2),   planned);
        Task("Сегодня утром",       Me,           now.AddHours(-4),    now.AddHours(-3), active);

        // Просрочка: незакрытая с истекшим сроком — и такая же, но выполненная (у нее
        // просрочки нет, сколько бы срок ни истек).
        Task("Просрочена",          Me,           now.AddDays(-5),     now.AddDays(-4),  planned);
        Task("Просрочена, но сдана", Me,          now.AddDays(-5),     now.AddDays(-4),  done);

        // Будущее, без срока, чужая, нераспределенная, без статуса вовсе, в работе.
        Task("Впереди",             "Петров",     now.AddDays(10),     now.AddDays(11),  planned);
        Task("Без срока, мне",      Me,           null,                null,             planned);
        Task("Без срока, ничья",    "",           null,                null,             "");
        Task("Выполнена без срока", Me,           null,                null,             done);
        Task("В работе без срока",  "Петров",     null,                null,             active);
    }

    [Fact]
    public void Sql_counts_match_the_in_memory_counts_on_a_mixed_set()
    {
        using var db = new TempDatabase();
        var repo = db.Constructor;
        var now  = new DateTime(2026, 8, 19, 14, 0, 0);

        var col = SampleData.AddCollection(repo, "Заявки").Id!.Value;
        EnableTasks(repo, col);
        SeedMixed(repo, col, now);

        AssertCountsAgree(repo, now);
    }

    /// <summary>Пустой раздел: все пункты нулевые и с обеих сторон одинаково.</summary>
    [Fact]
    public void Sql_counts_match_on_an_empty_section()
    {
        using var db = new TempDatabase();
        var repo = db.Constructor;

        var col = SampleData.AddCollection(repo, "Заявки").Id!.Value;
        EnableTasks(repo, col);

        AssertCountsAgree(repo, new DateTime(2026, 8, 19, 14, 0, 0));
    }

    /// <summary>Две коллекции: правила не путают их между собой (статус берется из своей).</summary>
    [Fact]
    public void Sql_counts_match_across_two_collections()
    {
        using var db = new TempDatabase();
        var repo = db.Constructor;
        var now  = new DateTime(2026, 8, 19, 14, 0, 0);

        var first = SampleData.AddCollection(repo, "Заявки").Id!.Value;
        EnableTasks(repo, first);
        SeedMixed(repo, first, now);

        var second = SampleData.AddCollection(repo, "Ремонты").Id!.Value;
        EnableTasks(repo, second);
        SeedMixed(repo, second, now);

        AssertCountsAgree(repo, now);
    }

    /// <summary>Поиск считается тем же условием, что и фильтр, — на смешанном наборе.</summary>
    [Fact]
    public void Sql_counts_match_when_a_search_is_active()
    {
        using var db = new TempDatabase();
        var repo = db.Constructor;
        var now  = new DateTime(2026, 8, 19, 14, 0, 0);

        var col = SampleData.AddCollection(repo, "Заявки").Id!.Value;
        EnableTasks(repo, col);
        SeedMixed(repo, col, now);

        AssertCountsAgree(repo, now, search: "срок");     // попадает в «Без срока …»
        AssertCountsAgree(repo, now, search: "СЕГОДНЯ");  // другой регистр — кириллица
        AssertCountsAgree(repo, now, search: "нет такой задачи");
    }

    /// <summary>Коллекция без поля-статуса: все статусы пустые — стадия «Запланировано» берет
    /// всех, остальные стадии никого, и SQL согласен с памятью.</summary>
    [Fact]
    public void Sql_counts_match_without_a_status_field()
    {
        using var db = new TempDatabase();
        var repo = db.Constructor;
        var now  = new DateTime(2026, 8, 19, 14, 0, 0);

        var col = SampleData.AddCollection(repo, "Заявки").Id!.Value;
        EnableTasks(repo, col);
        var statusId = repo.TaskRoleFields(col).StatusId!.Value;
        repo.DeleteField(statusId);
        for (var i = 0; i < 3; i++) SampleData.AddRecord(repo, col, $"Задача {i}");

        var stats = repo.GetTaskCollectionStats()[col];
        foreach (var rules in AllRules)
        {
            Assert.Equal(3, repo.CountTaskView(col, stats,
                ViewOf(TaskFilter.ForStage(ConstructorRepository.TaskStatusPlanned), "", now, rules)).Total);
            Assert.Equal(0, repo.CountTaskView(col, stats,
                ViewOf(TaskFilter.ForStage(ConstructorRepository.TaskStatusDone), "", now, rules)).Total);
            Assert.Equal(3, repo.CountTaskView(col, stats, ViewOf(TaskFilter.All, "", now, rules)).Total);
        }
    }

    /// <summary>Крышка счета пилюли: сверх нее точное число не считается — пилюле оно и не
    /// нужно, она печатает «999+». Цена запроса от размера базы после этого не зависит.</summary>
    [Fact]
    public void The_pill_count_stops_at_the_cap()
    {
        using var db = new TempDatabase();
        var repo = db.Constructor;

        var col = SampleData.AddCollection(repo, "Заявки").Id!.Value;
        EnableTasks(repo, col);
        for (var i = 0; i < 12; i++) SampleData.AddRecord(repo, col, $"Задача {i}");

        var stats = repo.GetTaskCollectionStats();
        var now   = DateTime.Now;
        var all   = new ConstructorRepository.TaskView("All", "", [Me], now);

        Assert.Equal(12, repo.CountTaskViews(stats, [all], cap: 1000)["All"]);
        Assert.Equal(5,  repo.CountTaskViews(stats, [all], cap: 5)["All"]);
    }

    /// <summary>Крышка НЕ распространяется на размеры групп: по ним строится полоса прокрутки,
    /// и обрезанное число сделало бы часть задач недостижимыми.</summary>
    [Fact]
    public void Group_sizes_are_never_capped()
    {
        using var db = new TempDatabase();
        var repo = db.Constructor;

        var col = SampleData.AddCollection(repo, "Заявки").Id!.Value;
        EnableTasks(repo, col);
        for (var i = 0; i < 12; i++) SampleData.AddRecord(repo, col, $"Задача {i}");

        var stats = repo.GetTaskCollectionStats()[col];
        var view  = new ConstructorRepository.TaskView("All", "", [Me], DateTime.Now);

        Assert.Equal(12, repo.CountTaskView(col, stats, view).Total);
    }

    /// <summary>Снятые галочки — ноль, а не «все»: та же семантика, что у списка.</summary>
    [Fact]
    public void No_active_collections_means_zero()
    {
        using var db = new TempDatabase();
        var repo = db.Constructor;

        var col = SampleData.AddCollection(repo, "Заявки").Id!.Value;
        EnableTasks(repo, col);
        SampleData.AddRecord(repo, col, "Задача");

        Assert.Empty(repo.GetTaskCollectionStats(new HashSet<int>()));
        Assert.Single(repo.GetTaskCollectionStats());
    }
}
