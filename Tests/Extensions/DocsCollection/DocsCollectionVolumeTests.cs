using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using SmartBase.Database;
using SmartBase.Extensions.DocsCollection;
using Xunit.Abstractions;

namespace SmartBase.Tests.Extensions.DocsCollection;

/// <summary>Дерево документов и объем больше одного пакета: проверяем все значения,
/// а время обхода и заливки выводим отдельно, без нестабильного порога в секундах.</summary>
public sealed class DocsCollectionVolumeTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData(1000)]
    [InlineData(8000)]
    [InlineData(10001)]
    public void Recursive_documents_keep_all_values_and_order(int count)
    {
        using var db = new TempDatabase();
        using var dir = new TempDir();
        for (var i = 0; i < count; i++)
            dir.Touch(Path.Combine($"Раздел{i % 80:00}", "Вложенная", $"Документ{i:00000}.PDF"));

        var watch = Stopwatch.StartNew();
        var docs = DocsCollectionBuilder.Scan(dir.Path, true, new HashSet<string>(), CancellationToken.None);
        output.WriteLine($"{count}: обход и сортировка {watch.Elapsed.TotalMilliseconds:F1} мс");
        Assert.Equal(count, docs.Count);
        var shell = DocsCollectionBuilder.CreateShell(db.Constructor, "Документы");

        watch.Restart();
        var created = DocsCollectionBuilder.FillRecords(db.Path, shell, docs, null, CancellationToken.None);
        output.WriteLine($"{count}: запись {watch.Elapsed.TotalMilliseconds:F1} мс");

        Assert.Equal(count, created);
        var records = db.Context.DbRecords.AsNoTracking().Where(r => r.CollectionId == shell.CollectionId)
            .OrderBy(r => r.Position).ToList();
        var values = db.Context.DbFieldValues.AsNoTracking().ToDictionary(v => (v.RecordId, v.FieldId), v => v.Value);
        Assert.Equal(count, records.Count);
        Assert.Equal(count * 3, values.Count);
        for (var i = 0; i < count; i++)
        {
            var record = records[i];
            Assert.Equal(docs[i].Name, record.Title);
            Assert.Equal((i + 1) * 1000, record.Position);
            Assert.Equal(docs[i].FolderName, values[(record.Id, shell.FolderFieldId)]);
            Assert.Equal("pdf", values[(record.Id, shell.FormatFieldId)]);
            Assert.Equal(docs[i].FullPath, values[(record.Id, shell.FileFieldId)]);
        }
    }

    /// <summary>Профиль общей SQLite-БД на локальном файле: ускорение не должно отключать
    /// надежный журнал и синхронизацию, которые нужны на сетевой папке.</summary>
    [Fact]
    public void Shared_database_keeps_safe_pragmas_and_valid_data()
    {
        using var dir = new TempDir();
        var path = Path.Combine(dir.Path, "shared.db");
        NetworkPath.ForceNetwork(path);
        try
        {
            using var db = DatabaseManager.Open(1, "test", path);
            var shell = DocsCollectionBuilder.CreateShell(db.Constructor, "Документы");
            var docs = Enumerable.Range(0, 8000).Select(i =>
                new DocEntry($"Документ{i}", "Папка", "pdf", Path.Combine(dir.Path, $"Документ{i}.pdf"))).ToList();
            var watch = Stopwatch.StartNew();

            Assert.Equal(docs.Count, DocsCollectionBuilder.FillRecords(path, shell, docs, null, CancellationToken.None));
            output.WriteLine($"8000: запись с профилем общей БД {watch.Elapsed.TotalMilliseconds:F1} мс");
            Assert.Equal(docs.Count, db.Constructor.CountRecords(shell.CollectionId));
            Assert.Equal(docs.Count * 3, db.Context.DbFieldValues.Count());

            using var ctx = new AppDbContext(path);
            ctx.ApplyBulkPerformancePragmas();
            ctx.Database.OpenConnection();
            using var cmd = ctx.Database.GetDbConnection().CreateCommand();
            object? Read(string sql) { cmd.CommandText = sql; return cmd.ExecuteScalar(); }
            Assert.Equal("delete", Read("PRAGMA journal_mode;"));
            Assert.Equal(2L, Read("PRAGMA synchronous;"));
            Assert.Equal(1L, Read("PRAGMA foreign_keys;"));
            Assert.Equal("ok", Read("PRAGMA integrity_check;"));
        }
        finally
        {
            try { SqliteGate.ClearAllPools(); }
            finally { NetworkPath.Forget(path); }
        }
    }
}
