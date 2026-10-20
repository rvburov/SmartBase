using Microsoft.EntityFrameworkCore;
using SmartBase.Core.Exporters.TableWriters;
using SmartBase.Core.Importers;
using SmartBase.Core.Importers.TableSources;
using SmartBase.Core.Models;
using SmartBase.Database;
using SmartBase.Database.Entities;
using SmartBase.Database.Repositories;

namespace SmartBase.Tests.Integration;

public sealed class RelationIoSafetyTests
{
    private static DbField Relation(ConstructorRepository repo, int source, int target, bool single = false, string separator = "comma")
    {
        var field = SampleData.AddField(repo, source, "Связи", "relation");
        field.ConfigJson = new RelationConfig(target, single, separator).ToConfigJson();
        repo.UpdateField(field);
        return field;
    }

    [Fact]
    public void Text_fallback_refuses_literal_label_versus_list_ambiguity()
    {
        using var database = new TempDatabase();
        var repo = database.Constructor;
        var source = SampleData.AddCollection(repo, "Источник").Id!.Value;
        var target = SampleData.AddCollection(repo, "Цели").Id!.Value;
        var field = Relation(repo, source, target);
        SampleData.AddRecord(repo, target, "А");
        SampleData.AddRecord(repo, target, "Б");
        SampleData.AddRecord(repo, target, "А, Б");
        Assert.Equal(ConstructorRepository.TitleMatch.Ambiguous, repo.ResolveRelationTitles(field.Id!.Value, ["А, Б"])["А, Б"].Match);
        SampleData.AddRecord(repo, target, "В");
        Assert.Equal(ConstructorRepository.TitleMatch.Ambiguous,
            repo.ResolveRelationTitles(field.Id.Value, ["А, Б, В"])["А, Б, В"].Match);
    }

    [Theory]
    [InlineData("dot", "А.Б", "В", "А.Б. В")]
    [InlineData("space", "А", "Б", "А Б")]
    [InlineData("semicolon", " А ", "Б,В", " А ; Б,В")]
    public void Text_fallback_uses_the_exact_separator_without_splitting_label_punctuation(
        string separator, string first, string second, string text)
    {
        using var database = new TempDatabase();
        var repo = database.Constructor;
        var source = SampleData.AddCollection(repo).Id!.Value;
        var target = SampleData.AddCollection(repo).Id!.Value;
        var field = Relation(repo, source, target, separator: separator);
        var a = SampleData.AddRecord(repo, target, first).Id!.Value;
        var b = SampleData.AddRecord(repo, target, second).Id!.Value;
        Assert.Equal([a, b], repo.ResolveRelationTitles(field.Id!.Value, [text])[text].Ids);
    }

    [Fact]
    public void Padded_text_fallback_preserves_exact_whitespace_labels_and_refuses_trimmed_namesakes()
    {
        using var database = new TempDatabase();
        var repo = database.Constructor;
        var source = SampleData.AddCollection(repo).Id!.Value;
        var target = SampleData.AddCollection(repo).Id!.Value;
        var field = Relation(repo, source, target);
        var a = SampleData.AddRecord(repo, target, "Проект А").Id!.Value;
        var b = SampleData.AddRecord(repo, target, "Проект Б").Id!.Value;
        var padded = "  Проект А, Проект Б ";
        Assert.Equal([a, b], repo.ResolveRelationTitles(field.Id!.Value, [padded])[padded].Ids);
        var literal = SampleData.AddRecord(repo, target, " Проект А ").Id!.Value;
        var exact = repo.ResolveRelationTitles(field.Id.Value, [" Проект А ", "Проект А", "  Проект А  "]);
        Assert.Equal([literal], exact[" Проект А "].Ids);
        Assert.Equal([a], exact["Проект А"].Ids);
        Assert.Equal(ConstructorRepository.TitleMatch.Ambiguous, exact["  Проект А  "].Match);
        SampleData.AddRecord(repo, target, "Проект А, Проект Б");
        Assert.Equal(ConstructorRepository.TitleMatch.Ambiguous, repo.ResolveRelationTitles(field.Id.Value, [padded])[padded].Match);
    }

    [Fact]
    public void Structured_bulk_and_undo_keep_database_identity_when_record_ids_overlap()
    {
        using var database = new TempDatabase();
        var repo = database.Constructor;
        var source = SampleData.AddCollection(repo).Id!.Value;
        var target = SampleData.AddCollection(repo).Id!.Value;
        var record = SampleData.AddRecord(repo, source, "Источник").Id!.Value;
        var endpoint = SampleData.AddRecord(repo, target, "Цель").Id!.Value;
        var field = Relation(repo, source, target);
        database.Context.DbRecordLinks.AddRange(
            new DbRecordLinkEntity { FieldId = field.Id!.Value, FromRecordId = record, TargetDbId = 0, ToRecordId = endpoint, Position = 1000 },
            new DbRecordLinkEntity { FieldId = field.Id.Value, FromRecordId = record, TargetDbId = 303, ToRecordId = endpoint, Position = 2000 });
        database.Context.SaveChanges();
        var changed = repo.SetLinksBulk(source, [ConstructorRepository.LinkWrite.FromTargets(record, field.Id.Value, [new(0, endpoint)])]);
        Assert.Equal(1, changed.Changed);
        Assert.Equal([new RelationTarget(0, endpoint), new RelationTarget(303, endpoint)], changed.Changes[0].OldTargets);
        var undo = repo.SetLinksBulk(source, [ConstructorRepository.LinkWrite.FromTargets(record, field.Id.Value,
            changed.Changes[0].OldTargets, ConstructorRepository.LinkTargetsKey(changed.Changes[0].NewTargets), changed.Changes[0].Config)]);
        Assert.Equal(1, undo.Changed);
        Assert.Equal([(endpoint, 0), (endpoint, 303)], repo.GetLinkEdges(field.Id.Value, record));
    }

    [Fact]
    public void Single_or_retarget_change_refuses_existing_many_edges_without_partial_metadata_changes()
    {
        using var database = new TempDatabase();
        var repo = database.Constructor;
        var source = SampleData.AddCollection(repo).Id!.Value;
        var target = SampleData.AddCollection(repo).Id!.Value;
        var other = SampleData.AddCollection(repo).Id!.Value;
        var record = SampleData.AddRecord(repo, source).Id!.Value;
        var a = SampleData.AddRecord(repo, target, "А").Id!.Value;
        var b = SampleData.AddRecord(repo, target, "Б").Id!.Value;
        var field = Relation(repo, source, target);
        repo.SetLinks(field.Id!.Value, record, [a, b]);
        field.ConfigJson = new RelationConfig(target, true).ToConfigJson();
        Assert.Throws<InvalidOperationException>(() => repo.UpdateField(field));
        using (var verification = new AppDbContext(database.Path))
            Assert.False(RelationConfig.FromConfig(verification.DbFields.Single(f => f.Id == field.Id).ConfigJson)!.Single);
        field.ConfigJson = new RelationConfig(other, false).ToConfigJson();
        Assert.Throws<InvalidOperationException>(() => repo.UpdateField(field));
        Assert.Equal([a, b], repo.GetLinkTargetIds(field.Id.Value, record));
    }

    [Fact]
    public void Relation_text_roundtrip_uses_display_property_and_removes_stale_storage()
    {
        using var database = new TempDatabase();
        var repo = database.Constructor;
        var source = SampleData.AddCollection(repo).Id!.Value;
        var target = SampleData.AddCollection(repo).Id!.Value;
        var code = SampleData.AddField(repo, target, "Код").Id!.Value;
        var record = SampleData.AddRecord(repo, source).Id!.Value;
        var endpoint = SampleData.AddRecord(repo, target, "Другое название").Id!.Value;
        repo.SetFieldValue(endpoint, code, "К-1");
        var field = Relation(repo, source, target);
        var config = new RelationConfig(target, false, DisplayField: code.ToString());
        field.ConfigJson = config.ToConfigJson(); repo.UpdateField(field);
        repo.SetLinks(field.Id!.Value, record, [endpoint]);
        field.FieldType = "text"; field.ConfigJson = "{}"; repo.UpdateField(field);
        Assert.Empty(repo.GetLinkEdges(field.Id.Value, record));
        Assert.Equal("К-1", database.Context.DbFieldValues.AsNoTracking().Single(v => v.FieldId == field.Id && v.RecordId == record).Value);
        field.FieldType = "relation"; field.ConfigJson = config.ToConfigJson(); repo.UpdateField(field);
        Assert.Equal([endpoint], repo.GetLinkTargetIds(field.Id.Value, record));
        Assert.False(database.Context.DbFieldValues.AsNoTracking().Any(v => v.FieldId == field.Id));
    }

    [Fact]
    public void Portable_json_roundtrip_remaps_self_edges_properties_and_computed_schema()
    {
        using var sourceDb = new TempDatabase();
        using var targetDb = new TempDatabase(2);
        using var directory = new TempDir();
        var occupied = SampleData.AddCollection(targetDb.Constructor, "Уже существует").Id!.Value;
        SampleData.AddField(targetDb.Constructor, occupied, "Существующее свойство");
        SampleData.AddRecord(targetDb.Constructor, occupied, "Существующая запись");
        var repo = sourceDb.Constructor;
        var source = SampleData.AddCollection(repo, "Граф").Id!.Value;
        var code = SampleData.AddField(repo, source, "Код").Id!.Value;
        var a = SampleData.AddRecord(repo, source, "Одинаково", 10).Id!.Value;
        var b = SampleData.AddRecord(repo, source, "Одинаково", 20).Id!.Value;
        repo.SetFieldValue(a, code, "А"); repo.SetFieldValue(b, code, "Б");
        var relation = Relation(repo, source, source);
        relation.ConfigJson = new RelationConfig(source, false, DisplayField: code.ToString(), SearchField: code.ToString()).ToConfigJson();
        repo.UpdateField(relation);
        repo.SetLinks(relation.Id!.Value, a, [b]);
        var lookup = repo.AddField(new DbField { CollectionId = source, Name = "Показ", FieldType = "lookup",
            ConfigJson = new RelationComputedConfig(relation.Id.Value, code.ToString()).ToConfigJson() });
        var cols = repo.PreparePortableExportColumnsIsolated(source, repo.GetExportColumns(source, true));
        var path = directory.File("graph.json");
        using (var writer = new JsonTableWriter(path))
        {
            writer.BeginTable("Граф", cols, true);
            foreach (var row in repo.ReadExportRowsIsolated(source, cols, false, default, portableRelations: true)) writer.WriteRow(row);
            writer.EndTable(); writer.Finish();
        }
        var input = new JsonTableSource(path);
        var schema = input.Analyze(null, default);
        var imported = targetDb.Constructor.ImportStreamedCollection("Копия", null, schema, input.ReadRows(default), default, batchSize: 1);
        var fields = targetDb.Constructor.GetFields(imported);
        var newRelation = fields.Single(f => f.Name == relation.Name);
        var newCode = fields.Single(f => f.Name == "Код").Id!.Value;
        var newRecords = targetDb.Constructor.GetRecords(imported);
        Assert.Equal([newRecords[1].Id!.Value], targetDb.Constructor.GetLinkTargetIds(newRelation.Id!.Value, newRecords[0].Id!.Value));
        var cfg = RelationConfig.FromConfig(newRelation.ConfigJson)!;
        Assert.Equal(imported, cfg.TargetCollectionId);
        Assert.Equal(newCode.ToString(), cfg.DisplayField);
        Assert.Equal(newCode.ToString(), cfg.SearchField);
        var newLookup = fields.Single(f => f.Name == lookup.Name);
        var computed = RelationComputedConfig.FromConfig(newLookup.ConfigJson)!;
        Assert.Equal(newRelation.Id, computed.RelationFieldId);
        Assert.Equal(newCode.ToString(), computed.TargetField);
        Assert.False(targetDb.Context.DbFieldValues.AsNoTracking().Any(v => v.FieldId == newRelation.Id || v.FieldId == newLookup.Id));
    }

    [Fact]
    public void Group_snapshot_restores_forward_edges_after_all_collections_and_fields_are_mapped()
    {
        using var sourceDb = new TempDatabase();
        using var targetDb = new TempDatabase(2);
        var occupied = SampleData.AddCollection(targetDb.Constructor, "Уже существует").Id!.Value;
        SampleData.AddField(targetDb.Constructor, occupied, "Существующее свойство");
        SampleData.AddRecord(targetDb.Constructor, occupied, "Существующая запись");
        var source = sourceDb.Constructor;
        var group = source.AddGroup(new DbGroup { Name = "Ветка" }).Id!.Value;
        var a = SampleData.AddCollection(source, "А", group).Id!.Value;
        var b = SampleData.AddCollection(source, "Б", group).Id!.Value;
        var code = SampleData.AddField(source, b, "Код").Id!.Value;
        var ar = SampleData.AddRecord(source, a, "Одинаково").Id!.Value;
        var br = SampleData.AddRecord(source, b, "Одинаково").Id!.Value;
        source.SetFieldValue(br, code, "Б-1");
        var relation = Relation(source, a, b);
        relation.ConfigJson = new RelationConfig(b, false, DisplayField: code.ToString()).ToConfigJson();
        source.UpdateField(relation); source.SetLinks(relation.Id!.Value, ar, [br]);
        var imported = targetDb.Constructor.ImportGroup(source.ExportGroup(group), null);
        var target = targetDb.Constructor;
        var collections = target.GetCollectionsByGroup(imported.Id!.Value);
        var newA = collections.Single(c => c.Name == "А").Id!.Value;
        var newB = collections.Single(c => c.Name == "Б").Id!.Value;
        var field = target.GetFields(newA).Single(f => f.FieldType == "relation");
        var newCode = target.GetFields(newB).Single(f => f.Name == "Код").Id!.Value;
        var cfg = RelationConfig.FromConfig(field.ConfigJson)!;
        Assert.Equal(newB, cfg.TargetCollectionId);
        Assert.Equal(newCode.ToString(), cfg.DisplayField);
        Assert.Equal([target.GetRecords(newB).Single().Id!.Value], target.GetLinkTargetIds(field.Id!.Value, target.GetRecords(newA).Single().Id!.Value));
    }

    [Fact]
    public void Unknown_external_identity_rejects_portable_import_before_collection_creation()
    {
        using var database = new TempDatabase();
        var before = database.Constructor.GetAllCollections().Count;
        var schema = new ImportSchema("Название", ["Связь"], 1, 0)
        {
            PortableSchema = new(1, 50, 77, "unknown-source", new() { [99] = "unknown-target" },
                [new(500, "Связь", "relation", new RelationConfig(60, false, TargetDbId: 99).ToConfigJson())]),
        };
        var row = new ImportRow("Запись", [""]) { PortableRow = new(700, "Запись", [], new() { [500] = [new(99, 1)] }) };
        Assert.Throws<InvalidOperationException>(() => database.Constructor.ImportStreamedCollection("Импорт", null, schema, [row], default));
        Assert.Equal(before, database.Constructor.GetAllCollections().Count);
    }
}
