using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using SmartBase.Core.Models;
using SmartBase.Database.Entities;
using SmartBase.Database.Repositories;
using SmartBase.Utils;
using Xunit.Abstractions;

namespace SmartBase.Tests.Integration;

/// <summary>
/// Два независимых контекста чередуют чтение, удаление соседом и сохранение редактора.
/// После устаревшей правки тот же контекст продолжает обычную работу без Clear/Detach:
/// отложенные журнал и события не должны протечь в следующую успешную операцию.
/// </summary>
public sealed class ConcurrentStaleRecoveryTests(ITestOutputHelper output)
{
    [SkippableTheory]
    [InlineData("sqlite", "record", "text")]
    [InlineData("sqlite-shared", "record", "text")]
    [InlineData("postgres", "record", "text")]
    [InlineData("sqlite", "record", "date")]
    [InlineData("sqlite-shared", "record", "date")]
    [InlineData("postgres", "record", "date")]
    [InlineData("sqlite", "field", "text")]
    [InlineData("sqlite-shared", "field", "text")]
    [InlineData("postgres", "field", "text")]
    [InlineData("sqlite", "field", "date")]
    [InlineData("sqlite-shared", "field", "date")]
    [InlineData("postgres", "field", "date")]
    public void Context_remains_usable_without_flushing_ghost_history_after_a_stale_edit(
        string profile, string deleted, string type)
    {
        using var fixture = new MultiUserTestDatabase(profile);
        using var seed = CurrentUser.ActAs("stale-recovery-seed", "Подготовка");
        const string healthyTitle = "Соседняя действующая запись";
        const string healthyDateName = "Действующая дата";
        const string healthyExecutorName = "Действующий исполнитель";
        const string oldRecipient = "Первый получатель";
        const string newRecipient = "Второй получатель";
        fixture.Manager.Context.DbUsers.AddRange(
            new DbUserEntity { UserKey = "stale-recipient-before", DisplayName = oldRecipient, IsApproved = true,
                CreatedAt = DateTime.UtcNow.ToString("o") },
            new DbUserEntity { UserKey = "stale-recipient-after", DisplayName = newRecipient, IsApproved = true,
                CreatedAt = DateTime.UtcNow.ToString("o") });
        fixture.Manager.Context.SaveChanges();

        var staleCollection = SampleData.AddCollection(fixture.Repo, "Устаревший редактор").Id!.Value;
        var staleField = (type == "date"
            ? SampleData.AddCalendarDateField(fixture.Repo, staleCollection, "Исчезающее свойство")
            : SampleData.AddField(fixture.Repo, staleCollection, "Исчезающее свойство")).Id!.Value;
        var staleRecord = SampleData.AddRecord(fixture.Repo, staleCollection, "Исчезающая цель").Id!.Value;
        fixture.Repo.SetFieldValue(staleRecord, staleField, type == "date" ? "2026-10-26" : "До удаления");

        // Отдельная коллекция: удаление поля не выключает роль исправного исполнителя.
        var healthyCollection = SampleData.AddCollection(fixture.Repo, "Продолжение работы").Id!.Value;
        var healthyDate = SampleData.AddCalendarDateField(fixture.Repo, healthyCollection, healthyDateName).Id!.Value;
        var healthyExecutor = SampleData.AddField(fixture.Repo, healthyCollection, healthyExecutorName, "user", 2000).Id!.Value;
        Assert.True(fixture.Repo.SetUserFieldTaskExec(healthyExecutor, true));
        var healthyRecord = SampleData.AddRecord(fixture.Repo, healthyCollection, healthyTitle).Id!.Value;
        fixture.Repo.SetFieldValue(healthyRecord, healthyDate, "2026-11-01");
        fixture.Repo.SetFieldValue(healthyRecord, healthyExecutor, oldRecipient);

        using var editor = fixture.OpenContext();
        var repository = new ConstructorRepository(editor);
        Assert.NotNull(repository.GetRecord(staleRecord));
        Assert.NotNull(editor.DbFields.Find(staleField));
        Assert.Single(editor.DbFieldValues.Where(value => value.RecordId == staleRecord && value.FieldId == staleField));

        // Удаление уже завершилось, но редактор сохраняет собственный прежний трекер.
        using (var neighbour = fixture.OpenContext())
        using (CurrentUser.ActAs("stale-recovery-neighbour", "Сосед"))
        {
            var other = new ConstructorRepository(neighbour);
            if (deleted == "record") other.DeleteRecord(staleRecord);
            else other.DeleteField(staleField);
        }

        List<ChangeLogEntity> historyBefore;
        List<TaskEventEntity> eventsBefore;
        string healthyCreatedAt;
        string healthyCreatedBy;
        string healthyUpdatedAt;
        string? staleAuditAfterDelete;
        using (var before = fixture.OpenContext())
        {
            historyBefore = before.ChangeLog.AsNoTracking().OrderBy(row => row.Id).ToList();
            eventsBefore = before.TaskEvents.AsNoTracking().OrderBy(row => row.Id).ToList();
            Assert.Single(eventsBefore); // Реальное уведомление, которое следующая правка обязана сохранить.
            var healthy = before.DbRecords.AsNoTracking().Single(record => record.Id == healthyRecord);
            healthyCreatedAt = healthy.CreatedAt;
            healthyCreatedBy = healthy.CreatedBy;
            healthyUpdatedAt = healthy.UpdatedAt;
            staleAuditAfterDelete = before.DbRecords.AsNoTracking().Where(record => record.Id == staleRecord)
                .Select(record => record.UpdatedAt).SingleOrDefault();
        }

        using var actor = CurrentUser.ActAs("stale-recovery-editor", "Редактор после удаления");
        Exception? staleError = null;
        try { repository.SetFieldValue(staleRecord, staleField, type == "date" ? "2026-10-27" : "После удаления"); }
        catch (Exception error) when (IsExpectedStaleFailure(error)) { staleError = error; }
        // Допускаем только известный отказ устаревшей цели либо безопасный no-op. Ни cleanup
        // тестом, ни повтор через новый контекст здесь не помогли бы настоящему редактору.
        output.WriteLine($"{profile}/{deleted}/{type}: устаревшая правка — {staleError?.GetType().Name ?? "no-op"}");
        repository.SetFieldValue(healthyRecord, healthyDate, "2026-11-02");
        repository.SetFieldValue(healthyRecord, healthyExecutor, newRecipient);

        using var check = fixture.OpenContext();
        MultiUserTestDatabase.AssertHealthy(check);
        Assert.Equal("2026-11-02", check.DbFieldValues.AsNoTracking()
            .Single(value => value.RecordId == healthyRecord && value.FieldId == healthyDate).Value);
        Assert.Equal(newRecipient, check.DbFieldValues.AsNoTracking()
            .Single(value => value.RecordId == healthyRecord && value.FieldId == healthyExecutor).Value);
        var calendar = Assert.Single(check.CalendarIndex.AsNoTracking()
            .Where(item => item.RecordId == healthyRecord && item.FieldId == healthyDate));
        Assert.StartsWith("2026-11-02", calendar.StartIso);
        var healthyAfter = check.DbRecords.AsNoTracking().Single(record => record.Id == healthyRecord);
        Assert.Equal(healthyCreatedAt, healthyAfter.CreatedAt);
        Assert.Equal(healthyCreatedBy, healthyAfter.CreatedBy);
        Assert.NotEqual(healthyUpdatedAt, healthyAfter.UpdatedAt);
        Assert.Equal(CurrentUser.DisplayName, healthyAfter.UpdatedBy);

        Assert.False(check.DbFieldValues.Any(value => value.RecordId == staleRecord && value.FieldId == staleField));
        Assert.False(check.CalendarIndex.Any(item => item.RecordId == staleRecord && item.FieldId == staleField));
        if (deleted == "record") Assert.False(check.DbRecords.Any(record => record.Id == staleRecord));
        else
        {
            Assert.False(check.DbFields.Any(field => field.Id == staleField));
            Assert.Equal(staleAuditAfterDelete, check.DbRecords.AsNoTracking()
                .Single(record => record.Id == staleRecord).UpdatedAt);
        }

        var history = check.ChangeLog.AsNoTracking().OrderBy(row => row.Id).ToList();
        var oldHistoryIds = historyBefore.Select(row => row.Id).ToHashSet();
        Assert.Equal(JsonSerializer.Serialize(historyBefore),
            JsonSerializer.Serialize(history.Where(row => oldHistoryIds.Contains(row.Id)).ToList()));
        var newHistory = history.Where(row => !oldHistoryIds.Contains(row.Id)).ToList();
        output.WriteLine("Новые строки журнала: " + JsonSerializer.Serialize(newHistory));
        Assert.All(newHistory, row =>
        {
            Assert.Equal("update", row.Action);
            Assert.Equal("record", row.TargetType);
            Assert.Equal(healthyTitle, row.TargetName);
            Assert.Equal(CurrentUser.Key, row.UserKey);
        });
        Assert.Equal(2, newHistory.Count);
        Assert.Single(newHistory.Where(row => row.Detail?.Contains(healthyDateName, StringComparison.Ordinal) == true));
        Assert.Single(newHistory.Where(row => row.Detail?.Contains(healthyExecutorName, StringComparison.Ordinal) == true));

        var events = check.TaskEvents.AsNoTracking().OrderBy(row => row.Id).ToList();
        var oldEventIds = eventsBefore.Select(row => row.Id).ToHashSet();
        Assert.Equal(JsonSerializer.Serialize(eventsBefore),
            JsonSerializer.Serialize(events.Where(row => oldEventIds.Contains(row.Id)).ToList()));
        var assignment = Assert.Single(events.Where(row => !oldEventIds.Contains(row.Id)));
        Assert.Equal(TaskEventKinds.Assigned, assignment.Kind);
        Assert.Equal(healthyRecord, assignment.RecordId);
        Assert.Equal(healthyExecutor, assignment.FieldId);
        Assert.Equal(healthyExecutor, assignment.ExecFieldId);
        Assert.Equal("stale-recipient-after", assignment.RecipientKey);
        Assert.Equal(CurrentUser.Key, assignment.ActorKey);
        Assert.Equal(oldRecipient, assignment.OldValue);
        Assert.Equal(newRecipient, assignment.NewValue);
    }

    private static bool IsExpectedStaleFailure(Exception error) => error.GetBaseException() switch
    {
        InvalidOperationException conflict => conflict.Message.StartsWith(
            "The instance of entity type 'DbFieldValueEntity' cannot be tracked because another instance with the same key value",
            StringComparison.Ordinal),
        PostgresException { SqlState: "23503" } => true,
        SqliteException { SqliteErrorCode: 19, SqliteExtendedErrorCode: 787 } => true,
        _ => false,
    };
}
