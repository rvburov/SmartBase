using Microsoft.EntityFrameworkCore;
using SmartBase.Core.Exporters;
using SmartBase.Core.Importers;
using SmartBase.Core.Models;
using SmartBase.Database;
using SmartBase.Database.Entities;
using SmartBase.Database.Repositories;

namespace SmartBase.Tests.Integration;

public sealed class RelationProjectionIoTests
{
    private static (int Collection, DbField Code, DbField Relation, int From, int To) Build(TempDatabase database, string projection)
    {
        var repo = database.Constructor;
        var collection = SampleData.AddCollection(repo, "Граф").Id!.Value;
        var code = SampleData.AddField(repo, collection, "Код");
        var from = SampleData.AddRecord(repo, collection, "Источник").Id!.Value;
        var to = SampleData.AddRecord(repo, collection, "Внутренняя цель").Id!.Value;
        repo.SetFieldValue(to, code.Id!.Value, "Внутренний код");
        var relation = SampleData.AddField(repo, collection, "Связь", "relation");
        relation.ConfigJson = new RelationConfig(collection, false,
            DisplayField: projection == "display" ? code.Id.Value.ToString() : RelationConfig.TitleField,
            SearchField: projection == "search" ? code.Id.Value.ToString() : null).ToConfigJson();
        repo.UpdateField(relation);
        repo.SetLinks(relation.Id!.Value, from, [to]);
        if (projection == "lookup") repo.AddField(new DbField { CollectionId = collection, Name = "Показ", FieldType = "lookup",
            ConfigJson = new RelationComputedConfig(relation.Id.Value, code.Id.Value.ToString()).ToConfigJson() });
        return (collection, code, relation, from, to);
    }

    private static void AddExternal(TempDatabase source, int relation, int from, int record) =>
        AddEdge(source, relation, from, record, 303);

    private static void AddEdge(TempDatabase source, int relation, int from, int record, int database)
    {
        source.Context.DbRecordLinks.Add(new DbRecordLinkEntity { FieldId = relation, FromRecordId = from,
            ToRecordId = record, TargetDbId = database, Position = 2000 });
        source.Context.SaveChanges();
    }

    [Theory]
    [InlineData("display")]
    [InlineData("search")]
    [InlineData("lookup")]
    public void Snapshot_refuses_changed_numeric_projection_with_mixed_database_targets_atomically(string projection)
    {
        using var source = new TempDatabase(101);
        using var target = new TempDatabase(202);
        using var external = new TempDatabase(303);
        using var resolver = RelationDbResolver.UseScopedResolver(id => id switch
        { 101 => source.Constructor, 202 => target.Constructor, 303 => external.Constructor, _ => null });
        var data = Build(source, projection);
        var outside = SampleData.AddCollection(external.Constructor).Id!.Value;
        var externalRecord = SampleData.AddRecord(external.Constructor, outside, "Внешняя цель").Id!.Value;
        AddExternal(source, data.Relation.Id!.Value, data.From, externalRecord);
        var occupied = SampleData.AddCollection(target.Constructor, "Уже существует").Id!.Value;
        SampleData.AddField(target.Constructor, occupied, "Занятый id");
        var snapshot = source.Constructor.ExportCollection(data.Collection);
        var before = target.Constructor.GetAllCollections().Count;
        Assert.Throws<InvalidOperationException>(() => target.Constructor.ImportCollection(snapshot, null));
        Assert.Equal(before, target.Constructor.GetAllCollections().Count);
        Assert.Equal([(data.To, 0), (externalRecord, 303)], source.Constructor.GetLinkEdges(data.Relation.Id.Value, data.From));
        Assert.False(target.Context.DbRecordLinks.AsNoTracking().Any());
        Assert.False(target.Context.ChangeTracker.HasChanges());
    }

    [Fact]
    public void Portable_export_refuses_numeric_internal_projection_with_external_mixed_edges()
    {
        using var source = new TempDatabase(101);
        var data = Build(source, "display");
        AddExternal(source, data.Relation.Id!.Value, data.From, 99);
        Assert.Throws<ExportException>(() => source.Constructor.PreparePortableExportColumnsIsolated(data.Collection,
            source.Constructor.GetExportColumns(data.Collection, true)));
        Assert.Equal([(data.To, 0), (99, 303)], source.Constructor.GetLinkEdges(data.Relation.Id.Value, data.From));
    }

    [Fact]
    public void Title_only_mixed_snapshot_keeps_edges_and_rebases_external_database()
    {
        using var source = new TempDatabase(101);
        using var target = new TempDatabase(202);
        using var external = new TempDatabase(303);
        using var resolver = RelationDbResolver.UseScopedResolver(id => id switch
        { 101 => source.Constructor, 202 => target.Constructor, 303 => external.Constructor, _ => null });
        var data = Build(source, "title");
        var outside = SampleData.AddCollection(external.Constructor).Id!.Value;
        var externalRecord = SampleData.AddRecord(external.Constructor, outside, "Внешняя цель").Id!.Value;
        AddExternal(source, data.Relation.Id!.Value, data.From, externalRecord);
        var imported = target.Constructor.ImportCollection(source.Constructor.ExportCollection(data.Collection), null);
        var relation = target.Constructor.GetFields(imported.Id!.Value).Single(f => f.FieldType == "relation");
        var records = target.Constructor.GetRecords(imported.Id.Value);
        Assert.Equal([(records.Single(r => r.Title == "Внутренняя цель").Id!.Value, 0), (externalRecord, 303)],
            target.Constructor.GetLinkEdges(relation.Id!.Value, records.Single(r => r.Title == "Источник").Id!.Value));
    }

    [Fact]
    public void Portable_rows_do_not_include_an_excluded_title_column()
    {
        using var source = new TempDatabase();
        var data = Build(source, "title");
        var columns = source.Constructor.GetExportColumns(data.Collection, true).Where(c => c.Key != "__title__").ToList();
        columns = source.Constructor.PreparePortableExportColumnsIsolated(data.Collection, columns);
        var rows = source.Constructor.ReadExportRowsIsolated(data.Collection, columns, false, default, portableRelations: true)
            .Select(r => System.Text.Json.JsonSerializer.Deserialize<RelationPortableRow>(r[^1])!).ToList();
        Assert.Equal(2, rows.Count);
        Assert.All(rows, row => Assert.Equal("", row.Title));
        Assert.Contains(rows, row => row.Id == data.From);
        Assert.Contains(rows, row => row.Id == data.To);
    }

    [Fact]
    public void Portable_import_refuses_changed_numeric_mixed_projection_and_rolls_back_all_rows()
    {
        using var source = new TempDatabase(101);
        using var target = new TempDatabase(202);
        using var external = new TempDatabase(303);
        using var resolver = RelationDbResolver.UseScopedResolver(id => id switch
        { 101 => source.Constructor, 202 => target.Constructor, 303 => external.Constructor, _ => null });
        var data = Build(source, "display");
        var outside = SampleData.AddCollection(external.Constructor).Id!.Value;
        var externalRecord = SampleData.AddRecord(external.Constructor, outside, "Внешняя цель").Id!.Value;
        var occupied = SampleData.AddCollection(target.Constructor, "Уже существует").Id!.Value;
        SampleData.AddField(target.Constructor, occupied, "Занятый id");
        var fields = source.Constructor.GetFields(data.Collection);
        var portable = new RelationPortableSchema(1, data.Collection, 101, source.Constructor.RelationDatabaseIdentity,
            new() { [303] = external.Constructor.RelationDatabaseIdentity },
            fields.Select(f => new RelationPortableField(f.Id!.Value, f.Name, f.FieldType, f.ConfigJson)).ToList());
        var schema = new ImportSchema("Название", fields.Select(f => f.Name).ToList(), 2, 0) { PortableSchema = portable };
        var a = new ImportRow("Источник", ["", ""]) { PortableRow = new(data.From, "Источник", [],
            new() { [data.Relation.Id!.Value] = [new(0, data.To), new(303, externalRecord)] }) };
        var b = new ImportRow("Цель", ["Код", ""]) { PortableRow = new(data.To, "Цель", new() { [data.Code.Id!.Value] = "Код" }, []) };
        var before = target.Constructor.GetAllCollections().Count;
        Assert.Throws<ImportException>(() => target.Constructor.ImportStreamedCollection("Импорт", null, schema, [a, b], default, batchSize: 1));
        Assert.Equal(before, target.Constructor.GetAllCollections().Count);
        Assert.False(target.Context.DbRecordLinks.AsNoTracking().Any());
        Assert.False(target.Context.ChangeTracker.HasChanges());
    }
}
