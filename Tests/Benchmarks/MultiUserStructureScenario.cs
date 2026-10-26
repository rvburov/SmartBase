using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SmartBase.Core.Models;
using SmartBase.Database;
using SmartBase.Database.Repositories;

namespace SmartBase.Tests.Benchmarks;

/// <summary>
/// Много редакторов структуры общей коллекции: каждый меняет только свое свойство и
/// свою ветку дерева. Проверяется также очистка метаданных при удалении свойств/групп.
/// </summary>
internal sealed class MultiUserStructureScenario : IMultiUserScenario
{
    private readonly MultiUserFixture _fixture;
    private readonly int _rootGroup;
    private readonly string _initialFields;
    private readonly string _initialCollections;
    private readonly ConcurrentDictionary<string, ExpectedField> _fields = new();
    private readonly ConcurrentDictionary<string, ExpectedRecord> _records = new();
    private readonly ConcurrentDictionary<string, ExpectedGroup> _groups = new();
    private readonly ConcurrentDictionary<string, ExpectedCollection> _collections = new();
    private readonly ConcurrentDictionary<int, byte> _deletedFields = new();
    private readonly ConcurrentDictionary<int, byte> _deletedRecords = new();
    private readonly ConcurrentDictionary<int, byte> _deletedCollections = new();
    private int _createdFields;
    private int _createdGroups;
    private int _completedCycles;

    private MultiUserStructureScenario(MultiUserFixture fixture, int rootGroup,
        string initialFields, string initialCollections)
    {
        _fixture = fixture;
        _rootGroup = rootGroup;
        _initialFields = initialFields;
        _initialCollections = initialCollections;
        _groups["root"] = new(rootGroup, "structure-root", null);
    }

    internal static IMultiUserScenario Create(MultiUserFixture fixture)
    {
        using var manager = DatabaseManager.Open(901, "Структура общей базы", fixture.Connection,
            provider: fixture.Options.Provider == "postgres" ? "postgres" : "sqlite");
        var group = manager.Constructor.AddGroup(new DbGroup { Name = "structure-root", Position = 3000 });
        return new MultiUserStructureScenario(fixture, group.Id!.Value,
            FieldSnapshot(manager.Context, fixture), CollectionSnapshot(manager.Context, fixture));
    }

    public void Execute(AppDbContext context, ConstructorRepository repo, MultiUserClient client, int round)
    {
        var tag = $"structure-client-{client.Id:D4}-round-{round:D4}";
        var field = client.Measure("schema-field-create", () => repo.AddField(new DbField
        {
            CollectionId = _fixture.SourceCollection, Name = tag, FieldType = "text",
            Position = (client.Id + 3) * 1000,
        }));
        _fields[tag] = new(field.Id!.Value, field.Name);
        Interlocked.Increment(ref _createdFields);

        var record = client.Measure("schema-record-create", () => repo.AddRecord(new DbRecord
        {
            CollectionId = _fixture.SourceCollection, Title = tag, Position = (client.Id + 1) * 1000,
        }));
        _records[tag] = new(record.Id!.Value, tag, null, "");
        client.Measure("schema-value-write", () => repo.SetFieldValue(record.Id.Value, field.Id.Value, tag));
        _records[tag] = new(record.Id.Value, tag, field.Id.Value, tag);

        field.Name = tag + "-renamed";
        client.Measure("schema-field-rename", () => repo.UpdateField(field));
        _fields[tag] = new(field.Id.Value, field.Name);
        client.Measure("schema-field-style", () =>
        {
            repo.SetColumnStyle(_fixture.SourceCollection, [field.Id.Value.ToString()], StyleAspect.Background, "#112233");
            repo.SetCellStyle(_fixture.SourceCollection, [(record.Id.Value, field.Id.Value.ToString())],
                StyleAspect.Text, "#334455");
        });
        client.Measure("schema-read", () =>
        {
            var fields = repo.GetFields(_fixture.SourceCollection);
            var own = fields.SingleOrDefault(f => f.Id == field.Id);
            if (own is null || own.Name != field.Name || own.FieldType != "text")
                throw new InvalidOperationException("Чтение структуры потеряло подтвержденное свойство");
            var values = repo.GetFieldValues(record.Id.Value);
            if (values.GetValueOrDefault(field.Id.Value) != tag)
                throw new InvalidOperationException("Правка структуры потеряла значение своего свойства");
        });

        var group = client.Measure("schema-group-create", () => repo.AddGroup(new DbGroup
        {
            Name = tag + "-group", ParentId = _rootGroup, Position = (client.Id + 1) * 1000,
        }));
        _groups[tag] = new(group.Id!.Value, group.Name, _rootGroup);
        Interlocked.Increment(ref _createdGroups);
        group.Name = tag + "-group-renamed";
        client.Measure("schema-group-rename", () => repo.UpdateGroup(group));
        _groups[tag] = new(group.Id.Value, group.Name, _rootGroup);

        var collection = client.Measure("schema-collection-create", () => repo.AddCollection(new DbCollection
        {
            Name = tag + "-collection", GroupId = group.Id, Position = 1000,
        }));
        _collections[tag] = new(collection.Id!.Value, collection.Name, group.Id.Value);
        collection.Name = tag + "-collection-renamed";
        client.Measure("schema-collection-rename", () => repo.UpdateCollection(collection));
        _collections[tag] = new(collection.Id.Value, collection.Name, group.Id.Value);
        client.Measure("schema-collection-settings", () =>
        {
            repo.SaveUiSettings(collection.Id.Value, true, true, "table");
            repo.SaveColumnWidths(collection.Id.Value, new Dictionary<string, double> { ["__title__"] = 180 });
        });
        client.Measure("schema-hierarchy-read", () =>
        {
            var ownGroup = repo.GetAllGroups().SingleOrDefault(g => g.Id == group.Id);
            var ownCollection = repo.GetAllCollections().SingleOrDefault(c => c.Id == collection.Id);
            if (ownGroup?.ParentId != _rootGroup || ownGroup.Name != group.Name
                || ownCollection?.GroupId != group.Id || ownCollection.Name != collection.Name)
                throw new InvalidOperationException("Дерево базы потеряло подтвержденную группу или коллекцию");
        });

        client.Measure("schema-field-delete", () => repo.DeleteField(field.Id.Value));
        _fields.TryRemove(tag, out _);
        _deletedFields[field.Id.Value] = 0;
        _records[tag] = new(record.Id.Value, tag, null, "");
        client.Measure("schema-record-delete", () => repo.DeleteRecord(record.Id.Value));
        _records.TryRemove(tag, out _);
        _deletedRecords[record.Id.Value] = 0;
        client.Measure("schema-group-delete", () => repo.DeleteGroup(group.Id.Value));
        _groups.TryRemove(tag, out _);
        _collections.TryRemove(tag, out _);
        _deletedCollections[collection.Id.Value] = 0;
        Interlocked.Increment(ref _completedCycles);
        context.ChangeTracker.Clear();
    }

    public IEnumerable<MultiUserCheck> Verify(MultiUserClient[] clients)
    {
        using var context = new AppDbContext(_fixture.Connection);
        context.ApplyPerformancePragmas();
        var expectedRounds = _fixture.Options.Clients * _fixture.Options.Rounds;
        yield return new("Структура: все участники создали и удалили свое свойство и ветку дерева",
            _completedCycles == expectedRounds && _createdFields == expectedRounds && _createdGroups == expectedRounds,
            $"циклов {_completedCycles}, свойств {_createdFields}, групп {_createdGroups}; ожидалось {expectedRounds}");

        var fields = context.DbFields.AsNoTracking()
            .Where(f => f.CollectionId == _fixture.SourceCollection && f.Name.StartsWith("structure-client-"))
            .ToDictionary(f => f.Id);
        var expectedFields = _fields.Values.ToDictionary(f => f.Id);
        yield return new("Структура: оставшиеся свойства точно соответствуют подтвержденным командам",
            fields.Keys.ToHashSet().SetEquals(expectedFields.Keys)
            && fields.All(f => f.Value.Name == expectedFields[f.Key].Name && f.Value.FieldType == "text"));
        var groups = context.DbGroups.AsNoTracking().Where(g => g.Name.StartsWith("structure-")).ToDictionary(g => g.Id);
        var expectedGroups = _groups.Values.ToDictionary(g => g.Id);
        yield return new("Структура: группы и родители соответствуют подтвержденной иерархии",
            groups.Keys.ToHashSet().SetEquals(expectedGroups.Keys)
            && groups.All(g => g.Value.Name == expectedGroups[g.Key].Name && g.Value.ParentId == expectedGroups[g.Key].Parent));
        var collections = context.DbCollections.AsNoTracking().Where(c => c.Name.StartsWith("structure-client-")).ToDictionary(c => c.Id);
        var expectedCollections = _collections.Values.ToDictionary(c => c.Id);
        yield return new("Структура: коллекции не потеряны и не оказались в соседней группе",
            collections.Keys.ToHashSet().SetEquals(expectedCollections.Keys)
            && collections.All(c => c.Value.Name == expectedCollections[c.Key].Name
                && c.Value.GroupId == expectedCollections[c.Key].Group));

        var records = context.DbRecords.AsNoTracking().Where(r => r.CollectionId == _fixture.SourceCollection).ToDictionary(r => r.Id);
        var expectedRecords = _records.Values.ToDictionary(r => r.Id);
        var values = context.DbFieldValues.AsNoTracking().Where(v => v.Record.CollectionId == _fixture.SourceCollection).ToList();
        yield return new("Структура: записи и значения соответствуют подтвержденным изменениям схемы",
            records.Keys.ToHashSet().SetEquals(expectedRecords.Keys)
            && records.All(r => r.Value.Title == expectedRecords[r.Key].Title)
            && values.Count == expectedRecords.Values.Count(r => r.Field is not null)
            && values.All(v => expectedRecords.TryGetValue(v.RecordId, out var expected)
                && expected.Field == v.FieldId && expected.Value == v.Value));

        var deletedFieldKeys = _deletedFields.Keys.Except(expectedFields.Keys).Select(id => id.ToString()).ToHashSet();
        var deletedRecordIds = _deletedRecords.Keys.Except(expectedRecords.Keys).ToHashSet();
        var deletedCollectionIds = _deletedCollections.Keys.Except(expectedCollections.Keys).ToHashSet();
        var metadata = context.ColumnMeta.AsNoTracking().ToList();
        var cellStyles = context.CellStyles.AsNoTracking().ToList();
        yield return new("Структура: удаленные свойства и коллекции не оставили метаданных оформления",
            !metadata.Any(m => deletedCollectionIds.Contains(m.CollectionId)
                || m.CollectionId == _fixture.SourceCollection && deletedFieldKeys.Contains(m.Key))
            && !cellStyles.Any(s => deletedCollectionIds.Contains(s.CollectionId)
                || s.CollectionId == _fixture.SourceCollection && (deletedFieldKeys.Contains(s.Key) || deletedRecordIds.Contains(s.RecordId)))
            && !context.UiCollectionSettings.Any(s => deletedCollectionIds.Contains(s.CollectionId)));
        yield return new("Структура: исходные свойства и коллекции стенда не изменены",
            FieldSnapshot(context, _fixture) == _initialFields
            && CollectionSnapshot(context, _fixture) == _initialCollections);
    }

    private static string FieldSnapshot(AppDbContext context, MultiUserFixture fixture) =>
        JsonSerializer.Serialize(context.DbFields.AsNoTracking()
            .Where(f => f.Id == fixture.SourceText || f.Id == fixture.SourceDate
                || f.Id == fixture.TargetText || f.Id == fixture.TargetDate)
            .OrderBy(f => f.Id)
            .Select(f => new { f.Id, f.CollectionId, f.Name, f.FieldType, f.ConfigJson, f.Position, f.IsCalStart,
                f.IsCalEnd, f.IsSystem, f.IsRequired, f.IsTaskExec, f.IsTaskAssigner, f.IsTaskStatus, f.IsDescription,
                f.IsDocFile, f.IsDocType }).ToList());

    private static string CollectionSnapshot(AppDbContext context, MultiUserFixture fixture) =>
        JsonSerializer.Serialize(context.DbCollections.AsNoTracking()
            .Where(c => c.Id == fixture.SourceCollection || c.Id == fixture.TargetCollection)
            .OrderBy(c => c.Id).Select(c => new { c.Id, c.Name, c.GroupId, c.Position, c.Icon, c.Color }).ToList());

    private sealed record ExpectedField(int Id, string Name);
    private sealed record ExpectedRecord(int Id, string Title, int? Field, string Value);
    private sealed record ExpectedGroup(int Id, string Name, int? Parent);
    private sealed record ExpectedCollection(int Id, string Name, int Group);
}
