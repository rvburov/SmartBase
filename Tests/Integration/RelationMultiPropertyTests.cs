using SmartBase.Core.Models;
using SmartBase.Database.Repositories;

namespace SmartBase.Tests.Integration;

/// <summary>Показ и поиск связи по нескольким свойствам цели.</summary>
public sealed class RelationMultiPropertyTests
{
    [Fact]
    public void A_link_shows_several_properties_and_skips_empty_ones()
    {
        using var f = new Fixture();
        Assert.Equal(["ДЕТ-001 · Кронштейн", "ДЕТ-002 · Пластина"],
            f.Repo.GetRelationLinks(f.Relation, f.Node).Select(l => l.Display));
        f.Repo.SetFieldValue(f.Bracket, f.Article, "");
        Assert.Equal("Кронштейн", f.Repo.GetRelationLinks(f.Relation, f.Node)[0].Display);
        // Сортировка и фильтр колонки видят тот же составной показ, что и ячейка.
        Assert.Equal("Кронштейн, ДЕТ-002 · Пластина", f.Repo.GetColumnValues(f.Nodes, f.Relation.ToString())[f.Node]);
    }

    [Fact]
    public void Search_finds_a_record_by_any_of_the_chosen_properties()
    {
        using var f = new Fixture();
        var key = f.Config().EffectiveSearchField;
        Assert.Equal([f.Plate], f.Repo.SearchRelationRecords(f.Parts, key, "алюм").Select(v => v.RecordId));
        Assert.Equal([f.Bracket], f.Repo.SearchRelationRecords(f.Parts, key, "дет-001").Select(v => v.RecordId));
        Assert.Equal([f.Bracket, f.Plate], f.Repo.SearchRelationRecords(f.Parts, key, "дет").Select(v => v.RecordId));
        // Название в поиск не выбрано — по нему не находится.
        Assert.Empty(f.Repo.SearchRelationRecords(f.Parts, key, "Пласт"));
    }

    // Название уже входит в составной показ — список выбора не повторяет его второй строкой.
    [Fact]
    public async Task The_picker_does_not_repeat_a_title_that_is_already_shown()
    {
        using var f = new Fixture();
        var data = await f.Repo.GetRelationPickerDataAsync(f.Node, f.Config(), null,
            [new RelationTarget(0, f.Bracket)]);
        Assert.Equal("ДЕТ-001 · Кронштейн", data.SelectedLabels.Values.Single());
        Assert.Empty(data.SelectedTitles);
    }

    [Fact]
    public void A_deleted_property_drops_out_of_the_display_without_breaking_it()
    {
        using var f = new Fixture();
        f.Repo.DeleteField(f.Article);
        Assert.Equal(["Кронштейн", "Пластина"], f.Repo.GetRelationLinks(f.Relation, f.Node).Select(l => l.Display));
    }

    [Fact]
    public void Copying_a_collection_remaps_every_chosen_property()
    {
        using var f = new Fixture();
        var self = SampleData.AddField(f.Repo, f.Parts, "Аналог", "relation");
        self.ConfigJson = new RelationConfig(f.Parts, true, DisplayField: $"{f.Article}|{f.Material}",
            SearchField: $"{f.Material}|{RelationConfig.TitleField}").ToConfigJson();
        f.Repo.UpdateField(self);
        var copy = f.Repo.DuplicateCollection(f.Parts).Id!.Value;
        var fields = f.Repo.GetFields(copy);
        var cfg = RelationConfig.FromConfig(fields.Single(x => x.Name == "Аналог").ConfigJson)!;
        var article = fields.Single(x => x.Name == "Артикул").Id!.Value.ToString();
        var material = fields.Single(x => x.Name == "Материал").Id!.Value.ToString();
        Assert.Equal([article, material], cfg.DisplayKeys);
        Assert.Equal([material, RelationConfig.TitleField], cfg.SearchKeys);
    }

    private sealed class Fixture : IDisposable
    {
        private readonly TempDatabase _db = new();
        public ConstructorRepository Repo => _db.Constructor;
        public int Parts { get; }
        public int Article { get; }
        public int Material { get; }
        public int Bracket { get; }
        public int Plate { get; }
        public int Nodes { get; }
        public int Node { get; }
        public int Relation { get; }

        public Fixture()
        {
            Parts = SampleData.AddCollection(Repo, "Детали").Id!.Value;
            Article = SampleData.AddField(Repo, Parts, "Артикул", "text").Id!.Value;
            Material = SampleData.AddField(Repo, Parts, "Материал", "text").Id!.Value;
            Bracket = SampleData.AddRecord(Repo, Parts, "Кронштейн").Id!.Value;
            Plate = SampleData.AddRecord(Repo, Parts, "Пластина").Id!.Value;
            Repo.SetFieldValue(Bracket, Article, "ДЕТ-001");
            Repo.SetFieldValue(Bracket, Material, "Сталь 3");
            Repo.SetFieldValue(Plate, Article, "ДЕТ-002");
            Repo.SetFieldValue(Plate, Material, "Алюминий");
            Nodes = SampleData.AddCollection(Repo, "Узлы").Id!.Value;
            Node = SampleData.AddRecord(Repo, Nodes, "Узел").Id!.Value;
            var relation = SampleData.AddField(Repo, Nodes, "Детали", "relation");
            relation.ConfigJson = new RelationConfig(Parts, false, DisplayField: $"{Article}|{RelationConfig.TitleField}",
                SearchField: $"{Article}|{Material}").ToConfigJson();
            Repo.UpdateField(relation);
            Relation = relation.Id!.Value;
            Repo.SetLinks(Relation, Node, [Bracket, Plate]);
        }

        public RelationConfig Config() =>
            RelationConfig.FromConfig(Repo.GetFields(Nodes).Single(x => x.Id == Relation).ConfigJson)!;

        public void Dispose() => _db.Dispose();
    }
}
