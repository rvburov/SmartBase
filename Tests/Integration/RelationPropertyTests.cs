using SmartBase.Core.Models;
using SmartBase.Database;
using SmartBase.Database.Entities;
using SmartBase.Database.Repositories;
using SmartBase.Utils;
using System.Diagnostics;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace SmartBase.Tests.Integration;

public sealed class RelationPropertyTests
{
    [Fact]
    public async Task Async_formatted_search_walks_pages_limits_results_and_isolates_replacement_queries()
    {
        using var db = new TempDatabase();
        var repo = db.Constructor;
        var col = SampleData.AddCollection(repo, "Файлы").Id!.Value;
        var file = SampleData.AddField(repo, col, "Файл", "file");
        var rows = Enumerable.Range(0, 520).Select(i => new BulkRecordRow($"R{i}",
            [i == 519 ? "/tmp/last/target.pdf" : $"/tmp/target-directory/item-{i}.pdf"]));
        var ids = repo.BulkAddRecords(col, [file.Id!.Value], rows, 520, default);
        var key = file.Id.Value.ToString();
        var older = repo.SearchRelationRecordsAsync(col, key, "item-", take: 3);
        var newer = repo.SearchRelationRecordsAsync(col, key, "target", take: 1);
        await Task.WhenAll(older, newer);
        Assert.Equal(3, older.Result.Count);
        Assert.Equal(ids[^1], Assert.Single(newer.Result).RecordId);
        Assert.Equal("target.pdf", newer.Result[0].Display);

        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            repo.SearchRelationRecordsAsync(col, key, "target", ct: cancelled.Token));

        using var duringSearch = new CancellationTokenSource();
        using var pageObserver = new CancelAfterFirstRecordPage(db.Path, duringSearch);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            repo.SearchRelationRecordsAsync(col, key, "target", ct: duringSearch.Token));
        Assert.Equal(1, pageObserver.PagesRead);
    }

    private static DbField Relation(ConstructorRepository repo, int source, int target, string display = "__title__", string? search = null, int dbId = 0)
    {
        var field = SampleData.AddField(repo, source, "Связь", "relation");
        field.ConfigJson = new RelationConfig(target, false, DisplayField: display, SearchField: search, TargetDbId: dbId).ToConfigJson();
        repo.UpdateField(field);
        return field;
    }

    [Fact]
    public void Display_search_and_stored_identity_are_independent()
    {
        using var db = new TempDatabase();
        var repo = db.Constructor;
        var targets = SampleData.AddCollection(repo, "Цели").Id!.Value;
        var source = SampleData.AddCollection(repo, "Источники").Id!.Value;
        var code = SampleData.AddField(repo, targets, "Артикул");
        var description = SampleData.AddField(repo, targets, "Описание");
        var target = SampleData.AddRecord(repo, targets, "Название без кода").Id!.Value;
        var row = SampleData.AddRecord(repo, source, "Источник").Id!.Value;
        repo.SetFieldValue(target, code.Id!.Value, "ДЕТ-050%");
        repo.SetFieldValue(target, description.Id!.Value, "КРОНШТЕЙН левый");
        var relation = Relation(repo, source, targets, code.Id.Value.ToString(), description.Id.Value.ToString());
        repo.SetLinkEdges(relation.Id!.Value, row, [new(0, target)]);

        Assert.Equal("ДЕТ-050%", repo.GetLinks(relation.Id.Value, row).Single().Title);
        Assert.Equal("ДЕТ-050%", repo.GetRelationLinksBatch(relation.Id.Value, [row])[row].Single().Display);
        Assert.Equal([target], repo.SearchRelationRecords(targets, description.Id.Value.ToString(), "кронштейн").Select(v => v.RecordId));
        Assert.Empty(repo.SearchRelationRecords(targets, description.Id.Value.ToString(), "Название"));
        Assert.Single(repo.SearchRelationRecords(targets, code.Id.Value.ToString(), "50%"));
        Assert.Empty(repo.SearchRelationRecords(targets, code.Id.Value.ToString(), "50_"));
        repo.SetFieldValue(target, code.Id.Value, "ДЕТ-051");
        Assert.Equal("ДЕТ-051", repo.GetRelationLinks(relation.Id.Value, row).Single().Display);
        Assert.Equal([(target, 0)], repo.GetLinkEdges(relation.Id.Value, row));
        var options = repo.GetRelationPropertyOptions(targets);
        Assert.Contains(options, o => o.Key == RelationConfig.TitleField);
        Assert.Contains(options, o => o.Key == code.Id.Value.ToString() && o.Name == "Артикул");
    }

    [Fact]
    public void All_existing_value_types_are_projected_with_their_format()
    {
        using var db = new TempDatabase();
        var repo = db.Constructor;
        var col = SampleData.AddCollection(repo).Id!.Value;
        var target = SampleData.AddRecord(repo, col, "Цель").Id!.Value;
        var values = new (string Type, string Raw, string Display)[]
        {
            ("text", "Текст\nвторая строка", "Текст\nвторая строка"), ("number", "42,5", "42,5"),
            ("date", "2026-10-20", "20.10.2026"), ("select", "A", "A"), ("status", "B", "B"),
            ("multiselect", "[\"A\",\"B\"]", "A, B"), ("checkbox", "0", "(Не отмечено)"),
            ("url", "https://example.test", "https://example.test"), ("user", "Пользователь", "Пользователь"),
            ("file", FileValue.Serialize(["/tmp/a,b.pdf", "/tmp/c.pdf"]), "a,b.pdf, c.pdf"),
        };
        foreach (var (type, raw, display) in values)
        {
            var field = SampleData.AddField(repo, col, type, type);
            repo.SetFieldValue(target, field.Id!.Value, raw);
            var value = repo.ReadRelationPropertyValues(col, field.Id.Value.ToString(), [target]).Single();
            Assert.Equal(display, value.Display);
            Assert.Equal(type, value.FieldType);
            Assert.Single(repo.SearchRelationRecords(col, field.Id.Value.ToString(), display));
        }
        var created = SampleData.AddField(repo, col, "Автор", "created_by");
        var system = repo.ReadRelationPropertyValues(col, created.Id!.Value.ToString(), [target]).Single();
        Assert.Equal(repo.GetRecord(target)!.CreatedBy, system.Raw);
        Assert.Equal(system.Raw, system.Display);
    }

    [Fact]
    public void Same_integer_ids_in_two_databases_keep_distinct_labels_and_edits()
    {
        using var a = new TempDatabase(1, "A");
        using var b = new TempDatabase(2, "B");
        using var resolver = RelationDbResolver.UseScopedResolver(id => id == 2 ? b.Constructor : id == 1 ? a.Constructor : null,
            repo => repo == a.Constructor ? 1 : 2);
        var localCol = SampleData.AddCollection(a.Constructor, "Локальные").Id!.Value;
        var remoteCol = SampleData.AddCollection(b.Constructor, "Удаленные").Id!.Value;
        var local = SampleData.AddRecord(a.Constructor, localCol, "Локальная").Id!.Value;
        var remote = SampleData.AddRecord(b.Constructor, remoteCol, "Удаленная").Id!.Value;
        Assert.Equal(local, remote);
        var sourceCol = SampleData.AddCollection(a.Constructor, "Источники").Id!.Value;
        var source = SampleData.AddRecord(a.Constructor, sourceCol, "Источник").Id!.Value;
        var field = Relation(a.Constructor, sourceCol, remoteCol, dbId: 2);
        a.Context.DbRecordLinks.AddRange(
            new DbRecordLinkEntity { FieldId = field.Id!.Value, FromRecordId = source, ToRecordId = local, TargetDbId = 0, Position = 1000 },
            new DbRecordLinkEntity { FieldId = field.Id.Value, FromRecordId = source, ToRecordId = remote, TargetDbId = 2, Position = 2000 });
        a.Context.SaveChanges();
        var links = a.Constructor.GetRelationLinks(field.Id.Value, source);
        Assert.Equal([new RelationTarget(0, local), new RelationTarget(2, remote)], links.Select(l => l.Target));
        Assert.Equal(["Локальная", "Удаленная"], links.Select(l => l.Display));
        a.Constructor.SetLinkEdges(field.Id.Value, source, [new(2, remote)]);
        Assert.Equal([(remote, 2)], a.Constructor.GetLinkEdges(field.Id.Value, source));
        a.Constructor.SetLinkEdges(field.Id.Value, source, []);
        Assert.Empty(a.Constructor.GetLinkEdges(field.Id.Value, source));
    }

    [Fact]
    public void Numeric_property_key_never_reads_a_coinciding_field_from_a_legacy_target_database()
    {
        using var a = new TempDatabase(1, "A");
        using var b = new TempDatabase(2, "B");
        using var resolver = RelationDbResolver.UseScopedResolver(id => id == 2 ? b.Constructor : id == 1 ? a.Constructor : null,
            repo => repo.DbPath == a.Path ? 1 : repo.DbPath == b.Path ? 2 : 0);
        var localCol = SampleData.AddCollection(a.Constructor, "Локальные").Id!.Value;
        var remoteCol = SampleData.AddCollection(b.Constructor, "Удаленные").Id!.Value;
        var localCode = SampleData.AddField(a.Constructor, localCol, "Постороннее свойство");
        var remoteCode = SampleData.AddField(b.Constructor, remoteCol, "Код для показа");
        Assert.Equal(localCode.Id, remoteCode.Id);
        var local = SampleData.AddRecord(a.Constructor, localCol, "Локальная").Id!.Value;
        var remote = SampleData.AddRecord(b.Constructor, remoteCol, "Удаленная").Id!.Value;
        a.Constructor.SetFieldValue(local, localCode.Id!.Value, "A-ONLY");
        b.Constructor.SetFieldValue(remote, remoteCode.Id!.Value, "B-ONLY");
        var sourceCol = SampleData.AddCollection(a.Constructor, "Источники").Id!.Value;
        var source = SampleData.AddRecord(a.Constructor, sourceCol, "Источник").Id!.Value;
        var key = remoteCode.Id.Value.ToString();
        var relation = Relation(a.Constructor, sourceCol, remoteCol, key, key, dbId: 2);
        a.Context.DbRecordLinks.AddRange(
            new DbRecordLinkEntity { FieldId = relation.Id!.Value, FromRecordId = source, ToRecordId = local, TargetDbId = 0, Position = 1000 },
            new DbRecordLinkEntity { FieldId = relation.Id.Value, FromRecordId = source, ToRecordId = remote, TargetDbId = 2, Position = 2000 });
        a.Context.SaveChanges();
        Assert.Equal([RelationProperty.MissingField, "B-ONLY"],
            a.Constructor.GetRelationLinks(relation.Id.Value, source).Select(l => l.Display));
        Assert.Equal(RelationProperty.MissingField, a.Constructor.GetLinks(relation.Id.Value, source).Single().Title);
        var config = RelationConfig.FromConfig(relation.ConfigJson)!;
        var selected = new[] { new RelationTarget(0, local), new RelationTarget(2, remote) };
        // Scoped resolver принадлежит потоку; worker сам устанавливает свой scope, а проверка
        // продолжает пользоваться исходным потоком до Dispose.
        var picker = a.Constructor.GetRelationPickerDataAsync(source, config, "A-ONLY", selected).GetAwaiter().GetResult();
        Assert.Empty(picker.Candidates);
        Assert.Equal(RelationProperty.MissingField, picker.SelectedLabels[new(0, local)]);
        Assert.Equal(RelationProperty.MissingField, picker.SelectedSearch[new(0, local)]);
        Assert.DoesNotContain("A-ONLY", picker.SelectedLabels.Values);
        Assert.Equal("A-ONLY", a.Constructor.ResolveRelationLabels([new(1, local)], key, expectedTargetDbId: 0)[new(1, local)]);
        var titles = a.Constructor.ResolveRelationLabels(selected, RelationConfig.TitleField, expectedTargetDbId: 2);
        Assert.Equal("Локальная", titles[new(0, local)]);
        Assert.Equal("Удаленная", titles[new(2, remote)]);
    }

    [Fact]
    public void Relation_property_cycles_terminate_and_auto_fields_reject_manual_changes()
    {
        using var db = new TempDatabase();
        var repo = db.Constructor;
        var col = SampleData.AddCollection(repo).Id!.Value;
        var first = SampleData.AddRecord(repo, col, "A").Id!.Value;
        var second = SampleData.AddRecord(repo, col, "B").Id!.Value;
        var field = Relation(repo, col, col);
        repo.SetLinkEdges(field.Id!.Value, first, [new(0, second)]);
        var entity = db.Context.DbFields.Single(f => f.Id == field.Id.Value);
        entity.ConfigJson = new RelationConfig(col, false, DisplayField: field.Id.Value.ToString()).ToConfigJson();
        db.Context.SaveChanges();
        Assert.Equal(RelationProperty.Cycle, repo.ReadRelationPropertyValues(col, field.Id.Value.ToString(), [first]).Single().Display);
        entity.ConfigJson = new RelationConfig(col, false, Auto: new(RelationConfig.TitleField, RelationConfig.TitleField)).ToConfigJson();
        db.Context.SaveChanges();
        Assert.Throws<InvalidOperationException>(() => repo.SetLinkEdges(field.Id.Value, first, []));
        Assert.Equal([(second, 0)], repo.GetLinkEdges(field.Id.Value, first));
    }

    [Fact]
    public void A_relation_field_cannot_write_edges_for_a_record_of_another_collection()
    {
        using var db = new TempDatabase();
        var repo = db.Constructor;
        var first = SampleData.AddCollection(repo, "Первая").Id!.Value;
        var second = SampleData.AddCollection(repo, "Вторая").Id!.Value;
        var target = SampleData.AddRecord(repo, first, "Цель").Id!.Value;
        var unrelated = SampleData.AddRecord(repo, second, "Чужой источник").Id!.Value;
        var field = Relation(repo, first, first);
        Assert.Throws<InvalidOperationException>(() => repo.SetLinkEdges(field.Id!.Value, unrelated, [new(0, target)]));
        Assert.Empty(repo.GetLinkEdges(field.Id!.Value, unrelated));
    }

    /// <summary>Отмена происходит по факту чтения первой страницы, без ожидания условного числа миллисекунд.</summary>
    private sealed class CancelAfterFirstRecordPage : IObserver<DiagnosticListener>,
        IObserver<KeyValuePair<string, object?>>, IDisposable
    {
        private readonly string _path;
        private readonly CancellationTokenSource _cancel;
        private readonly List<IDisposable> _subscriptions = [];
        private readonly IDisposable _all;
        private int _pages;
        public int PagesRead => Volatile.Read(ref _pages);

        public CancelAfterFirstRecordPage(string path, CancellationTokenSource cancel)
        {
            _path = path;
            _cancel = cancel;
            _all = DiagnosticListener.AllListeners.Subscribe(this);
        }

        public void OnNext(DiagnosticListener listener)
        {
            if (listener.Name != "Microsoft.EntityFrameworkCore") return;
            lock (_subscriptions) _subscriptions.Add(listener.Subscribe(this,
                name => name == "Microsoft.EntityFrameworkCore.Database.Command.CommandExecuted"));
        }

        public void OnNext(KeyValuePair<string, object?> value)
        {
            if (value.Value is not CommandExecutedEventData data
                || !(data.Command.Connection?.ConnectionString.Contains(_path, StringComparison.Ordinal) ?? false)
                || !data.Command.CommandText.Contains("db_records", StringComparison.Ordinal)
                || !data.Command.CommandText.Contains("LIMIT", StringComparison.OrdinalIgnoreCase)) return;
            if (Interlocked.Increment(ref _pages) == 1) _cancel.Cancel();
        }

        public void OnError(Exception error) { }
        public void OnCompleted() { }
        public void Dispose()
        {
            _all.Dispose();
            lock (_subscriptions) foreach (var subscription in _subscriptions) subscription.Dispose();
        }
    }
}
