using SmartBase.Core.Models;
using SmartBase.Database.Repositories;
using SmartBase.Views.Tasks;

namespace SmartBase.Tests.Integration;

/// <summary>
/// Счётчики быстрого фильтра, посчитанные В БАЗЕ
/// (<c>ConstructorRepository.CountTasksMatching</c>).
///
/// На маленьком разделе их считает проход по памяти (<c>TaskFilters.Counts</c>), на большом —
/// SQL: прочитанной части там мало, и счёт по ней занижал бы цифру в пилюле. Значит, два
/// счётчика обязаны давать ОДНО И ТО ЖЕ на одних и тех же данных — иначе у человека, чья база
/// подросла, цифры поедут сами собой.
///
/// Каждый тест ниже строит набор задач и сверяет ВСЕ шесть правил разом: правила определены
/// парой (SQL здесь, C# в TaskFilters), и разъехаться они могут в любом.
/// </summary>
public sealed class TaskFilterCountsTests
{
    private const string Me = "Иванов Иван";
    private const int Cap = 1000;

    private static int EnableTasks(ConstructorRepository repo, int collectionId)
    {
        var exec = SampleData.AddField(repo, collectionId, "Исполнитель", "user", position: 1000);
        repo.SetUserFieldTaskExec(exec.Id!.Value, true);
        return exec.Id!.Value;
    }

    private static int DueFieldOf(ConstructorRepository repo, int collectionId) =>
        repo.GetFields(collectionId).First(f => f.FieldType == "date" && f.IsCalStart).Id!.Value;

    /// <summary>
    /// Сверяет все шесть правил: точный счёт в SQL против прохода по памяти. Поиск участвует
    /// на равных — в SQL он уходит тем же условием, что и фильтр.
    /// </summary>
    private static void AssertCountsAgree(ConstructorRepository repo, DateTime now, string search = "")
    {
        IEnumerable<TaskRecord> all = repo.GetTaskRecords(limit: 1_000_000);
        if (search.Length > 0)
            all = all.Where(t => t.Title.Contains(search, StringComparison.CurrentCultureIgnoreCase));

        var inMemory = TaskFilters.Counts(all.ToList(), now, Me);
        var stats    = repo.GetTaskCollectionStats();

        foreach (var kind in TaskFilters.All)
        {
            var view = new ConstructorRepository.TaskView(kind.ToString(), search, [Me], now);
            var sql  = stats.Sum(kv => repo.CountTaskView(kv.Key, kv.Value, view).Total);
            Assert.Equal(inMemory[kind], sql);
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
        var done    = ConstructorRepository.TaskStatusDone;

        // Сегодня: однодневная и многодневная, идущая ЧЕРЕЗ сегодня.
        Task("Сегодня, мне",        Me,           now,                 now.AddHours(2),  planned);
        Task("Через сегодня",       "Петров",     now.AddDays(-2),     now.AddDays(2),   planned);

        // Просрочка: незакрытая с истёкшим сроком — и такая же, но выполненная (у неё
        // просрочки нет, сколько бы срок ни истёк).
        Task("Просрочена",          Me,           now.AddDays(-5),     now.AddDays(-4),  planned);
        Task("Просрочена, но сдана", Me,          now.AddDays(-5),     now.AddDays(-4),  done);

        // Будущее, без срока, чужая, нераспределённая, без статуса вовсе.
        Task("Впереди",             "Петров",     now.AddDays(10),     now.AddDays(11),  planned);
        Task("Без срока, мне",      Me,           null,                null,             planned);
        Task("Без срока, ничья",    "",           null,                null,             "");
        Task("Выполнена без срока", Me,           null,                null,             done);
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

    /// <summary>Пустой раздел: все шесть нулевые и с обеих сторон одинаково.</summary>
    [Fact]
    public void Sql_counts_match_on_an_empty_section()
    {
        using var db = new TempDatabase();
        var repo = db.Constructor;

        var col = SampleData.AddCollection(repo, "Заявки").Id!.Value;
        EnableTasks(repo, col);

        AssertCountsAgree(repo, new DateTime(2026, 8, 19, 14, 0, 0));
    }

    /// <summary>Две коллекции: правила не путают их между собой (статус берётся из своей).</summary>
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

    /// <summary>Крышка счёта пилюли: сверх неё точное число не считается — пилюле оно и не
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

        Assert.Equal(12, repo.CountTaskViews(stats, ["All"], "", [Me], now, cap: 1000)["All"]);
        Assert.Equal(5,  repo.CountTaskViews(stats, ["All"], "", [Me], now, cap: 5)["All"]);
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
