using Npgsql;
using SmartBase.Database;
using SmartBase.Database.Repositories;

namespace SmartBase.Tests.Integration;

/// <summary>
/// Поиск по названию записи на PostgreSQL — у задач, в календаре и в окне выбора связи. Все три
/// писались под SQLite, где LIKE не различает регистр латиницы, а PostgreSQL в LIKE различает его
/// всегда: по «gost» не находилось «GoSt». Проверка — сверкой, как у библиотеки
/// (<see cref="PgLibraryTests"/>): один сценарий идет на SQLite и на PostgreSQL, ответы обязаны
/// совпасть строка в строку, а главные из них закреплены и сами по себе. Без стенда (порт 5455)
/// тесты помечаются Skipped.
/// </summary>
public sealed class PgSearchTests
{
    private static void ResetSchema(string connString)
    {
        using var conn = new NpgsqlConnection(connString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DROP SCHEMA IF EXISTS public CASCADE; CREATE SCHEMA public;";
        cmd.ExecuteNonQuery();
    }

    [SkippableFact]
    public void Title_search_answers_on_PostgreSQL_the_same_as_on_SQLite()
    {
        Skip.IfNot(PgTestServer.IsAvailable, "Стенд PostgreSQL sb-pg (порт 5455) не поднят");

        List<string> lite;
        using (var db = new TempDatabase()) lite = Scenario(db.Constructor);

        var cs = PgTestServer.ConnectionString(PgTestServer.Database1);
        ResetSchema(cs);
        List<string> pg;
        using (var m = DatabaseManager.Open(101, "pg-search", cs, provider: "postgres"))
        {
            Assert.True(m.Context.IsPostgres);
            pg = Scenario(m.Constructor);
        }

        var onlyLite = lite.Except(pg).ToList();
        var onlyPg   = pg.Except(lite).ToList();
        Assert.True(onlyLite.Count == 0 && onlyPg.Count == 0,
                    "только SQLite:\n  " + string.Join("\n  ", onlyLite) +
                    "\nтолько PostgreSQL:\n  " + string.Join("\n  ", onlyPg));
        Assert.Equal(lite, pg);

        // Сверка ловит расхождение провайдеров, но не промах, общий для обоих.
        string Answer(string what) =>
            pg.Single(l => l.StartsWith(what + ": ", StringComparison.Ordinal))[(what.Length + 2)..];

        // Регистр латиницы не различается нигде.
        Assert.Equal("2 (со сроком 1)", Answer("задачи «gost»: счет"));
        Assert.Contains("GoSt-отчет",   Answer("задачи «gost»: со сроком"));
        Assert.Contains("Gost report",  Answer("задачи «gost»: без срока"));
        Assert.Contains("GoSt-отчет",   Answer("календарь «gost»"));
        Assert.Contains("GoSt-отчет",   Answer("выбор связи «gost»"));
        Assert.Contains("Gost report",  Answer("выбор связи «gost»"));

        // Окно выбора связи не различает регистр и у кириллицы — как фильтр колонки и как его
        // собственный список уже выбранных. Раньше голый LIKE не находил «Договор» по «договор».
        Assert.Contains("ДОГОВОР поставки", Answer("выбор связи «договор»"));
        Assert.Contains("ДОГОВОР поставки", Answer("выбор связи «ПОСТАВКИ»"));
        Assert.Contains("договор аренды",   Answer("выбор связи «ДоГоВоР»"));
        Assert.Contains("ТехЗадание",       Answer("выбор связи «техзадание»"));

        // Знаки LIKE в запросе — обычные знаки.
        Assert.Equal("[Скидка 50%]", Answer("выбор связи «50%»"));
        Assert.Equal("[a_b]",        Answer("выбор связи «a_b»"));
        Assert.Contains("Скидка 50%",       Answer("задачи «50%»: без срока"));
        Assert.DoesNotContain("Скидка 500", Answer("задачи «50%»: без срока"));
    }

    // ── Сценарий ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Каждый ответ — строка «что спросили: что ответили». Записи описаны названиями: id у
    /// провайдеров свои.
    /// </summary>
    private static List<string> Scenario(ConstructorRepository repo)
    {
        var lines = new List<string>();
        void Say(string what, object answer) => lines.Add($"{what}: {answer}");
        var now = new DateTime(2026, 9, 16, 12, 0, 0);

        // Задачи: раздел включает поле-исполнитель, оно же заводит срок. Срок — у части задач:
        // поиск идет по обоим потокам, «со сроком» и «без срока».
        var tasks = SampleData.AddCollection(repo, "Заявки").Id!.Value;
        var exec  = SampleData.AddField(repo, tasks, "Исполнитель", "user", position: 1000);
        repo.SetUserFieldTaskExec(exec.Id!.Value, true);
        var due   = repo.GetFields(tasks).First(f => f.FieldType == "date" && f.IsCalStart).Id!.Value;

        var titles = new Dictionary<int, string>();
        void Task(string title, int position, string? dueDate = null)
        {
            var id = SampleData.AddRecord(repo, tasks, title, position).Id!.Value;
            titles[id] = title;
            if (dueDate is not null) repo.SetFieldValue(id, due, dueDate);
        }

        Task("GoSt-отчет",       10, "2026-09-20");
        Task("Gost report",      20);
        Task("ДОГОВОР поставки", 30, "2026-09-21");
        Task("договор аренды",   40);
        Task("Скидка 50%",       50);
        Task("Скидка 500",       60);
        Task("a_b",              70);
        Task("axb",              80);
        Task("ТехЗадание",       90);

        // Календарь: свое поле-дата, события в окне сентября. Задачи со сроком попадают в
        // календарь тоже — срок у них календарное поле.
        var events = SampleData.AddCollection(repo, "События").Id!.Value;
        var date   = SampleData.AddCalendarDateField(repo, events).Id!.Value;
        foreach (var (title, day) in new[] { ("Gost-аудит", "2026-09-10"), ("GOST сверка", "2026-09-11"),
                                            ("Совещание", "2026-09-12") })
        {
            var id = SampleData.AddRecord(repo, events, title).Id!.Value;
            repo.SetFieldValue(id, date, day);
        }

        string Titles(IEnumerable<int> ids) => "[" + string.Join(", ", ids.Select(id => titles[id])) + "]";

        // ── Задачи: счет и оба потока страниц ──
        var stats = repo.GetTaskCollectionStats()[tasks];
        foreach (var q in new[] { "gost", "GOST", "договор", "ДОГОВОР", "50%", "a_b", "скидка" })
        {
            var view = new ConstructorRepository.TaskView("All", q, ["Тестов Тест"], now);
            var size = repo.CountTaskView(tasks, stats, view);
            Say($"задачи «{q}»: счет", $"{size.Total} (со сроком {size.Dated})");
            Say($"задачи «{q}»: со сроком", Titles(repo.GetTaskPageIds(tasks, stats, view, dated: true, 0, 50)));
            Say($"задачи «{q}»: без срока", Titles(repo.GetTaskPageIds(tasks, stats, view, dated: false, 0, 50)));
        }

        // ── Календарь: поиск событий окна ──
        foreach (var q in new[] { "gost", "GOST", "договор", "сверка" })
        {
            var found = repo.SearchCalendarEventsAsync(new DateTime(2026, 9, 1), new DateTime(2026, 10, 1),
                                                       null, q, 100).GetAwaiter().GetResult();
            Say($"календарь «{q}»", "[" + string.Join(", ", found.Select(e => $"{e.Title} {e.StartDt:dd.MM}")) + "]");
        }

        // ── Окно выбора связи: кандидаты из коллекции ──
        foreach (var q in new[] { "gost", "GOST", "договор", "ПОСТАВКИ", "ДоГоВоР", "техзадание", "50%", "a_b", "" })
            Say($"выбор связи «{q}»",
                "[" + string.Join(", ", repo.SearchRecordTitles(tasks, q, 100).Select(r => r.Title)) + "]");

        return lines;
    }
}
