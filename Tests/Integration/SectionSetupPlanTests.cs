using SmartBase.Core.Models;
using SmartBase.Database.Repositories;

namespace SmartBase.Tests.Integration;

/// <summary>
/// План подготовки коллекции к разделу: что UI покажет пользователю ДО того, как создание
/// события или задачи заведет коллекции столбцы. Главный инвариант — план совпадает с тем, что
/// реально сделают <c>AddCalendarRecord</c>/<c>AddTaskRecord</c>, и пустеет, как только
/// коллекция готова (тогда окно подтверждения не показывается вовсе).
/// Инварианты из docs/features/tasks.md и docs/ui/dialogs.md.
/// </summary>
public sealed class SectionSetupPlanTests
{
    private static string[] Created(RoleAdoptionPlan plan) => plan.FieldsToCreateIfAdopted.ToArray();

    // ── Календарь ─────────────────────────────────────────────────────────────

    [Fact]
    public void Calendar_plan_lists_columns_the_event_would_create()
    {
        using var db = new TempDatabase();
        var repo = db.Constructor;
        var col  = SampleData.AddCollection(repo, "События").Id!.Value;

        var plan = repo.PlanCalendarSetup(col);

        Assert.True(plan.ChangesCollection);
        Assert.Equal(["Дата", "Описание"], Created(plan));
        Assert.Empty(plan.Candidates);
    }

    /// <summary>Коллекция готова — спрашивать не о чем, следующее событие создается молча.</summary>
    [Fact]
    public void Calendar_plan_is_empty_after_first_event()
    {
        using var db = new TempDatabase();
        var repo = db.Constructor;
        var col  = SampleData.AddCollection(repo, "События").Id!.Value;

        repo.AddCalendarRecord(new CalendarRecord
        {
            CollectionId = col, Title = "Планерка",
            StartDt = new DateTime(2026, 4, 1, 10, 0, 0), EndDt = new DateTime(2026, 4, 1, 11, 0, 0),
        });

        Assert.False(repo.PlanCalendarSetup(col).ChangesCollection);
    }

    [Fact]
    public void Calendar_plan_offers_to_adopt_named_column()
    {
        using var db = new TempDatabase();
        var repo = db.Constructor;
        var col  = SampleData.AddCollection(repo, "События").Id!.Value;
        var mine = SampleData.AddField(repo, col, "Описание", "text", position: 1000).Id!.Value;

        var plan = repo.PlanCalendarSetup(col);

        Assert.Contains(plan.Candidates, c => c.FieldId == mine && c.Role == FieldRole.Description);
        // Подтянутому столбцу близнеца не заводим — создается только то, чего нет.
        Assert.Equal(["Дата"], Created(plan));
    }

    /// <summary>Поле-дата выбрано в окне события — новую «Дату» заводить не нужно.</summary>
    [Fact]
    public void Calendar_plan_respects_the_chosen_date_field()
    {
        using var db = new TempDatabase();
        var repo = db.Constructor;
        var col  = SampleData.AddCollection(repo, "События").Id!.Value;
        var when = SampleData.AddField(repo, col, "Когда", "date", position: 1000).Id!.Value;
        repo.SetDateFieldVisible(when, false);   // источником календаря не является

        Assert.Equal(["Дата", "Описание"], Created(repo.PlanCalendarSetup(col)));
        Assert.Equal(["Описание"], Created(repo.PlanCalendarSetup(col, preferredDateFieldId: when)));
    }

    /// <summary>
    /// Поле-дату перевели в «Текст», флаг `IsCalStart` на нем остался. Источником календаря оно
    /// быть не может (проекция строится по типу «Дата»), поэтому роль считается свободной и
    /// событие заведет настоящее поле-дату — вместо падения «Не удалось создать поле «Дата»».
    /// </summary>
    [Fact]
    public void Calendar_plan_ignores_stale_flag_on_text_field()
    {
        using var db = new TempDatabase();
        var repo = db.Constructor;
        var col  = SampleData.AddCollection(repo, "События").Id!.Value;
        var when = SampleData.AddField(repo, col, "Когда", "date", position: 1000).Id!.Value;

        var field = repo.GetFields(col).First(f => f.Id == when);
        Assert.True(field.IsCalStart);
        field.FieldType = "text";
        repo.UpdateField(field);

        Assert.Contains("Дата", Created(repo.PlanCalendarSetup(col)));

        var ev = repo.AddCalendarRecord(new CalendarRecord
        {
            CollectionId = col, Title = "Планерка",
            StartDt = new DateTime(2026, 4, 1, 10, 0, 0), EndDt = new DateTime(2026, 4, 1, 11, 0, 0),
        });
        Assert.Contains(repo.GetFields(col), f => f.Name == "Дата" && f.FieldType == "date" && f.IsCalStart);
        Assert.NotNull(ev.Id);
    }

    // ── Задачи ────────────────────────────────────────────────────────────────

    [Fact]
    public void Task_plan_lists_the_full_set_for_a_fresh_collection()
    {
        using var db = new TempDatabase();
        var repo = db.Constructor;
        var col  = SampleData.AddCollection(repo, "Заявки").Id!.Value;

        var plan = repo.PlanTaskSetup(col);

        Assert.True(plan.ChangesCollection);
        Assert.Equal(["Исполнитель", "Назначил", "Статус", "Дата", "Описание"], Created(plan));
    }

    /// <summary>
    /// Исполнителем становится существующее user-поле (так делает `EnsureTaskCollection`), и план
    /// обязан это показать: иначе окно обещало бы новый столбец, а раздел забрал бы «Менеджера».
    /// </summary>
    [Fact]
    public void Task_plan_shows_that_an_existing_user_field_becomes_the_executor()
    {
        using var db = new TempDatabase();
        var repo = db.Constructor;
        var col  = SampleData.AddCollection(repo, "Заявки").Id!.Value;
        var mgr  = SampleData.AddField(repo, col, "Менеджер", "user", position: 1000).Id!.Value;

        var plan = repo.PlanTaskSetup(col);

        Assert.Contains(plan.Candidates, c => c.FieldId == mgr && c.Role == FieldRole.TaskExec);
        Assert.DoesNotContain("Исполнитель", Created(plan));

        // И это действительно происходит — план не расходится с записью.
        repo.AddTaskRecord(col, "Позвонить", "Иванов Иван", "Петров Петр",
                           ConstructorRepository.TaskStatusPlanned, due: null);
        Assert.True(repo.GetFields(col).First(f => f.Id == mgr).IsTaskExec);
    }

    /// <summary>Перенос задачи в постороннюю коллекцию чужое user-поле не забирает.</summary>
    [Fact]
    public void Task_plan_without_exec_adoption_creates_its_own_field()
    {
        using var db = new TempDatabase();
        var repo = db.Constructor;
        var col  = SampleData.AddCollection(repo, "Заявки").Id!.Value;
        var mgr  = SampleData.AddField(repo, col, "Менеджер", "user", position: 1000).Id!.Value;

        var plan = repo.PlanTaskSetup(col, adoptExec: false);

        Assert.DoesNotContain(plan.Candidates, c => c.FieldId == mgr);
        Assert.Contains("Исполнитель", Created(plan));
    }

    [Fact]
    public void Task_plan_is_empty_after_first_task()
    {
        using var db = new TempDatabase();
        var repo = db.Constructor;
        var col  = SampleData.AddCollection(repo, "Заявки").Id!.Value;

        repo.AddTaskRecord(col, "Позвонить", "Иванов Иван", "Петров Петр",
                           ConstructorRepository.TaskStatusPlanned, due: null);

        Assert.False(repo.PlanTaskSetup(col).ChangesCollection);
        Assert.False(repo.PlanCalendarSetup(col).ChangesCollection);   // задача готовит и календарь
    }

    /// <summary>
    /// Удаленную роль правка задачи возвращает: раньше введенный статус было некуда писать и он
    /// молча пропадал. План видит пропажу (UI спросит), а доводка набора ее чинит.
    /// </summary>
    [Fact]
    public void Deleted_status_role_reappears_in_the_plan_and_is_restored()
    {
        using var db = new TempDatabase();
        var repo = db.Constructor;
        var col  = SampleData.AddCollection(repo, "Заявки").Id!.Value;
        repo.AddTaskRecord(col, "Позвонить", "Иванов Иван", "Петров Петр",
                           ConstructorRepository.TaskStatusPlanned, due: null);

        repo.DeleteField(repo.GetFields(col).First(f => f.IsTaskStatus).Id!.Value);
        Assert.Contains("Статус", Created(repo.PlanTaskSetup(col, adoptExec: false)));

        repo.EnsureTaskCollection(col, adoptExisting: false);

        Assert.Single(repo.GetFields(col), f => f.IsTaskStatus);
        Assert.False(repo.PlanTaskSetup(col, adoptExec: false).ChangesCollection);
    }
}
