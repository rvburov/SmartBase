using System.Security.Cryptography;
using SmartBase.Core.Models;
using SmartBase.Database.Backup;

namespace SmartBase.Tests.Integration;

public sealed class RelationIntegrityRepairTests
{
    [SkippableTheory]
    [InlineData("sqlite")]
    [InlineData("postgres")]
    public void Repair_removes_only_explicitly_selected_confirmed_edges_after_a_real_verified_backup(string provider)
    {
        using var f = new RelationIntegrityFixture(provider);
        RequireBackupTool(f);
        var valid = f.InsertEdge(f.Relation, f.SourceRecord, 0, f.LocalRecords[0]);
        var selected = f.InsertEdge(f.Relation, f.SourceRecord, 0, 950001);
        var retained = f.InsertEdge(f.Relation, f.SourceRecord, 0, 950002);
        var report = f.Scan();
        var issue = Assert.Single(report.Issues.Where(i => i.EdgeId == selected && i.CanRepair));
        var plan = f.Preview(report, [issue.Key]);
        Assert.True(plan.CanApply, plan.Message);
        Assert.Equal(1, plan.EdgesToRemove);
        var originalEdges = f.EdgeIds();
        var callbacks = 0;
        RelationIntegrityBackup? receipt = null;

        var result = Apply(f, plan, async (request, ct) =>
        {
            callbacks++;
            Assert.Equal(plan.SourceDbId, request.SourceDbId);
            Assert.Equal(plan.SourceDatabaseIdentity, request.SourceDatabaseIdentity);
            Assert.Equal(plan.Fingerprint, request.PlanFingerprint);
            Assert.Equal(originalEdges, f.EdgeIds());
            return receipt = await Backup(f, request, ct);
        });

        Assert.Equal(RelationIntegrityStatus.Applied, result.Status);
        Assert.True(result.Applied, result.Message);
        Assert.Equal(1, result.RemovedEdges);
        Assert.Equal(1, callbacks);
        Assert.NotNull(receipt);
        Assert.True(File.Exists(receipt.ArtifactPath));
        Assert.Equal(receipt.ContentSha256, Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(receipt.ArtifactPath))));
        Assert.Equal([valid, retained], f.EdgeIds());
        if (!f.Source.Context.IsPostgres) Assert.Equal(originalEdges, f.EdgeIds(receipt.ArtifactPath));
        else Assert.Equal("PGDMP"u8.ToArray(), File.ReadAllBytes(receipt.ArtifactPath).Take(5).ToArray());
    }

    [Fact]
    public void Missing_or_failed_backup_never_changes_the_source_and_errors_do_not_leak_driver_details()
    {
        using var f = new RelationIntegrityFixture();
        f.InsertEdge(f.Relation, f.SourceRecord, 0, 951001);
        var plan = f.Preview(f.Scan());
        var before = f.Snapshot();
        var absent = f.Source.Constructor.ApplyRelationIntegrityRepairAsync(plan, null!).GetAwaiter().GetResult();
        Assert.Equal(RelationIntegrityStatus.BackupFailed, absent.Status);
        Assert.Equal(0, absent.RemovedEdges);
        Assert.Equal(before, f.Snapshot());
        var calls = 0;
        var failed = Apply(f, plan, (_, _) =>
        {
            calls++;
            return Task.FromException<RelationIntegrityBackup>(new IOException("Password=PRIVATE_TEST_SECRET; " + f.Source.Path));
        });
        Assert.Equal(RelationIntegrityStatus.BackupFailed, failed.Status);
        Assert.Equal(0, failed.RemovedEdges);
        Assert.Equal(1, calls);
        Assert.DoesNotContain("PRIVATE_TEST_SECRET", failed.Message);
        Assert.DoesNotContain(f.Source.Path, failed.Message);
        Assert.Equal(before, f.Snapshot());
    }

    [Fact]
    public void Cancellation_after_a_real_backup_preserves_every_edge()
    {
        using var f = new RelationIntegrityFixture();
        f.InsertEdge(f.Relation, f.SourceRecord, 0, 952001);
        var plan = f.Preview(f.Scan());
        var before = f.Snapshot();
        using var cancel = new CancellationTokenSource();
        RelationIntegrityBackup? backup = null;
        var result = Apply(f, plan, async (request, token) =>
        {
            backup = await Backup(f, request, token);
            cancel.Cancel();
            return backup;
        }, cancel.Token);
        Assert.Equal(RelationIntegrityStatus.Cancelled, result.Status);
        Assert.Equal(0, result.RemovedEdges);
        Assert.NotNull(backup);
        Assert.True(File.Exists(backup.ArtifactPath));
        Assert.Equal(before, f.Snapshot());
        Assert.Equal(f.EdgeIds(), f.EdgeIds(backup.ArtifactPath));
    }

    [Fact]
    public void Precancel_and_backup_callback_cancellation_never_start_deletions()
    {
        using var f = new RelationIntegrityFixture();
        f.InsertEdge(f.Relation, f.SourceRecord, 0, 952002);
        var report = f.Scan();
        var plan = f.Preview(report);
        var before = f.Snapshot();
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();
        var preview = f.Source.Constructor.PreviewRelationIntegrityRepairAsync(report, ct: cancel.Token).GetAwaiter().GetResult();
        Assert.Equal(RelationIntegrityStatus.Cancelled, preview.Status);
        Assert.False(preview.CanApply);
        var called = false;
        var result = Apply(f, plan, (request, ct) =>
        {
            called = true;
            return Backup(f, request, ct);
        }, cancel.Token);
        Assert.Equal(RelationIntegrityStatus.Cancelled, result.Status);
        Assert.Equal(0, result.RemovedEdges);
        Assert.False(called);
        using var callbackCancellation = new CancellationTokenSource();
        var callbackResult = Apply(f, plan, (_, _) =>
        {
            callbackCancellation.Cancel();
            return Task.FromCanceled<RelationIntegrityBackup>(callbackCancellation.Token);
        });
        Assert.Equal(RelationIntegrityStatus.Cancelled, callbackResult.Status);
        Assert.Equal(0, callbackResult.RemovedEdges);
        Assert.Equal(before, f.Snapshot());
    }

    [Fact]
    public void Changed_backup_artifact_is_rejected_before_any_delete()
    {
        using var f = new RelationIntegrityFixture();
        f.InsertEdge(f.Relation, f.SourceRecord, 0, 953001);
        var plan = f.Preview(f.Scan());
        var before = f.Snapshot();
        var result = Apply(f, plan, async (request, ct) =>
        {
            var backup = await Backup(f, request, ct);
            await File.AppendAllTextAsync(backup.ArtifactPath, "tampered-after-verification", ct);
            return backup;
        });
        Assert.Equal(RelationIntegrityStatus.BackupFailed, result.Status);
        Assert.Equal(0, result.RemovedEdges);
        Assert.Equal(before, f.Snapshot());
    }

    [Theory]
    [InlineData("edge")]
    [InlineData("config")]
    public void Source_changes_after_backup_require_a_new_scan_without_overwriting_the_new_data(string mutation)
    {
        using var f = new RelationIntegrityFixture();
        f.InsertEdge(f.Relation, f.SourceRecord, 0, 954001);
        var plan = f.Preview(f.Scan());
        string? afterExternalWrite = null;
        var result = Apply(f, plan, async (request, ct) =>
        {
            var backup = await Backup(f, request, ct);
            if (mutation == "edge") f.InsertEdge(f.Relation, f.SourceRecord, 0, f.LocalRecords[0]);
            else f.SetConfig(f.Relation, new RelationConfig(f.LocalCollection, false, Separator: "semicolon").ToConfigJson());
            afterExternalWrite = f.Snapshot();
            return backup;
        });
        Assert.Equal(RelationIntegrityStatus.Conflict, result.Status);
        Assert.Equal(0, result.RemovedEdges);
        Assert.NotNull(afterExternalWrite);
        Assert.Equal(afterExternalWrite, f.Snapshot());
    }

    [Fact]
    public void Changed_foreign_target_proof_after_backup_cannot_authorize_orphan_deletion()
    {
        using var f = new RelationIntegrityFixture();
        var field = f.RelationField(f.SourceCollection, f.RemoteCollection, "Внешняя цель", targetDb: RelationIntegrityFixture.TargetId);
        f.InsertEdge(field, f.SourceRecord, RelationIntegrityFixture.TargetId, 955001);
        var plan = f.Preview(f.Scan());
        Assert.True(plan.CanApply, plan.Message);
        var before = f.Snapshot();
        var result = Apply(f, plan, async (request, ct) =>
        {
            var backup = await Backup(f, request, ct);
            f.RestoreMissingRemoteRecord(955001);
            return backup;
        });
        Assert.Equal(RelationIntegrityStatus.Conflict, result.Status);
        Assert.Equal(0, result.RemovedEdges);
        Assert.Equal(before, f.Snapshot());
        Assert.DoesNotContain(f.Scan().Issues, i => i.Kind == RelationIntegrityIssueKind.MissingTargetRecord);
    }

    [SkippableTheory]
    [InlineData("sqlite")]
    [InlineData("postgres")]
    public void Audit_failure_rolls_back_deletions_and_a_fresh_retry_is_idempotent(string provider)
    {
        using var f = new RelationIntegrityFixture(provider);
        RequireBackupTool(f);
        f.InsertEdge(f.Relation, f.SourceRecord, 0, 957001);
        f.InsertEdge(f.Relation, f.SourceRecord, 0, 957002);
        var trigger = f.Source.Context.IsPostgres
            ? "CREATE FUNCTION reject_integrity_audit() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN RAISE EXCEPTION 'integrity audit failure'; END; $$; CREATE TRIGGER reject_integrity_audit BEFORE INSERT ON change_log FOR EACH ROW EXECUTE FUNCTION reject_integrity_audit()"
            : "CREATE TRIGGER reject_integrity_audit BEFORE INSERT ON change_log BEGIN SELECT RAISE(ABORT, 'integrity audit failure'); END";
        f.ExecuteSource(trigger);
        var plan = f.Preview(f.Scan());
        var before = f.Snapshot();
        var failed = Apply(f, plan, (request, ct) => Backup(f, request, ct));
        Assert.Equal(RelationIntegrityStatus.Unavailable, failed.Status);
        Assert.False(failed.Applied);
        Assert.Equal(0, failed.RemovedEdges);
        Assert.Equal(before, f.Snapshot());
        f.ExecuteSource(f.Source.Context.IsPostgres
            ? "DROP TRIGGER reject_integrity_audit ON change_log; DROP FUNCTION reject_integrity_audit()"
            : "DROP TRIGGER reject_integrity_audit");
        var retry = f.Preview(f.Scan());
        var success = Apply(f, retry, (request, ct) => Backup(f, request, ct));
        Assert.Equal(RelationIntegrityStatus.Applied, success.Status);
        Assert.Equal(2, success.RemovedEdges);
        Assert.Empty(f.EdgeIds());
        var after = f.Snapshot();
        var repeated = Apply(f, retry, (request, ct) => Backup(f, request, ct));
        Assert.Equal(RelationIntegrityStatus.Conflict, repeated.Status);
        Assert.Equal(0, repeated.RemovedEdges);
        Assert.Equal(after, f.Snapshot());
        var noWork = f.Preview(f.Scan());
        Assert.False(noWork.CanApply);
        var callbackCalled = false;
        var empty = Apply(f, noWork, (request, ct) =>
        {
            callbackCalled = true;
            return Backup(f, request, ct);
        });
        Assert.False(empty.Applied);
        Assert.Equal(0, empty.RemovedEdges);
        Assert.False(callbackCalled);
        Assert.Equal(after, f.Snapshot());
    }

    [SkippableFact]
    public void Postgres_interruption_after_verified_backup_preserves_data_and_allows_a_fresh_retry()
    {
        using var f = new RelationIntegrityFixture("postgres");
        RequireBackupTool(f);
        var valid = f.InsertEdge(f.Relation, f.SourceRecord, 0, f.LocalRecords[0]);
        f.InsertEdge(f.Relation, f.SourceRecord, 0, 959001);
        var plan = f.Preview(f.Scan());
        Assert.True(plan.CanApply, plan.Message);
        var before = f.Snapshot();
        RelationIntegrityBackup? retainedBackup = null;
        var terminatedConnections = 0;
        try
        {
            var interrupted = Apply(f, plan, async (request, ct) =>
            {
                retainedBackup = await Backup(f, request, ct);
                terminatedConnections = f.InterruptPostgresSource();
                return retainedBackup;
            });
            Assert.True(terminatedConnections > 0, "Должно быть прервано настоящее подключение к временной базе");
            Assert.Equal(RelationIntegrityStatus.Unavailable, interrupted.Status);
            Assert.False(interrupted.Applied);
            Assert.Equal(0, interrupted.RemovedEdges);
            Assert.Same(retainedBackup, interrupted.Backup);
        }
        finally
        {
            f.RestorePostgresSource();
        }

        Assert.Equal(before, f.Snapshot());
        Assert.NotNull(retainedBackup);
        Assert.True(File.Exists(retainedBackup.ArtifactPath));
        Assert.Equal(retainedBackup.ContentSha256,
            Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(retainedBackup.ArtifactPath))));
        var freshReport = f.Scan();
        Assert.Equal(RelationIntegrityStatus.Completed, freshReport.Status);
        var retry = f.Preview(freshReport);
        Assert.True(retry.CanApply, retry.Message);
        var repaired = Apply(f, retry, (request, ct) => Backup(f, request, ct));
        Assert.Equal(RelationIntegrityStatus.Applied, repaired.Status);
        Assert.Equal(1, repaired.RemovedEdges);
        Assert.Equal([valid], f.EdgeIds());
        Assert.DoesNotContain(f.Scan().Issues, i => i.Kind == RelationIntegrityIssueKind.MissingTargetRecord);
        Assert.True(File.Exists(retainedBackup.ArtifactPath));
    }

    [Fact]
    public void Stale_reports_and_a_plan_from_another_database_cannot_authorize_deletions()
    {
        using var first = new RelationIntegrityFixture();
        first.InsertEdge(first.Relation, first.SourceRecord, 0, 958001);
        var report = first.Scan();
        var plan = first.Preview(report);
        first.InsertEdge(first.Relation, first.SourceRecord, 0, first.LocalRecords[0]);
        var before = first.Snapshot();
        var stale = first.Preview(report);
        Assert.Equal(RelationIntegrityStatus.Conflict, stale.Status);
        Assert.False(stale.CanApply);
        Assert.Equal(before, first.Snapshot());
        using var second = new RelationIntegrityFixture();
        second.InsertEdge(second.Relation, second.SourceRecord, 0, 958001);
        var secondBefore = second.Snapshot();
        var called = false;
        var wrongDatabase = Apply(second, plan, (request, ct) =>
        {
            called = true;
            return Backup(second, request, ct);
        });
        Assert.Equal(RelationIntegrityStatus.InvalidPlan, wrongDatabase.Status);
        Assert.Equal(0, wrongDatabase.RemovedEdges);
        Assert.False(called);
        Assert.Equal(secondBefore, second.Snapshot());
    }

    private static void RequireBackupTool(RelationIntegrityFixture fixture)
    {
        if (fixture.Source.Context.IsPostgres) Skip.If(PgDump.FindBinary() is null, "Клиент pg_dump не найден");
    }

    private static Task<RelationIntegrityBackup> Backup(RelationIntegrityFixture fixture,
        RelationIntegrityBackupRequest request, CancellationToken ct) =>
        RelationIntegrityBackupService.CreateVerifiedAsync(fixture.Source, request, fixture.Backups.Path,
            fixture.PgInfo, ct: ct);

    private static RelationIntegrityRepairResult Apply(RelationIntegrityFixture fixture, RelationIntegrityRepairPlan plan,
        Func<RelationIntegrityBackupRequest, CancellationToken, Task<RelationIntegrityBackup>> backup,
        CancellationToken ct = default) => fixture.Source.Constructor.ApplyRelationIntegrityRepairAsync(plan, backup, ct).GetAwaiter().GetResult();
}
