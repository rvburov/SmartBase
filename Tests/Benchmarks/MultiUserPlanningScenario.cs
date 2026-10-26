using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SmartBase.Core.Models;
using SmartBase.Database;
using SmartBase.Database.Repositories;

namespace SmartBase.Tests.Benchmarks;

/// <summary>
/// Задачи и календарь на общем наборе данных: запись/переназначение/смена стадии и срока,
/// чтение тех же страниц и счетчиков, которыми пользуются соответствующие разделы.
/// </summary>
internal sealed class MultiUserPlanningScenario : IMultiUserScenario
{
    private static readonly DateTime Begin = new(2026, 11, 1);
    private readonly MultiUserFixture _fixture;
    private readonly int _collection;
    private readonly int _executor;
    private readonly int _date;
    private readonly ConcurrentDictionary<int, ExpectedTask> _acknowledged = new();

    private MultiUserPlanningScenario(MultiUserFixture fixture, int collection, int executor, int date)
    {
        _fixture = fixture;
        _collection = collection;
        _executor = executor;
        _date = date;
    }

    internal static IMultiUserScenario Create(MultiUserFixture fixture)
    {
        using var manager = DatabaseManager.Open(901, "Совместное планирование", fixture.Connection,
            provider: fixture.Options.Provider == "postgres" ? "postgres" : "sqlite");
        var repo = manager.Constructor;
        var collection = repo.AddCollection(new DbCollection { Name = "Общие задачи", Position = 3000 }).Id!.Value;
        var executor = repo.EnsureTaskCollection(collection);
        var date = repo.GetFields(collection).First(f => f.FieldType == "date" && f.IsCalStart).Id!.Value;
        return new MultiUserPlanningScenario(fixture, collection, executor, date);
    }

    public void Execute(AppDbContext context, ConstructorRepository repo, MultiUserClient client, int round)
    {
        var title = $"planning-client-{client.Id:D4}-round-{round:D4}";
        var actor = $"Участник {client.Id:D4}";
        var initialExecutor = User((client.Id + 1) % _fixture.Options.Clients);
        var initialDue = Due(client.Id, round, 0);
        var task = client.Measure("task-create", () => repo.AddTaskRecord(_collection, title,
            initialExecutor, actor, ConstructorRepository.TaskStatusActive, initialDue));
        var id = task.Id!.Value;
        _acknowledged[id] = new(id, title, initialExecutor, actor, ConstructorRepository.TaskStatusActive,
            initialDue.Start, initialDue.End, "", actor);

        var finalDue = Due(client.Id, round, 30);
        task.Title = title + "-edited";
        task.Executor = User((client.Id + 2) % _fixture.Options.Clients);
        task.Description = $"Описание {client.Id}:{round}";
        task.Status = client.Id % 2 == 0 ? ConstructorRepository.TaskStatusDone : ConstructorRepository.TaskStatusActive;
        task.DueStart = finalDue.Start;
        task.DueEnd = finalDue.End;
        client.Measure("task-update", () => repo.UpdateTaskRecord(task));
        _acknowledged[id] = new(id, task.Title, task.Executor, task.Assigner, task.Status,
            finalDue.Start, finalDue.End, task.Description, actor);

        client.Measure("task-page", () =>
        {
            var active = new HashSet<int> { _collection };
            var stats = repo.GetTaskCollectionStats(active)[_collection];
            var view = new ConstructorRepository.TaskView("All", "", [], Begin);
            var page = repo.GetTaskPageIds(_collection, stats, view, dated: true, skip: 0, take: 20);
            if (page.Count == 0 || page.Count > 20 || page.Distinct().Count() != page.Count)
                throw new InvalidOperationException("Страница задач пуста или содержит повторения");
            var cards = repo.GetTaskRecordsByIds(_collection, page);
            if (!page.ToHashSet().SetEquals(cards.Select(c => c.Id!.Value))
                || cards.Any(c => c.CollectionId != _collection || c.ExecFieldId != _executor))
                throw new InvalidOperationException("Карточки задач не соответствуют странице или ролям коллекции");
        });
        client.Measure("task-calendar", () =>
        {
            var active = new HashSet<int> { _collection };
            var events = repo.GetCalendarRecordsByRangeCapped(Begin, Begin.AddMonths(3), active, 20);
            var wrong = events.Where(e => e.CollectionId != _collection || e.FieldId != _date).ToList();
            if (wrong.Count > 0)
                throw new InvalidOperationException($"Календарь вернул чужую коллекцию/поле: ожидается CollectionId={_collection}, " +
                    $"FieldId={_date}; неверных строк {wrong.Count}. Первые 8: " + JsonSerializer.Serialize(wrong.Take(8)
                        .Select(e => new { e.Id, e.FieldId, e.CollectionId, e.StartDt, e.EndDt, e.AllDay })));
            var duplicates = events.GroupBy(e => (e.Id, e.FieldId)).Where(group => group.Count() > 1).ToList();
            if (duplicates.Count > 0)
                throw new InvalidOperationException($"Календарь вернул повторяющиеся ключи (Id, FieldId): групп {duplicates.Count}. " +
                    "Первые 8 строк повторов: " + JsonSerializer.Serialize(duplicates.SelectMany(group => group).Take(8)
                        .Select(e => new { e.Id, e.FieldId, e.CollectionId, e.StartDt, e.EndDt, e.AllDay })));
            var days = repo.GetCalendarDayCountsByRange(Begin, Begin.AddMonths(3), active);
            if (days.Any(d => d.Count <= 0))
                throw new InvalidOperationException("Счетчик календаря не положителен");
        });
        context.ChangeTracker.Clear();
    }

    public IEnumerable<MultiUserCheck> Verify(MultiUserClient[] clients)
    {
        using var context = new AppDbContext(_fixture.Connection);
        context.ApplyPerformancePragmas();
        var repo = new ConstructorRepository(context);
        var expected = _acknowledged.Values.ToDictionary(t => t.Id);
        var actual = context.DbRecords.AsNoTracking().Where(r => r.CollectionId == _collection).ToList();
        var cards = repo.GetTaskRecords(new HashSet<int> { _collection }, Math.Max(expected.Count + 100, 100));
        var cardsById = cards.ToDictionary(t => t.Id!.Value);
        yield return new("Планирование: все полные циклы создали ожидаемое число задач",
            expected.Count == _fixture.Options.Clients * _fixture.Options.Rounds
            && actual.Count == expected.Count,
            $"факт {actual.Count}, подтверждено {expected.Count}, запланировано {_fixture.Options.Clients * _fixture.Options.Rounds}");
        yield return new("Планирование: нет лишних или пропавших подтвержденных задач",
            expected.Keys.ToHashSet().SetEquals(actual.Select(r => r.Id))
            && expected.Keys.ToHashSet().SetEquals(cardsById.Keys));

        var exact = true;
        foreach (var wanted in expected.Values)
        {
            var row = actual.FirstOrDefault(r => r.Id == wanted.Id);
            if (row is null || !cardsById.TryGetValue(wanted.Id, out var card)) { exact = false; continue; }
            exact &= card.Title == wanted.Title && card.Description == wanted.Description
                && card.Executor == wanted.Executor && card.Assigner == wanted.Assigner
                && card.Status == wanted.Status && card.DueStart == wanted.Start && card.DueEnd == wanted.End
                && card.ExecFieldId == _executor && card.DueFieldId == _date && !card.AllDay
                && row.CreatedBy == wanted.Author && row.UpdatedBy == wanted.Author;
        }
        yield return new("Планирование: исполнители, назначившие, стадии, даты и авторы сохранены", exact);
        var stats = repo.GetTaskCollectionStats(new HashSet<int> { _collection })[_collection];
        yield return new("Планирование: счет задач и выполненных совпадает с подтвержденными карточками",
            stats.Total == expected.Count && stats.Done == expected.Values.Count(t => t.Status == ConstructorRepository.TaskStatusDone));
        var projection = context.CalendarIndex.AsNoTracking().Where(i => i.CollectionId == _collection).ToList();
        yield return new("Планирование: каждой подтвержденной дате соответствует календарная строка",
            projection.Count == expected.Count && projection.All(i => expected.TryGetValue(i.RecordId, out var wanted)
                && i.FieldId == _date && i.StartIso == wanted.Start.ToString("o") && i.EndIso == wanted.End.ToString("o")));
        var expectedDays = expected.Values.GroupBy(t => t.Start.ToString("yyyy-MM-dd"))
            .OrderBy(g => g.Key).Select(g => (Day: g.Key, Count: g.Count())).ToList();
        var actualDays = repo.GetCalendarDayCountsByRange(Begin, Begin.AddMonths(3), new HashSet<int> { _collection });
        yield return new("Планирование: пользовательские счетчики дней совпадают с ожидаемыми сроками",
            expectedDays.SequenceEqual(actualDays));
    }

    private static string User(int id) => $"Участник {id:D4}";

    private static DateValue Due(int client, int round, int extraDays)
    {
        var start = Begin.AddDays(client % 20 + round % 3 + extraDays).AddHours(10);
        return new(start, start.AddHours(1), false);
    }

    private sealed record ExpectedTask(int Id, string Title, string Executor, string Assigner,
        string Status, DateTime Start, DateTime End, string Description, string Author);
}
