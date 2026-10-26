using System.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using SmartBase.Database;
using SmartBase.Database.Entities;
using SmartBase.Database.Repositories;

namespace SmartBase.Tests.Integration;

public sealed class SqliteTransactionAdmissionTests
{
    [Theory]
    [InlineData("sqlite", false)]
    [InlineData("sqlite", true)]
    [InlineData("sqlite-shared", false)]
    [InlineData("sqlite-shared", true)]
    public async Task A_single_metadata_save_cannot_bypass_waiting_writers(string profile, bool async)
    {
        using var fixture = new MultiUserTestDatabase(profile);
        var collection = SampleData.AddCollection(fixture.Repo, "Одиночная запись настроек").Id!.Value;
        using var writer = fixture.OpenContext();
        writer.UiCollectionSettings.Add(new UiCollectionSettingEntity
        {
            CollectionId = collection, ColResize = true, RowResize = true, ViewMode = "table",
        });
        var admission = Queue(fixture);
        using var holder = admission.Enter(TimeSpan.Zero); // Только очередь, физического DB lock нет.
        var save = Task.Run(async () =>
        {
            if (async) await writer.SaveChangesAsync();
            else writer.SaveChanges();
        });
        var queued = false;
        try { queued = SpinWait.SpinUntil(() => admission.WaitingCount == 1, TimeSpan.FromSeconds(2)); }
        finally
        {
            holder.Dispose();
            await save.WaitAsync(TimeSpan.FromSeconds(4));
        }
        Assert.True(queued, "Одиночный SaveChanges обошел очередь писателей");
        using var read = fixture.OpenContext();
        var saved = Assert.Single(read.UiCollectionSettings.Where(row => row.CollectionId == collection));
        Assert.True(saved.ColResize);
        Assert.True(saved.RowResize);
        Assert.Equal("table", saved.ViewMode);
    }

    [Theory]
    [InlineData("sqlite")]
    [InlineData("sqlite-shared")]
    public async Task Disposing_a_caller_transaction_releases_admission_while_its_connection_stays_open(string profile)
    {
        using var fixture = new MultiUserTestDatabase(profile);
        using var first = fixture.OpenContext();
        using var second = fixture.OpenContext();
        first.Database.OpenConnection();
        second.Database.OpenConnection();
        second.Database.SetCommandTimeout(2);
        var admission = Queue(fixture);
        using var holder = first.Database.BeginTransaction();
        var next = Task.Run(() =>
        {
            using var transaction = second.Database.BeginTransaction();
            transaction.Rollback();
        });
        var queued = false;
        try { queued = SpinWait.SpinUntil(() => admission.WaitingCount == 1, TimeSpan.FromSeconds(1)); }
        finally
        {
            holder.Dispose();
            await next.WaitAsync(TimeSpan.FromSeconds(4));
        }
        Assert.True(queued);
        Assert.Equal(ConnectionState.Open, first.Database.GetDbConnection().State);
        Assert.Equal(0, admission.WaitingCount);
        using var probe = admission.Enter(TimeSpan.Zero);
    }

    [Fact]
    public async Task Cancelling_a_queued_EF_begin_removes_its_waiter_and_leaves_the_pinned_connection_usable()
    {
        using var fixture = new MultiUserTestDatabase("sqlite");
        using var nextContext = fixture.OpenContext();
        nextContext.Database.OpenConnection();
        using var holder = fixture.Manager.Context.Database.BeginTransaction();
        using var cancellation = new CancellationTokenSource();
        var next = nextContext.Database.BeginTransactionAsync(cancellation.Token);
        try
        {
            Assert.True(SpinWait.SpinUntil(() => Queue(fixture).WaitingCount == 1, TimeSpan.FromSeconds(1)));
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await next);
            Assert.Equal(0, Queue(fixture).WaitingCount);
        }
        finally { holder.Rollback(); }
        using var retry = await nextContext.Database.BeginTransactionAsync();
        retry.Rollback();
    }

    [Fact]
    public void A_native_begin_failure_releases_the_admitted_slot_before_the_context_is_disposed()
    {
        using var fixture = new MultiUserTestDatabase("sqlite");
        using var failing = fixture.OpenContext();
        failing.Database.OpenConnection();
        failing.Database.SetCommandTimeout(1);
        ((SqliteConnection)failing.Database.GetDbConnection()).DefaultTimeout = 1;
        failing.Database.ExecuteSqlRaw("PRAGMA busy_timeout = 100");
        using var outside = SqliteGate.NewConnection(failing.Database.GetConnectionString()!);
        outside.Open();
        using var holder = outside.BeginTransaction(); // Native caller: deliberately outside EF admission.
        var error = Record.Exception(() =>
        {
            using var transaction = failing.Database.BeginTransaction();
        });
        Assert.NotNull(error);
        Assert.Equal(5, Assert.IsType<SqliteException>(error.GetBaseException()).SqliteErrorCode);
        using (Queue(fixture).Enter(TimeSpan.Zero)) { }
        holder.Rollback();
        using var retry = failing.Database.BeginTransaction();
        retry.Rollback();
    }

    [Fact]
    public void Native_deferred_read_snapshots_do_not_acquire_write_admission()
    {
        using var fixture = new MultiUserTestDatabase("sqlite");
        using var reader = fixture.OpenContext();
        reader.Database.OpenConnection();
        using var snapshot = ((SqliteConnection)reader.Database.GetDbConnection()).BeginTransaction(deferred: true);
        using var writerAdmission = Queue(fixture).Enter(TimeSpan.Zero);
        Assert.Equal(0, Queue(fixture).WaitingCount);
    }

    [Fact]
    public void Repository_and_caller_owned_transactions_use_only_one_SQLite_admission_lease()
    {
        using var fixture = new MultiUserTestDatabase("sqlite");
        var collection = SampleData.AddCollection(fixture.Repo, "Без двойного входа").Id!.Value;
        var field = SampleData.AddField(fixture.Repo, collection, "Значение").Id!.Value;
        var record = SampleData.AddRecord(fixture.Repo, collection, "Запись").Id!.Value;
        using var context = fixture.OpenContext();
        context.Database.SetCommandTimeout(1);
        var repo = new ConstructorRepository(context);
        repo.SetFieldValue(record, field, "Своя транзакция");
        using (var outer = context.Database.BeginTransaction())
        {
            repo.SetFieldValue(record, field, "Внешняя транзакция");
            Assert.Same(outer, context.Database.CurrentTransaction);
            outer.Commit();
        }
        using var read = fixture.OpenContext();
        Assert.Equal("Внешняя транзакция", Assert.Single(read.DbFieldValues.Where(value => value.RecordId == record)).Value);
    }

    private static WriteAdmission Queue(MultiUserTestDatabase fixture) =>
        WriteAdmission.ForDatabase("sqlite:" + Path.GetFullPath(fixture.Path), 1);
}
