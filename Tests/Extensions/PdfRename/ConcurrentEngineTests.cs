using SmartBase.Extensions.PdfRename;

namespace SmartBase.Tests.Extensions.PdfRename;

public sealed class ConcurrentEngineTests
{
    [Fact]
    public void Eight_rename_jobs_preserve_all_bytes_and_do_not_mix_folders()
    {
        var paths = ConcurrentTasks.Run(8, index => new Fixture(index), (f, index, ct) =>
        {
            var plan = PdfRenamer.BuildPlan(f.Directory.Path);
            var summary = PdfRenamer.Process(f.Directory.Path, plan, false, null, ct);
            Assert.Equal((1, 0, 0), (summary.Renamed, summary.Errors, summary.Conflicts));
            Assert.False(summary.Cancelled);
            var target = Path.Combine(f.Directory.Path, $"АБВГ.{index + 1:D3}.pdf");
            Assert.False(File.Exists(f.Source));
            Assert.Equal(f.Hash, ConcurrentTasks.Fingerprint(target));
            Assert.Equal(target, Assert.Single(Directory.GetFiles(f.Directory.Path)));
            return target;
        });
        Assert.Equal(8, paths.Distinct(StringComparer.Ordinal).Count());
    }

    private sealed class Fixture : IDisposable
    {
        public TempDir Directory { get; } = new("concurrent-rename");
        public string Source { get; }
        public string Hash { get; }
        public Fixture(int index)
        {
            Source = Path.Combine(Directory.Path, $"АБВГ.{index + 1:D3} описание.pdf");
            // Этот движок меняет только имена, декодирования PDF в его контракте нет.
            File.WriteAllBytes(Source, Enumerable.Range(0, 4096).Select(n => (byte)((n + index) % 251)).ToArray());
            Hash = ConcurrentTasks.Fingerprint(Source);
        }
        public void Dispose() => Directory.Dispose();
    }
}
