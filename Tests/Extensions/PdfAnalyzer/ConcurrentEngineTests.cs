using ClosedXML.Excel;
using SmartBase.Extensions.PdfAnalyzer;

namespace SmartBase.Tests.Extensions.PdfAnalyzer;

public sealed class ConcurrentEngineTests
{
    [Fact]
    public void Eight_pdf_reports_are_readable_and_describe_only_their_inputs()
    {
        var paths = ConcurrentTasks.Run(8, index => new Fixture(index), (f, index, ct) =>
        {
            var result = PdfAnalyzerEngine.Run(f.Input, f.Report, ExportType.All, false, false, false, null, ct);
            Assert.Equal((1, 1, 0), (result.TotalFiles, result.Processed, result.Errors));
            using var book = new XLWorkbook(result.SavePath);
            var sheet = book.Worksheet("ОПИСЬ");
            Assert.Equal(2, sheet.LastRowUsed()!.RowNumber());
            var text = string.Join("|", sheet.Row(2).CellsUsed().Select(c => c.GetString()));
            Assert.Contains($"АБВГ.301234.{index + 1:D3}", text);
            Assert.Equal(f.Hash, ConcurrentTasks.Fingerprint(f.Source));
            Assert.Equal(f.Report, result.SavePath);
            Assert.Single(Directory.GetFiles(f.Output));
            return result.SavePath;
        });
        Assert.Equal(8, paths.Distinct(StringComparer.Ordinal).Count());
    }

    private sealed class Fixture : IDisposable
    {
        private readonly TempDir _dir = new("concurrent-analysis");
        public string Input { get; }
        public string Output { get; }
        public string Report { get; }
        public string Source { get; }
        public string Hash { get; }
        public Fixture(int index)
        {
            Input = Directory.CreateDirectory(Path.Combine(_dir.Path, "документы")).FullName;
            Output = Directory.CreateDirectory(Path.Combine(_dir.Path, "результат")).FullName;
            Report = Path.Combine(Output, "опись.xlsx");
            Source = Path.Combine(Input, $"АБВГ.301234.{index + 1:D3}СБ Изделие {index}.pdf");
            PdfStand.Write(Source, PdfStand.A4, PdfStand.A3);
            Hash = ConcurrentTasks.Fingerprint(Source);
        }
        public void Dispose() => _dir.Dispose();
    }
}
