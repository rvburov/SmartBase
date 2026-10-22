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
        Assert.StartsWith("Удалить нельзя: на запись ссылается «Счета · Клиент» (1 запись)", error.Message, StringComparison.Ordinal);
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
        // Читаем базу, а не трекер EF: удаляет отдельное подключение.
        Assert.Equal(2, f.Repo.GetRecords(f.Clients).Count);
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
        var refused = Assert.Throws<RelationDeleteRestrictedException>(() =>
            other.Constructor.MoveCollectionIsolated(f.Db.Path, f.Clients, targetGroupId: null));
        Assert.StartsWith("Перенести нельзя", refused.Message, StringComparison.Ordinal);
        Assert.Throws<RelationDeleteRestrictedException>(() =>
            other.Constructor.MoveGroupIsolated(f.Db.Path, f.Group, targetParentId: null));
        Assert.Throws<RelationDeleteRestrictedException>(() =>
            ConstructorRepository.MoveCollection(f.Repo, other.Constructor, f.Clients, null, "Клиенты"));
        Assert.Empty(other.Constructor.GetAllCollections());
        Assert.Equal(2, f.Repo.GetRecords(f.Clients).Count);
    }

    [Fact]
    public async Task The_delete_window_names_who_points_in()
    {
        using var f = new Fixture(restrict: false);
        var second = SampleData.AddRecord(f.Repo, f.Invoices, "Счет 2").Id!.Value;
        f.Repo.SetLinks(f.Relation, second, [f.Client]);
        var check = await RelationDeleteImpact.CheckRecordsAsync(f.Repo, [f.Client]);
        Assert.Null(check.Blocked);
        Assert.Equal(" На нее ссылается «Счета · Клиент» (2 записи) — эта связь будет удалена.", check.Warning("нее"));
        Assert.Equal(" На нее ссылается «Счета · Клиент» (2 записи) — эта связь оборвется.", check.Warning("нее", move: true));
        Assert.Equal("", (await RelationDeleteImpact.CheckRecordsAsync(f.Repo, [f.Free])).Warning("нее"));
        // Источники среди удаляемых не считаются.
        Assert.Equal("«Счета · Клиент» (1 запись)", (await RelationDeleteImpact.CheckRecordsAsync(f.Repo, [f.Client, second])).References);
        Assert.Equal("«Счета · Клиент» (2 записи)", (await RelationDeleteImpact.CheckCollectionsAsync([(f.Repo, [f.Clients])])).References);

        f.SetRestrict(true);
        var blocked = await RelationDeleteImpact.CheckRecordsAsync(f.Repo, [f.Client]);
        Assert.StartsWith("Удалить нельзя: на запись ссылается «Счета · Клиент» (2 записи)", blocked.Blocked);
        Assert.NotNull((await RelationDeleteImpact.CheckCollectionsAsync([(f.Repo, [f.Clients])])).Blocked);
        Assert.Null((await RelationDeleteImpact.CheckCollectionsAsync([(f.Repo, [f.Clients, f.Invoices])])).Blocked);
    }

    // Перенос копирует записи с новыми номерами: ссылка между переносимыми обрывается, поэтому
    // запрет держит и ее — до копии, без дублей.
    [Fact]
    public async Task Moving_records_linked_to_each_other_is_refused_before_the_copy()
    {
        using var f = new Fixture(restrict: true);
        var parent = SampleData.AddField(f.Repo, f.Clients, "Головная", "relation");
        parent.ConfigJson = new RelationConfig(f.Clients, true, RestrictDelete: true).ToConfigJson();
        f.Repo.UpdateField(parent);
        f.Repo.SetLinks(f.Relation, f.Invoice, []);
        f.Repo.SetLinks(parent.Id!.Value, f.Free, [f.Client]);
        var archive = SampleData.AddCollection(f.Repo, "Архив").Id!.Value;

        var error = Assert.Throws<RelationDeleteRestrictedException>(() => f.Repo.MoveRecordsToCollectionIsolated(
            f.Db.Path, f.Clients, [f.Free, f.Client], archive, createMissingFields: true, sourceDbId: 0, targetDbId: 0));
        Assert.StartsWith("Перенести нельзя: на записи ссылается «Клиенты · Головная» (1 запись)", error.Message, StringComparison.Ordinal);
        Assert.Empty(f.Repo.GetRecords(archive));
        Assert.Equal(2, f.Repo.GetRecords(f.Clients).Count);
        Assert.NotNull((await RelationDeleteImpact.CheckRecordsAsync(f.Repo, [f.Free, f.Client], move: true)).Blocked);
        // Удаление обеих — источник уходит вместе с целью.
        Assert.Null((await RelationDeleteImpact.CheckRecordsAsync(f.Repo, [f.Free, f.Client])).Blocked);
        f.Repo.DeleteRecordsData([f.Free, f.Client]);
        Assert.Empty(f.Repo.GetRecords(f.Clients));
    }

    [Fact]
    public async Task A_plain_link_between_moved_records_is_named_as_breaking()
    {
        using var f = new Fixture(restrict: false);
        var parent = SampleData.AddField(f.Repo, f.Clients, "Головная", "relation");
        parent.ConfigJson = new RelationConfig(f.Clients, true).ToConfigJson();
        f.Repo.UpdateField(parent);
        f.Repo.SetLinks(f.Relation, f.Invoice, []);
        f.Repo.SetLinks(parent.Id!.Value, f.Free, [f.Client]);
        Assert.Equal("«Клиенты · Головная» (1 запись)",
            (await RelationDeleteImpact.CheckRecordsAsync(f.Repo, [f.Free, f.Client], move: true)).References);
        Assert.Equal("", (await RelationDeleteImpact.CheckRecordsAsync(f.Repo, [f.Free, f.Client])).References);
    }

    // Входящее сообщение, календарь и задачи удаляют набор по одной записи: ссылки внутри набора
    // запрету не мешают, иначе удаление обрывалось бы на середине.
    [Fact]
    public void Records_deleted_one_by_one_in_one_operation_do_not_block_each_other()
    {
        using var f = new Fixture(restrict: true);
        Assert.Throws<RelationDeleteRestrictedException>(() => f.Repo.DeleteRecord(f.Client));
        Assert.Throws<RelationDeleteRestrictedException>(() => f.Repo.DeleteRecord(f.Client, [f.Free]));
        Assert.Throws<RelationDeleteRestrictedException>(() => f.Repo.DeleteCalendarRecords([f.Client, f.Free]));
        f.Repo.DeleteRecord(f.Client, [f.Client, f.Invoice]);
        f.Repo.DeleteRecord(f.Invoice, [f.Client, f.Invoice]);
        Assert.Equal([f.Free], f.Repo.GetRecords(f.Clients).Select(r => r.Id!.Value));
        Assert.Empty(f.Repo.GetRecords(f.Invoices));
    }

    // Календарь и задачи удаляют набор базы одной операцией: цель и ссылающийся источник уходят
    // вместе, а нехватка прав на любую запись набора не удаляет ни одной — иначе удаленная цель
    // оставила бы источник с оборванной запрещающей связью.
    [Fact]
    public void A_calendar_set_is_deleted_whole_or_not_at_all()
    {
        using var f = new Fixture(restrict: true);
        DenyDelete(f, f.Invoices);
        Assert.Throws<PermissionDeniedException>(() => f.Repo.DeleteCalendarRecords([f.Client, f.Invoice]));
        Assert.Equal(2, f.Repo.GetRecords(f.Clients).Count);
        Assert.Equal([f.Client], f.Repo.GetLinkEdges(f.Relation, f.Invoice).Select(e => e.ToRecordId));

        using var g = new Fixture(restrict: true);
        g.Repo.DeleteCalendarRecords([g.Client, g.Invoice]);
        Assert.Equal([g.Free], g.Repo.GetRecords(g.Clients).Select(r => r.Id!.Value));
        Assert.Empty(g.Repo.GetRecords(g.Invoices));
    }

    // Перенос узлов сайдбара в другую базу идет по узлу за раз: ссылка между узлами обрывается,
    // и окно ее называет; при удалении тех же узлов она не мешает.
    [Fact]
    public async Task Links_between_moved_nodes_count_as_breaking()
    {
        using var f = new Fixture(restrict: true);
        Assert.NotNull((await RelationDeleteImpact.CheckCollectionsAsync([(f.Repo, [f.Clients]), (f.Repo, [f.Invoices])], move: true)).Blocked);
        Assert.Null((await RelationDeleteImpact.CheckCollectionsAsync([(f.Repo, [f.Clients]), (f.Repo, [f.Invoices])])).Blocked);
        // Ссылки внутри одного переносимого узла копия переписывает — они не мешают.
        Assert.Null((await RelationDeleteImpact.CheckCollectionsAsync([(f.Repo, [f.Clients, f.Invoices])], move: true)).Blocked);
    }

    // Сайдбар удаляет выделенные узлы по одному: коллекции всей операции идут в каждый вызов.
    [Fact]
    public void Nodes_deleted_in_one_operation_do_not_block_each_other()
    {
        using var f = new Fixture(restrict: true);
        Assert.Throws<RelationDeleteRestrictedException>(() => f.Repo.DeleteGroupIsolated(f.Group));
        f.Repo.DeleteGroupIsolated(f.Group, collectionsTogether: [f.Clients, f.Invoices]);
        f.Repo.DeleteCollectionIsolated(f.Invoices, collectionsTogether: [f.Clients, f.Invoices]);
        Assert.DoesNotContain(f.Repo.GetAllCollections(), c => c.Id == f.Clients || c.Id == f.Invoices);
    }

    // Служебная уборка (откат копии, остаток прерванного переноса) запрет не проверяет: иначе
    // маркер не снимался бы никогда, а записи оставались бы в двух базах.
    [Fact]
    public void Internal_cleanup_is_not_held_by_the_restriction()
    {
        using var f = new Fixture(restrict: true);
        Assert.Throws<RelationDeleteRestrictedException>(() => f.Repo.DeleteCollectionData(f.Clients, log: false));
        f.Repo.DeleteCollectionData(f.Clients, log: false, enforceRestrict: false);
        Assert.Empty(f.Repo.GetRecords(f.Clients));
    }

    [Fact]
    public async Task A_collection_closed_to_the_user_is_not_named()
    {
        using var f = new Fixture(restrict: false);
        var me = f.Db.Context.DbUsers.First(u => u.UserKey == SmartBase.Utils.CurrentUser.Key);
        f.Db.Permissions.SetMask(f.Db.Permissions.CurrentUserId, null, (int)PermAction.All);
        f.Db.Permissions.SetMask(f.Db.Permissions.CurrentUserId, f.Invoices, (int)(PermAction.All & ~PermAction.ViewCollection));
        me.IsAdmin = false;
        me.IsApproved = true;
        f.Db.Context.SaveChanges();
        f.Db.Permissions.Reload(notify: false);

        var references = (await RelationDeleteImpact.CheckRecordsAsync(f.Repo, [f.Client])).References;
        Assert.Equal("связи закрытых коллекций", references);
        Assert.DoesNotContain("Счета", references, StringComparison.Ordinal);

        // Охрана изолированного подключения называет ссылки с правами того, кто удаляет.
        f.SetRestrictAsAdmin(true);
        var refused = Assert.Throws<RelationDeleteRestrictedException>(() => f.Repo.DeleteRecordsIsolated([f.Client]));
        Assert.Contains("связи закрытых коллекций", refused.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("Счета", refused.Message, StringComparison.Ordinal);
    }

    private static void DenyDelete(Fixture f, int collection)
    {
        var me = f.Db.Context.DbUsers.First(u => u.UserKey == SmartBase.Utils.CurrentUser.Key);
        f.Db.Permissions.SetMask(f.Db.Permissions.CurrentUserId, null, (int)PermAction.All);
        f.Db.Permissions.SetMask(f.Db.Permissions.CurrentUserId, collection, (int)(PermAction.All & ~PermAction.DeleteRecord));
        me.IsAdmin = false;
        me.IsApproved = true;
        f.Db.Context.SaveChanges();
        f.Db.Permissions.Reload(notify: false);
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

        // Настройку свойства меняет администратор: у пониженного пользователя права на правку нет.
        public void SetRestrictAsAdmin(bool restrict)
        {
            var me = Db.Context.DbUsers.First(u => u.UserKey == SmartBase.Utils.CurrentUser.Key);
            me.IsAdmin = true;
            Db.Context.SaveChanges();
            Db.Permissions.Reload(notify: false);
            SetRestrict(restrict);
            me.IsAdmin = false;
            Db.Context.SaveChanges();
            Db.Permissions.Reload(notify: false);
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
