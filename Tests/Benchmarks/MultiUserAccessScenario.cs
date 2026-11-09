using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using SmartBase.Core.Models;
using SmartBase.Database;
using SmartBase.Database.Entities;
using SmartBase.Database.Repositories;
using SmartBase.Utils;

namespace SmartBase.Tests.Benchmarks;

/// <summary>Одновременная работа читателей и авторов с разными правами.
/// Ожидаемый отказ считается проверенным запретом, а не успешно выполненной записью.</summary>
internal sealed class MultiUserAccessScenario : IMultiUserScenario
{
    private readonly MultiUserFixture _fixture;
    private readonly int _collection;
    private readonly int _field;
    private readonly int _protectedRecord;
    private readonly ConcurrentDictionary<int, ExpectedRecord> _acknowledged = new();
    private readonly ConcurrentDictionary<(int Client, int Round, string Action), byte> _denied = new();

    private MultiUserAccessScenario(MultiUserFixture fixture, int collection, int field, int protectedRecord)
    {
        _fixture = fixture;
        _collection = collection;
        _field = field;
        _protectedRecord = protectedRecord;
    }

    internal static IMultiUserScenario Create(MultiUserFixture fixture)
    {
        using var manager = DatabaseManager.Open(901, "Разные права участников", fixture.Connection,
            provider: fixture.Options.Provider == "postgres" ? "postgres" : "sqlite");
        var repo = manager.Constructor;
        var collection = repo.AddCollection(new DbCollection { Name = "Права под нагрузкой", Position = 3000 }).Id!.Value;
        var field = repo.AddField(new DbField
            { CollectionId = collection, Name = "Значение", FieldType = "text", Position = 1000 }).Id!.Value;
        var protectedRecord = repo.AddRecord(new DbRecord
            { CollectionId = collection, Title = "Защищенная запись", Position = 1000 }).Id!.Value;
        repo.SetFieldValue(protectedRecord, field, "Не изменять");
        manager.Context.ChangeTracker.Clear();
        var users = manager.Context.DbUsers.AsNoTracking().Where(u => u.UserKey.StartsWith("client-"))
            .ToDictionary(u => u.UserKey, u => u.Id);
        for (var client = 0; client < fixture.Options.Clients; client++)
        {
            manager.Context.DbPermissions.Add(new DbPermissionEntity
            {
                UserId = users[MultiUserFixture.UserKey(client)], CollectionId = collection,
                ActionsMask = client % 2 == 0 ? (int)PermAction.All : (int)PermAction.ViewCollection,
            });
        }
        manager.Context.SaveChanges();
        return new MultiUserAccessScenario(fixture, collection, field, protectedRecord);
    }

    public void Execute(AppDbContext context, ConstructorRepository repo, MultiUserClient client, int round)
    {
        if (client.Id % 2 == 0)
        {
            var title = $"Разрешено {client.Id:D4}:{round:D4}";
            var record = client.Measure("access-allowed-create", () => repo.AddRecord(new DbRecord
            {
                CollectionId = _collection, Title = title, Position = checked((client.Id + 2) * 1000),
            }));
            var id = record.Id!.Value;
            var expected = new ExpectedRecord(id, title, "", CurrentUser.DisplayName, MultiUserFixture.UserKey(client.Id));
            _acknowledged[id] = expected;
            var value = $"Значение {client.Id}:{round}";
            client.Measure("access-allowed-edit", () => repo.SetFieldValue(id, _field, value));
            _acknowledged[id] = expected with { Value = value };
        }
        else
        {
            client.Measure("access-denied-create", () => ExpectDenied(client.Id, round, "create", () =>
                repo.AddRecord(new DbRecord { CollectionId = _collection, Title = $"Запрещено {client.Id}:{round}" })));
            client.Measure("access-denied-edit", () => ExpectDenied(client.Id, round, "edit", () =>
                repo.SetFieldValue(_protectedRecord, _field, $"Запрещено {client.Id}:{round}")));
            client.Measure("access-denied-delete", () => ExpectDenied(client.Id, round, "delete", () =>
                repo.DeleteRecord(_protectedRecord)));
        }

        client.Measure("access-read", () =>
        {
            if (repo.Permissions is null || !repo.Permissions.Can(PermAction.ViewCollection, _collection))
                throw new InvalidOperationException("Участник потерял предусмотренное право просмотра");
            var page = repo.GetRecordsPageAfter(_collection, null, null, 20);
            if (page.Count == 0 || page.Count > 20 || page.Any(r => r.CollectionId != _collection))
                throw new InvalidOperationException("Просмотр разрешенной коллекции вернул неверную страницу");
            repo.GetFieldValuesBatch(page.Select(r => r.Id!.Value).ToList());
        });
        context.ChangeTracker.Clear();
    }

    private void ExpectDenied(int client, int round, string action, Action write)
    {
        try { write(); }
        catch (PermissionDeniedException)
        {
            if (!_denied.TryAdd((client, round, action), 0))
                throw new InvalidOperationException("Один ожидаемый отказ учтен повторно");
            return;
        }
        throw new InvalidOperationException("Запрещенная операция прошла без отказа: " + action);
    }

    public IEnumerable<MultiUserCheck> Verify(MultiUserClient[] clients)
    {
        using var context = new AppDbContext(_fixture.Connection);
        var expected = _acknowledged.ToDictionary(p => p.Key, p => p.Value);
        var allowedClients = (_fixture.Options.Clients + 1) / 2;
        var deniedClients = _fixture.Options.Clients / 2;
        var plannedWrites = allowedClients * _fixture.Options.Rounds;
        var plannedDenials = deniedClients * _fixture.Options.Rounds * 3;
        var actual = context.DbRecords.AsNoTracking()
            .Where(r => r.CollectionId == _collection && r.Id != _protectedRecord).ToList();
        var values = context.DbFieldValues.AsNoTracking().Where(v => v.FieldId == _field).ToList();
        yield return new("Права: разрешенные авторы завершили ожидаемое число созданий",
            expected.Count == plannedWrites && actual.Count == plannedWrites,
            $"авторов {allowedClients}, факт записей {actual.Count}, подтверждено {expected.Count}, запланировано {plannedWrites}");
        yield return new("Права: все запрещенные создания, правки и удаления получили ожидаемый отказ",
            _denied.Count == plannedDenials,
            $"читателей без права записи {deniedClients}, отказов {_denied.Count}, ожидалось {plannedDenials}; это не успешные записи");
        yield return new("Права: защищенная запись осталась на месте с прежним значением",
            context.DbRecords.Any(r => r.Id == _protectedRecord && r.CollectionId == _collection && r.Title == "Защищенная запись")
            && values.Count(v => v.RecordId == _protectedRecord && v.Value == "Не изменять") == 1);
        yield return new("Права: нет неподтвержденных или потерянных разрешенных записей",
            expected.Keys.ToHashSet().SetEquals(actual.Select(r => r.Id)));
        var exact = true;
        foreach (var record in actual)
        {
            if (!expected.TryGetValue(record.Id, out var wanted)) { exact = false; continue; }
            exact &= record.Title == wanted.Title && record.CreatedBy == wanted.Author && record.UpdatedBy == wanted.Author
                && (values.FirstOrDefault(v => v.RecordId == record.Id)?.Value ?? "") == wanted.Value;
        }
        yield return new("Права: разрешенные значения и авторы совпадают с подтвержденными командами", exact);
        yield return new("Права: число ячеек совпадает с защищенным значением и подтвержденными правками",
            values.Count == 1 + expected.Values.Count(v => v.Value.Length > 0));
        var deniedKeys = Enumerable.Range(0, _fixture.Options.Clients).Where(id => id % 2 != 0)
            .Select(MultiUserFixture.UserKey).ToHashSet();
        var deniedNames = Enumerable.Range(0, _fixture.Options.Clients).Where(id => id % 2 != 0)
            .Select(id => $"Участник {id:D4}").ToHashSet();
        yield return new("Права: у читателей нет ни записей, ни событий журнала о выполненной правке",
            actual.All(r => !deniedNames.Contains(r.CreatedBy))
            && !context.ChangeLog.Any(l => deniedKeys.Contains(l.UserKey) && l.TargetType == "record"));
        var logged = context.ChangeLog.Where(l => l.TargetType == "record").Select(l => l.UserKey).Distinct().ToHashSet();
        yield return new("Права: журнал содержит разрешенных авторов с подтвержденными записями",
            expected.Values.Select(r => r.UserKey).Distinct().All(logged.Contains));
    }

    private sealed record ExpectedRecord(int Id, string Title, string Value, string Author, string UserKey);
}
