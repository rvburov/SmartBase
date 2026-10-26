using Microsoft.EntityFrameworkCore;
using SmartBase.Database;
using SmartBase.Database.Repositories;
using SmartBase.Extensions.DocsCollection;

namespace SmartBase.Tests.Extensions.DocsCollection;

public sealed class ConcurrentEngineTests
{
    [Fact]
    public void Eight_scans_fill_only_their_own_collections_without_changing_files()
    {
        var paths = ConcurrentTasks.Run(8, index => new Fixture(index), (f, index, ct) =>
        {
            var docs = DocsCollectionBuilder.Scan(f.Files.Path, true, new HashSet<string>(), ct);
            Assert.Equal(2, docs.Count);
            var count = DocsCollectionBuilder.FillRecords(f.Database.Path, f.Shell, docs, null, ct);
            Assert.Equal(2, count);
            using var context = new AppDbContext(f.Database.Path);
            var repo = new ConstructorRepository(context);
            var records = repo.GetRecords(f.Shell.CollectionId);
            Assert.Equal(2, records.Count);
            Assert.Equal(new[] { $"Документ {index}", $"Приложение {index}" }, records.Select(r => r.Title).Order().ToArray());
            foreach (var record in records)
            {
                var entry = Assert.Single(docs, d => d.Name == record.Title);
                var values = repo.GetFieldValues(record.Id!.Value);
                Assert.Equal(entry.FullPath, values[f.Shell.FileFieldId]);
                Assert.Equal(entry.Format, values[f.Shell.FormatFieldId]);
                Assert.Equal(entry.FolderName, values.GetValueOrDefault(f.Shell.FolderFieldId, ""));
            }
            Assert.Equal(2, context.DbRecords.AsNoTracking().Count());
            Assert.Equal(f.Hashes, f.Sources.Select(ConcurrentTasks.Fingerprint).ToArray());
            return f.Database.Path;
        });
        Assert.Equal(8, paths.Distinct(StringComparer.Ordinal).Count());
    }

    private sealed class Fixture : IDisposable
    {
        public TempDatabase Database { get; } = new("concurrent-docs");
        public TempDir Files { get; } = new("concurrent-docs");
        public DocsShell Shell { get; }
        public string[] Sources { get; }
        public string[] Hashes { get; }
        public Fixture(int index)
        {
            Shell = DocsCollectionBuilder.CreateShell(Database.Constructor, $"Документы {index}");
            // Расширение собирает метаданные файлов, содержимое PDF/книг здесь не декодируется.
            Sources = [Files.Touch($"Документ {index}.pdf"), Files.Touch(Path.Combine("Приложения", $"Приложение {index}.xlsx"))];
            for (var i = 0; i < Sources.Length; i++) File.WriteAllText(Sources[i], $"синтетическое содержимое {index}-{i}");
            Hashes = Sources.Select(ConcurrentTasks.Fingerprint).ToArray();
        }
        public void Dispose() { Database.Dispose(); Files.Dispose(); }
    }
}
