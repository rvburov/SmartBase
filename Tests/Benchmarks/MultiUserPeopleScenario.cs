using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using SmartBase.Core.Models;
using SmartBase.Database;
using SmartBase.Database.Entities;
using SmartBase.Database.Repositories;
using SmartBase.Utils;

namespace SmartBase.Tests.Benchmarks;

/// <summary>Все участники работают с данными независимо от сохраненных старых масок доступа.</summary>
internal sealed class MultiUserPeopleScenario : IMultiUserScenario
{
    private readonly MultiUserFixture _fixture;
    private readonly int _collection;
    private readonly int _field;
    private readonly ConcurrentDictionary<int, ExpectedRecord> _acknowledged = new();

    private MultiUserPeopleScenario(MultiUserFixture fixture, int collection, int field)
    { _fixture = fixture; _collection = collection; _field = field; }

    internal static IMultiUserScenario Create(MultiUserFixture fixture)
    {
        using var manager = DatabaseManager.Open(901, "Участники общей базы", fixture.Connection,
            provider: fixture.Options.Provider == "postgres" ? "postgres" : "sqlite");
        var collection = manager.Constructor.AddCollection(new DbCollection { Name = "Участники под нагрузкой", Position = 3000 }).Id!.Value;
        var field = manager.Constructor.AddField(new DbField
            { CollectionId = collection, Name = "Значение", FieldType = "text", Position = 1000 }).Id!.Value;
        foreach (var user in manager.Context.DbUsers.Where(u => u.UserKey.StartsWith("client-")))
        {
            user.IsAdmin = false; user.IsApproved = false; user.AccessDecided = true;
            manager.Context.DbPermissions.Add(new DbPermissionEntity
                { UserId = user.Id, CollectionId = collection, ActionsMask = 0 });
        }
        manager.Context.SaveChanges();
        return new MultiUserPeopleScenario(fixture, collection, field);
    }

    public void Execute(AppDbContext context, ConstructorRepository repo, MultiUserClient client, int round)
    {
        var title = $"Участник {client.Id:D4}:{round:D4}";
        var record = client.Measure("people-create", () => repo.AddRecord(new DbRecord
            { CollectionId = _collection, Title = title, Position = checked((client.Id + 2) * 1000) }));
        var id = record.Id!.Value;
        var value = $"Значение {client.Id}:{round}";
        client.Measure("people-edit", () => repo.SetFieldValue(id, _field, value));
        _acknowledged[id] = new(id, title, value, CurrentUser.DisplayName, MultiUserFixture.UserKey(client.Id));
        client.Measure("people-read", () =>
        {
            var page = repo.GetRecordsPageAfter(_collection, null, null, 20);
            if (page.Count == 0 || page.Count > 20 || page.Any(r => r.CollectionId != _collection))
                throw new InvalidOperationException("Общая коллекция вернула неверную страницу");
            repo.GetFieldValuesBatch(page.Select(r => r.Id!.Value).ToList());
        });
        context.ChangeTracker.Clear();
    }

    public IEnumerable<MultiUserCheck> Verify(MultiUserClient[] clients)
    {
        using var context = new AppDbContext(_fixture.Connection);
        var expected = _acknowledged.ToDictionary(p => p.Key, p => p.Value);
        var actual = context.DbRecords.AsNoTracking().Where(r => r.CollectionId == _collection).ToList();
        var values = context.DbFieldValues.AsNoTracking().Where(v => v.FieldId == _field).ToDictionary(v => v.RecordId, v => v.Value);
        yield return new("Все участники создали и изменили записи при старых нулевых масках",
            actual.Count == _fixture.Options.Clients * _fixture.Options.Rounds && expected.Keys.ToHashSet().SetEquals(actual.Select(r => r.Id)));
        yield return new("Значения и авторы совпадают с подтвержденными командами",
            actual.All(record => expected.TryGetValue(record.Id, out var wanted)
                && record.Title == wanted.Title && record.CreatedBy == wanted.Author && record.UpdatedBy == wanted.Author
                && values.GetValueOrDefault(record.Id) == wanted.Value));
        var logged = context.ChangeLog.Where(l => l.TargetType == "record").Select(l => l.UserKey).Distinct().ToHashSet();
        yield return new("История сохраняет авторство всех участников",
            expected.Values.Select(r => r.UserKey).Distinct().All(logged.Contains));
    }

    private sealed record ExpectedRecord(int Id, string Title, string Value, string Author, string UserKey);
}
