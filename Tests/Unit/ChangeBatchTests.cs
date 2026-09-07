using SmartBase.Database;

namespace SmartBase.Tests.Unit;

/// <summary>
/// Копилка своих коммитов (<see cref="ChangeBatch"/>): сводит пачку коммитов по базе в один
/// итог — набор типов, момент последнего коммита, признак структурности, — и сообщает, когда
/// пора заводить таймер обновления.
/// </summary>
public sealed class ChangeBatchTests
{
    private static ChangeBus.Change Rec(string action = "update") => new(action, "record", null);
    private static ChangeBus.Change Field()                        => new("create", "field", "Коллекция");

    [Fact]
    public void First_add_reports_empty_before_and_later_adds_do_not()
    {
        var batch = new ChangeBatch();

        Assert.True(batch.IsEmpty);
        Assert.True(batch.Add(@"C:\db\a.db", [Rec()], 10));
        Assert.False(batch.Add(@"C:\db\a.db", [Rec()], 11));
        Assert.False(batch.Add(@"C:\db\b.db", [Rec()], 12));
        Assert.False(batch.IsEmpty);
    }

    [Fact]
    public void Drain_merges_commits_of_one_database_and_keeps_the_latest_stamp()
    {
        var batch = new ChangeBatch();
        batch.Add(@"C:\db\a.db", [Rec("create")], 10);
        batch.Add(@"C:\DB\A.DB", [Rec("update"), Rec("delete")], 30);   // тот же файл иным регистром
        batch.Add(@"C:\db\a.db", [Rec()], 20);                          // запоздавший — момент не откатывает

        var entries = batch.Drain();

        var one = Assert.Single(entries);
        Assert.Equal(30, one.Stamp);
        Assert.Equal(["record"], one.Types.ToList());
        Assert.False(one.Structural);
        Assert.True(batch.IsEmpty);
    }

    [Fact]
    public void Structural_flag_follows_groups_collections_and_fields()
    {
        var batch = new ChangeBatch();
        batch.Add("a", [Rec()], 1);
        batch.Add("a", [Field()], 2);
        batch.Add("b", [new ChangeBus.Change("delete", "records", null)], 3);

        var byPath = batch.Drain().ToDictionary(e => e.DbPath);

        Assert.True(byPath["a"].Structural);
        Assert.Equal(["field", "record"], byPath["a"].Types.Order().ToList());
        Assert.False(byPath["b"].Structural);
    }

    [Fact]
    public void Drain_on_empty_batch_returns_nothing()
    {
        var batch = new ChangeBatch();
        Assert.Empty(batch.Drain());
        Assert.True(batch.IsEmpty);
    }
}
