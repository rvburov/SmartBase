using Microsoft.EntityFrameworkCore;
using SmartBase.Core.Models;
using SmartBase.Database;
using SmartBase.Database.Repositories;

namespace SmartBase.Tests.Integration;

/// <summary>Данные из связанных записей живут только вместе со своей связью.</summary>
public sealed class RelationDependentsTests
{
    [Fact]
    public void Deleting_a_relation_removes_only_its_own_data_columns()
    {
        using var f = new Fixture();
        Assert.Equal([f.Phone, f.Count], f.Repo.GetRelationDependentFields(f.Relation).Select(x => x.Id!.Value));
        f.Repo.SetColumnStyle(f.Orders, [f.Phone.ToString()], StyleAspect.Background, "#FF0000");

        f.Repo.DeleteField(f.Relation);

        var left = f.Repo.GetFields(f.Orders).Select(x => x.Id!.Value).ToList();
        Assert.DoesNotContain(f.Phone, left);
        Assert.DoesNotContain(f.Count, left);
        Assert.Contains(f.OtherLookup, left);
        Assert.False(f.Db.Context.ColumnMeta.AsNoTracking().Any(m => m.Key == f.Phone.ToString()));
        Assert.False(f.Db.Context.CellStyles.AsNoTracking().Any(c => c.Key == f.Phone.ToString()));
    }

    [Fact]
    public void The_background_delete_path_removes_them_too()
    {
        using var f = new Fixture();
        f.Repo.DeleteFieldIsolated(f.Relation);
        using var check = new AppDbContext(f.Db.Path);
        var left = check.DbFields.AsNoTracking().Where(x => x.CollectionId == f.Orders).Select(x => x.Id).ToList();
        Assert.DoesNotContain(f.Phone, left);
        Assert.DoesNotContain(f.Count, left);
        Assert.Contains(f.OtherLookup, left);
    }

    [Fact]
    public void Turning_a_relation_into_text_removes_its_data_columns()
    {
        using var f = new Fixture();
        var relation = f.Repo.GetFields(f.Orders).Single(x => x.Id == f.Relation);
        relation.FieldType = "text";
        relation.ConfigJson = "{}";
        f.Repo.UpdateField(relation);
        var left = f.Repo.GetFields(f.Orders).Select(x => x.Id!.Value).ToList();
        Assert.DoesNotContain(f.Phone, left);
        Assert.DoesNotContain(f.Count, left);
        Assert.Contains(f.OtherLookup, left);
        Assert.Equal("Клиент 1, Клиент 2", f.Repo.GetFieldValues(f.Order)[f.Relation]);
    }

    [Fact]
    public void Data_columns_are_saved_as_one_unit_right_after_their_relation()
    {
        using var f = new Fixture();
        var ids = f.Repo.SaveRelationDataColumns(f.Relation,
        [
            new(f.Phone, "Телефон клиента", f.PhoneSource.ToString(), "lookup"),
            new(null, "Последний договор", f.Signed.ToString(), "latest"),
            new(null, "Первый договор", f.Signed.ToString(), "earliest"),
        ]);
        Assert.Equal(f.Phone, ids[0]);
        Assert.DoesNotContain(f.Count, f.Repo.GetFields(f.Orders).Select(x => x.Id!.Value));
        var latest = f.Repo.GetFields(f.Orders).Single(x => x.Id == ids[1]);
        Assert.Equal("rollup", latest.FieldType);
        Assert.Equal("2026-10-20", f.Repo.GetFieldValues(f.Order)[ids[1]]);
        Assert.Equal("2026-03-01", f.Repo.GetFieldValues(f.Order)[ids[2]]);
        Assert.Equal("20.10.2026", FieldTypeInfo.FormatComputedDisplay(f.Repo.GetFieldValues(f.Order)[ids[1]], "rollup"));
        var order = f.Repo.GetColumnOrder(f.Orders);
        Assert.Equal([f.Relation, ids[0], ids[1], ids[2]],
            order.Where(p => int.TryParse(p.Key, out _)).OrderBy(p => p.Value).Select(p => int.Parse(p.Key))
                .Where(id => id == f.Relation || ids.Contains(id)));
    }

    [Fact]
    public void A_failing_data_column_changes_nothing()
    {
        using var f = new Fixture();
        var before = f.Repo.GetFields(f.Orders).Select(x => (x.Id, x.Name, x.FieldType, x.ConfigJson)).ToList();
        // Имя занято обычным свойством коллекции, а дата — не число: ни одна строка не записывается.
        Assert.Throws<InvalidOperationException>(() => f.Repo.SaveRelationDataColumns(f.Relation,
            [new(null, "Новый столбец", f.PhoneSource.ToString(), "lookup"), new(null, "Клиент", f.PhoneSource.ToString(), "lookup")]));
        Assert.Throws<InvalidOperationException>(() => f.Repo.SaveRelationDataColumns(f.Relation,
            [new(f.Phone, "Телефон клиента", f.Signed.ToString(), "sum")]));
        Assert.Equal(before, f.Repo.GetFields(f.Orders).Select(x => (x.Id, x.Name, x.FieldType, x.ConfigJson)).ToList());
    }

    private sealed class Fixture : IDisposable
    {
        public TempDatabase Db { get; } = new();
        public ConstructorRepository Repo => Db.Constructor;
        public int Orders { get; }
        public int Order { get; }
        public int Relation { get; }
        public int Phone { get; }
        public int Count { get; }
        public int OtherLookup { get; }
        public int PhoneSource { get; }
        public int Signed { get; }

        public Fixture()
        {
            var clients = SampleData.AddCollection(Repo, "Клиенты").Id!.Value;
            var phone = SampleData.AddField(Repo, clients, "Телефон", "text").Id!.Value;
            PhoneSource = phone;
            Signed = SampleData.AddField(Repo, clients, "Договор", "date").Id!.Value;
            var client = SampleData.AddRecord(Repo, clients, "Клиент 1").Id!.Value;
            var second = SampleData.AddRecord(Repo, clients, "Клиент 2").Id!.Value;
            Repo.SetFieldValue(client, Signed, "2026-03-01");
            Repo.SetFieldValue(second, Signed, "2026-10-20");
            Orders = SampleData.AddCollection(Repo, "Заказы").Id!.Value;
            Order = SampleData.AddRecord(Repo, Orders, "Заказ").Id!.Value;
            Relation = AddRelation("Клиент", clients, single: false);
            Repo.SetLinks(Relation, Order, [client, second]);
            var other = AddRelation("Плательщик", clients);
            Phone = Repo.AddField(Computed("Телефон клиента", "lookup", new RelationComputedConfig(Relation, phone.ToString()))).Id!.Value;
            Count = Repo.AddField(Computed("Число клиентов", "rollup", new RelationComputedConfig(Relation, Aggregate: "count"))).Id!.Value;
            OtherLookup = Repo.AddField(Computed("Телефон плательщика", "lookup", new RelationComputedConfig(other, phone.ToString()))).Id!.Value;
            // Порядок колонки у вычисляемого столбца — метаданные, которые тоже должны уйти.
            Repo.SaveColumnOrder(Orders, ["__title__", Relation.ToString(), Phone.ToString(), Count.ToString()]);
        }

        private int AddRelation(string name, int target, bool single = true)
        {
            var field = SampleData.AddField(Repo, Orders, name, "relation");
            field.ConfigJson = new RelationConfig(target, single).ToConfigJson();
            Repo.UpdateField(field);
            return field.Id!.Value;
        }

        private DbField Computed(string name, string type, RelationComputedConfig config) => new()
        {
            CollectionId = Orders, Name = name, FieldType = type, ConfigJson = config.ToConfigJson(), Position = 9000,
        };

        public void Dispose() => Db.Dispose();
    }
}
