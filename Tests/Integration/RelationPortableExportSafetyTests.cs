using SmartBase.Core.Exporters;
using SmartBase.Core.Models;
using SmartBase.Database;
using SmartBase.Database.Entities;
using SmartBase.Database.Repositories;

namespace SmartBase.Tests.Integration;

public sealed class RelationPortableExportSafetyTests
{
    [Theory]
    [InlineData("lookup")]
    [InlineData("rollup")]
    public void Self_computation_requires_its_target_property_even_when_relation_displays_the_title(string type)
    {
        using var database = new TempDatabase();
        var repo = database.Constructor;
        var collection = SampleData.AddCollection(repo, "Граф").Id!.Value;
        var number = SampleData.AddField(repo, collection, "Сумма", "number").Id!.Value;
        var relation = Relation(repo, collection, collection);
        var computed = repo.AddField(new DbField
        {
            CollectionId = collection, Name = "Вычисление", FieldType = type,
            ConfigJson = new RelationComputedConfig(relation, number.ToString(), type == "lookup" ? "lookup" : "sum").ToConfigJson(),
        });
        var all = repo.GetExportColumns(collection, includeHidden: true);
        var incomplete = all.Where(c => c.FieldId != number).ToList();

        var error = Assert.Throws<ExportException>(() => repo.PreparePortableExportColumnsIsolated(collection, incomplete));
        Assert.Contains(computed.Name, error.Message, StringComparison.Ordinal);
        var complete = repo.PreparePortableExportColumnsIsolated(collection, all);
        Assert.Contains(complete[0].PortableSchema!.Fields, f => f.Id == number);
    }

    [Fact]
    public void An_external_computations_target_property_is_not_a_required_source_column()
    {
        using var database = new TempDatabase();
        var repo = database.Constructor;
        var source = SampleData.AddCollection(repo, "Источник").Id!.Value;
        var target = SampleData.AddCollection(repo, "Цели").Id!.Value;
        var number = SampleData.AddField(repo, target, "Сумма", "number").Id!.Value;
        var relation = Relation(repo, source, target);
        repo.AddField(new DbField
        {
            CollectionId = source, Name = "Сумма целей", FieldType = "rollup",
            ConfigJson = new RelationComputedConfig(relation, number.ToString(), "sum").ToConfigJson(),
        });

        var columns = repo.PreparePortableExportColumnsIsolated(source, repo.GetExportColumns(source, true));
        Assert.DoesNotContain(columns[0].PortableSchema!.Fields, f => f.Id == number);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(101)]
    public void A_visible_self_link_to_a_hidden_target_requires_exporting_hidden_records(int targetDbId)
    {
        using var database = new TempDatabase(101);
        var repo = database.Constructor;
        using var resolver = RelationDbResolver.UseScopedResolver(id => id == 101 ? repo : null,
            candidate => candidate.DbPath == database.Path ? 101 : 0);
        var collection = SampleData.AddCollection(repo, "Граф").Id!.Value;
        var code = SampleData.AddField(repo, collection, "Код").Id!.Value;
        var source = SampleData.AddRecord(repo, collection, "Видимая").Id!.Value;
        var target = SampleData.AddRecord(repo, collection, "Скрытая").Id!.Value;
        var relation = Relation(repo, collection, collection, targetDbId);
        repo.AddField(new DbField
        {
            CollectionId = collection, Name = "Код цели", FieldType = "lookup",
            ConfigJson = new RelationComputedConfig(relation, code.ToString()).ToConfigJson(),
        });
        database.Context.DbRecordLinks.Add(new DbRecordLinkEntity
        {
            FieldId = relation, FromRecordId = source, ToRecordId = target, TargetDbId = targetDbId, Position = 1000,
        });
        database.Context.SaveChanges();
        repo.SaveRecordDisplay(collection, [(target, false, 1)]);
        var columns = repo.GetExportColumns(collection, includeHidden: true);

        var error = Assert.Throws<ExportException>(() => repo.PreparePortableExportColumnsIsolated(collection, columns, excludeHidden: true));
        Assert.Contains("скрыт", error.Message, StringComparison.Ordinal);
        var complete = repo.PreparePortableExportColumnsIsolated(collection, columns, excludeHidden: false);
        Assert.Equal(2, repo.ReadExportRowsIsolated(collection, complete, false, default, portableRelations: true).Count());
        Assert.Equal([(target, targetDbId)], repo.GetLinkEdges(relation, source));
        // Ребро, целиком оставшееся за границей экспорта, не препятствует выгрузке видимых записей.
        repo.SaveRecordDisplay(collection, [(source, false, 1), (target, false, 1)]);
        Assert.NotNull(repo.PreparePortableExportColumnsIsolated(collection, columns, excludeHidden: true)[0].PortableSchema);
    }

    private static int Relation(ConstructorRepository repo, int source, int target, int targetDbId = 0) =>
        repo.AddField(new DbField
        {
            CollectionId = source, Name = "Связь", FieldType = "relation",
            ConfigJson = new RelationConfig(target, false, TargetDbId: targetDbId).ToConfigJson(),
        }).Id!.Value;
}
