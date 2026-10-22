using System.Collections.Concurrent;
using System.Data.Common;
using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using SmartBase.Core.Models;
using SmartBase.Database;
using SmartBase.Database.Repositories;

namespace SmartBase.Tests.Integration;

public sealed class RelationIntegrityScanTests
{
    [SkippableTheory]
    [InlineData("sqlite")]
    [InlineData("postgres")]
    public void Scan_distinguishes_confirmed_damage_from_valid_mixed_edges_without_writing(string provider)
    {
        using var f = new RelationIntegrityFixture(provider);
        var missing = f.InsertEdge(f.Relation, f.SourceRecord, 0, 900001);
        f.InsertEdge(f.Relation, 900002, 0, f.LocalRecords[0], bypassConstraints: true);
        f.InsertEdge(900003, f.SourceRecord, 0, f.LocalRecords[0], bypassConstraints: true);
        var text = f.InsertField(f.SourceCollection, "Обычный текст", "text");
        f.InsertEdge(text, f.SourceRecord, 0, f.LocalRecords[0]);
        var wrongSource = f.RelationField(f.LocalCollection, f.LocalCollection, "Другая исходная коллекция");
        f.InsertEdge(wrongSource, f.SourceRecord, 0, f.LocalRecords[0]);
        var self = f.RelationField(f.SourceCollection, f.SourceCollection, "Самоссылка");
        f.InsertEdge(self, f.SourceRecord, 0, f.SourceRecord);
        var first = f.InsertEdge(f.Relation, f.SourceRecord, 0, f.LocalRecords[0], position: 1000);
        var duplicate = f.InsertEdge(f.Relation, f.SourceRecord, RelationIntegrityFixture.SourceId, f.LocalRecords[0], position: 2000);
        var single = f.RelationField(f.SourceCollection, f.LocalCollection, "Одна цель", single: true);
        f.InsertEdge(single, f.SourceRecord, 0, f.LocalRecords[0]);
        f.InsertEdge(single, f.SourceRecord, 0, f.LocalRecords[1]);
        var mixed = f.RelationField(f.SourceCollection, f.RemoteCollection, "Исторические цели", targetDb: RelationIntegrityFixture.TargetId);
        var legacy = f.InsertEdge(mixed, f.SourceRecord, 0, f.LocalRecords[1]);
        var remote = f.InsertEdge(mixed, f.SourceRecord, RelationIntegrityFixture.TargetId, f.RemoteRecords[1]);
        var before = f.Snapshot();
        var progress = new ConcurrentQueue<RelationIntegrityProgress>();

        var report = f.Scan(new(PageSize: 2), new InlineProgress<RelationIntegrityProgress>(progress.Enqueue));

        Assert.Equal(RelationIntegrityStatus.Completed, report.Status);
        Assert.Equal(12, report.ScannedEdges);
        foreach (var kind in new[] { RelationIntegrityIssueKind.MissingSourceRecord, RelationIntegrityIssueKind.MissingSourceField,
                     RelationIntegrityIssueKind.InvalidSourceFieldType, RelationIntegrityIssueKind.SourceCollectionMismatch,
                     RelationIntegrityIssueKind.MissingTargetRecord, RelationIntegrityIssueKind.InvalidSelfLink,
                     RelationIntegrityIssueKind.CanonicalDuplicate, RelationIntegrityIssueKind.SingleCardinality })
            Assert.Contains(report.Issues, i => i.Kind == kind);
        Assert.Contains(report.Issues, i => i.EdgeId == missing && i.Kind == RelationIntegrityIssueKind.MissingTargetRecord && i.IsConfirmed && i.CanRepair);
        Assert.Contains(report.Issues, i => i.EdgeId == duplicate && i.Kind == RelationIntegrityIssueKind.CanonicalDuplicate && i.CanRepair);
        Assert.DoesNotContain(report.Issues, i => i.EdgeId == first && i.Kind == RelationIntegrityIssueKind.CanonicalDuplicate && i.CanRepair);
        Assert.DoesNotContain(report.Issues, i => (i.EdgeId == legacy || i.EdgeId == remote) && i.CanRepair);
        Assert.DoesNotContain(report.Issues, i => i.FieldId == single && i.Kind == RelationIntegrityIssueKind.SingleCardinality && i.CanRepair);
        Assert.NotEmpty(progress);
        Assert.Equal(report.ScannedEdges, progress.Last().ScannedEdges);
        Assert.Equal(before, f.Snapshot());
        var preview = f.Preview(report);
        Assert.True(preview.CanApply, preview.Message);
        Assert.DoesNotContain(preview.Issues, i => i.EdgeId == first || i.EdgeId == legacy || i.EdgeId == remote || i.FieldId == single);
        Assert.Equal(before, f.Snapshot());
    }

    [Theory]
    [InlineData("missing", RelationIntegrityIssueKind.MissingTargetRecord, true)]
    [InlineData("unavailable", RelationIntegrityIssueKind.TargetUnavailable, false)]
    [InlineData("denied", RelationIntegrityIssueKind.TargetAccessDenied, false)]
    public void Target_proof_keeps_unavailable_and_forbidden_targets_out_of_repair(string state,
        RelationIntegrityIssueKind expected, bool repairable)
    {
        using var f = new RelationIntegrityFixture();
        var field = f.RelationField(f.SourceCollection, f.RemoteCollection, "Внешняя связь", targetDb: RelationIntegrityFixture.TargetId);
        var target = state == "missing" ? 910001 : f.RemoteRecords[0];
        var edge = f.InsertEdge(field, f.SourceRecord, RelationIntegrityFixture.TargetId, target);
        if (state == "unavailable") f.SetTargetAvailable(false);
        if (state == "denied") f.SetRights(f.Target, PermAction.All & ~PermAction.ViewCollection);
        var before = f.Snapshot();
        var targetBefore = f.Snapshot(f.Target);

        var report = f.Scan();

        Assert.Equal(RelationIntegrityStatus.Completed, report.Status);
        var issue = Assert.Single(report.Issues.Where(i => i.EdgeId == edge && i.Kind == expected));
        Assert.Equal(repairable, issue.CanRepair);
        if (!repairable) Assert.False(issue.IsConfirmed);
        var serialized = JsonSerializer.Serialize(report);
        if (state != "missing")
        {
            Assert.DoesNotContain(RelationIntegrityFixture.SecretTitle, serialized);
            Assert.DoesNotContain(RelationIntegrityFixture.SecretCollection, serialized);
        }
        Assert.DoesNotContain(f.Target.Path, serialized);
        Assert.DoesNotContain("Password=", serialized, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(before, f.Snapshot());
        Assert.Equal(targetBefore, f.Snapshot(f.Target));
        var plan = f.Preview(report);
        Assert.Equal(repairable, plan.CanApply);
        Assert.Equal(repairable ? 1 : 0, plan.EdgesToRemove);
    }

    [Fact]
    public void Invalid_configuration_dependencies_and_cycles_are_diagnostics_not_automatic_edits()
    {
        using var f = new RelationIntegrityFixture();
        var malformed = f.InsertField(f.SourceCollection, "Поврежденная настройка", "relation", "{broken");
        var missing = f.InsertField(f.SourceCollection, "Нет настройки", "relation", "{}");
        var missingProperty = f.InsertField(f.SourceCollection, "Нет свойства цели", "relation",
            new RelationConfig(f.LocalCollection, false, DisplayField: "920001").ToConfigJson());
        var wrongProperty = f.InsertField(f.SourceCollection, "Свойство другой коллекции", "text");
        var invalid = f.InsertField(f.SourceCollection, "Неверная зависимость", "relation",
            new RelationConfig(f.LocalCollection, false, SearchField: wrongProperty.ToString(CultureInfo.InvariantCulture)).ToConfigJson());
        var missingRelation = f.InsertField(f.SourceCollection, "Нет исходной связи", "lookup",
            new RelationComputedConfig(920002).ToConfigJson());
        var lookup = f.InsertField(f.SourceCollection, "Циклическое значение", "lookup",
            new RelationComputedConfig(f.Relation).ToConfigJson());
        f.SetConfig(f.Relation, new RelationConfig(f.LocalCollection, false,
            Auto: new(lookup.ToString(CultureInfo.InvariantCulture), RelationConfig.TitleField)).ToConfigJson());
        var before = f.Snapshot();

        var report = f.Scan();

        Assert.Equal(RelationIntegrityStatus.Completed, report.Status);
        Assert.Contains(report.Issues, i => i.FieldId == malformed && i.Kind == RelationIntegrityIssueKind.InvalidConfiguration);
        Assert.Contains(report.Issues, i => i.FieldId == missing && i.Kind == RelationIntegrityIssueKind.MissingConfiguration);
        Assert.Contains(report.Issues, i => i.FieldId == missingProperty && i.Kind == RelationIntegrityIssueKind.MissingDependency);
        Assert.Contains(report.Issues, i => i.FieldId == invalid && i.Kind is RelationIntegrityIssueKind.InvalidDependency or RelationIntegrityIssueKind.MissingDependency);
        Assert.Contains(report.Issues, i => i.FieldId == missingRelation && i.Kind == RelationIntegrityIssueKind.MissingDependency);
        Assert.Contains(report.Issues, i => i.Kind == RelationIntegrityIssueKind.DependencyCycle && (i.FieldId == f.Relation || i.FieldId == lookup));
        Assert.DoesNotContain(report.Issues, i => i.CanRepair);
        Assert.False(f.Preview(report).CanApply);
        Assert.Equal(before, f.Snapshot());
    }

    [Fact]
    public void Truncation_retains_exact_counts_and_cannot_select_an_issue_hidden_from_the_report()
    {
        using var f = new RelationIntegrityFixture();
        for (var i = 0; i < 121; i++) f.InsertEdge(f.Relation, f.SourceRecord, 0, 930000 + i);
        var before = f.Snapshot();
        var full = f.Scan(new(PageSize: 50, MaxIssues: 200));
        var limited = f.Scan(new(PageSize: 50, MaxIssues: 2));
        Assert.Equal(RelationIntegrityStatus.Completed, limited.Status);
        Assert.True(limited.IsTruncated);
        Assert.Equal(121, limited.ScannedEdges);
        Assert.Equal(121, limited.Counts[RelationIntegrityIssueKind.MissingTargetRecord]);
        Assert.Equal(2, limited.Issues.Count);
        var hidden = full.Issues.First(i => i.CanRepair && limited.Issues.All(visible => visible.Key != i.Key));
        var plan = f.Preview(limited, [hidden.Key]);
        Assert.False(plan.CanApply);
        Assert.Equal(RelationIntegrityStatus.InvalidPlan, plan.Status);
        Assert.Equal(0, plan.EdgesToRemove);
        Assert.Equal(before, f.Snapshot());
    }

    [Fact]
    public void Duplicate_and_single_cell_state_survive_a_scan_page_boundary()
    {
        using var f = new RelationIntegrityFixture();
        var records = f.Source.Constructor.BulkAddRecords(f.LocalCollection, [],
            Enumerable.Range(0, 55).Select(i => new BulkRecordRow($"Цель {i}", [])), 55, default);
        f.SetConfig(f.Relation, new RelationConfig(f.LocalCollection, true).ToConfigJson());
        f.InsertEdge(f.Relation, f.SourceRecord, 0, f.LocalRecords[0], position: 0);
        for (var i = 0; i < records.Count; i++)
            f.InsertEdge(f.Relation, f.SourceRecord, 0, records[i], position: i + 1);
        var duplicate = f.InsertEdge(f.Relation, f.SourceRecord, RelationIntegrityFixture.SourceId, f.LocalRecords[0], position: 60);
        var report = f.Scan(new(PageSize: 50));
        Assert.Equal(RelationIntegrityStatus.Completed, report.Status);
        Assert.Equal(57, report.ScannedEdges);
        Assert.Equal(1, report.Counts[RelationIntegrityIssueKind.CanonicalDuplicate]);
        Assert.Equal(1, report.Counts[RelationIntegrityIssueKind.SingleCardinality]);
        Assert.Contains(report.Issues, i => i.EdgeId == duplicate && i.Kind == RelationIntegrityIssueKind.CanonicalDuplicate && i.CanRepair);
        Assert.DoesNotContain(report.Issues, i => i.Kind == RelationIntegrityIssueKind.MissingTargetRecord);
    }

    [Fact]
    public void Source_denial_and_precancel_do_not_expose_or_mutate_data()
    {
        using var f = new RelationIntegrityFixture();
        f.InsertEdge(f.Relation, f.SourceRecord, 0, 940000);
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();
        var before = f.Snapshot();
        var cancelled = f.Source.Constructor.ScanRelationIntegrityAsync(ct: cancel.Token).GetAwaiter().GetResult();
        Assert.Equal(RelationIntegrityStatus.Cancelled, cancelled.Status);
        Assert.Equal(before, f.Snapshot());
        f.SetRights(f.Source, PermAction.None);
        before = f.Snapshot();
        var denied = f.Scan();
        Assert.Equal(RelationIntegrityStatus.NoAccess, denied.Status);
        Assert.Empty(denied.Issues);
        Assert.Empty(denied.Counts);
        Assert.Equal(0, denied.ScannedEdges);
        Assert.DoesNotContain("Исходная запись", JsonSerializer.Serialize(denied));
        Assert.Equal(before, f.Snapshot());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void A_physically_missing_database_is_not_recreated_by_scanning(bool sourceMissing)
    {
        using var f = new RelationIntegrityFixture();
        var field = f.RelationField(f.SourceCollection, f.RemoteCollection, "Внешняя связь", targetDb: RelationIntegrityFixture.TargetId);
        f.InsertEdge(field, f.SourceRecord, RelationIntegrityFixture.TargetId, f.RemoteRecords[0]);
        var before = sourceMissing ? null : f.Snapshot();
        var original = sourceMissing ? f.Source.Path : f.Target.Path;
        var offline = f.MoveOffline(sourceMissing ? f.Source : f.Target);
        var hash = SHA256.HashData(File.ReadAllBytes(offline));
        var report = f.Scan();
        Assert.Equal(sourceMissing ? RelationIntegrityStatus.Unavailable : RelationIntegrityStatus.Completed, report.Status);
        Assert.False(File.Exists(original), "Проверка не должна создавать пустую базу на месте недоступной");
        Assert.Equal(hash, SHA256.HashData(File.ReadAllBytes(offline)));
        if (!sourceMissing)
        {
            Assert.Contains(report.Issues, i => i.Kind == RelationIntegrityIssueKind.TargetUnavailable && !i.IsConfirmed && !i.CanRepair);
            Assert.DoesNotContain(report.Issues, i => i.Kind == RelationIntegrityIssueKind.MissingTargetRecord);
            Assert.Equal(before, f.Snapshot());
        }
        Assert.DoesNotContain(original, report.Message);
    }

    private sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}

/// <summary>Тесты синхронно дожидаются worker API: scoped resolver создается и снимается на одном потоке.</summary>
internal sealed class RelationIntegrityFixture : IDisposable
{
    public const int SourceId = 4101;
    public const int TargetId = 4202;
    public const string SecretTitle = "CONFIDENTIAL_TARGET_8f2b";
    public const string SecretCollection = "CONFIDENTIAL_COLLECTION_8f2b";
    private readonly TempDatabase? _sqlite;
    private readonly string? _postgresDatabase;
    private readonly IDisposable? _resolver;
    private bool _targetAvailable = true;
    public DatabaseManager Source { get; } = null!;
    public TempDatabase Remote { get; }
    public DatabaseManager Target => Remote.Manager;
    public TempDir Backups { get; }
    public PgConnectionInfo? PgInfo { get; }
    public int SourceCollection { get; }
    public int LocalCollection { get; }
    public int RemoteCollection { get; }
    public int SourceRecord { get; }
    public int[] LocalRecords { get; }
    public int[] RemoteRecords { get; }
    public int Relation { get; }

    public RelationIntegrityFixture(string provider = "sqlite")
    {
        if (provider == "postgres")
            Skip.IfNot(PgTestServer.IsAvailable, "Стенд PostgreSQL sb-pg (порт 5455) не поднят");
        Remote = new(TargetId, "target");
        Backups = new();
        try
        {
            if (provider == "postgres")
            {
                _postgresDatabase = "sb_integrity_" + Guid.NewGuid().ToString("N")[..12];
                PgAdmin($"CREATE DATABASE {_postgresDatabase}");
                PgInfo = new(PgTestServer.Host, PgTestServer.Port, _postgresDatabase,
                    PgTestServer.User, PgTestServer.Password, PgConnectionInfo.DefaultSslMode);
                Source = DatabaseManager.Open(SourceId, "integrity-source", PgInfo.ToConnectionString(), provider: "postgres");
            }
            else
            {
                _sqlite = new(SourceId, "integrity-source");
                Source = _sqlite.Manager;
            }
            _resolver = RelationDbResolver.UseScopedResolver(id => id == SourceId ? Source.Constructor
                    : id == TargetId && _targetAvailable ? Target.Constructor : null,
                repo => repo.DbPath == Source.Path ? SourceId : repo.DbPath == Target.Path ? TargetId : 0);
            LocalCollection = SampleData.AddCollection(Source.Constructor, "Локальные цели").Id!.Value;
            LocalRecords = [SampleData.AddRecord(Source.Constructor, LocalCollection, "Локальная А").Id!.Value,
                SampleData.AddRecord(Source.Constructor, LocalCollection, "Локальная Б").Id!.Value];
            SourceCollection = SampleData.AddCollection(Source.Constructor, "Источники").Id!.Value;
            SourceRecord = SampleData.AddRecord(Source.Constructor, SourceCollection, "Исходная запись").Id!.Value;
            RemoteCollection = SampleData.AddCollection(Target.Constructor, SecretCollection).Id!.Value;
            RemoteRecords = [SampleData.AddRecord(Target.Constructor, RemoteCollection, SecretTitle).Id!.Value,
                SampleData.AddRecord(Target.Constructor, RemoteCollection, "Удаленная Б").Id!.Value];
            Relation = RelationField(SourceCollection, LocalCollection, "Ручная связь");
            WaitStartupRepairs(Source);
            WaitStartupRepairs(Target);
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    // Разовые ремонты открытия SQLite (RepairNullTitles, RepairFormattedNumbers) идут в фоне и
    // дописывают метку в db_meta: без ожидания она попадает между снимками «до» и «после».
    private static void WaitStartupRepairs(DatabaseManager manager)
    {
        if (manager.Context.IsPostgres) return;
        using var connection = Open(manager);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM db_meta WHERE \"Key\" IN ('repair.title-null', 'repair.number-display')";
        var watch = Stopwatch.StartNew();
        while (Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture) < 2 && watch.Elapsed < TimeSpan.FromSeconds(10))
            Thread.Sleep(20);
    }

    public RelationIntegrityReport Scan(RelationIntegrityScanOptions? options = null,
        IProgress<RelationIntegrityProgress>? progress = null) =>
        Source.Constructor.ScanRelationIntegrityAsync(options, progress).GetAwaiter().GetResult();

    public RelationIntegrityRepairPlan Preview(RelationIntegrityReport report, IReadOnlyCollection<string>? keys = null) =>
        Source.Constructor.PreviewRelationIntegrityRepairAsync(report, keys).GetAwaiter().GetResult();

    public void SetTargetAvailable(bool value) => _targetAvailable = value;

    public int RelationField(int source, int target, string name, bool single = false, int targetDb = 0) =>
        InsertField(source, name, "relation", new RelationConfig(target, single, TargetDbId: targetDb).ToConfigJson());

    public int InsertField(int collection, string name, string type, string config = "{}")
    {
        using var connection = Open(Source);
        using var command = connection.CreateCommand();
        command.CommandText = ColumnSql(connection, "INSERT INTO db_fields (\"CollectionId\",\"Name\",\"FieldType\",\"ConfigJson\",\"Position\",\"IsCalStart\",\"IsCalEnd\",\"IsSystem\",\"IsRequired\",\"IsTaskExec\",\"IsTaskAssigner\",\"IsTaskStatus\",\"IsDescription\",\"IsDocFile\",\"IsDocType\") VALUES (@col,@name,@type,@cfg,1000,0,0,0,0,0,0,0,0,0,0) RETURNING \"Id\"");
        Parameter(command, "@col", collection); Parameter(command, "@name", name);
        Parameter(command, "@type", type); Parameter(command, "@cfg", config);
        return Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    public int InsertEdge(int field, int source, int targetDb, int target, int position = 1000, bool bypassConstraints = false)
    {
        using var connection = Open(Source, foreignKeys: !bypassConstraints);
        if (bypassConstraints && Source.Context.IsPostgres) Execute(connection, "SET session_replication_role = replica");
        try
        {
            using var command = connection.CreateCommand();
            command.CommandText = ColumnSql(connection, "INSERT INTO db_record_links (\"FieldId\",\"FromRecordId\",\"TargetDbId\",\"ToRecordId\",\"Position\") VALUES (@field,@source,@db,@target,@position) RETURNING \"Id\"");
            Parameter(command, "@field", field); Parameter(command, "@source", source);
            Parameter(command, "@db", targetDb); Parameter(command, "@target", target); Parameter(command, "@position", position);
            return Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture);
        }
        finally { if (bypassConstraints && Source.Context.IsPostgres) Execute(connection, "SET session_replication_role = origin"); }
    }

    public void SetConfig(int field, string config)
    {
        using var connection = Open(Source);
        using var command = connection.CreateCommand();
        command.CommandText = ColumnSql(connection, "UPDATE db_fields SET \"ConfigJson\"=@config WHERE \"Id\"=@id");
        Parameter(command, "@config", config); Parameter(command, "@id", field); command.ExecuteNonQuery();
    }

    public void SetRights(DatabaseManager manager, PermAction mask)
    {
        using (var context = new AppDbContext(manager.Path))
        {
            var user = context.DbUsers.Single(u => u.UserKey == SmartBase.Utils.CurrentUser.Key);
            user.IsAdmin = true;
            user.IsApproved = true;
            context.SaveChanges();
        }
        manager.Permissions.Reload(notify: false);
        manager.Permissions.SetMask(manager.Permissions.CurrentUserId, null, (int)mask);
        using (var context = new AppDbContext(manager.Path))
        {
            var user = context.DbUsers.Single(u => u.UserKey == SmartBase.Utils.CurrentUser.Key);
            user.IsAdmin = false;
            context.SaveChanges();
        }
        manager.Permissions.Reload(notify: false);
    }

    public void RevokeAdministratorWithoutReload(DatabaseManager manager)
    {
        using var context = new AppDbContext(manager.Path);
        var user = context.DbUsers.Single(u => u.UserKey == SmartBase.Utils.CurrentUser.Key);
        user.IsAdmin = false;
        context.SaveChanges();
    }

    public IReadOnlyList<int> EdgeIds(string? sqliteArtifact = null)
    {
        using var connection = sqliteArtifact is null ? Open(Source) : OpenSqlite(sqliteArtifact);
        using var command = connection.CreateCommand();
        command.CommandText = ColumnSql(connection, "SELECT \"Id\" FROM db_record_links ORDER BY \"Id\"");
        using var reader = command.ExecuteReader();
        var result = new List<int>();
        while (reader.Read()) result.Add(Convert.ToInt32(reader.GetValue(0), CultureInfo.InvariantCulture));
        return result;
    }

    public string Snapshot(DatabaseManager? manager = null)
    {
        manager ??= Source;
        using var connection = Open(manager);
        using var list = connection.CreateCommand();
        list.CommandText = manager.Context.IsPostgres ? "SELECT tablename FROM pg_tables WHERE schemaname='public' ORDER BY tablename"
            : "SELECT name FROM sqlite_master WHERE type='table' ORDER BY name";
        var names = new List<string>();
        using (var reader = list.ExecuteReader()) while (reader.Read()) names.Add(reader.GetString(0));
        var tables = new SortedDictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var name in names)
        {
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT * FROM \"" + name.Replace("\"", "\"\"") + "\"";
            using var reader = command.ExecuteReader();
            var rows = new List<string>();
            while (reader.Read())
            {
                var values = new object?[reader.FieldCount];
                for (var i = 0; i < values.Length; i++) values[i] = reader.IsDBNull(i) ? null : reader.GetValue(i);
                rows.Add(JsonSerializer.Serialize(values));
            }
            rows.Sort(StringComparer.Ordinal);
            tables[name] = rows;
        }
        return JsonSerializer.Serialize(tables);
    }

    public void ExecuteSource(string sql)
    {
        using var connection = Open(Source);
        Execute(connection, sql);
    }

    public void RestoreMissingRemoteRecord(int id)
    {
        using var connection = Open(Target);
        using var command = connection.CreateCommand();
        command.CommandText = ColumnSql(connection, "INSERT INTO db_records (\"Id\",\"CollectionId\",\"CreatedAt\",\"UpdatedAt\",\"CreatedBy\",\"UpdatedBy\",\"Position\",\"AllDay\",\"Title\") VALUES (@id,@col,'','','','',1000,TRUE,'Возвращенная цель')");
        Parameter(command, "@id", id); Parameter(command, "@col", RemoteCollection); command.ExecuteNonQuery();
    }

    public string MoveOffline(DatabaseManager manager)
    {
        if (manager.Context.IsPostgres) throw new InvalidOperationException("Файловая фикстура предназначена для SQLite");
        var path = manager.Path;
        manager.Dispose();
        AppDbContext.ResetPool(path);
        var offline = Backups.File(manager.DbId + "-offline.db");
        foreach (var suffix in new[] { "", "-wal", "-shm" })
            if (File.Exists(path + suffix)) File.Move(path + suffix, offline + suffix);
        return offline;
    }

    private static DbConnection Open(DatabaseManager manager, bool foreignKeys = true)
    {
        DbConnection connection = manager.Context.IsPostgres ? new NpgsqlConnection(manager.Path)
            : SqliteGate.NewConnection(new SqliteConnectionStringBuilder { DataSource = manager.Path, ForeignKeys = foreignKeys, Pooling = false }.ToString());
        connection.Open();
        return connection;
    }

    private static SqliteConnection OpenSqlite(string path)
    {
        var connection = SqliteGate.NewConnection(new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        connection.Open();
        return connection;
    }

    // В PostgreSQL имена колонок схемы строчные, в SQLite сохранен исходный регистр.
    private static string ColumnSql(DbConnection connection, string sql) =>
        connection is NpgsqlConnection ? sql.Replace("\"", "") : sql;

    private static void Execute(DbConnection connection, string sql)
    {
        using var command = connection.CreateCommand(); command.CommandText = sql; command.ExecuteNonQuery();
    }

    private static void Parameter(DbCommand command, string name, object value)
    {
        var parameter = command.CreateParameter(); parameter.ParameterName = name; parameter.Value = value; command.Parameters.Add(parameter);
    }

    private static void PgAdmin(string sql)
    {
        using var connection = new NpgsqlConnection(PgTestServer.ConnectionString("postgres"));
        connection.Open(); Execute(connection, sql);
    }

    public int InterruptPostgresSource()
    {
        var database = OwnPostgresSource();
        // Открытый probe гарантирует реальный обрыв сессии, даже если остальные пулы пусты.
        using var probe = new NpgsqlConnection(Source.Path);
        probe.Open();
        using var maintenance = new NpgsqlConnection(PgTestServer.ConnectionString("postgres"));
        maintenance.Open();
        Execute(maintenance, $"ALTER DATABASE {database} ALLOW_CONNECTIONS false");
        using var command = maintenance.CreateCommand();
        command.CommandText = "SELECT pg_terminate_backend(pid) FROM pg_stat_activity WHERE datname=@database AND pid<>pg_backend_pid()";
        Parameter(command, "@database", database);
        using var reader = command.ExecuteReader();
        var terminated = 0;
        while (reader.Read()) if (reader.GetBoolean(0)) terminated++;
        return terminated;
    }

    public void RestorePostgresSource()
    {
        var database = OwnPostgresSource();
        try { PgAdmin($"ALTER DATABASE {database} ALLOW_CONNECTIONS true"); }
        finally
        {
            using var pool = new NpgsqlConnection(Source.Path);
            NpgsqlConnection.ClearPool(pool);
        }
    }

    private string OwnPostgresSource()
    {
        const string prefix = "sb_integrity_";
        if (!Source.Context.IsPostgres || _postgresDatabase is null
            || !_postgresDatabase.StartsWith(prefix, StringComparison.Ordinal)
            || _postgresDatabase.Length != prefix.Length + 12
            || _postgresDatabase[prefix.Length..].Any(c => c is not (>= '0' and <= '9') and not (>= 'a' and <= 'f'))
            || new NpgsqlConnectionStringBuilder(Source.Path).Database != _postgresDatabase
            || PgInfo is null || PgInfo.Host != PgTestServer.Host || PgInfo.Port != PgTestServer.Port
            || PgInfo.Database != _postgresDatabase)
            throw new InvalidOperationException("Прерывать подключения можно только к собственной временной базе integrity");
        return _postgresDatabase;
    }

    public void Dispose()
    {
        try
        {
            _resolver?.Dispose();
            if (_sqlite is not null) _sqlite.Dispose();
            else
            {
                Source?.Dispose();
                NpgsqlConnection.ClearAllPools();
                if (_postgresDatabase is not null) PgAdmin($"DROP DATABASE IF EXISTS {_postgresDatabase} WITH (FORCE)");
            }
        }
        finally { Remote.Dispose(); Backups.Dispose(); }
    }
}
