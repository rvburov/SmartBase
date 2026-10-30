using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using SmartBase.Core.Models;
using SmartBase.Database;
using SmartBase.Database.Entities;
using SmartBase.Database.Repositories;

namespace SmartBase.Tests.Integration;

/// <summary>Фоновая автоподстановка: реальные файлы, соседние подключения, отмена записи
/// и отложенная работа только после коммита исходного редактирования.</summary>
public sealed class FileAutoSafetyTests
{
    [SkippableTheory]
    [InlineData("sqlite")]
    [InlineData("postgres")]
    public void Async_resolution_reports_catalog_and_match_stages_and_commits_all_results(string provider)
    {
        using var f = new Fixture(provider);
        var progress = new ConcurrentQueue<double>();

        var result = f.Resolve(new InlineProgress(progress.Enqueue));

        Assert.Equal(ConstructorRepository.FileAutoResolveResult.Ok, result);
        Assert.Contains(progress, double.IsNaN);
        Assert.Contains(.45, progress);
        Assert.Equal(1d, progress.Last());
        Assert.Equal(f.PathFor("A"), f.Value(f.One));
        Assert.Equal(f.PathFor("B"), f.Value(f.Two));
        Assert.Equal("A", f.Source(f.One));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Cancellation_at_catalog_or_match_stage_preserves_every_previous_value(bool afterCatalog)
    {
        using var f = new Fixture();
        using var cancel = new CancellationTokenSource();
        var before = f.Snapshot();
        var stopped = 0;
        var result = f.Resolve(new InlineProgress(value =>
        {
            if ((afterCatalog ? value == .45 : double.IsNaN(value))
                && Interlocked.Exchange(ref stopped, 1) == 0) cancel.Cancel();
        }), cancel.Token);

        Assert.Equal(1, stopped);
        Assert.Equal(ConstructorRepository.FileAutoResolveResult.Cancelled, result);
        Assert.Equal(before, f.Snapshot());
    }

    [SkippableTheory]
    [InlineData("sqlite", "config")]
    [InlineData("postgres", "config")]
    [InlineData("sqlite", "source")]
    [InlineData("postgres", "source")]
    [InlineData("sqlite", "title")]
    [InlineData("postgres", "title")]
    [InlineData("sqlite", "value")]
    [InlineData("postgres", "value")]
    public void A_late_external_change_rejects_the_old_snapshot_without_overwriting_current_data(
        string provider, string mutation)
    {
        using var f = new Fixture(provider, titleSource: mutation == "title");
        string? afterExternalChange = null;
        var changed = 0;
        var result = f.Resolve(new InlineProgress(value =>
        {
            if (value != .45 || Interlocked.Exchange(ref changed, 1) != 0) return;
            using var other = f.Other();
            if (mutation == "config")
                other.DbFields.Single(field => field.Id == f.FileField).ConfigJson =
                    (f.Rule with { Enabled = false }).ToConfigJson();
            else if (mutation == "title")
                other.DbRecords.Single(record => record.Id == f.One).Title = "C";
            else
            {
                var field = mutation == "source" ? f.SourceField : f.FileField;
                other.DbFieldValues.Single(v => v.RecordId == f.One && v.FieldId == field).Value =
                    mutation == "source" ? "C" : f.PathFor("changed-by-neighbour");
            }
            other.SaveChanges();
            afterExternalChange = f.Snapshot();
        }));

        Assert.Equal(1, changed);
        Assert.NotNull(afterExternalChange);
        Assert.Equal(ConstructorRepository.FileAutoResolveResult.Conflict, result);
        Assert.Equal(afterExternalChange, f.Snapshot());
    }

    [Fact]
    public void Cancellation_after_a_thousand_staged_writes_rolls_back_the_entire_update()
    {
        using var f = new Fixture(titleSource: true);
        using (var seed = f.Other())
        {
            seed.DbRecords.AddRange(Enumerable.Range(0, 1001).Select(i => new DbRecordEntity
            {
                CollectionId = f.Collection, Title = "A", Position = (i + 10) * 1000,
            }));
            seed.SaveChanges();
        }
        var before = f.Snapshot();
        using var cancel = new CancellationTokenSource();
        var stoppedDuringWrite = false;
        var result = f.Resolve(new InlineProgress(value =>
        {
            if (value <= .85 || value >= 1 || double.IsNaN(value)) return;
            stoppedDuringWrite = true;
            cancel.Cancel();
        }), cancel.Token);

        Assert.True(stoppedDuringWrite);
        Assert.Equal(ConstructorRepository.FileAutoResolveResult.Cancelled, result);
        Assert.Equal(before, f.Snapshot());
    }

    [Fact]
    public void An_inaccessible_folder_preserves_previous_values_instead_of_clearing_them()
    {
        using var f = new Fixture();
        f.Configure(f.Rule with { Folder = Path.Combine(f.Folder, "missing-folder") });
        var before = f.Snapshot();

        Assert.Equal(ConstructorRepository.FileAutoResolveResult.FolderInaccessible, f.Resolve());
        Assert.Equal(before, f.Snapshot());
    }

    [Fact]
    public void More_than_ten_thousand_actual_matches_abort_without_partial_or_truncated_values()
    {
        using var f = new Fixture();
        f.Configure(f.Rule with { PartialMatch = true });
        using (var other = f.Other())
        {
            other.DbFieldValues.Single(v => v.RecordId == f.One && v.FieldId == f.SourceField).Value = "match";
            other.SaveChanges();
        }
        for (var i = 0; i <= FileAutoCatalog.MaxMatches; i++)
            File.WriteAllText(f.PathFor($"match-{i:D5}"), "");
        var before = f.Snapshot();

        Assert.Equal(ConstructorRepository.FileAutoResolveResult.TooManyMatches, f.Resolve());
        Assert.Equal(before, f.Snapshot());
    }

    [SkippableTheory]
    [InlineData("sqlite")]
    [InlineData("postgres")]
    public void An_audit_write_failure_rolls_back_all_file_changes_and_a_fresh_retry_can_succeed(string provider)
    {
        using var f = new Fixture(provider);
        using (var setup = f.Other())
            setup.Database.ExecuteSqlRaw(f.Manager.Context.IsPostgres
                ? "CREATE FUNCTION reject_file_auto_audit() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN RAISE EXCEPTION 'file auto audit failure'; END; $$; CREATE TRIGGER reject_file_auto_audit BEFORE INSERT ON change_log FOR EACH ROW EXECUTE FUNCTION reject_file_auto_audit()"
                : "CREATE TRIGGER reject_file_auto_audit BEFORE INSERT ON change_log BEGIN SELECT RAISE(ABORT, 'file auto audit failure'); END");
        var before = f.Snapshot();

        Assert.Equal(ConstructorRepository.FileAutoResolveResult.Unavailable, f.Resolve());
        Assert.Equal(before, f.Snapshot());
        using (var setup = f.Other())
            setup.Database.ExecuteSqlRaw(f.Manager.Context.IsPostgres
                ? "DROP TRIGGER reject_file_auto_audit ON change_log; DROP FUNCTION reject_file_auto_audit()"
                : "DROP TRIGGER reject_file_auto_audit");
        Assert.Equal(ConstructorRepository.FileAutoResolveResult.Ok, f.Resolve());
        Assert.Equal(f.PathFor("A"), f.Value(f.One));
        Assert.Equal(f.PathFor("B"), f.Value(f.Two));
    }

    [SkippableTheory]
    [InlineData("sqlite", false)]
    [InlineData("postgres", false)]
    [InlineData("sqlite", true)]
    [InlineData("postgres", true)]
    public void Source_edits_defer_work_until_commit_and_old_work_reads_the_latest_source(
        string provider, bool titleSource)
    {
        using var f = new Fixture(provider, titleSource);
        var requested = new List<FileAutoWork>();
        void Receive(FileAutoWork work)
        {
            if (work.DbPath == f.Manager.Path) requested.Add(work);
        }
        ConstructorRepository.FileAutoWorkRequested += Receive;
        try
        {
            var previous = f.Value(f.One);
            using (var transaction = f.Manager.Context.Database.BeginTransaction())
            {
                Assert.Empty(f.EditSource(f.One, "B"));
                Assert.Empty(requested);
                Assert.Equal(previous, f.Value(f.One));
                transaction.Commit();
            }
            var first = Assert.Single(requested);
            Assert.Equal(f.FileField, first.FieldId);
            Assert.Equal(f.One, first.RecordId);
            Assert.Equal("B", f.Source(f.One));
            Assert.Equal(previous, f.Value(f.One));

            Assert.Empty(f.EditSource(f.One, "C"));
            Assert.Equal(2, requested.Count);
            Assert.Equal(previous, f.Value(f.One));
            Assert.Equal(ConstructorRepository.FileAutoResolveResult.Ok,
                ConstructorRepository.ExecuteFileAutoWork(first));
            Assert.Equal(f.PathFor("C"), f.Value(f.One));
            Assert.Equal(f.OldTwo, f.Value(f.Two));
        }
        finally { ConstructorRepository.FileAutoWorkRequested -= Receive; }
    }

    [SkippableTheory]
    [InlineData("sqlite")]
    [InlineData("postgres")]
    public void Rollback_discards_pending_requests_and_the_next_commit_publishes_only_its_own_work(string provider)
    {
        using var f = new Fixture(provider);
        var requested = new List<FileAutoWork>();
        void Receive(FileAutoWork work)
        {
            if (work.DbPath == f.Manager.Path) requested.Add(work);
        }
        ConstructorRepository.FileAutoWorkRequested += Receive;
        try
        {
            var before = f.Snapshot();
            using (var transaction = f.Manager.Context.Database.BeginTransaction())
            {
                Assert.Empty(f.EditSource(f.One, "B"));
                Assert.Empty(requested);
                transaction.Rollback();
            }
            f.Manager.Context.ChangeTracker.Clear();
            Assert.Empty(requested);
            Assert.Equal(before, f.Snapshot());
            using (var transaction = f.Manager.Context.Database.BeginTransaction())
            {
                Assert.Empty(f.EditSource(f.Two, "C"));
                Assert.Empty(requested);
                transaction.Commit();
            }
            var work = Assert.Single(requested);
            Assert.Equal(f.Two, work.RecordId);
            Assert.Equal(ConstructorRepository.FileAutoResolveResult.Ok,
                ConstructorRepository.ExecuteFileAutoWork(work));
            Assert.Equal(f.OldOne, f.Value(f.One));
            Assert.Equal(f.PathFor("C"), f.Value(f.Two));
        }
        finally { ConstructorRepository.FileAutoWorkRequested -= Receive; }
    }

    [Fact]
    public void Disabling_a_rule_prevents_new_requests_and_makes_already_queued_work_harmless()
    {
        using var f = new Fixture();
        var requested = new List<FileAutoWork>();
        void Receive(FileAutoWork work)
        {
            if (work.DbPath == f.Manager.Path) requested.Add(work);
        }
        ConstructorRepository.FileAutoWorkRequested += Receive;
        try
        {
            Assert.Empty(f.EditSource(f.One, "B"));
            var queued = Assert.Single(requested);
            f.Configure(f.Rule with { Enabled = false });
            var before = f.Snapshot();
            var progress = new ConcurrentQueue<double>();
            Assert.Equal(ConstructorRepository.FileAutoResolveResult.NoRule,
                f.Resolve(new InlineProgress(progress.Enqueue)));
            Assert.Empty(progress);
            Assert.Equal(ConstructorRepository.FileAutoResolveResult.NoRule,
                ConstructorRepository.ExecuteFileAutoWork(queued));
            Assert.Equal(before, f.Snapshot());
            Assert.Empty(f.EditSource(f.One, "C"));
            Assert.Single(requested);
            Assert.Equal(f.OldOne, f.Value(f.One));
        }
        finally { ConstructorRepository.FileAutoWorkRequested -= Receive; }
    }

    [Fact]
    public void A_global_listener_without_a_matching_scope_keeps_synchronous_fallback_and_any_accepting_scope_enables_work()
    {
        using var f = new Fixture();
        var requested = new List<FileAutoWork>();
        var acceptsThisDatabase = false;
        void Receive(FileAutoWork work) => requested.Add(work);
        bool Accepts(string path) => acceptsThisDatabase && path == f.Manager.Path;
        bool AnotherScope(string path) => false;
        ConstructorRepository.FileAutoWorkRequested += Receive;
        ConstructorRepository.FileAutoWorkSupported += Accepts;
        ConstructorRepository.FileAutoWorkSupported += AnotherScope;
        try
        {
            var synchronous = Assert.Single(f.EditSource(f.One, "B"));
            Assert.Equal(f.FileField, synchronous.FieldId);
            Assert.Equal(f.PathFor("B"), synchronous.Value);
            Assert.Equal(f.PathFor("B"), f.Value(f.One));
            Assert.Empty(requested);

            // Последний подписчик отказывает: достаточно согласия первого, а не результата
            // обычного вызова multicast Func, который вернул бы только последний bool.
            acceptsThisDatabase = true;
            Assert.Empty(f.EditSource(f.One, "C"));
            var queued = Assert.Single(requested);
            Assert.Equal(f.Manager.Path, queued.DbPath);
            Assert.Equal(f.FileField, queued.FieldId);
            Assert.Equal(f.One, queued.RecordId);
            Assert.Equal(f.PathFor("B"), f.Value(f.One));
            Assert.Equal(ConstructorRepository.FileAutoResolveResult.Ok,
                ConstructorRepository.ExecuteFileAutoWork(queued));
            Assert.Equal(f.PathFor("C"), f.Value(f.One));
        }
        finally
        {
            ConstructorRepository.FileAutoWorkRequested -= Receive;
            ConstructorRepository.FileAutoWorkSupported -= Accepts;
            ConstructorRepository.FileAutoWorkSupported -= AnotherScope;
        }
    }

    [Fact]
    public void Source_edits_use_a_catalog_snapshot_until_explicit_refresh_then_reuse_it_without_a_folder_scan()
    {
        using var f = new Fixture();
        Assert.Equal(ConstructorRepository.FileAutoResolveResult.Ok, f.Resolve());
        var added = f.PathFor("D");
        File.WriteAllText(added, "появился после готового снимка");

        var beforeRefresh = Assert.Single(f.EditSource(f.One, "D"));
        Assert.Equal("", beforeRefresh.Value);
        Assert.Equal("", f.Value(f.One));
        Assert.Equal(ConstructorRepository.FileAutoResolveResult.Ok, f.Resolve());
        Assert.Equal(added, f.Value(f.One));
        Assert.Equal(f.PathFor("B"), Assert.Single(f.EditSource(f.One, "B")).Value);

        var offline = f.Folder + "-offline";
        Directory.Move(f.Folder, offline);
        try
        {
            // Каталог хранит пути снимка. При недоступной папке новый обход не смог бы
            // найти D и оставил бы прежнее B; готовый снимок отвечает сразу без файлового IO.
            var fromSnapshot = Assert.Single(f.EditSource(f.One, "D"));
            Assert.Equal(added, fromSnapshot.Value);
            Assert.Equal(added, f.Value(f.One));
        }
        finally
        {
            Directory.Move(offline, f.Folder);
        }
    }

    private sealed class InlineProgress(Action<double> report) : IProgress<double>
    {
        public void Report(double value) => report(value);
    }

    private sealed class Fixture : IDisposable
    {
        private readonly RelationIntegrityFixture _database;
        private readonly bool _titleSource;
        public DatabaseManager Manager => _database.Source;
        public ConstructorRepository Repo => Manager.Constructor;
        public int Collection { get; }
        public int SourceField { get; }
        public int FileField { get; }
        public int One { get; }
        public int Two { get; }
        public string Folder { get; }
        public string OldOne => PathFor("previous-one");
        public string OldTwo => PathFor("previous-two");
        public FileAutoRule Rule { get; private set; }

        public Fixture(string provider = "sqlite", bool titleSource = false)
        {
            _database = new(provider);
            _titleSource = titleSource;
            try
            {
                Folder = _database.Backups.File("files");
                Directory.CreateDirectory(Folder);
                foreach (var name in new[] { "A", "B", "C" }) File.WriteAllText(PathFor(name), "fixture");
                Collection = SampleData.AddCollection(Repo, "Безопасная автоподстановка").Id!.Value;
                SourceField = SampleData.AddField(Repo, Collection, "Имя файла", "text").Id!.Value;
                var file = SampleData.AddField(Repo, Collection, "Файл", "file");
                FileField = file.Id!.Value;
                One = SampleData.AddRecord(Repo, Collection, "A").Id!.Value;
                Two = SampleData.AddRecord(Repo, Collection, "B").Id!.Value;
                Repo.SetFieldValue(One, SourceField, "A");
                Repo.SetFieldValue(Two, SourceField, "B");
                Repo.SetFieldValue(One, FileField, OldOne);
                Repo.SetFieldValue(Two, FileField, OldTwo);
                Rule = new(titleSource ? FileAutoRule.TitleSource : SourceField.ToString(), Folder, "pdf");
                file.ConfigJson = Rule.ToConfigJson();
                Repo.UpdateField(file);
            }
            catch { _database.Dispose(); throw; }
        }

        public string PathFor(string name) => Path.Combine(Folder, name + ".pdf");
        public AppDbContext Other() => new(Manager.Path);
        public string Snapshot() => _database.Snapshot();

        public string Value(int record)
        {
            using var read = Other();
            return read.DbFieldValues.AsNoTracking().Where(v => v.RecordId == record && v.FieldId == FileField)
                .Select(v => v.Value).SingleOrDefault() ?? "";
        }

        public string Source(int record)
        {
            using var read = Other();
            return _titleSource ? read.DbRecords.AsNoTracking().Single(r => r.Id == record).Title
                : read.DbFieldValues.AsNoTracking().Single(v => v.RecordId == record && v.FieldId == SourceField).Value;
        }

        public IReadOnlyList<(int FieldId, string Value)> EditSource(int record, string value) => _titleSource
            ? Repo.SetRecordTitle(record, value) : Repo.SetFieldValue(record, SourceField, value);

        public void Configure(FileAutoRule rule)
        {
            using var other = Other();
            other.DbFields.Single(f => f.Id == FileField).ConfigJson = rule.ToConfigJson();
            other.SaveChanges();
            Rule = rule;
        }



        public ConstructorRepository.FileAutoResolveResult Resolve(IProgress<double>? progress = null,
            CancellationToken ct = default) => Repo.ResolveAutoFileFieldAsync(FileField, progress, ct).GetAwaiter().GetResult();

        public void Dispose() => _database.Dispose();
    }
}
