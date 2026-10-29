using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SmartBase.Core.Exporters;
using SmartBase.Core.Models;
using SmartBase.Database;
using SmartBase.Database.Entities;
using SmartBase.Database.Repositories;
using SmartBase.Integrations;
using SmartBase.Utils;

namespace SmartBase.Tests.Integration;

/// <summary>Аудит берется из заголовка записи во всех читающих путях, без EAV и чужой схемы.</summary>
[Collection(EgressCollection.Name)]
public sealed class AuditColumnQueryTests
{
    private static readonly string[] Types = ["created_by", "updated_by", "created_at", "updated_at"];

    [SkippableTheory]
    [InlineData("sqlite")]
    [InlineData("postgres")]
    public async Task All_audit_columns_use_fresh_metadata_for_counts_filters_sort_and_export(string provider)
    {
        using var data = new AuditData(provider);
        var repo = data.Database.Repo;
        foreach (var type in Types)
        {
            var fid = data.Fields[type];
            var key = fid.ToString(CultureInfo.InvariantCulture);
            var expected = data.Ids.ToDictionary(id => id, id => data.Raw[id][type]);
            Assert.Equal(expected.OrderBy(p => p.Key), repo.GetColumnValues(data.Collection, key).OrderBy(p => p.Key));
            Counts(expected.Values, repo.GetFieldValueCounts(data.Collection, fid));
            Counts(expected.Values, await repo.GetColumnCountsAsync(data.Collection, key));
            var list = await repo.GetColumnValueListAsync(data.Collection, key);
            Assert.False(list.Capped);
            Counts(expected.Values, list.Values);
            var total = await repo.GetColumnValueTotalsAsync(data.Collection, key);
            Assert.Equal(expected.Values.Where(v => v != "").Distinct().LongCount(), total.Distinct);
            Assert.Equal(1, total.Empty);

            var wanted = data.Raw[data.Ids[0]][type];
            var selected = new Dictionary<string, HashSet<string>> { [key] = [wanted] };
            Assert.Equal(data.Ids.Where(id => expected[id] == wanted), repo.GetFilteredRecordIds(data.Collection, selected));
            var excluded = new Dictionary<string, HashSet<string>> { [key] = FilterExclusion.Exclude([wanted]) };
            Assert.Equal(data.Ids.Where(id => expected[id] != wanted), repo.GetFilteredRecordIds(data.Collection, excluded));
            Assert.Equal([data.Ids[2]], repo.GetFilteredRecordIds(data.Collection,
                new Dictionary<string, HashSet<string>> { [key] = [""] }));
            Assert.Equal(data.Ids.Where(id => id != data.Ids[2]), repo.GetFilteredRecordIds(data.Collection,
                new Dictionary<string, HashSet<string>> { [key] = FilterExclusion.Exclude([""]) }));

            // Свой фильтр не урезает список; чужой фильтр и скрытые записи урезают.
            var cascade = new Dictionary<string, HashSet<string>>
            {
                [key] = ["не выбранное значение"], ["__title__"] = ["R1", "R3"],
            };
            Counts([expected[data.Ids[0]], ""], repo.GetColumnCounts(data.Collection, key, cascade));
            Counts(expected.Where(p => p.Key != data.Ids[3]).Select(p => p.Value),
                repo.GetColumnCounts(data.Collection, key, excludeHidden: true));

            int[] ascending = type switch
            {
                "created_at" => [data.Ids[0], data.Ids[3], data.Ids[1], data.Ids[2]],
                "updated_at" => [data.Ids[1], data.Ids[0], data.Ids[3], data.Ids[2]],
                _ => [data.Ids[0], data.Ids[3], data.Ids[1], data.Ids[2]],
            };
            int[] descending = type == "updated_at"
                ? [data.Ids[0], data.Ids[3], data.Ids[1], data.Ids[2]]
                : [data.Ids[1], data.Ids[0], data.Ids[3], data.Ids[2]];
            // Тип из живой схемы важнее устаревшего снимка UI.
            Assert.Equal(ascending, await repo.GetFilteredRecordIdsAsync(data.Collection, new Dictionary<string, HashSet<string>>(), false, key, true, "text", default));
            Assert.Equal(descending, await repo.GetFilteredRecordIdsAsync(data.Collection, new Dictionary<string, HashSet<string>>(), false, key, false, type, default));
        }

        // Столбцы выбраны и переставлены явно. Маленькая keyset-страница ловит потери
        // и повторы на границах; скрытая строка не должна уехать в экспорт.
        var columns = repo.GetExportColumnsIsolated(data.Collection, true)
            .Where(c => c.FieldType is "created_at" or "updated_by").Reverse().ToList();
        var rows = repo.ReadExportRowsIsolated(data.Collection, columns, true, default, pageSize: 1).ToList();
        var visible = data.Ids.Where(id => id != data.Ids[3]).ToArray();
        Assert.Equal(visible.Length, rows.Count);
        for (var r = 0; r < rows.Count; r++)
            Assert.Equal(columns.Select(c => data.Raw[visible[r]][c.FieldType]), rows[r]);
        var portable = repo.ReadExportRowsIsolated(data.Collection, columns, true, default,
            pageSize: 1, portableRelations: true).ToList();
        for (var r = 0; r < portable.Count; r++)
        {
            var payload = JsonSerializer.Deserialize<RelationPortableRow>(portable[r][^1]);
            Assert.NotNull(payload);
            foreach (var column in columns)
                Assert.Equal(data.Raw[visible[r]][column.FieldType], payload.Values[column.FieldId!.Value]);
        }

        // Долгоживущий Context держит старый объект в трекере. Новые значения автора
        // и даты обязаны прийти из БД, а не из него или подложенной EAV-строки.
        foreach (var type in Types)
        {
            var fresh = type is "created_at" or "updated_at" ? "2026-12-31T23:59:59.7654321-03:00" : "Свежий " + type;
            data.Set(data.Ids[0], type, fresh);
            var key = data.Fields[type].ToString();
            Assert.Equal(fresh, repo.GetColumnValues(data.Collection, key)[data.Ids[0]]);
            Assert.Contains(await repo.GetColumnCountsAsync(data.Collection, key), item => item.Value == fresh && item.Count == 1);
            var selected = repo.GetExportColumnsIsolated(data.Collection, true).Where(c => c.FieldId == data.Fields[type]).ToList();
            Assert.Equal(fresh, repo.ReadExportRowsIsolated(data.Collection, selected, false, default).First()[0]);
        }
    }

    [SkippableTheory]
    [InlineData("sqlite")]
    [InlineData("postgres")]
    public async Task Foreign_and_stale_field_keys_never_project_audit_metadata(string provider)
    {
        using var data = new AuditData(provider);
        var repo = data.Database.Repo;
        var foreign = SampleData.AddCollection(repo, "Чужая коллекция").Id!.Value;
        var foreignField = SampleData.AddField(repo, foreign, "Автор", "created_by").Id!.Value;
        SampleData.AddRecord(repo, foreign, "Чужая запись", 10);
        var key = foreignField.ToString(CultureInfo.InvariantCulture);
        Assert.Empty(repo.GetColumnValues(data.Collection, key));
        Assert.Empty(repo.GetFieldValueCounts(data.Collection, foreignField));
        Assert.Empty(repo.GetColumnCounts(data.Collection, key));
        Assert.Empty(await repo.GetColumnCountsAsync(data.Collection, key));
        Assert.Empty((await repo.GetColumnValueListAsync(data.Collection, key)).Values);
        Assert.Equal((0L, 0), await repo.GetColumnValueTotalsAsync(data.Collection, key));
        Assert.Empty(repo.GetFilteredRecordIds(data.Collection,
            new Dictionary<string, HashSet<string>> { [key] = [CurrentUser.DisplayName] }));
        Assert.Equal(data.Ids, await repo.GetFilteredRecordIdsAsync(data.Collection, new Dictionary<string, HashSet<string>>(), false, key, true, "created_by", default));

        var foreignColumns = repo.GetExportColumnsIsolated(foreign, true).Where(c => c.FieldId == foreignField).ToList();
        Assert.All(repo.ReadExportRowsIsolated(data.Collection, foreignColumns, false, default), row => Assert.Equal([""], row));

        var ownField = data.Fields["created_by"];
        var snapshot = repo.GetExportColumnsIsolated(data.Collection, true).Where(c => c.FieldId == ownField).ToList();
        using (var change = data.Database.OpenContext())
            change.DbFields.Where(f => f.Id == ownField).ExecuteUpdate(s => s.SetProperty(f => f.FieldType, "text"));
        var changed = repo.ReadExportRowsIsolated(data.Collection, snapshot, false, default, pageSize: 1).ToList();
        Assert.Equal("EAV остался от старого типа", changed[0][0]);
        Assert.Equal("", changed[1][0]);
        using (var change = data.Database.OpenContext()) change.DbFields.Where(f => f.Id == ownField).ExecuteDelete();
        Assert.All(repo.ReadExportRowsIsolated(data.Collection, snapshot, false, default), row => Assert.Equal([""], row));
    }

    [SkippableTheory]
    [InlineData("sqlite")]
    [InlineData("postgres")]
    public async Task Capped_audit_lists_search_and_totals_read_metadata_with_bounded_results(string provider)
    {
        using var database = new MultiUserTestDatabase(provider);
        var repo = database.Repo;
        var collection = SampleData.AddCollection(repo, "Много метаданных").Id!.Value;
        var fields = Types.ToDictionary(t => t, t => SampleData.AddField(repo, collection, t, t).Id!.Value);
        const int count = ConstructorRepository.FilterValueCap + 2;
        var start = new DateTimeOffset(2026, 1, 1, 12, 34, 56, TimeSpan.Zero).AddTicks(1234567);
        var rows = Enumerable.Range(0, count).Select(i => new DbRecordEntity
        {
            CollectionId = collection, Title = $"R{i}", Position = i + 1,
            CreatedBy = $"Автор {i:D4}", UpdatedBy = $"Изменил {i:D4}",
            CreatedAt = start.AddDays(i).ToString("o"), UpdatedAt = start.AddDays(i).AddHours(1).ToString("o"),
        }).ToList();
        using (var writer = database.OpenContext()) { writer.DbRecords.AddRange(rows); writer.SaveChanges(); }
        var empty = SampleData.AddRecord(repo, collection, "Без метаданных", count + 1).Id!.Value;
        using (var writer = database.OpenContext())
            writer.DbRecords.Where(r => r.Id == empty).ExecuteUpdate(s => s
                .SetProperty(r => r.CreatedBy, "").SetProperty(r => r.UpdatedBy, "")
                .SetProperty(r => r.CreatedAt, "").SetProperty(r => r.UpdatedAt, ""));

        foreach (var type in Types)
        {
            var key = fields[type].ToString(CultureInfo.InvariantCulture);
            var list = await repo.GetColumnValueListAsync(collection, key);
            Assert.True(list.Capped);
            Assert.True(list.More);
            Assert.Equal(ConstructorRepository.FilterValueCap, list.Values.Count);
            Assert.DoesNotContain(list.Values, value => value.Value == "");
            Assert.Equal(((long)count, 1), await repo.GetColumnValueTotalsAsync(collection, key));
            var target = type switch
            {
                "created_by" => $"Автор {count - 1:D4}", "updated_by" => $"Изменил {count - 1:D4}",
                "created_at" => start.AddDays(count - 1).ToString("o"),
                _ => start.AddDays(count - 1).AddHours(1).ToString("o"),
            };
            var found = await repo.GetColumnValueListAsync(collection, key,
                search: FieldTypeInfo.FormatComputedDisplay(target, type));
            Assert.Equal((target, 1), Assert.Single(found.Values));
            Assert.False(found.More);
            var included = new Dictionary<string, HashSet<string>> { [key] = [target] };
            Assert.Equal([rows[^1].Id], repo.GetFilteredRecordIds(collection, included));
            var excluded = new Dictionary<string, HashSet<string>> { [key] = FilterExclusion.Exclude([target]) };
            Assert.Equal(count, repo.GetFilteredRecordIds(collection, excluded).Count);
        }

        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        var auditKey = fields["created_at"].ToString();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => repo.GetColumnValueListAsync(collection, auditKey, ct: cancelled.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => repo.GetColumnValueTotalsAsync(collection, auditKey, ct: cancelled.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => repo.GetFilteredRecordIdsAsync(collection,
            new Dictionary<string, HashSet<string>>(), false, auditKey, true, "created_at", cancelled.Token));
    }

    [SkippableTheory]
    [InlineData("sqlite")]
    [InlineData("postgres")]
    public async Task Granted_api_reads_exact_audit_snapshots_and_denied_audit_queries_stay_denied(string provider)
    {
        using var data = new AuditData(provider);
        var repo = data.Database.Repo;
        var previous = EgressGate.Policy;
        try
        {
            EgressGate.AttachPolicy(new EgressPolicySet([new EgressPolicy(EgressMode.Internal,
                [], [], [], 1000, 1024 * 1024, 60, null, false, "проверка чтения аудита")]));
            var keys = new[] { data.Fields["updated_at"].ToString(), ReadGrant.TitleKey, data.Fields["created_by"].ToString() };
            var first = repo.ApiReadIsolated(data.Collection, keys, "", 0, 2);
            Assert.Null(first.Trouble);
            Assert.True(first.More);
            Assert.Equal(data.Ids.Take(2), first.Rows.Select(r => r.Id));
            for (var i = 0; i < 2; i++) Assert.Equal(
                new[] { data.Raw[data.Ids[i]]["updated_at"], $"R{i + 1}", data.Raw[data.Ids[i]]["created_by"] }, first.Rows[i].Values);
            var next = repo.ApiReadIsolated(data.Collection, keys, "", first.Rows[^1].Id, 2);
            Assert.False(next.More);
            Assert.Equal(data.Ids.Skip(2), next.Rows.Select(r => r.Id));
        }
        finally { EgressGate.AttachPolicy(previous); }

        // Закрываем доступ настоящим кешем PermissionService, который передается фоновым клонам.
        using (var writer = data.Database.OpenContext())
        {
            var me = writer.DbUsers.Single(u => u.UserKey == CurrentUser.Key);
            me.IsAdmin = false;
            me.IsApproved = true;
            writer.DbPermissions.RemoveRange(writer.DbPermissions.Where(p => p.UserId == me.Id));
            writer.DbPermissions.Add(new DbPermissionEntity { UserId = me.Id, CollectionId = data.Collection, ActionsMask = 0 });
            writer.SaveChanges();
        }
        data.Database.Manager.Permissions.Reload(notify: false);
        var field = data.Fields["created_by"];
        var key = field.ToString();
        Assert.Throws<PermissionDeniedException>(() => repo.GetColumnValues(data.Collection, key));
        Assert.Throws<PermissionDeniedException>(() => repo.GetFieldValueCounts(data.Collection, field));
        Assert.Throws<PermissionDeniedException>(() => repo.GetFilteredRecordIds(data.Collection,
            new Dictionary<string, HashSet<string>> { [key] = ["Иванов Иван"] }));
        await Assert.ThrowsAsync<PermissionDeniedException>(() => repo.GetColumnCountsAsync(data.Collection, key));
        await Assert.ThrowsAsync<PermissionDeniedException>(() => repo.GetColumnValueListAsync(data.Collection, key));
        await Assert.ThrowsAsync<PermissionDeniedException>(() => repo.GetColumnValueTotalsAsync(data.Collection, key));
        await Assert.ThrowsAsync<PermissionDeniedException>(() => repo.GetFilteredRecordIdsAsync(data.Collection,
            new Dictionary<string, HashSet<string>>(), false, key, true, "created_by", default));
    }

    [SkippableTheory]
    [InlineData("sqlite")]
    [InlineData("postgres")]
    public void Saved_outgoing_bindings_read_fresh_audit_for_all_and_selected_rows_after_type_change(string provider)
    {
        using var data = new AuditData(provider, auditFieldsAsText: true);
        var repo = data.Database.Repo;
        var root = Directory.CreateTempSubdirectory("sb_audit_outgoing_").FullName;
        var appDb = Path.Combine(root, "app.db");
        var previous = EgressGate.Policy;
        try
        {
            EgressGate.AttachPolicy(new EgressPolicySet([new EgressPolicy(EgressMode.Internal,
                [], [], [], 1000, 1024 * 1024, 60, null, false, "проверка исходящего аудита")]));
            using var registry = DatabaseRegistry.Open(Path.Combine(root, "default.db"), appDb);
            var cards = registry.Integrations;
            var connection = cards.Save(IntegrationConnection.New() with
                { Name = "Проверка аудита", Address = "https://api.example.test" });
            var cases = new List<(IntegrationRequest Request, OutgoingBinding Binding, int[] Expected)>();

            void Save(OutgoingBinding binding, int[] expected)
            {
                Assert.Empty(binding.Problems());
                var request = cards.SaveRequest(IntegrationRequest.New() with
                {
                    ConnectionId = connection.Id, Name = $"Состав {cases.Count}",
                    Method = RequestMethod.Post, Path = "/audit",
                });
                cards.SaveOutgoing(request.Id, binding);
                Assert.Equal(binding.Mark(), cards.Outgoing(request.Id)!.Mark());
                cases.Add((request, binding, expected));
            }

            // Сохраняем обычные текстовые поля, доступные окну исходящей привязки.
            // Имена и id сохраняются в app.db до штатной смены типа через UpdateField.
            Assert.All(repo.GetFields(data.Collection), field => Assert.Equal("text", field.FieldType));
            Save(new OutgoingBinding(data.Database.Manager.DbId, data.Collection,
                [.. Types.Reverse().Select(type => new OutgoingField(data.Fields[type], type)),
                 new OutgoingField(null, "title")]), data.Ids);
            foreach (var type in Types)
            {
                var wanted = data.Raw[data.Ids[0]][type];
                foreach (var test in new[] { ScenarioTest.Equals, ScenarioTest.NotEquals, ScenarioTest.Empty })
                {
                    var expected = data.Ids.Where(id => test switch
                    {
                        ScenarioTest.Equals => data.Raw[id][type] == wanted,
                        ScenarioTest.NotEquals => data.Raw[id][type] != wanted,
                        _ => data.Raw[id][type] == "",
                    }).ToArray();
                    // Поле условия не входит в исходящий состав: оно читается как extra.
                    Save(new OutgoingBinding(data.Database.Manager.DbId, data.Collection,
                        [new OutgoingField(null, "title")], Conditions:
                        [new ScenarioCondition(type, test, test == ScenarioTest.Empty ? "" : wanted)]), expected);
                }
            }
            foreach (var field in repo.GetFields(data.Collection))
            {
                field.FieldType = Types.Single(type => data.Fields[type] == field.Id);
                repo.UpdateField(field);
            }

            var foreign = SampleData.AddCollection(repo, "Другое выделение").Id!.Value;
            var foreignId = SampleData.AddRecord(repo, foreign, "Чужая строка", 1).Id!.Value;
            var selectedIds = data.Ids.Reverse().Append(foreignId).ToArray();
            foreach (var scenario in cases)
            {
                // Сохраненная привязка не меняется от смены типа; реальный OutgoingSource
                // перечитывает ее из app.db и проходит прежние проверки привязки и вывода данных.
                Assert.Equal(scenario.Binding.Mark(), cards.Outgoing(scenario.Request.Id)!.Mark());
                var full = OutgoingSource.Build(cards, scenario.Request, [data.Database.Manager]);
                var selected = OutgoingSource.Build(cards, scenario.Request, [data.Database.Manager], selectedIds);
                Assert.Null(full.Trouble);
                Assert.Null(selected.Trouble);
                Assert.NotNull(full.Payload);
                Assert.NotNull(selected.Payload);
                foreach (var payload in new[] { full.Payload!, selected.Payload! })
                {
                    Assert.Equal(scenario.Expected.Length, payload.Rows);
                    using var json = JsonDocument.Parse(payload.Body);
                    var rows = json.RootElement.EnumerateArray().ToArray();
                    Assert.Equal(scenario.Expected.Length, rows.Length);
                    for (var index = 0; index < rows.Length; index++)
                    {
                        var id = scenario.Expected[index];
                        Assert.Equal(scenario.Binding.Fields.Select(field => field.Name),
                            rows[index].EnumerateObject().Select(property => property.Name));
                        foreach (var field in scenario.Binding.Fields)
                        {
                            var expected = field.FieldId is int fid
                                ? data.Raw[id][Types.Single(type => data.Fields[type] == fid)]
                                : $"R{Array.IndexOf(data.Ids, id) + 1}";
                            Assert.Equal(expected, rows[index].GetProperty(field.Name).GetString());
                        }
                    }
                }
                Assert.Equal(full.Payload!.Body, selected.Payload!.Body);
            }
        }
        finally
        {
            EgressGate.AttachPolicy(previous);
            TempDatabase.Cleanup(appDb);
            Directory.Delete(root, recursive: true);
        }
    }

    private static void Counts(IEnumerable<string> expected, IEnumerable<(string Value, int Count)> actual) =>
        Assert.Equal(expected.GroupBy(v => v, StringComparer.Ordinal).Select(g => (g.Key, g.Count())).OrderBy(v => v.Key, StringComparer.Ordinal),
            actual.Select(v => (Key: v.Value, Count: v.Count)).OrderBy(v => v.Key, StringComparer.Ordinal));

    private sealed class AuditData : IDisposable
    {
        public MultiUserTestDatabase Database { get; }
        public int Collection { get; }
        public Dictionary<string, int> Fields { get; }
        public int[] Ids { get; }
        public Dictionary<int, Dictionary<string, string>> Raw { get; } = [];

        public AuditData(string provider, bool auditFieldsAsText = false)
        {
            Database = new(provider);
            var repo = Database.Repo;
            Collection = SampleData.AddCollection(repo, "Аудит").Id!.Value;
            Fields = Types.ToDictionary(t => t, t => SampleData.AddField(repo, Collection, t, auditFieldsAsText ? "text" : t).Id!.Value);
            Ids = Enumerable.Range(0, 4).Select(i => SampleData.AddRecord(repo, Collection, $"R{i + 1}", i * 10 + 10).Id!.Value).ToArray();
            Raw[Ids[0]] = Values("Иванов Иван", "Романов Роман", "2026-10-28T08:00:00.0000001+02:00", "2026-10-28T09:00:00.0000002+02:00");
            Raw[Ids[1]] = Values("Петров Петр", "Сидоров Сидор", "2026-10-28T06:30:00.0000000Z", "2026-10-28T06:30:00.0000000Z");
            Raw[Ids[2]] = Values("", "", "", "");
            Raw[Ids[3]] = Values("Иванов Иван", "Романов Роман", "2026-10-28T06:00:00.0000001Z", "2026-10-28T07:00:00.0000002Z");
            using var writer = Database.OpenContext();
            foreach (var id in Ids)
            {
                var raw = Raw[id];
                writer.DbRecords.Where(r => r.Id == id).ExecuteUpdate(s => s
                    .SetProperty(r => r.CreatedBy, raw["created_by"]).SetProperty(r => r.UpdatedBy, raw["updated_by"])
                    .SetProperty(r => r.CreatedAt, raw["created_at"]).SetProperty(r => r.UpdatedAt, raw["updated_at"]));
            }
            foreach (var fid in Fields.Values) writer.DbFieldValues.Add(new DbFieldValueEntity
            {
                RecordId = Ids[0], FieldId = fid, Value = "EAV остался от старого типа",
            });
            writer.SaveChanges();
            repo.SaveRecordDisplay(Collection, [(Ids[3], false, 1)]);
        }

        public void Set(int id, string type, string value)
        {
            Raw[id][type] = value;
            using var writer = Database.OpenContext();
            var record = writer.DbRecords.Single(r => r.Id == id);
            switch (type)
            {
                case "created_by": record.CreatedBy = value; break;
                case "updated_by": record.UpdatedBy = value; break;
                case "created_at": record.CreatedAt = value; break;
                case "updated_at": record.UpdatedAt = value; break;
                default: throw new ArgumentOutOfRangeException(nameof(type));
            }
            writer.SaveChanges();
        }

        private static Dictionary<string, string> Values(string createdBy, string updatedBy, string createdAt, string updatedAt) =>
            new() { ["created_by"] = createdBy, ["updated_by"] = updatedBy, ["created_at"] = createdAt, ["updated_at"] = updatedAt };

        public void Dispose() => Database.Dispose();
    }
}
