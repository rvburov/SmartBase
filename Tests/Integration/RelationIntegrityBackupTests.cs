using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using SmartBase.Core.Models;
using SmartBase.Database;
using SmartBase.Database.Backup;
using SmartBase.Database.Entities;
using SmartBase.Database.Repositories;

namespace SmartBase.Tests.Integration;

public sealed class RelationIntegrityBackupTests
{
    private sealed record Graph(int Collection, int From, int To, int Relation, int Number);

    private static Graph Seed(DatabaseManager manager)
    {
        var repo = manager.Constructor;
        var collection = SampleData.AddCollection(repo, "До исправления").Id!.Value;
        var code = SampleData.AddField(repo, collection, "Код").Id!.Value;
        var sourceKey = SampleData.AddField(repo, collection, "Искать код").Id!.Value;
        var number = SampleData.AddField(repo, collection, "Сумма", "number").Id!.Value;
        var a = SampleData.AddRecord(repo, collection, "А, Б", 1100).Id!.Value;
        var b = SampleData.AddRecord(repo, collection, "Б", 2500).Id!.Value;
        repo.SetFieldValue(a, code, "А"); repo.SetFieldValue(b, code, "Б");
        repo.SetFieldValue(a, sourceKey, "Б");
        repo.SetFieldValue(a, number, "2"); repo.SetFieldValue(b, number, "9,5");
        var relation = repo.AddField(new DbField { CollectionId = collection, Name = "Ручная связь", FieldType = "relation",
            ConfigJson = new RelationConfig(collection, false, "semicolon", DisplayField: code.ToString(), SearchField: RelationConfig.TitleField).ToConfigJson() });
        repo.SetLinks(relation.Id!.Value, a, [b]);
        repo.AddField(new DbField { CollectionId = collection, Name = "Автоматическая связь", FieldType = "relation",
            ConfigJson = new RelationConfig(collection, false, Auto: new(sourceKey.ToString(), code.ToString())).ToConfigJson() });
        repo.AddField(new DbField { CollectionId = collection, Name = "Подпись цели", FieldType = "lookup",
            ConfigJson = new RelationComputedConfig(relation.Id.Value, code.ToString()).ToConfigJson() });
        repo.AddField(new DbField { CollectionId = collection, Name = "Итог", FieldType = "rollup",
            ConfigJson = new RelationComputedConfig(relation.Id.Value, number.ToString(), "sum").ToConfigJson() });
        var legacy = repo.AddField(new DbField { CollectionId = collection, Name = "Прежние межбазовые цели", FieldType = "relation",
            ConfigJson = new RelationConfig(collection, false).ToConfigJson() });
        manager.Context.DbRecordLinks.AddRange(
            new DbRecordLinkEntity { FieldId = legacy.Id!.Value, FromRecordId = a, ToRecordId = b, TargetDbId = 0, Position = 100 },
            new DbRecordLinkEntity { FieldId = legacy.Id.Value, FromRecordId = a, ToRecordId = b, TargetDbId = 303, Position = 900 },
            // Семантически неверное ребро тоже должно сохраниться в копии до исправления.
            new DbRecordLinkEntity { FieldId = legacy.Id.Value, FromRecordId = a, ToRecordId = a, TargetDbId = 0, Position = 1500 });
        manager.Context.SaveChanges();
        foreach (var (type, raw, config) in new[]
        {
            ("text", "Первая строка\nВторая", "{}"), ("date", "2026-10-21", "{}"), ("checkbox", "1", "{}"),
            ("select", "Первый", "{\"options\":[\"Первый\",\"Второй\"]}"),
            ("status", "Готово", "{\"options\":[\"Готово\"]}"),
            ("multiselect", "[\"А\",\"Б\"]", "{\"options\":[\"А\",\"Б\"],\"separator\":\"newline\"}"),
            ("url", "https://example.com/backup", "{}"), ("file", "/tmp/repair-before.txt", "{}"), ("user", "Участник", "{}"),
        })
        {
            var field = repo.AddField(new DbField { CollectionId = collection, Name = $"Тип {type}", FieldType = type, ConfigJson = config });
            repo.SetFieldValue(a, field.Id!.Value, raw);
        }
        return new(collection, a, b, relation.Id.Value, number);
    }

    private static RelationIntegrityBackupRequest Request(DatabaseManager source, string fingerprint = "подтвержденный-план") =>
        new(source.DbId, source.Constructor.RelationDatabaseIdentity, fingerprint, source.Provider == "postgres" ? "postgres" : "sqlite");

    private static string Snapshot(AppDbContext context, ConstructorRepository repo)
    {
        var records = context.DbRecords.AsNoTracking().OrderBy(r => r.Id).ToList();
        var computed = repo.GetRelationComputedValues(records.Select(r => r.Id).ToList())
            .OrderBy(p => p.Key).Select(p => new { RecordId = p.Key, Values = p.Value.OrderBy(v => v.Key).ToArray() }).ToArray();
        return JsonSerializer.Serialize(new
        {
            Collections = context.DbCollections.AsNoTracking().OrderBy(c => c.Id).ToList(),
            Fields = context.DbFields.AsNoTracking().OrderBy(f => f.Id).ToList(), Records = records,
            Values = context.DbFieldValues.AsNoTracking().OrderBy(v => v.RecordId).ThenBy(v => v.FieldId).ToList(),
            Edges = context.DbRecordLinks.AsNoTracking().OrderBy(e => e.Id).ToList(), Computed = computed,
        });
    }

    [Fact]
    public async Task Sqlite_backup_restores_original_ids_edges_configs_and_computed_values()
    {
        using var source = new TempDatabase();
        using var directory = new TempDir();
        var graph = Seed(source.Manager);
        var before = Snapshot(source.Context, source.Constructor);
        var receipt = await RelationIntegrityBackupService.CreateVerifiedAsync(source.Manager, Request(source.Manager), directory.File("copies"));
        Assert.Equal(before, Snapshot(source.Context, source.Constructor));
        Assert.Equal(source.Constructor.RelationDatabaseIdentity, receipt.SourceDatabaseIdentity);
        Assert.Equal("sqlite", receipt.Provider);
        Assert.Equal("подтвержденный-план", receipt.PlanFingerprint);
        Assert.Equal(receipt.ContentSha256, Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(receipt.ArtifactPath))));
        source.Constructor.SetLinks(graph.Relation, graph.From, []);
        source.Constructor.SetFieldValue(graph.To, graph.Number, "99");
        var restoredPath = directory.File("restored.db");
        File.Copy(receipt.ArtifactPath, restoredPath);
        using var restored = new AppDbContext(restoredPath);
        var repo = new ConstructorRepository(restored);
        Assert.Equal(before, Snapshot(restored, repo));
        Assert.NotEqual(before, Snapshot(source.Context, source.Constructor));
        Assert.Equal(2, restored.DbRecordLinks.AsNoTracking().Count(l => l.TargetDbId == 303 || l.ToRecordId == graph.From));
    }

    [Fact]
    public async Task Sqlite_cancellation_after_copy_creation_publishes_nothing_and_keeps_source()
    {
        using var source = new TempDatabase();
        using var directory = new TempDir();
        Seed(source.Manager);
        var before = Snapshot(source.Context, source.Constructor);
        using var cancellation = new CancellationTokenSource();
        var copies = directory.File("copies");
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => RelationIntegrityBackupService.CreateVerifiedAsync(
            source.Manager, Request(source.Manager), copies, stage: (message, _) =>
            { if (message.StartsWith("Проверяю копию", StringComparison.Ordinal)) cancellation.Cancel(); }, ct: cancellation.Token));
        Assert.Equal(before, Snapshot(source.Context, source.Constructor));
        Assert.Empty(Directory.GetFiles(copies));
    }

    [Fact]
    public async Task Every_confirmation_creates_a_fresh_copy_without_overwriting_older_backups()
    {
        using var source = new TempDatabase();
        using var directory = new TempDir();
        Seed(source.Manager);
        var old = directory.File("old.db");
        await File.WriteAllTextAsync(old, "предыдущая копия");
        var first = await RelationIntegrityBackupService.CreateVerifiedAsync(source.Manager, Request(source.Manager, "первый"), directory.Path);
        var second = await RelationIntegrityBackupService.CreateVerifiedAsync(source.Manager, Request(source.Manager, "второй"), directory.Path);
        Assert.NotEqual(first.ArtifactPath, second.ArtifactPath);
        Assert.True(File.Exists(first.ArtifactPath)); Assert.True(File.Exists(second.ArtifactPath));
        Assert.Equal("предыдущая копия", await File.ReadAllTextAsync(old));
        Assert.Equal(3, Directory.GetFiles(directory.Path).Length);
    }

    [Fact]
    public async Task Mismatched_source_or_pre_cancelled_request_never_starts_backup()
    {
        using var source = new TempDatabase();
        using var directory = new TempDir();
        var copies = directory.File("copies");
        var request = Request(source.Manager) with { SourceDatabaseIdentity = "другая-база" };
        await Assert.ThrowsAsync<InvalidOperationException>(() => RelationIntegrityBackupService.CreateVerifiedAsync(source.Manager, request, copies));
        Assert.False(Directory.Exists(copies));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => RelationIntegrityBackupService.CreateVerifiedAsync(source.Manager,
            Request(source.Manager), copies, ct: new CancellationToken(true)));
        Assert.False(Directory.Exists(copies));
    }

    [SkippableFact]
    public async Task Postgres_verified_archive_restores_to_a_new_temporary_database_with_full_graph()
    {
        Skip.IfNot(PgTestServer.IsAvailable, "Стенд PostgreSQL (порт 5455) не поднят");
        Skip.If(PgDump.FindBinary() is null || RelationIntegrityBackupService.FindRestoreBinary() is null, "Нужны pg_dump и pg_restore");
        using var directory = new TempDir();
        var info = new PgConnectionInfo(PgTestServer.Host, PgTestServer.Port, PgTestServer.Database1,
            PgTestServer.User, PgTestServer.Password, PgConnectionInfo.DefaultSslMode);
        await using (var connection = new NpgsqlConnection(info.ToConnectionString()))
        {
            await connection.OpenAsync();
            await using var reset = connection.CreateCommand();
            reset.CommandText = "DROP SCHEMA IF EXISTS public CASCADE; CREATE SCHEMA public;";
            await reset.ExecuteNonQueryAsync();
        }
        using var source = DatabaseManager.Open(101, "Резервная копия PG", info.ToConnectionString(), provider: "postgres");
        var graph = Seed(source);
        var before = Snapshot(source.Context, source.Constructor);
        var receipt = await RelationIntegrityBackupService.CreateVerifiedAsync(source, Request(source), directory.Path, info);
        Assert.Equal(before, Snapshot(source.Context, source.Constructor));
        Assert.Equal("postgres", receipt.Provider);
        Assert.False(receipt.ArtifactPath.Contains("Password", StringComparison.OrdinalIgnoreCase));
        source.Constructor.SetLinks(graph.Relation, graph.From, []);
        source.Constructor.SetFieldValue(graph.To, graph.Number, "99");
        var temporaryName = "sb_relation_restore_" + Guid.NewGuid().ToString("N");
        await DatabaseDdlAsync(info, temporaryName, create: true);
        try
        {
            var restoreInfo = info with { Database = temporaryName };
            await RestoreTemporaryAsync(restoreInfo, receipt.ArtifactPath);
            using var restored = new AppDbContext(restoreInfo.ToConnectionString());
            Assert.Equal(before, Snapshot(restored, new ConstructorRepository(restored)));
            Assert.NotEqual(before, Snapshot(source.Context, source.Constructor));
        }
        finally { await DatabaseDdlAsync(info, temporaryName, create: false); }
    }

    [SkippableFact]
    public async Task Postgres_service_refuses_an_archive_corrupted_before_mandatory_verification()
    {
        Skip.IfNot(PgTestServer.IsAvailable, "Стенд PostgreSQL (порт 5455) не поднят");
        Skip.If(PgDump.FindBinary() is null || RelationIntegrityBackupService.FindRestoreBinary() is null, "Нужны pg_dump и pg_restore");
        using var directory = new TempDir();
        var copies = directory.File("only-this-attempt");
        var info = new PgConnectionInfo(PgTestServer.Host, PgTestServer.Port, PgTestServer.Database1,
            PgTestServer.User, PgTestServer.Password, PgConnectionInfo.DefaultSslMode);
        await using (var connection = new NpgsqlConnection(info.ToConnectionString()))
        {
            await connection.OpenAsync();
            await using var reset = connection.CreateCommand();
            reset.CommandText = "DROP SCHEMA IF EXISTS public CASCADE; CREATE SCHEMA public;";
            await reset.ExecuteNonQueryAsync();
        }
        using var source = DatabaseManager.Open(101, "Проверка обязательной проверки архива", info.ToConnectionString(), provider: "postgres");
        var collection = SampleData.AddCollection(source.Constructor).Id!.Value;
        SampleData.AddRecord(source.Constructor, collection, "Источник остается прежним");
        var before = Snapshot(source.Context, source.Constructor);
        var corrupted = false;
        await Assert.ThrowsAsync<InvalidOperationException>(() => RelationIntegrityBackupService.CreateVerifiedAsync(source,
            Request(source), copies, info, (message, _) =>
            {
                if (message != "Проверяю резервный архив PostgreSQL…") return;
                // Папка принадлежит только этому тесту; заменяется лишь еще не опубликованный файл.
                var pending = Directory.GetFiles(copies, "*.dump.pending").Single();
                File.WriteAllText(pending, "PGDMP архив поврежден до проверки");
                corrupted = true;
            }));
        Assert.True(corrupted, "Тест должен дойти до проверки уже созданного pg_dump архива.");
        Assert.Empty(Directory.GetFiles(copies));
        Assert.Equal(before, Snapshot(source.Context, source.Constructor));
    }

    [SkippableFact]
    public async Task Corrupt_postgres_archive_does_not_pass_full_read_verification()
    {
        Skip.If(RelationIntegrityBackupService.FindRestoreBinary() is null, "Нужен pg_restore");
        using var directory = new TempDir();
        var archive = directory.File("broken.dump");
        await File.WriteAllTextAsync(archive, "PGDMP поврежденный архив");
        await Assert.ThrowsAsync<InvalidDataException>(() => RelationIntegrityBackupService.VerifyPgArchiveAsync(archive));
    }

    private static async Task DatabaseDdlAsync(PgConnectionInfo info, string name, bool create)
    {
        // Этот тест никогда не восстанавливает и не удаляет существующую пользовательскую базу.
        Assert.StartsWith("sb_relation_restore_", name);
        await using var connection = new NpgsqlConnection((info with { Database = PgDatabaseCreator.MaintenanceDb }).ToConnectionString());
        await connection.OpenAsync();
        string sql;
        await using (var format = connection.CreateCommand())
        {
            format.CommandText = create ? "SELECT format('CREATE DATABASE %I', @name)" : "SELECT format('DROP DATABASE %I WITH (FORCE)', @name)";
            format.Parameters.AddWithValue("name", name);
            sql = (string)(await format.ExecuteScalarAsync())!;
        }
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    private static async Task RestoreTemporaryAsync(PgConnectionInfo info, string archive)
    {
        Assert.StartsWith("sb_relation_restore_", info.Database);
        var start = new ProcessStartInfo(RelationIntegrityBackupService.FindRestoreBinary()!)
        { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var value in new[] { "--exit-on-error", "--no-owner", "--no-privileges", "--no-password", "--host", info.Host,
                     "--port", info.Port.ToString(System.Globalization.CultureInfo.InvariantCulture), "--username", info.User, "--dbname", info.Database, archive })
            start.ArgumentList.Add(value);
        start.Environment["PGPASSWORD"] = info.Password;
        start.Environment["PGSSLMODE"] = info.SslMode.ToLowerInvariant();
        using var process = Process.Start(start)!;
        await Task.WhenAll(process.StandardOutput.BaseStream.CopyToAsync(Stream.Null),
            process.StandardError.BaseStream.CopyToAsync(Stream.Null), process.WaitForExitAsync());
        Assert.Equal(0, process.ExitCode);
    }
}
