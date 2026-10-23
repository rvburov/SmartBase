using Microsoft.EntityFrameworkCore;
using SmartBase.Core.Models;
using SmartBase.Database.Repositories;

namespace SmartBase.Tests.Integration;

/// <summary>
/// Автоподбор по нескольким парам свойств (ИНН + КПП) и сравнение текста без учета регистра и
/// пробелов по краям. Первая пара обязательна, в остальных пустое совпадает только с пустым.
/// </summary>
public sealed class RelationAutoPairsTests
{
    private sealed class Fixture : IDisposable
    {
        public TempDatabase Database { get; } = new();
        public ConstructorRepository Repo => Database.Constructor;
        public int Clients { get; }
        public int Inn { get; }
        public int Kpp { get; }
        public int Orders { get; }
        public int OrderInn { get; }
        public int OrderKpp { get; }
        public int Relation { get; }

        public Fixture()
        {
            Clients = SampleData.AddCollection(Repo, "Клиенты").Id!.Value;
            Inn = SampleData.AddField(Repo, Clients, "ИНН", "text").Id!.Value;
            Kpp = SampleData.AddField(Repo, Clients, "КПП", "text").Id!.Value;
            Orders = SampleData.AddCollection(Repo, "Заказы").Id!.Value;
            OrderInn = SampleData.AddField(Repo, Orders, "ИНН покупателя", "text").Id!.Value;
            OrderKpp = SampleData.AddField(Repo, Orders, "КПП покупателя", "text").Id!.Value;
            var relation = SampleData.AddField(Repo, Orders, "Покупатель", "relation");
            relation.ConfigJson = new RelationConfig(Clients, true).ToConfigJson();
            Repo.UpdateField(relation);
            Relation = relation.Id!.Value;
        }

        public int Client(string title, string inn, string kpp)
        {
            var id = SampleData.AddRecord(Repo, Clients, title).Id!.Value;
            Repo.SetFieldValue(id, Inn, inn);
            Repo.SetFieldValue(id, Kpp, kpp);
            return id;
        }

        public int Order(string title, string inn, string kpp)
        {
            var id = SampleData.AddRecord(Repo, Orders, title).Id!.Value;
            Repo.SetFieldValue(id, OrderInn, inn);
            Repo.SetFieldValue(id, OrderKpp, kpp);
            return id;
        }

        public DbField Proposed(RelationAutoRule rule, bool single = true) => new()
        {
            Id = Relation, CollectionId = Orders, Name = "Покупатель", FieldType = "relation", Position = 1000,
            ConfigJson = new RelationConfig(Clients, single, Auto: rule).ToConfigJson(),
        };

        public RelationAutoRule InnAndKpp(bool fold = false) =>
            new([new(OrderInn.ToString(), Inn.ToString()), new(OrderKpp.ToString(), Kpp.ToString())], fold);

        public int[] Links(int order) => Repo.GetLinkEdges(Relation, order).Select(e => e.ToRecordId).ToArray();

        public void Dispose() => Database.Dispose();
    }

    [Fact]
    public void Branches_with_the_same_inn_are_told_apart_by_kpp()
    {
        using var f = new Fixture();
        var head = f.Client("Головной офис", "7700", "01");
        var branch = f.Client("Филиал", "7700", "02");
        var toBranch = f.Order("Заказ 1", "7700", "02");
        var toHead = f.Order("Заказ 2", "7700", "01");

        // Одна пара: у ИНН два клиента — неоднозначно, с примерами записей.
        var byInn = f.Repo.PreviewAutoRelation(f.Proposed(new RelationAutoRule(f.OrderInn.ToString(), f.Inn.ToString())));
        Assert.Equal(RelationAutoStatus.Ambiguous, byInn.Status);
        Assert.Equal("Для 2 записей найдено несколько целей, например «Заказ 1», «Заказ 2». " +
                     "Добавьте пару свойств или выберите «Несколько».", byInn.Message);

        var preview = f.Repo.PreviewAutoRelation(f.Proposed(f.InnAndKpp()));
        Assert.True(preview.CanApply, preview.Message);
        f.Repo.SaveAutoRelationField(f.Proposed(f.InnAndKpp()), preview);
        Assert.Equal([branch], f.Links(toBranch));
        Assert.Equal([head], f.Links(toHead));
        // Правка второй пары пересчитывает связь.
        f.Repo.SetFieldValue(toBranch, f.OrderKpp, "01");
        f.Repo.RefreshAutoRelationsForChange(f.Orders, f.OrderKpp);
        Assert.Equal([head], f.Links(toBranch));
        f.Repo.SetFieldValue(head, f.Kpp, "09");
        f.Repo.RefreshAutoRelationsForChange(f.Clients, f.Kpp);
        Assert.Empty(f.Links(toBranch));
    }

    [Fact]
    public void A_sole_trader_without_kpp_matches_by_inn_and_an_empty_inn_matches_nothing()
    {
        using var f = new Fixture();
        var trader = f.Client("ИП Петров", "5000", "");
        f.Client("ООО Ромашка", "7700", "01");
        var traderOrder = f.Order("Заказ ИП", "5000", " ");
        var noKpp = f.Order("Заказ без КПП", "7700", "");
        var noInn = f.Order("Заказ без ИНН", "", "01");

        var preview = f.Repo.PreviewAutoRelation(f.Proposed(f.InnAndKpp()));
        Assert.True(preview.CanApply, preview.Message);
        Assert.Equal(2, preview.Unmatched);
        Assert.Equal(1, preview.UnmatchedEmpty);
        f.Repo.SaveAutoRelationField(f.Proposed(f.InnAndKpp()), preview);
        Assert.Equal([trader], f.Links(traderOrder));
        Assert.Empty(f.Links(noKpp));
        Assert.Empty(f.Links(noInn));
    }

    [Fact]
    public void Folding_matches_case_and_edge_spaces_and_counts_such_matches()
    {
        using var f = new Fixture();
        var client = f.Client("ООО Ромашка", "77 00", "ab");
        var order = f.Order("Заказ", "  77 00 ", "AB");
        var exact = f.Repo.PreviewAutoRelation(f.Proposed(f.InnAndKpp()));
        Assert.Equal(1, exact.Unmatched);

        var preview = f.Repo.PreviewAutoRelation(f.Proposed(f.InnAndKpp(fold: true)), detailed: true);
        Assert.True(preview.CanApply, preview.Message);
        Assert.Equal(0, preview.Unmatched);
        Assert.Equal(1, preview.FoldOnly);
        // Без окна точный индекс не строится.
        Assert.Equal(0, f.Repo.PreviewAutoRelation(f.Proposed(f.InnAndKpp(fold: true))).FoldOnly);
        f.Repo.SaveAutoRelationField(f.Proposed(f.InnAndKpp(fold: true)), preview);
        Assert.Equal([client], f.Links(order));
        Assert.True(RelationConfig.FromConfig(f.Repo.GetFields(f.Orders).Single(x => x.Id == f.Relation).ConfigJson)!.Auto!.Fold);
    }

    [Fact]
    public void A_pair_on_the_field_itself_or_a_missing_or_incompatible_pair_is_named()
    {
        using var f = new Fixture();
        var number = SampleData.AddField(f.Repo, f.Clients, "Номер", "number").Id!.Value;
        var self = new RelationAutoRule([new(f.OrderInn.ToString(), f.Inn.ToString()), new(f.Relation.ToString(), f.Kpp.ToString())]);
        Assert.Equal("Пара 2: свойство не может подбирать связи по самому себе.", f.Repo.PreviewAutoRelation(f.Proposed(self)).Message);
        var incompatible = new RelationAutoRule([new(f.OrderInn.ToString(), f.Inn.ToString()), new(f.OrderKpp.ToString(), number.ToString())]);
        Assert.Equal("Пара 2: для сопоставления выбраны несовместимые типы свойств.", f.Repo.PreviewAutoRelation(f.Proposed(incompatible)).Message);
        var missing = new RelationAutoRule([new(f.OrderInn.ToString(), f.Inn.ToString()), new(f.OrderKpp.ToString(), "999999")]);
        Assert.Equal("Пара 2: свойство для сопоставления не найдено.", f.Repo.PreviewAutoRelation(f.Proposed(missing)).Message);
    }

    [Fact]
    public void A_change_of_any_pair_after_the_preview_requires_a_new_one()
    {
        using var f = new Fixture();
        f.Client("Головной офис", "7700", "01");
        var order = f.Order("Заказ", "7700", "01");
        var preview = f.Repo.PreviewAutoRelation(f.Proposed(f.InnAndKpp()));
        f.Repo.SetFieldValue(order, f.OrderKpp, "02");
        var error = Assert.Throws<RelationAutoApplyException>(() => f.Repo.SaveAutoRelationField(f.Proposed(f.InnAndKpp()), preview));
        Assert.Equal(RelationAutoStatus.Conflict, error.Status);
    }

    [Theory]
    [InlineData("{\"sourceField\":\"__title__\",\"targetField\":\"2147483647\"}", RelationAutoState.Lost)]
    [InlineData("{\"sourceField\":\"__title__\",\"targetField\":\"2147483647\",\"format\":3,\"pairs\":[]}", RelationAutoState.Newer)]
    [InlineData("{\"sourceField\":\"__title__\",\"targetField\":\"2147483647\",\"format\":2,\"pairs\":[]}", RelationAutoState.Damaged)]
    public void A_rule_that_cannot_be_matched_keeps_links_and_still_allows_a_rename(string auto, RelationAutoState state)
    {
        using var f = new Fixture();
        var client = f.Client("Головной офис", "7700", "01");
        var order = f.Order("Заказ", "7700", "01");
        f.Repo.SaveAutoRelationField(f.Proposed(f.InnAndKpp()), f.Repo.PreviewAutoRelation(f.Proposed(f.InnAndKpp())));
        Assert.Equal([client], f.Links(order));
        var field = f.Repo.GetFields(f.Orders).Single(x => x.Id == f.Relation);
        // Так правило оставляет прежняя или более новая версия программы.
        f.Database.Context.Database.ExecuteSqlRaw("UPDATE db_fields SET ConfigJson = {0} WHERE Id = {1}",
            $"{{\"targetCollectionId\":{f.Clients},\"single\":true,\"separator\":\"comma\",\"auto\":{auto}}}", f.Relation);
        field = f.Repo.GetFields(f.Orders).Single(x => x.Id == f.Relation);
        Assert.Equal(state, RelationConfig.FromConfig(field.ConfigJson)!.Auto!.State);

        f.Repo.SetFieldValue(order, f.OrderKpp, "02");
        Assert.Empty(f.Repo.RefreshAutoRelationsForChange(f.Orders, f.OrderKpp));
        Assert.Equal([client], f.Links(order));
        Assert.Equal(RelationAutoStatus.InvalidRule, f.Repo.PreviewAutoRelation(field).Status);
        // Ручная правка ячейки по-прежнему закрыта.
        Assert.ThrowsAny<InvalidOperationException>(() => f.Repo.SetLinks(f.Relation, order, []));

        var configBefore = field.ConfigJson;
        field.Name = "Покупатель по ИНН";
        f.Repo.UpdateField(field);
        var renamed = f.Repo.GetFields(f.Orders).Single(x => x.Id == f.Relation);
        Assert.Equal("Покупатель по ИНН", renamed.Name);
        Assert.Equal(configBefore, renamed.ConfigJson);
        Assert.Equal([client], f.Links(order));
        // Новые пары задаются как обычно; подменить правило на непрочитанное другое нельзя.
        renamed.ConfigJson = new RelationConfig(f.Clients, true, Auto: RelationAutoRule.LostRule()).ToConfigJson();
        if (state != RelationAutoState.Lost) Assert.Throws<InvalidOperationException>(() => f.Repo.UpdateField(renamed));
    }

    [Fact]
    public void A_newer_rule_is_information_in_the_integrity_check()
    {
        using var f = new Fixture();
        f.Database.Context.Database.ExecuteSqlRaw("UPDATE db_fields SET ConfigJson = {0} WHERE Id = {1}",
            $"{{\"targetCollectionId\":{f.Clients},\"single\":true,\"auto\":{{\"sourceField\":\"__title__\",\"targetField\":\"2147483647\",\"format\":3}}}}",
            f.Relation);
        var report = f.Repo.ScanRelationIntegrityAsync(new RelationIntegrityScanOptions(FieldId: f.Relation)).GetAwaiter().GetResult();
        var issue = Assert.Single(report.Issues, i => i.Kind == RelationIntegrityIssueKind.NewerAutoRule);
        Assert.True(issue.IsInformational);
        Assert.DoesNotContain(report.Issues, i => i.Kind is RelationIntegrityIssueKind.MissingDependency or RelationIntegrityIssueKind.InvalidConfiguration);

        f.Database.Context.Database.ExecuteSqlRaw("UPDATE db_fields SET ConfigJson = {0} WHERE Id = {1}",
            $"{{\"targetCollectionId\":{f.Clients},\"single\":true,\"auto\":{{\"sourceField\":\"__title__\",\"targetField\":\"2147483647\"}}}}",
            f.Relation);
        report = f.Repo.ScanRelationIntegrityAsync(new RelationIntegrityScanOptions(FieldId: f.Relation)).GetAwaiter().GetResult();
        Assert.Contains(report.Issues, i => i.Kind == RelationIntegrityIssueKind.InvalidConfiguration && i.IsConfirmed);
    }

    [Fact]
    public void The_integrity_check_looks_at_every_pair()
    {
        using var f = new Fixture();
        f.Repo.SaveAutoRelationField(f.Proposed(f.InnAndKpp()), f.Repo.PreviewAutoRelation(f.Proposed(f.InnAndKpp())));
        f.Repo.DeleteField(f.Kpp);
        var report = f.Repo.ScanRelationIntegrityAsync(new RelationIntegrityScanOptions(FieldId: f.Relation)).GetAwaiter().GetResult();
        Assert.Contains(report.Issues, i => i.Kind == RelationIntegrityIssueKind.MissingDependency);
    }

    [Fact]
    public void A_copied_collection_remaps_every_pair()
    {
        using var f = new Fixture();
        var name = SampleData.AddField(f.Repo, f.Clients, "Головной ИНН", "text").Id!.Value;
        var self = SampleData.AddField(f.Repo, f.Clients, "Головной офис", "relation");
        self.ConfigJson = new RelationConfig(f.Clients, true,
            Auto: new([new(name.ToString(), f.Inn.ToString()), new(f.Kpp.ToString(), f.Kpp.ToString())], fold: true)).ToConfigJson();
        f.Repo.UpdateField(self);
        var copy = f.Repo.DuplicateCollection(f.Clients).Id!.Value;
        var fields = f.Repo.GetFields(copy);
        string Id(string field) => fields.Single(x => x.Name == field).Id!.Value.ToString();
        var rule = RelationConfig.FromConfig(fields.Single(x => x.Name == "Головной офис").ConfigJson)!.Auto!;
        Assert.Equal(new RelationAutoRule([new(Id("Головной ИНН"), Id("ИНН")), new(Id("КПП"), Id("КПП"))], fold: true), rule);
    }

    [Fact]
    public void Moving_records_creates_the_auto_field_with_its_own_keys()
    {
        using var f = new Fixture();
        var client = f.Client("Головной офис", "7700", "01");
        var order = f.Order("Заказ", "7700", "01");
        f.Repo.SaveAutoRelationField(f.Proposed(f.InnAndKpp()), f.Repo.PreviewAutoRelation(f.Proposed(f.InnAndKpp())));
        var archive = SampleData.AddCollection(f.Repo, "Архив заказов").Id!.Value;

        var moved = f.Repo.MoveRecordsToCollectionIsolated(f.Database.Path, f.Orders, [order], archive,
            createMissingFields: true, sourceDbId: 0, targetDbId: 0);

        var fields = f.Repo.GetFields(archive);
        string Id(string field) => fields.Single(x => x.Name == field).Id!.Value.ToString();
        var relation = fields.Single(x => x.Name == "Покупатель");
        Assert.Equal(new RelationAutoRule([new(Id("ИНН покупателя"), f.Inn.ToString()), new(Id("КПП покупателя"), f.Kpp.ToString())]),
            RelationConfig.FromConfig(relation.ConfigJson)!.Auto);
        Assert.Equal([client], f.Repo.GetLinkEdges(relation.Id!.Value, moved.Single()).Select(e => e.ToRecordId));
        // Правило работает по свойствам новой коллекции.
        var kpp = int.Parse(Id("КПП покупателя"));
        f.Repo.SetFieldValue(moved.Single(), kpp, "99");
        f.Repo.RefreshAutoRelationsForChange(archive, kpp);
        Assert.Empty(f.Repo.GetLinkEdges(relation.Id!.Value, moved.Single()));
    }

    [Fact]
    public void Export_requires_the_source_property_of_every_pair()
    {
        using var f = new Fixture();
        f.Repo.SaveAutoRelationField(f.Proposed(f.InnAndKpp()), f.Repo.PreviewAutoRelation(f.Proposed(f.InnAndKpp())));
        var columns = f.Repo.GetExportColumns(f.Orders, true).Where(c => c.FieldId != f.OrderKpp).ToList();
        Assert.Throws<SmartBase.Core.Exporters.ExportException>(() => f.Repo.PreparePortableExportColumnsIsolated(f.Orders, columns));
    }
}
