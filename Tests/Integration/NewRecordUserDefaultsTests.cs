using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using SmartBase.Core.Models;
using SmartBase.Database.Entities;
using SmartBase.Database.Repositories;
using SmartBase.Utils;

namespace SmartBase.Tests.Integration;

/// <summary>Правило новой записи общее для таблицы, задач и календаря; перенос новой записью не является.</summary>
public sealed class NewRecordUserDefaultsTests
{
    [SkippableTheory]
    [InlineData("sqlite", "table")]
    [InlineData("sqlite", "task")]
    [InlineData("sqlite", "dated-task")]
    [InlineData("sqlite", "calendar")]
    [InlineData("postgres", "table")]
    [InlineData("postgres", "task")]
    [InlineData("postgres", "dated-task")]
    [InlineData("postgres", "calendar")]
    public void Every_interactive_creation_path_initializes_user_fields_without_overriding_selected_roles(
        string profile, string entryPoint)
    {
        using var fixture = new MultiUserTestDatabase(profile);
        var repo = fixture.Repo;
        var collection = SampleData.AddCollection(repo, "Новая запись").Id!.Value;
        var roles = EnableTasks(repo, collection);
        var approver = SampleData.AddField(repo, collection, "Согласующий", "user", position: 5000).Id!.Value;
        var observer = SampleData.AddField(repo, collection, "Наблюдатель", "user", position: 6000).Id!.Value;
        var text = SampleData.AddField(repo, collection, "Комментарий", "text", position: 7000).Id!.Value;

        var id = Create(repo, collection, entryPoint);

        using var check = fixture.OpenContext();
        var values = new ConstructorRepository(check).GetFieldValues(id);
        Assert.Equal(CurrentUser.DisplayName, values[approver]);
        Assert.Equal(CurrentUser.DisplayName, values[observer]);
        Assert.Equal("", values.GetValueOrDefault(text, ""));
        if (entryPoint is "task" or "dated-task")
        {
            Assert.Equal("Иванов Иван", values[roles.Executor]);
            Assert.Equal("Петров Петр", values[roles.Assigner]);
            Assert.Equal(ConstructorRepository.TaskStatusPlanned, values[roles.Status]);
        }
        Assert.Single(check.DbRecords.Where(record => record.CollectionId == collection));
        MultiUserTestDatabase.AssertHealthy(check);
    }

    [SkippableTheory]
    [InlineData("sqlite", false)]
    [InlineData("sqlite", true)]
    [InlineData("postgres", false)]
    [InlineData("postgres", true)]
    public void Explicitly_empty_task_roles_replace_defaults_without_clearing_unrelated_user_fields(string profile, bool withDue)
    {
        using var fixture = new MultiUserTestDatabase(profile);
        var repo = fixture.Repo;
        var collection = SampleData.AddCollection(repo, "Пустой выбор ролей").Id!.Value;
        var roles = EnableTasks(repo, collection);
        var approver = SampleData.AddField(repo, collection, "Согласующий", "user", position: 5000).Id!.Value;

        var task = repo.AddTaskRecord(collection, "Без исполнителя", "", "", ConstructorRepository.TaskStatusPlanned,
            withDue ? new DateValue(new DateTime(2026, 10, 28), new DateTime(2026, 10, 28), true) : null);

        using var check = fixture.OpenContext();
        var values = new ConstructorRepository(check).GetFieldValues(task.Id!.Value);
        Assert.Equal("", values.GetValueOrDefault(roles.Executor, ""));
        Assert.Equal("", values.GetValueOrDefault(roles.Assigner, ""));
        Assert.Equal(CurrentUser.DisplayName, values[approver]);
        Assert.Empty(check.TaskEvents);
        MultiUserTestDatabase.AssertHealthy(check);
    }

    [SkippableTheory]
    [InlineData("sqlite")]
    [InlineData("postgres")]
    public void Failed_calendar_defaults_roll_back_header_schema_projection_and_history_then_allow_retry(string profile)
    {
        using var fixture = new MultiUserTestDatabase(profile);
        var repo = fixture.Repo;
        var context = fixture.Manager.Context;
        var collection = SampleData.AddCollection(repo, "Отказ создания").Id!.Value;
        var approver = SampleData.AddField(repo, collection, "Согласующий", "user").Id!.Value;
        var fieldCount = context.DbFields.Count();
        var logCount = context.ChangeLog.LongCount();
        var calendar = Calendar(collection);
        const string failure = "Отказ начального значения";
        void FailDefault(object? _, EntityTrackedEventArgs args)
        {
            if (args.Entry is { State: EntityState.Added, Entity: DbFieldValueEntity value }
                && value.FieldId == approver) throw new InvalidOperationException(failure);
        }

        context.ChangeTracker.Tracked += FailDefault;
        try
        {
            Assert.Equal(failure, Assert.Throws<InvalidOperationException>(() => repo.AddCalendarRecord(calendar)).Message);
        }
        finally { context.ChangeTracker.Tracked -= FailDefault; }

        Assert.Null(calendar.Id);
        Assert.Equal(collection, calendar.CollectionId);
        Assert.Equal(0, calendar.FieldId);
        using (var check = fixture.OpenContext())
        {
            Assert.Empty(check.DbRecords);
            Assert.Empty(check.DbFieldValues);
            Assert.Empty(check.CalendarIndex);
            Assert.Empty(check.TaskEvents);
            Assert.Equal(fieldCount, check.DbFields.Count());
            Assert.Equal(logCount, check.ChangeLog.LongCount());
            MultiUserTestDatabase.AssertHealthy(check);
        }

        repo.AddCalendarRecord(calendar);
        using var retried = fixture.OpenContext();
        var header = Assert.Single(retried.DbRecords);
        Assert.Equal(calendar.Id, header.Id);
        Assert.Equal(CurrentUser.DisplayName, Assert.Single(retried.DbFieldValues.Where(value => value.FieldId == approver)).Value);
        Assert.Equal(header.Id, Assert.Single(retried.CalendarIndex).RecordId);
        MultiUserTestDatabase.AssertHealthy(retried);
    }

    [SkippableTheory]
    [InlineData("sqlite", false)]
    [InlineData("sqlite", true)]
    [InlineData("postgres", false)]
    [InlineData("postgres", true)]
    public void Failed_task_defaults_roll_back_record_values_projection_events_and_history(string profile, bool withDue)
    {
        using var fixture = new MultiUserTestDatabase(profile);
        var repo = fixture.Repo;
        var context = fixture.Manager.Context;
        var collection = SampleData.AddCollection(repo, "Отказ задачи").Id!.Value;
        EnableTasks(repo, collection);
        var approver = SampleData.AddField(repo, collection, "Согласующий", "user", position: 5000).Id!.Value;
        var logCount = context.ChangeLog.LongCount();
        const string failure = "Отказ значения задачи";
        void FailDefault(object? _, EntityTrackedEventArgs args)
        {
            if (args.Entry is { State: EntityState.Added, Entity: DbFieldValueEntity value }
                && value.FieldId == approver) throw new InvalidOperationException(failure);
        }

        context.ChangeTracker.Tracked += FailDefault;
        try
        {
            Assert.Equal(failure, Assert.Throws<InvalidOperationException>(() =>
                Create(repo, collection, withDue ? "dated-task" : "task")).Message);
        }
        finally { context.ChangeTracker.Tracked -= FailDefault; }

        using (var check = fixture.OpenContext())
        {
            Assert.Empty(check.DbRecords);
            Assert.Empty(check.DbFieldValues);
            Assert.Empty(check.CalendarIndex);
            Assert.Empty(check.TaskEvents);
            Assert.Equal(logCount, check.ChangeLog.LongCount());
            MultiUserTestDatabase.AssertHealthy(check);
        }

        var id = Create(repo, collection, withDue ? "dated-task" : "task");
        using var retried = fixture.OpenContext();
        Assert.Equal(id, Assert.Single(retried.DbRecords).Id);
        Assert.Equal(CurrentUser.DisplayName, Assert.Single(retried.DbFieldValues.Where(value => value.FieldId == approver)).Value);
        Assert.Single(retried.TaskEvents);
        MultiUserTestDatabase.AssertHealthy(retried);
    }

    [SkippableTheory]
    [InlineData("sqlite")]
    [InlineData("postgres")]
    public void Calendar_move_within_a_database_preserves_empty_and_historical_users(string profile)
    {
        using var fixture = new MultiUserTestDatabase(profile);
        var source = UserCollection(fixture.Repo, "Источник", targetOnly: false);
        var target = UserCollection(fixture.Repo, "Цель", targetOnly: true);
        var calendar = Calendar(source.Collection);
        fixture.Repo.AddCalendarRecord(calendar);
        fixture.Repo.SetFieldValue(calendar.Id!.Value, source.Empty, "");
        fixture.Repo.SetFieldValue(calendar.Id.Value, source.Historical, "Исторический пользователь");
        var oldId = calendar.Id.Value;

        calendar.CollectionId = target.Collection;
        fixture.Repo.UpdateCalendarRecord(calendar);

        using var check = fixture.OpenContext();
        Assert.False(check.DbRecords.Any(record => record.Id == oldId));
        AssertPreservedUsers(new ConstructorRepository(check), calendar.Id!.Value, target);
        MultiUserTestDatabase.AssertHealthy(check);
    }

    [SkippableTheory]
    [InlineData("sqlite")]
    [InlineData("postgres")]
    public void Cross_database_calendar_transfer_does_not_replace_empty_users_with_the_person_moving_it(string profile)
    {
        using var sourceFixture = new MultiUserTestDatabase(profile);
        using var targetFixture = new MultiUserTestDatabase(profile);
        var source = UserCollection(sourceFixture.Repo, "Источник", targetOnly: false);
        var target = UserCollection(targetFixture.Repo, "Цель", targetOnly: true);
        var original = Calendar(source.Collection);
        sourceFixture.Repo.AddCalendarRecord(original);
        sourceFixture.Repo.SetFieldValue(original.Id!.Value, source.Empty, "");
        sourceFixture.Repo.SetFieldValue(original.Id.Value, source.Historical, "Исторический пользователь");

        // Та же граница создания цели, что у переноса в окне календаря. Это существующая
        // запись: пустые значения и поля, которых нет в источнике, не получают defaults.
        var moved = Calendar(target.Collection);
        targetFixture.Repo.AddCalendarRecordForTransfer(moved);
        targetFixture.Repo.CopyFieldValuesByName(sourceFixture.Repo, original.Id.Value, source.Collection,
            moved.Id!.Value, target.Collection, createMissingFields: false, excludeTargetFieldId: moved.FieldId);
        sourceFixture.Repo.DeleteCalendarRecord(original.Id.Value);

        using var sourceCheck = sourceFixture.OpenContext();
        using var targetCheck = targetFixture.OpenContext();
        Assert.Empty(sourceCheck.DbRecords);
        AssertPreservedUsers(new ConstructorRepository(targetCheck), moved.Id.Value, target);
        MultiUserTestDatabase.AssertHealthy(sourceCheck);
        MultiUserTestDatabase.AssertHealthy(targetCheck);
    }

    private static (int Executor, int Assigner, int Status) EnableTasks(ConstructorRepository repo, int collection)
    {
        var executor = SampleData.AddField(repo, collection, "Исполнитель", "user", position: 1000).Id!.Value;
        repo.SetUserFieldTaskExec(executor, true);
        var roles = repo.TaskRoleFields(collection);
        return (executor, roles.AssignerId!.Value, roles.StatusId!.Value);
    }

    private static CalendarRecord Calendar(int collection) => new()
    {
        CollectionId = collection, Title = "Общая запись",
        StartDt = new DateTime(2026, 10, 28, 9, 0, 0),
        EndDt = new DateTime(2026, 10, 28, 10, 0, 0),
    };

    private static int Create(ConstructorRepository repo, int collection, string entryPoint) => entryPoint switch
    {
        "table" => repo.AddRecord(new DbRecord { CollectionId = collection, Title = "Общая запись", Position = 1000 }).Id!.Value,
        "calendar" => repo.AddCalendarRecord(Calendar(collection)).Id!.Value,
        "task" or "dated-task" => repo.AddTaskRecord(collection, "Общая запись", "Иванов Иван", "Петров Петр",
            ConstructorRepository.TaskStatusPlanned, entryPoint == "dated-task"
                ? new DateValue(new DateTime(2026, 10, 28), new DateTime(2026, 10, 28), true) : null).Id!.Value,
        _ => throw new ArgumentOutOfRangeException(nameof(entryPoint)),
    };

    private sealed record Users(int Collection, int Empty, int Historical, int? TargetOnly);

    private static Users UserCollection(ConstructorRepository repo, string name, bool targetOnly)
    {
        var collection = SampleData.AddCollection(repo, name).Id!.Value;
        var empty = SampleData.AddField(repo, collection, "Согласующий", "user", position: 1000).Id!.Value;
        var historical = SampleData.AddField(repo, collection, "Наблюдатель", "user", position: 2000).Id!.Value;
        var other = targetOnly ? SampleData.AddField(repo, collection, "Только в цели", "user", position: 3000).Id : null;
        return new Users(collection, empty, historical, other);
    }

    private static void AssertPreservedUsers(ConstructorRepository repo, int record, Users fields)
    {
        Assert.Equal(fields.Collection, repo.GetRecord(record)!.CollectionId);
        var values = repo.GetFieldValues(record);
        Assert.Equal("", values.GetValueOrDefault(fields.Empty, ""));
        Assert.Equal("Исторический пользователь", values[fields.Historical]);
        Assert.Equal("", values.GetValueOrDefault(fields.TargetOnly!.Value, ""));
    }
}
