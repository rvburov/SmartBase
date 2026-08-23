using SmartBase.Core.Models;
using SmartBase.Database.Repositories;
using SmartBase.Views.Tasks;

namespace SmartBase.Tests.Integration;

/// <summary>
/// Постраничное чтение раздела «Задачи»
/// (Database/Repositories/ConstructorRepository.TasksPaging.cs).
///
/// Раздел живет в двух режимах: маленькая база читается целиком и сортируется в памяти,
/// большая — страницами из БД. Главное требование к паре — НЕРАЗЛИЧИМОСТЬ: страницы обязаны
/// выдавать ровно ту же последовательность, что сплошной прочит с
/// <c>TaskGrouping.ByDueThenId</c>. Разойдись они — и человек, у которого база подросла,
/// увидел бы другой порядок задач без единого изменения в данных.
///
/// Порядок собирается двумя потоками (сначала «со сроком» по проекции calendar_index, затем
/// «без срока» по db_records), поэтому проверяются и стык между ними, и границы страниц.
/// </summary>
public sealed class TaskPagingTests
{
    // Коллекция с задачами: user-поле-исполнитель + авто-роли («Назначил», «Статус», «Дата»,
    // «Описание») — то же, что делает тумблер в сайдбаре.
    private static int EnableTasks(ConstructorRepository repo, int collectionId)
    {
        var exec = SampleData.AddField(repo, collectionId, "Исполнитель", "user", position: 1000);
        repo.SetUserFieldTaskExec(exec.Id!.Value, true);
        return exec.Id!.Value;
    }

    private static int DueFieldOf(ConstructorRepository repo, int collectionId) =>
        repo.GetFields(collectionId).First(f => f.FieldType == "date" && f.IsCalStart).Id!.Value;

    private static void SetDue(ConstructorRepository repo, int recordId, int dueFieldId, DateTime day) =>
        repo.SetFieldValue(recordId, dueFieldId,
            new DateValue(day, day.AddHours(1), false).ToStorage());

    /// <summary>Вид «Все задачи» без поиска — тот, в котором сверяется базовый порядок.</summary>
    private static ConstructorRepository.TaskView AllView =>
        new("All", "", [], new DateTime(2026, 8, 19, 12, 0, 0));

    /// <summary>Эталон: все прочитанное разом и отсортированное правилом раздела.</summary>
    private static List<int> ExpectedOrder(ConstructorRepository repo, int collectionId)
    {
        var all = repo.GetTaskRecords(limit: 1_000_000).Where(t => t.CollectionId == collectionId).ToList();
        all.Sort(TaskGrouping.ByDueThenId);
        return all.Select(t => t.Id!.Value).ToList();
    }

    /// <summary>
    /// Тот же список, но собранный страницами по <paramref name="pageSize"/>. Разбиение
    /// страницы на потоки берется из БОЕВОГО <see cref="TaskStreams.Split"/>, а не повторяется
    /// здесь: иначе тест проверял бы только SQL и молчал бы о том, что виртуальный список режет
    /// страницу иначе.
    /// </summary>
    private static List<int> PagedOrder(ConstructorRepository repo, int collectionId, int pageSize,
                                        ConstructorRepository.TaskView? view = null)
    {
        view ??= AllView;
        var stats = repo.GetTaskCollectionStats()[collectionId];
        var size  = repo.CountTaskView(collectionId, stats, view);
        var result = new List<int>();

        for (var row = 0; row < size.Total; row += pageSize)
        {
            var take = Math.Min(pageSize, size.Total - row);
            foreach (var (dated, skip, n) in TaskStreams.Split(size.Dated, row, take))
            {
                var ids = repo.GetTaskPageIds(collectionId, stats, view, dated, skip, n);
                var got = repo.GetTaskRecordsByIds(collectionId, ids);
                Assert.Equal(ids, got.Select(t => t.Id!.Value));   // гидратация не теряет строк
                result.AddRange(ids);
            }
        }
        return result;
    }

    // ── Порядок ──────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(1)]    // страница в одну строку — каждый шаг попадает на стык
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]    // ровно граница между потоками
    [InlineData(7)]
    [InlineData(100)]  // одна страница на все
    public void Pages_reproduce_the_in_memory_order_exactly(int pageSize)
    {
        using var db = new TempDatabase();
        var repo = db.Constructor;

        var col = SampleData.AddCollection(repo, "Заявки").Id!.Value;
        EnableTasks(repo, col);
        var due = DueFieldOf(repo, col);

        // Сроки вперемешку с «без даты», и заводятся НЕ по порядку дат: если бы страницы
        // читали по Position, тест бы этого не заметил.
        var march = new DateTime(2026, 3, 10, 9, 0, 0);
        var a = SampleData.AddRecord(repo, col, "Третья по сроку").Id!.Value;
        SetDue(repo, a, due, march.AddDays(20));

        var b = SampleData.AddRecord(repo, col, "Без срока — раз").Id!.Value;

        var c = SampleData.AddRecord(repo, col, "Первая по сроку").Id!.Value;
        SetDue(repo, c, due, march);

        var d = SampleData.AddRecord(repo, col, "Без срока — два").Id!.Value;

        var e = SampleData.AddRecord(repo, col, "Вторая по сроку").Id!.Value;
        SetDue(repo, e, due, march.AddDays(5));

        var f = SampleData.AddRecord(repo, col, "Четвертая по сроку").Id!.Value;
        SetDue(repo, f, due, march.AddDays(40));

        var expected = ExpectedOrder(repo, col);

        // Сам эталон должен быть тем, что мы думаем: срок ↑, «без даты» в конец по Id.
        Assert.Equal([c, e, a, f, b, d], expected);
        Assert.Equal(expected, PagedOrder(repo, col, pageSize));
    }

    /// <summary>Совпадающие сроки разводятся по Id — иначе строка на границе страниц могла бы
    /// повториться или потеряться.</summary>
    [Fact]
    public void Equal_due_dates_are_ordered_by_id()
    {
        using var db = new TempDatabase();
        var repo = db.Constructor;

        var col = SampleData.AddCollection(repo, "Заявки").Id!.Value;
        EnableTasks(repo, col);
        var due = DueFieldOf(repo, col);

        var same = new DateTime(2026, 3, 10, 9, 0, 0);
        var ids = new List<int>();
        for (var i = 0; i < 5; i++)
        {
            var id = SampleData.AddRecord(repo, col, $"Задача {i}").Id!.Value;
            SetDue(repo, id, due, same);
            ids.Add(id);
        }

        Assert.Equal(ids, ExpectedOrder(repo, col));
        Assert.Equal(ids, PagedOrder(repo, col, pageSize: 2));
    }

    [Fact]
    public void A_collection_without_any_due_dates_pages_by_id()
    {
        using var db = new TempDatabase();
        var repo = db.Constructor;

        var col = SampleData.AddCollection(repo, "Заявки").Id!.Value;
        EnableTasks(repo, col);

        var ids = Enumerable.Range(0, 6)
            .Select(i => SampleData.AddRecord(repo, col, $"Задача {i}").Id!.Value)
            .ToList();

        var stats = repo.GetTaskCollectionStats()[col];
        Assert.Equal(0, repo.CountTaskView(col, stats, AllView).Dated);
        Assert.Equal(ids, PagedOrder(repo, col, pageSize: 4));
    }

    // ── Размеры и форма коллекции ────────────────────────────────────────────

    [Fact]
    public void Stats_report_total_done_and_dated()
    {
        using var db = new TempDatabase();
        var repo = db.Constructor;

        var col = SampleData.AddCollection(repo, "Заявки").Id!.Value;
        EnableTasks(repo, col);
        var due    = DueFieldOf(repo, col);
        var status = repo.TaskRoleFields(col).StatusId!.Value;

        var withDue = SampleData.AddRecord(repo, col, "Со сроком").Id!.Value;
        SetDue(repo, withDue, due, new DateTime(2026, 3, 10, 9, 0, 0));
        repo.SetFieldValue(withDue, status, ConstructorRepository.TaskStatusDone);

        SampleData.AddRecord(repo, col, "Без срока");
        SampleData.AddRecord(repo, col, "Тоже без срока");

        var stats = repo.GetTaskCollectionStats()[col];
        var size  = repo.CountTaskView(col, stats, AllView);

        Assert.Equal(3, stats.Total);
        Assert.Equal(1, stats.Done);
        Assert.Equal(1, size.Dated);
        Assert.Equal(due, stats.DueFieldId);
        Assert.False(stats.MultiDate);
    }

    /// <summary>
    /// Два календарных поля-даты: сроком служит наименьшая из них, одна запись дает несколько
    /// строк проекции — двумя индексными потоками такой порядок не выражается. Коллекция
    /// обязана сама об этом сказать, чтобы ее читали общим путем (медленнее, зато полностью).
    /// </summary>
    [Fact]
    public void A_collection_with_two_calendar_dates_says_so()
    {
        using var db = new TempDatabase();
        var repo = db.Constructor;

        var col = SampleData.AddCollection(repo, "Заявки").Id!.Value;
        EnableTasks(repo, col);

        var second = SampleData.AddField(repo, col, "Срок", "date", position: 4000).Id!.Value;
        repo.SetDateFieldVisible(second, true);

        // Счетчики строятся от записей: у коллекции без единой записи размеров нет вовсе.
        SampleData.AddRecord(repo, col, "Задача");

        var stats = repo.GetTaskCollectionStats()[col];

        Assert.True(stats.MultiDate);
        Assert.Null(stats.DueFieldId);
    }

    /// <summary>
    /// И главное про такую коллекцию: недостижимых задач в ней нет. Порядок общего пути обязан
    /// совпадать с сортировкой в памяти так же, как у обычной коллекции.
    /// </summary>
    [Fact]
    public void A_collection_with_two_calendar_dates_still_pages_in_the_right_order()
    {
        using var db = new TempDatabase();
        var repo = db.Constructor;

        var col = SampleData.AddCollection(repo, "Заявки").Id!.Value;
        EnableTasks(repo, col);
        var first = DueFieldOf(repo, col);

        var second = SampleData.AddField(repo, col, "Срок", "date", position: 4000).Id!.Value;
        repo.SetDateFieldVisible(second, true);

        var march = new DateTime(2026, 3, 10, 9, 0, 0);
        var a = SampleData.AddRecord(repo, col, "Поздняя").Id!.Value;
        SetDue(repo, a, first, march.AddDays(30));

        // Срок этой записи задан ВТОРЫМ полем-датой — и он раньше, значит она идет первой.
        var b = SampleData.AddRecord(repo, col, "Ранняя по второму полю").Id!.Value;
        SetDue(repo, b, second, march);

        SampleData.AddRecord(repo, col, "Без срока");

        Assert.True(repo.GetTaskCollectionStats()[col].MultiDate);
        Assert.Equal(ExpectedOrder(repo, col), PagedOrder(repo, col, pageSize: 2));
    }

    /// <summary>Коллекция без записей в размерах не появляется: группы у нее все равно не будет.</summary>
    [Fact]
    public void An_empty_collection_has_no_stats_row()
    {
        using var db = new TempDatabase();
        var repo = db.Constructor;

        var col = SampleData.AddCollection(repo, "Заявки").Id!.Value;
        EnableTasks(repo, col);

        Assert.DoesNotContain(col, repo.GetTaskCollectionStats().Keys);
    }

    /// <summary>Пустой набор активных коллекций — ни одной, как и у списка (галочки сняты).</summary>
    [Fact]
    public void Stats_honour_the_active_collections()
    {
        using var db = new TempDatabase();
        var repo = db.Constructor;

        var col = SampleData.AddCollection(repo, "Заявки").Id!.Value;
        EnableTasks(repo, col);
        SampleData.AddRecord(repo, col, "Задача");

        Assert.Contains(col, repo.GetTaskCollectionStats().Keys);
        Assert.Empty(repo.GetTaskCollectionStats(new HashSet<int>()));
    }
}
