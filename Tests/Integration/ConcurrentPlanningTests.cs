using Microsoft.EntityFrameworkCore;
using SmartBase.Core.Models;
using SmartBase.Database;
using SmartBase.Database.Repositories;

namespace SmartBase.Tests.Integration;

/// <summary>
/// Задачи и календарь при одновременных командах разных сессий: проверяются карточки,
/// постраничное чтение, роли коллекции, полные даты, проекция и счетчики дней.
/// </summary>
public sealed class ConcurrentPlanningTests
{
    private const int Clients = 8;
    private static readonly DateTime Begin = new(2026, 11, 1);

    private static DateValue Due(int client, int extraDays = 0) => new(
        Begin.AddDays(client + extraDays).AddHours(10),
        Begin.AddDays(client + extraDays).AddHours(11), false);

    [SkippableTheory]
    [InlineData("sqlite")]
    [InlineData("sqlite-shared")]
    [InlineData("postgres")]
    public void Concurrent_tasks_keep_their_own_executor_stage_due_and_paged_card(string profile)
    {
        using var f = new MultiUserTestDatabase(profile);
        var collection = SampleData.AddCollection(f.Repo, "Совместное планирование").Id!.Value;
        var executor = f.Repo.EnsureTaskCollection(collection);
        var ids = new int[Clients];

        MultiUserTestDatabase.RunClients(f, Clients, (repo, _, client) =>
        {
            var task = repo.AddTaskRecord(collection, $"Задача {client}", $"Исполнитель {client}",
                $"Назначил {client}", ConstructorRepository.TaskStatusActive, Due(client));
            ids[client] = task.Id!.Value;
            task.Title = $"Измененная задача {client}";
            task.Description = $"Описание участника {client}";
            task.Executor = $"Новый исполнитель {client}";
            task.Status = client % 2 == 0 ? ConstructorRepository.TaskStatusDone : ConstructorRepository.TaskStatusActive;
            task.DueStart = Due(client, 10).Start;
            task.DueEnd = Due(client, 10).End;
            repo.UpdateTaskRecord(task);

            var actual = Assert.Single(repo.GetTaskRecords(new HashSet<int> { collection }, Clients * 4)
                .Where(t => t.Id == task.Id));
            AssertTask(actual, client, collection, executor);
            var events = repo.GetCalendarRecordsByRangeCapped(Begin, Begin.AddMonths(1),
                new HashSet<int> { collection }, Clients * 4);
            var own = Assert.Single(events.Where(e => e.Id == task.Id));
            Assert.Equal(task.DueStart, own.StartDt);
            Assert.Equal(task.DueEnd, own.EndDt);
        });

        using var check = f.OpenContext();
        var repository = new ConstructorRepository(check);
        var tasks = repository.GetTaskRecords(new HashSet<int> { collection }, Clients * 4);
        Assert.Equal(Clients, tasks.Count);
        Assert.Equal(Clients, ids.Distinct().Count());
        for (var client = 0; client < Clients; client++)
            AssertTask(Assert.Single(tasks.Where(t => t.Id == ids[client])), client, collection, executor);

        var stats = repository.GetTaskCollectionStats(new HashSet<int> { collection })[collection];
        Assert.Equal(Clients, stats.Total);
        Assert.Equal(Clients / 2, stats.Done);
        var all = new ConstructorRepository.TaskView("All", "", [], Begin);
        var size = repository.CountTaskView(collection, stats, all);
        Assert.Equal(Clients, size.Total);
        Assert.Equal(Clients, size.Dated);
        var page = repository.GetTaskPageIds(collection, stats, all, dated: true, skip: 0, take: Clients * 2);
        Assert.Equal(ids.Order(), page.Order());
        var cards = repository.GetTaskRecordsByIds(collection, page);
        Assert.Equal(ids.Order(), cards.Select(t => t.Id!.Value).Order());
        AssertDayCountsMatchProjection(check, repository, collection);
        MultiUserTestDatabase.AssertHealthy(check);
    }

    [SkippableTheory]
    [InlineData("sqlite")]
    [InlineData("sqlite-shared")]
    [InlineData("postgres")]
    public void Concurrent_task_moves_keep_roles_and_do_not_mix_source_and_target_dates(string profile)
    {
        using var f = new MultiUserTestDatabase(profile);
        var source = SampleData.AddCollection(f.Repo, "Исходные задачи").Id!.Value;
        var target = SampleData.AddCollection(f.Repo, "Целевые задачи").Id!.Value;
        f.Repo.EnsureTaskCollection(source);
        var targetExecutor = f.Repo.EnsureTaskCollection(target);
        var sourceIds = new int[Clients];
        var targetIds = new int[Clients];
        for (var client = 0; client < Clients; client++)
            sourceIds[client] = f.Repo.AddTaskRecord(source, $"Перенос {client}", $"Исполнитель {client}",
                $"Назначил {client}", ConstructorRepository.TaskStatusActive, Due(client)).Id!.Value;
        f.Manager.Context.ChangeTracker.Clear();

        MultiUserTestDatabase.RunClients(f, Clients, (repo, _, client) =>
        {
            var task = Assert.Single(repo.GetTaskRecords(new HashSet<int> { source }, Clients * 4)
                .Where(t => t.Id == sourceIds[client]));
            task.CollectionId = target;
            task.Title = $"Перенесенная задача {client}";
            task.DueStart = Due(client, 10).Start;
            task.DueEnd = Due(client, 10).End;
            repo.UpdateTaskRecord(task);
            targetIds[client] = task.Id!.Value;
            Assert.NotEqual(sourceIds[client], task.Id);
            Assert.Equal(targetExecutor, task.ExecFieldId);
        });

        using var check = f.OpenContext();
        var repository = new ConstructorRepository(check);
        Assert.Empty(check.DbRecords.Where(r => r.CollectionId == source));
        Assert.Equal(Clients, targetIds.Distinct().Count());
        var cards = repository.GetTaskRecords(new HashSet<int> { target }, Clients * 4);
        Assert.Equal(Clients, cards.Count);
        var sourceSet = sourceIds.ToHashSet();
        Assert.False(check.DbRecords.Any(r => sourceSet.Contains(r.Id)));
        for (var client = 0; client < Clients; client++)
        {
            var card = Assert.Single(cards.Where(t => t.Id == targetIds[client]));
            Assert.Equal($"Перенесенная задача {client}", card.Title);
            Assert.Equal(target, card.CollectionId);
            Assert.Equal(targetExecutor, card.ExecFieldId);
            Assert.Equal($"Исполнитель {client}", card.Executor);
            Assert.Equal($"Назначил {client}", card.Assigner);
            Assert.Equal(ConstructorRepository.TaskStatusActive, card.Status);
            Assert.Equal(Due(client, 10).Start, card.DueStart);
            Assert.Equal(Due(client, 10).End, card.DueEnd);
            Assert.NotNull(card.DueFieldId);
            var row = Assert.Single(check.CalendarIndex.AsNoTracking().Where(i => i.RecordId == targetIds[client]));
            Assert.Equal(target, row.CollectionId);
            Assert.Equal(card.DueFieldId, row.FieldId);
        }
        AssertDayCountsMatchProjection(check, repository, target);
        MultiUserTestDatabase.AssertHealthy(check);
    }

    [SkippableTheory]
    [InlineData("sqlite")]
    [InlineData("sqlite-shared")]
    [InlineData("postgres")]
    public void Calendar_reader_survives_parallel_retime_clear_delete_and_create(string profile)
    {
        using var f = new MultiUserTestDatabase(profile);
        var collection = SampleData.AddCollection(f.Repo, "Совместный календарь").Id!.Value;
        var date = SampleData.AddCalendarDateField(f.Repo, collection).Id!.Value;
        var initial = new int[Clients];
        var added = new int[Clients];
        for (var client = 0; client < Clients; client++)
        {
            var record = new CalendarRecord
            {
                CollectionId = collection, FieldId = date, Title = $"Событие {client}",
                StartDt = Due(client).Start, EndDt = Due(client).End,
            };
            f.Repo.AddCalendarRecord(record);
            initial[client] = record.Id!.Value;
        }
        f.Manager.Context.ChangeTracker.Clear();

        // Один участник читает пользовательские окна, остальные выполняют три разных
        // этапа записи. Барьер отделяет этапы, но не прячет конкуренцию внутри этапа.
        using var phase = new Barrier(Clients);
        MultiUserTestDatabase.RunClients(f, Clients, (repo, _, client) =>
        {
            try
            {
                for (var stage = 0; stage < 3; stage++)
                {
                    Assert.True(phase.SignalAndWait(TimeSpan.FromSeconds(40)), "Начало этапа календаря не достигнуто");
                    if (client == 0)
                    {
                        var events = repo.GetCalendarRecordsByRangeCapped(Begin, Begin.AddMonths(1),
                            new HashSet<int> { collection }, Clients * 4);
                        Assert.Equal(events.Count, events.Select(e => (e.Id, e.FieldId)).Distinct().Count());
                        Assert.All(events, e => Assert.Equal(collection, e.CollectionId));
                        var counts = repo.GetCalendarDayCountsByRange(Begin, Begin.AddMonths(1),
                            new HashSet<int> { collection });
                        Assert.All(counts, c => Assert.InRange(c.Count, 1, Clients * 2));
                    }
                    else if (stage == 0)
                    {
                        repo.UpdateCalendarRecord(new CalendarRecord
                        {
                            Id = initial[client], CollectionId = collection, FieldId = date,
                            Title = $"Другая дата {client}", StartDt = Due(client, 10).Start, EndDt = Due(client, 10).End,
                        });
                    }
                    else if (stage == 1)
                    {
                        if (client % 2 == 0) repo.DeleteCalendarRecord(initial[client]);
                        else repo.SetFieldValue(initial[client], date, "");
                    }
                    else
                    {
                        var record = new CalendarRecord
                        {
                            CollectionId = collection, FieldId = date, Title = $"Новое событие {client}",
                            StartDt = Due(client, 20).Start, EndDt = Due(client, 20).End,
                        };
                        repo.AddCalendarRecord(record);
                        added[client] = record.Id!.Value;
                    }
                    Assert.True(phase.SignalAndWait(TimeSpan.FromSeconds(40)), "Конец этапа календаря не достигнут");
                }
            }
            finally { phase.RemoveParticipant(); }
        });

        using var check = f.OpenContext();
        var repository = new ConstructorRepository(check);
        var eventsAfter = repository.GetCalendarRecordsByRangeCapped(Begin, Begin.AddMonths(1),
            new HashSet<int> { collection }, Clients * 4);
        var expected = added.Skip(1).Append(initial[0]).ToHashSet();
        Assert.Equal(Clients, eventsAfter.Count);
        Assert.True(expected.SetEquals(eventsAfter.Select(e => e.Id!.Value)));
        for (var client = 1; client < Clients; client++)
        {
            Assert.False(check.CalendarIndex.Any(i => i.RecordId == initial[client]));
            if (client % 2 == 0) Assert.False(check.DbRecords.Any(r => r.Id == initial[client]));
            else Assert.True(check.DbRecords.Any(r => r.Id == initial[client]));
        }
        AssertDayCountsMatchProjection(check, repository, collection);
        MultiUserTestDatabase.AssertHealthy(check);
    }

    private static void AssertTask(TaskRecord task, int client, int collection, int executor)
    {
        Assert.Equal($"Измененная задача {client}", task.Title);
        Assert.Equal($"Описание участника {client}", task.Description);
        Assert.Equal(collection, task.CollectionId);
        Assert.Equal(executor, task.ExecFieldId);
        Assert.Equal($"Новый исполнитель {client}", task.Executor);
        Assert.Equal($"Назначил {client}", task.Assigner);
        Assert.Equal(client % 2 == 0 ? ConstructorRepository.TaskStatusDone : ConstructorRepository.TaskStatusActive,
            task.Status);
        Assert.Equal(Due(client, 10).Start, task.DueStart);
        Assert.Equal(Due(client, 10).End, task.DueEnd);
        Assert.False(task.AllDay);
    }

    private static void AssertDayCountsMatchProjection(AppDbContext check, ConstructorRepository repo, int collection)
    {
        var expected = check.CalendarIndex.AsNoTracking().Where(i => i.CollectionId == collection).ToList()
            .GroupBy(i => i.StartIso[..10]).OrderBy(g => g.Key).Select(g => (g.Key, g.Count())).ToList();
        Assert.Equal(expected, repo.GetCalendarDayCountsByRange(Begin, Begin.AddMonths(1),
            new HashSet<int> { collection }));
    }
}
