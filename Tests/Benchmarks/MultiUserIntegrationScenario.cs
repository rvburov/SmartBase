using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SmartBase.Core.Importers;
using SmartBase.Core.Models;
using SmartBase.Database;
using SmartBase.Database.Repositories;
using SmartBase.Integrations;
using SmartBase.Utils;

namespace SmartBase.Tests.Benchmarks;

/// <summary>Параллельная укладка ответа, повтор по ключу, шаг сценария и подготовка исходящего JSON.
/// Внешний сервер не используется: проверяются команды приложения и точность подготовленного тела.</summary>
internal sealed class MultiUserIntegrationScenario : IMultiUserScenario
{
    private readonly MultiUserFixture _fixture;
    private readonly int _collection;
    private readonly int _key;
    private readonly int _value;
    private readonly CollectionBinding _binding;
    private readonly ImportSchema _schema = new("Название", ["Ключ", "Значение"], 1, 0);
    private readonly OutgoingBinding _outgoing;
    private readonly ConcurrentDictionary<int, ExpectedRecord> _acknowledged = new();

    private MultiUserIntegrationScenario(MultiUserFixture fixture, int collection, int key, int value)
    {
        _fixture = fixture;
        _collection = collection;
        _key = key;
        _value = value;
        _binding = new CollectionBinding(901, collection,
            [new BindingColumn("Название", null), new BindingColumn("Ключ", key), new BindingColumn("Значение", value)],
            MatchColumn: "Ключ");
        _outgoing = new OutgoingBinding(901, collection,
            [new OutgoingField(null, "title"), new OutgoingField(key, "key"), new OutgoingField(value, "value")]);
    }

    internal static IMultiUserScenario Create(MultiUserFixture fixture)
    {
        using var manager = DatabaseManager.Open(901, "Совместный обмен", fixture.Connection,
            provider: fixture.Options.Provider == "postgres" ? "postgres" : "sqlite");
        var repo = manager.Constructor;
        var collection = repo.AddCollection(new DbCollection { Name = "Совместный обмен", Position = 3000 }).Id!.Value;
        var key = repo.AddField(new DbField
            { CollectionId = collection, Name = "Ключ", FieldType = "text", Position = 1000 }).Id!.Value;
        var value = repo.AddField(new DbField
            { CollectionId = collection, Name = "Значение", FieldType = "text", Position = 2000 }).Id!.Value;
        // Разрешается только подготовка тела для синтетической базы. Сетевые адреса и
        // производственные учетные данные у сценария отсутствуют.


        return new MultiUserIntegrationScenario(fixture, collection, key, value);
    }

    public void Execute(AppDbContext context, ConstructorRepository repo, MultiUserClient client, int round)
    {
        var key = $"integration-{client.Id:D4}-{round:D4}";
        var title = $"Пакет {client.Id:D4}:{round:D4}";
        var first = client.Measure("integration-create", () => repo.ApplyRowsIsolated(_binding, _schema,
            [new ImportRow(title, [key, "Принято"])], client.Stop));
        if (first.Created != 1 || first.Updated != 0 || first.Ambiguous != 0 || first.Empty != 0)
            throw new InvalidOperationException("Первичная укладка не создала ровно одну запись");
        var ids = context.DbFieldValues.AsNoTracking()
            .Where(v => v.FieldId == _key && v.Value == key).Select(v => v.RecordId).ToList();
        if (ids.Count != 1) throw new InvalidOperationException("Ключ первичной укладки не обозначает одну запись");
        var id = ids[0];
        var expected = new ExpectedRecord(id, key, title, "Принято", CurrentUser.DisplayName);
        _acknowledged[id] = expected;

        var second = client.Measure("integration-retry", () => repo.ApplyRowsIsolated(_binding, _schema,
            [new ImportRow(title, [key, "Повтор принят"])], client.Stop));
        if (second.Created != 0 || second.Updated != 1 || second.Ambiguous != 0 || second.Empty != 0)
            throw new InvalidOperationException("Повтор по ключу создал дубль или не обновил запись");
        _acknowledged[id] = expected = expected with { Value = "Повтор принят" };

        var finalTitle = title + " после сценария";
        var finalValue = $"Результат \"{client.Id}:{round}\"\nстрока 2";
        var scenario = client.Measure("integration-scenario", () => repo.WriteRecordsIsolated(_collection,
            [new Dictionary<string, string>
            {
                ["Название"] = finalTitle, ["Ключ"] = key, ["Значение"] = finalValue,
            }], client.Stop, matchField: "Ключ", createMissing: false));
        if (scenario.Trouble is not null || scenario.Created != 0 || scenario.Updated != 1 || scenario.Ambiguous != 0)
            throw new InvalidOperationException("Шаг сценария не обновил свою запись по ключу: " + scenario.Trouble);
        _acknowledged[id] = expected = expected with { Title = finalTitle, Value = finalValue };

        client.Measure("integration-export", () =>
        {
            var rows = repo.ReadOutgoingIsolated(_outgoing, 1, client.Stop, records: [id]);
            if (rows.Trouble is not null || rows.Columns.Count != 3 || rows.Rows.Count != 1)
                throw new InvalidOperationException("Подготовка исходящей записи не удалась: " + rows.Trouble);
            var payload = OutgoingBody.Build(_outgoing, rows.Columns, rows.Rows, rows.Collection);
            using var json = JsonDocument.Parse(payload.Body);
            if (json.RootElement.GetArrayLength() != 1) throw new InvalidOperationException("Исходящий JSON содержит не одну запись");
            var row = json.RootElement[0];
            if (row.GetProperty("title").GetString() != finalTitle || row.GetProperty("key").GetString() != key
                || row.GetProperty("value").GetString() != finalValue)
                throw new InvalidOperationException("Исходящий JSON смешал или повредил значения участников");
        });
        context.ChangeTracker.Clear();
    }

    public IEnumerable<MultiUserCheck> Verify(MultiUserClient[] clients)
    {
        using var context = new AppDbContext(_fixture.Connection);
        var expected = _acknowledged.ToDictionary(p => p.Key, p => p.Value);
        var actual = context.DbRecords.AsNoTracking().Where(r => r.CollectionId == _collection).ToList();
        var keys = context.DbFieldValues.AsNoTracking().Where(v => v.FieldId == _key).ToList();
        var values = context.DbFieldValues.AsNoTracking().Where(v => v.FieldId == _value).ToList();
        yield return new("Обмен: каждый полный цикл оставил одну запись без дублей повтора",
            expected.Count == _fixture.Options.Clients * _fixture.Options.Rounds && actual.Count == expected.Count,
            $"факт {actual.Count}, подтверждено {expected.Count}, запланировано {_fixture.Options.Clients * _fixture.Options.Rounds}");
        yield return new("Обмен: нет лишних или пропавших подтвержденных записей",
            expected.Keys.ToHashSet().SetEquals(actual.Select(r => r.Id)));
        yield return new("Обмен: ключи уникальны и обе колонки присутствуют у каждой записи",
            keys.Count == expected.Count && values.Count == expected.Count
            && keys.Select(v => v.Value).Distinct(StringComparer.Ordinal).Count() == keys.Count);
        var exact = true;
        foreach (var row in actual)
        {
            if (!expected.TryGetValue(row.Id, out var wanted)) { exact = false; continue; }
            exact &= row.Title == wanted.Title && row.CreatedBy == wanted.Author && row.UpdatedBy == wanted.Author
                && keys.FirstOrDefault(v => v.RecordId == row.Id)?.Value == wanted.Key
                && values.FirstOrDefault(v => v.RecordId == row.Id)?.Value == wanted.Value;
        }
        yield return new("Обмен: названия, ключи, значения и авторы точно соответствуют подтвержденным командам", exact);
    }

    private sealed record ExpectedRecord(int Id, string Key, string Title, string Value, string Author);
}
