using SmartBase.Core.Models;
using SmartBase.Database.Repositories;
using SmartBase.Utils;

namespace SmartBase.Tests.Integration;

/// <summary>
/// Какие коллекции подключены к календарю и задачам — по ним боковая панель раздела ограничивает
/// список, когда включена настройка «Только коллекции с датами в календаре» / «Только коллекции с
/// исполнителем» (<c>GetCalendarCollectionIds</c>, <c>GetTaskCollectionIds</c>). И подключение
/// новой коллекции, созданной в такой панели (<c>EnsureCalendarCollection</c>,
/// <c>EnsureTaskCollection</c>): без него коллекция пропала бы из списка в момент создания.
/// </summary>
public sealed class SectionCollectionsTests
{
    // Множество id — по возрастанию, чтобы сравнивать с ожидаемым списком.
    private static int[] Ids(HashSet<int> ids) => [.. ids.Order()];

    [Fact]
    public void Calendar_collections_are_those_with_a_visible_date_field()
    {
        using var db = new TempDatabase();
        var repo = db.Constructor;

        var shown   = SampleData.AddCollection(repo, "С датой").Id!.Value;
        SampleData.AddCalendarDateField(repo, shown);
        SampleData.AddCalendarDateField(repo, shown, "Вторая дата", position: 2000);   // одна коллекция — один id

        var hidden  = SampleData.AddCollection(repo, "Дата выключена").Id!.Value;
        var dateOff = SampleData.AddField(repo, hidden, "Срок", "date");

        var noDates = SampleData.AddCollection(repo, "Без дат").Id!.Value;
        SampleData.AddField(repo, noDates, "Текст");

        Assert.Equal(new[] { shown }, Ids(repo.GetCalendarCollectionIds()));

        // Тумблер «Отображать в календаре» подключает коллекцию, снятый — отключает.
        repo.SetDateFieldVisible(dateOff.Id!.Value, true);
        Assert.Equal(new[] { shown, hidden }, Ids(repo.GetCalendarCollectionIds()));
        repo.SetDateFieldVisible(dateOff.Id!.Value, false);
        Assert.Equal(new[] { shown }, Ids(repo.GetCalendarCollectionIds()));
    }

    [Fact]
    public void Task_collections_are_those_with_an_executor()
    {
        using var db = new TempDatabase();
        var repo = db.Constructor;

        var tasks = SampleData.AddCollection(repo, "Заявки").Id!.Value;
        var exec  = SampleData.AddField(repo, tasks, "Исполнитель", "user");
        repo.SetUserFieldTaskExec(exec.Id!.Value, true);

        // Пользовательское поле без тумблера и поле-дата в календаре задач не дают.
        var plain = SampleData.AddCollection(repo, "Сотрудники").Id!.Value;
        SampleData.AddField(repo, plain, "Руководитель", "user");
        var dated = SampleData.AddCollection(repo, "События").Id!.Value;
        SampleData.AddCalendarDateField(repo, dated);

        Assert.Equal(new[] { tasks }, Ids(repo.GetTaskCollectionIds()));

        repo.SetUserFieldTaskExec(exec.Id!.Value, false);
        Assert.Empty(repo.GetTaskCollectionIds());
    }

    [Fact]
    public void New_collection_is_connected_to_calendar_with_a_visible_date_field()
    {
        using var db = new TempDatabase();
        var repo = db.Constructor;
        var col  = SampleData.AddCollection(repo, "Новая").Id!.Value;
        Assert.DoesNotContain(col, repo.GetCalendarCollectionIds());

        var fieldId = repo.EnsureCalendarCollection(col);

        var field = Assert.Single(repo.GetFields(col));
        Assert.Equal(fieldId, field.Id);
        Assert.Equal("date", field.FieldType);
        Assert.Equal("Дата", field.Name);
        Assert.True(field.IsCalStart);
        Assert.Contains(col, repo.GetCalendarCollectionIds());

        // Повтор ничего не заводит: коллекция уже подключена.
        Assert.Equal(fieldId, repo.EnsureCalendarCollection(col));
        Assert.Single(repo.GetFields(col));
    }

    [Fact]
    public void Connecting_to_calendar_turns_on_an_existing_date_instead_of_adding_one()
    {
        using var db = new TempDatabase();
        var repo = db.Constructor;
        var col  = SampleData.AddCollection(repo, "Проекты").Id!.Value;
        var due  = SampleData.AddField(repo, col, "Дедлайн", "date");

        Assert.Equal(due.Id, repo.EnsureCalendarCollection(col));
        Assert.True(Assert.Single(repo.GetFields(col)).IsCalStart);
    }

    [Fact]
    public void New_collection_is_connected_to_tasks_with_executor_assigner_and_status()
    {
        using var db = new TempDatabase();
        var repo = db.Constructor;
        var col  = SampleData.AddCollection(repo, "Новая").Id!.Value;

        repo.EnsureTaskCollection(col);

        Assert.Contains(col, repo.GetTaskCollectionIds());
        var fields = repo.GetFields(col);
        Assert.Contains(fields, f => f.FieldType == "user" && f.IsTaskExec && f.Name == "Исполнитель");
        Assert.Contains(fields, f => f.IsTaskAssigner && f.Name == "Назначил");
        Assert.Contains(fields, f => f.IsTaskStatus && f.Name == "Статус");
    }

    [Fact]
    public void Connecting_without_field_rights_is_refused_and_leaves_the_collection_as_is()
    {
        using var db = new TempDatabase();
        var repo = db.Constructor;
        var col  = SampleData.AddCollection(repo, "Новая").Id!.Value;

        db.Permissions.SetMask(db.Permissions.CurrentUserId, null,
                               (int)(PermAction.All & ~PermAction.CreateField & ~PermAction.EditField));
        var me = db.Context.DbUsers.First(u => u.UserKey == CurrentUser.Key);
        me.IsAdmin    = false;
        me.IsApproved = true;
        db.Context.SaveChanges();
        db.Permissions.Reload(notify: false);

        // Боковая панель ловит отказ и оставляет коллекцию созданной, но не подключенной.
        Assert.Throws<PermissionDeniedException>(() => repo.EnsureCalendarCollection(col));
        Assert.Throws<PermissionDeniedException>(() => repo.EnsureTaskCollection(col));
        Assert.Empty(repo.GetFields(col));
        Assert.DoesNotContain(col, repo.GetCalendarCollectionIds());
        Assert.DoesNotContain(col, repo.GetTaskCollectionIds());
    }
}
