using SmartBase.Core.Models;
using SmartBase.Database;
using SmartBase.Database.Repositories;

namespace SmartBase.Tests.Integration;

/// <summary>
/// Настройка связи «Запретить удаление»: запись, на которую ссылается такая связь, не удаляется и
/// не переносится ни одним путем репозитория, пока ссылка есть. Обычная связь по-прежнему просто
/// снимается. Окно удаления называет, кто ссылается.
/// </summary>
public sealed class RelationRestrictDeleteTests
{
    [Fact]
    public void A_restricted_link_keeps_the_target_record()
    {
        using var f = new Fixture(restrict: true);
        var error = Assert.Throws<RelationDeleteRestrictedException>(() => f.Repo.DeleteRecord(f.Client));
        Assert.Contains("«Счета · Клиент» — 1 запись", error.Message, StringComparison.Ordinal);
        Assert.NotNull(f.Repo.GetRecord(f.Client));
        Assert.Equal([f.Client], f.Repo.GetLinkEdges(f.Relation, f.Invoice).Select(e => e.ToRecordId));
        // Запись без ссылок удаляется как обычно.
        f.Repo.DeleteRecord(f.Free);
        Assert.Null(f.Repo.GetRecord(f.Free));
    }

    [Fact]
    public void Removing_the_link_allows_the_delete()
    {
        using var f = new Fixture(restrict: true);
        f.Repo.SetLinks(f.Relation, f.Invoice, []);
        f.Repo.DeleteRecord(f.Client);
        Assert.Null(f.Repo.GetRecord(f.Client));
    }

    [Fact]
    public void A_plain_link_is_just_removed_with_the_target()
    {
        using var f = new Fixture(restrict: false);
        f.Repo.DeleteRecord(f.Client);
        Assert.Null(f.Repo.GetRecord(f.Client));
        Assert.Empty(f.Repo.GetLinkEdges(f.Relation, f.Invoice));
    }

    [Fact]
    public void A_batch_is_refused_before_its_first_record()
    {
        using var f = new Fixture(restrict: true);
        Assert.Throws<RelationDeleteRestrictedException>(() => f.Repo.DeleteRecordsIsolated([f.Free, f.Client]));
        Assert.NotNull(f.Repo.GetRecord(f.Free));
        Assert.NotNull(f.Repo.GetRecord(f.Client));
        Assert.Throws<RelationDeleteRestrictedException>(() => f.Repo.DeleteCalendarRecord(f.Client));
    }

    [Fact]
    public void A_source_deleted_together_with_its_target_does_not_block()
    {
        using var f = new Fixture(restrict: true);
        f.Repo.DeleteRecordsData([f.Invoice, f.Client]);
        Assert.Equal([f.Free], f.Repo.GetRecords(f.Clients).Select(r => r.Id!.Value));
        Assert.Empty(f.Repo.GetRecords(f.Invoices));
    }

    [Fact]
    public void A_collection_or_group_with_referenced_records_stays()
    {
        using var f = new Fixture(restrict: true);
        Assert.Throws<RelationDeleteRestrictedException>(() => f.Repo.DeleteCollection(f.Clients));
        Assert.Throws<RelationDeleteRestrictedException>(() => f.Repo.DeleteCollectionIsolated(f.Clients));
        Assert.Throws<RelationDeleteRestrictedException>(() => f.Repo.DeleteGroup(f.Group));
        Assert.Throws<RelationDeleteRestrictedException>(() => f.Repo.DeleteGroupIsolated(f.Group));
        Assert.Equal(2, f.Repo.GetRecords(f.Clients).Count);
        // Источник ссылки уходит вместе с целью — коллекцию можно удалить.
        f.Repo.DeleteCollection(f.Invoices);
        f.Repo.DeleteGroup(f.Group);
        Assert.DoesNotContain(f.Repo.GetAllCollections(), c => c.Id == f.Clients);
    }

    [Fact]
    public void Links_inside_the_deleted_branch_do_not_block()
    {
        using var f = new Fixture(restrict: true);
        var invoices = f.Repo.GetAllCollections().Single(c => c.Id == f.Invoices);
        invoices.GroupId = f.Group;
        f.Repo.UpdateCollection(invoices);
        f.Repo.DeleteGroup(f.Group);
        Assert.DoesNotContain(f.Repo.GetAllCollections(), c => c.Id == f.Clients || c.Id == f.Invoices);
    }

    [Fact]
    public void Moving_a_referenced_record_or_collection_is_refused_too()
    {
        using var f = new Fixture(restrict: true);
        var archive = SampleData.AddCollection(f.Repo, "Архив").Id!.Value;
        Assert.Throws<RelationDeleteRestrictedException>(() => f.Repo.MoveRecordsToCollectionIsolated(
            f.Db.Path, f.Clients, [f.Client], archive, createMissingFields: false, sourceDbId: 0, targetDbId: 0));
        Assert.Empty(f.Repo.GetRecords(archive));
        Assert.NotNull(f.Repo.GetRecord(f.Client));

        using var other = new TempDatabase(dbId: 2, name: "Другая");
        Assert.Throws<RelationDeleteRestrictedException>(() =>
            other.Constructor.MoveCollectionIsolated(f.Db.Path, f.Clients, targetGroupId: null));
        Assert.Throws<RelationDeleteRestrictedException>(() =>
            other.Constructor.MoveGroupIsolated(f.Db.Path, f.Group, targetParentId: null));
        Assert.Throws<RelationDeleteRestrictedException>(() =>
            ConstructorRepository.MoveCollection(f.Repo, other.Constructor, f.Clients, null, "Клиенты"));
        Assert.Empty(other.Constructor.GetAllCollections());
        Assert.Equal(2, f.Repo.GetRecords(f.Clients).Count);
    }

    [Fact]
    public void The_delete_window_names_who_points_in()
    {
        using var f = new Fixture(restrict: false);
        var second = SampleData.AddRecord(f.Repo, f.Invoices, "Счет 2").Id!.Value;
        f.Repo.SetLinks(f.Relation, second, [f.Client]);
        var check = RelationDeleteImpact.CheckRecords(f.Repo, [f.Client]);
        Assert.Null(check.Blocked);
        Assert.Equal(" На них ссылаются «Счета · Клиент» — 2 записи — эти связи будут удалены.", check.Warning);
        Assert.Equal("", RelationDeleteImpact.CheckRecords(f.Repo, [f.Free]).Warning);
        // Источники среди удаляемых не считаются.
        Assert.Equal(" На них ссылаются «Счета · Клиент» — 1 запись — эти связи будут удалены.",
            RelationDeleteImpact.CheckRecords(f.Repo, [f.Client, second]).Warning);
        Assert.Contains("«Счета · Клиент» — 2 записи", RelationDeleteImpact.CheckCollections(f.Repo, [f.Clients]).Warning,
            StringComparison.Ordinal);

        f.SetRestrict(true);
        var blocked = RelationDeleteImpact.CheckRecords(f.Repo, [f.Client]);
        Assert.StartsWith("Удалить или перенести нельзя: на записи ссылаются «Счета · Клиент» — 2 записи", blocked.Blocked);
        Assert.NotNull(RelationDeleteImpact.CheckCollections(f.Repo, [f.Clients]).Blocked);
        Assert.Null(RelationDeleteImpact.CheckCollections(f.Repo, [f.Clients, f.Invoices]).Blocked);
    }

    [Theory]
    [InlineData(1, "запись")]
    [InlineData(2, "записи")]
    [InlineData(5, "записей")]
    [InlineData(11, "записей")]
    [InlineData(21, "запись")]
    [InlineData(104, "записи")]
    [InlineData(112, "записей")]
    public void Record_counts_are_declined(int count, string word) =>
        Assert.Equal(word, ConstructorRepository.RecordsWord(count));

    private sealed class Fixture : IDisposable
    {
        public TempDatabase Db { get; } = new();
        public ConstructorRepository Repo => Db.Constructor;
        public int Group { get; }
        public int Clients { get; }
        public int Client { get; }
        public int Free { get; }
        public int Invoices { get; }
        public int Invoice { get; }
        public int Relation { get; }

        public Fixture(bool restrict)
        {
            Group = Repo.AddGroup(new DbGroup { Name = "Справочники" }).Id!.Value;
            Clients = SampleData.AddCollection(Repo, "Клиенты", Group).Id!.Value;
            Client = SampleData.AddRecord(Repo, Clients, "ООО Ромашка").Id!.Value;
            Free = SampleData.AddRecord(Repo, Clients, "ООО Василек").Id!.Value;
            Invoices = SampleData.AddCollection(Repo, "Счета").Id!.Value;
            Invoice = SampleData.AddRecord(Repo, Invoices, "Счет 1").Id!.Value;
            Relation = SampleData.AddField(Repo, Invoices, "Клиент", "relation").Id!.Value;
            SetRestrict(restrict);
            Repo.SetLinks(Relation, Invoice, [Client]);
        }

        public void SetRestrict(bool restrict)
        {
            var field = Repo.GetFields(Invoices).Single(x => x.Id == Relation);
            field.ConfigJson = new RelationConfig(Clients, true, RestrictDelete: restrict).ToConfigJson();
            Repo.UpdateField(field);
        }

        public void Dispose() => Db.Dispose();
    }
}
