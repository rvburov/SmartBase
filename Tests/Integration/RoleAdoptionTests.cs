using SmartBase.Core.Models;
using SmartBase.Database.Repositories;

namespace SmartBase.Tests.Integration;

/// <summary>
/// Подтягивание ролей по имени столбца: включение раздела (задачи/календарь) доводит коллекцию
/// до полного набора ролей, а одноимённые столбцы либо получают роль (данные едут в раздел),
/// либо остаются нетронутыми, и роли достаются новым полям. Совпадение — ТОЛЬКО по имени;
/// исключение одно — «Дата», где проекция календаря строится по полям типа «Дата».
/// Инварианты из docs/features/tasks.md и docs/architecture/data-types/description.md.
/// </summary>
public sealed class RoleAdoptionTests
{
    private static int Exec(ConstructorRepository repo, int collectionId, bool adoptByName = true)
    {
        var exec = SampleData.AddField(repo, collectionId, "Исполнитель", "user", position: 1000);
        repo.SetUserFieldTaskExec(exec.Id!.Value, true, adoptByName);
        return exec.Id!.Value;
    }

    private static DbField? ByName(ConstructorRepository repo, int collectionId, string name) =>
        repo.GetFields(collectionId).FirstOrDefault(f => f.Name == name);

    // ── Полный набор ролей ────────────────────────────────────────────────────

    [Fact]
    public void Task_toggle_creates_full_role_set()
    {
        using var db = new TempDatabase();
        var repo = db.Constructor;
        var col  = SampleData.AddCollection(repo, "Заявки").Id!.Value;

        Exec(repo, col);

        var fields = repo.GetFields(col);
        Assert.Contains(fields, f => f.IsTaskAssigner && f.Name == "Назначил");
        Assert.Contains(fields, f => f.IsTaskStatus   && f.Name == "Статус");
        Assert.Contains(fields, f => f.IsCalStart     && f.FieldType == "date");
        Assert.Contains(fields, f => f.IsDescription);
    }

    /// <summary>Задача без срока всё равно оставляет коллекции поле-дату — срок можно вписать потом.</summary>
    [Fact]
    public void Task_without_due_still_creates_date_field()
    {
        using var db = new TempDatabase();
        var repo = db.Constructor;
        var col  = SampleData.AddCollection(repo, "Задачи").Id!.Value;

        repo.AddTaskRecord(col, "Позвонить", "Иванов Иван", "Петров Пётр",
                           ConstructorRepository.TaskStatusPlanned, due: null);

        Assert.Contains(repo.GetFields(col), f => f.IsCalStart && f.FieldType == "date");
    }

    // ── План: что нашли и что создадим ────────────────────────────────────────

    [Fact]
    public void Plan_lists_matching_columns_and_fields_to_create()
    {
        using var db = new TempDatabase();
        var repo = db.Constructor;
        var col  = SampleData.AddCollection(repo).Id!.Value;
        var exec = SampleData.AddField(repo, col, "Исполнитель", "user", position: 1000).Id!.Value;
        // Тип столбцов роли не мешает: решает только имя.
        SampleData.AddField(repo, col, "Статус", "text", position: 2000);
        SampleData.AddField(repo, col, "Описание", "text", position: 3000);

        var plan = repo.PlanRoles(col, ConstructorRepository.TaskRoles, exec);

        Assert.True(plan.NeedsConfirmation);
        Assert.Equal(["Статус", "Описание"], plan.Candidates.Select(c => c.FieldName).ToArray());
        // Отказ заведёт поля под ВСЕ роли без носителя — и те, что нашлись по имени тоже
        // (тогда с суффиксом, потому что имя занято).
        Assert.Equal(["Назначил", "Статус 2", "Дата", "Описание 2"],
                     plan.FieldsToCreateIfDeclined.ToArray());
    }

    /// <summary>Совпадений нет — спрашивать не о чем, окно подтверждения не нужно.</summary>
    [Fact]
    public void Plan_without_matches_needs_no_confirmation()
    {
        using var db = new TempDatabase();
        var repo = db.Constructor;
        var col  = SampleData.AddCollection(repo).Id!.Value;
        var exec = SampleData.AddField(repo, col, "Исполнитель", "user", position: 1000).Id!.Value;

        Assert.False(repo.PlanRoles(col, ConstructorRepository.TaskRoles, exec).NeedsConfirmation);
    }

    // ── Подтвердить / отменить ────────────────────────────────────────────────

    [Fact]
    public void Adopting_marks_existing_columns_and_pulls_their_data()
    {
        using var db = new TempDatabase();
        var repo = db.Constructor;
        var col  = SampleData.AddCollection(repo, "Заявки").Id!.Value;
        var assigner = SampleData.AddField(repo, col, "Назначил", "user", position: 2000).Id!.Value;
        var status   = SampleData.AddField(repo, col, "Статус", "status", position: 3000).Id!.Value;

        var exec = Exec(repo, col, adoptByName: true);

        var fields = repo.GetFields(col);
        Assert.True(fields.First(f => f.Id == assigner).IsTaskAssigner);
        Assert.True(fields.First(f => f.Id == status).IsTaskStatus);
        // Новых близнецов не завели.
        Assert.Single(fields, f => f.Name == "Назначил");
        Assert.Single(fields, f => f.Name == "Статус");

        // Данные подтянулись: значения этих столбцов читает раздел задач.
        var rec = SampleData.AddRecord(repo, col, "Заявка").Id!.Value;
        repo.SetFieldValue(rec, exec, "Иванов Иван");
        repo.SetFieldValue(rec, assigner, "Петров Пётр");
        repo.SetFieldValue(rec, status, ConstructorRepository.TaskStatusActive);

        var task = Assert.Single(repo.GetTaskRecords());
        Assert.Equal("Петров Пётр", task.Assigner);
        Assert.Equal(ConstructorRepository.TaskStatusActive, task.Status);
    }

    [Fact]
    public void Declining_leaves_columns_alone_and_creates_separate_fields()
    {
        using var db = new TempDatabase();
        var repo = db.Constructor;
        var col  = SampleData.AddCollection(repo, "Заявки").Id!.Value;
        var assigner = SampleData.AddField(repo, col, "Назначил", "user", position: 2000).Id!.Value;
        var status   = SampleData.AddField(repo, col, "Статус", "status", position: 3000).Id!.Value;

        Exec(repo, col, adoptByName: false);

        var fields = repo.GetFields(col);
        Assert.False(fields.First(f => f.Id == assigner).IsTaskAssigner);
        Assert.False(fields.First(f => f.Id == status).IsTaskStatus);

        var newAssigner = ByName(repo, col, "Назначил 2");
        var newStatus   = ByName(repo, col, "Статус 2");
        Assert.NotNull(newAssigner);
        Assert.NotNull(newStatus);
        Assert.True(newAssigner!.IsTaskAssigner);
        Assert.True(newStatus!.IsTaskStatus);
        Assert.Equal("status", newStatus.FieldType);
    }

    // ── Переключение на другой столбец ────────────────────────────────────────

    /// <summary>
    /// Роль уже размечена, но пользователь переименовал ДРУГОЙ столбец в «Статус» и снова
    /// включает раздел: план обязан предложить переключение, а подтверждение — перенести роль
    /// (у прежнего носителя она снимается, двух статусов у коллекции не бывает).
    /// </summary>
    [Fact]
    public void Role_moves_to_the_renamed_column_on_confirmation()
    {
        using var db = new TempDatabase();
        var repo = db.Constructor;
        var col  = SampleData.AddCollection(repo, "Заявки").Id!.Value;
        var exec = Exec(repo, col);

        var oldStatus = repo.GetFields(col).First(f => f.IsTaskStatus);
        // Прежний носитель переименован, а его имя занял другой столбец.
        oldStatus.Name = "Старый статус";
        repo.UpdateField(oldStatus);
        var renamed = SampleData.AddField(repo, col, "Статус", "status", position: 9000).Id!.Value;

        var plan = repo.PlanRoles(col, ConstructorRepository.TaskRoles, exec);
        Assert.Contains(plan.Candidates, c => c.FieldId == renamed && c.Role == FieldRole.TaskStatus);

        repo.EnsureRoles(col, ConstructorRepository.TaskRoles, adoptByName: true, excludeFieldId: exec);

        var fields = repo.GetFields(col);
        Assert.True(fields.First(f => f.Id == renamed).IsTaskStatus);
        Assert.False(fields.First(f => f.Id == oldStatus.Id).IsTaskStatus);
        Assert.Single(fields, f => f.IsTaskStatus);
    }

    // ── Ограничение роли «Дата» ───────────────────────────────────────────────

    /// <summary>
    /// Имя решает — кроме «Даты»: календарная проекция строится только по полям типа «Дата»,
    /// поэтому текстовый столбец с таким именем сроком стать не может и заводится своё поле.
    /// </summary>
    [Fact]
    public void Date_role_skips_column_of_wrong_type()
    {
        using var db = new TempDatabase();
        var repo = db.Constructor;
        var col  = SampleData.AddCollection(repo, "Заявки").Id!.Value;
        var text = SampleData.AddField(repo, col, "Дата", "text", position: 2000).Id!.Value;
        var exec = SampleData.AddField(repo, col, "Исполнитель", "user", position: 1000).Id!.Value;

        var plan = repo.PlanRoles(col, ConstructorRepository.TaskRoles, exec);
        Assert.DoesNotContain(plan.Candidates, c => c.FieldId == text);

        repo.SetUserFieldTaskExec(exec, true);

        var fields = repo.GetFields(col);
        Assert.False(fields.First(f => f.Id == text).IsCalStart);
        var created = ByName(repo, col, "Дата 2");
        Assert.NotNull(created);
        Assert.Equal("date", created!.FieldType);
        Assert.True(created.IsCalStart);
    }

    // ── Календарь ─────────────────────────────────────────────────────────────

    [Fact]
    public void Calendar_toggle_ensures_description_role()
    {
        using var db = new TempDatabase();
        var repo = db.Constructor;
        var col  = SampleData.AddCollection(repo, "События").Id!.Value;
        var date = SampleData.AddField(repo, col, "Когда", "date", position: 1000).Id!.Value;

        // Первое поле-дата уже видно в календаре — выключаем и включаем заново, как это
        // делает тумблер в диалоге свойства.
        repo.SetDateFieldVisible(date, false);
        repo.SetDateFieldVisible(date, true);

        Assert.NotNull(repo.DescriptionFieldId(col));
    }

    [Fact]
    public void Calendar_toggle_can_decline_existing_description_column()
    {
        using var db = new TempDatabase();
        var repo = db.Constructor;
        var col  = SampleData.AddCollection(repo, "События").Id!.Value;
        var date = SampleData.AddField(repo, col, "Когда", "date", position: 1000).Id!.Value;
        var mine = SampleData.AddField(repo, col, "Описание", "text", position: 2000).Id!.Value;
        repo.SetDateFieldVisible(date, false);

        var plan = repo.PlanRoles(col, ConstructorRepository.CalendarRoles, date);
        Assert.Contains(plan.Candidates, c => c.FieldId == mine && c.Role == FieldRole.Description);
        Assert.Equal(["Описание 2"], plan.FieldsToCreateIfDeclined.ToArray());

        repo.SetDateFieldVisible(date, true, adoptByName: false);

        Assert.False(repo.GetFields(col).First(f => f.Id == mine).IsDescription);
        Assert.Equal("Описание 2", repo.GetFields(col).First(f => f.IsDescription).Name);
    }
}
