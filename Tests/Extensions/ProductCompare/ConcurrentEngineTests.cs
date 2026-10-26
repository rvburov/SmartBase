using ClosedXML.Excel;
using SmartBase.Extensions.ProductCompare;

namespace SmartBase.Tests.Extensions.ProductCompare;

public sealed class ConcurrentEngineTests
{
    [Fact]
    public void Eight_comparisons_keep_old_and_new_rows_with_their_own_workbooks()
    {
        var paths = ConcurrentTasks.Run(8, index => new Fixture(index), (f, index, ct) =>
        {
            var result = ProductCompareEngine.Run(f.Template, f.Old, f.New, f.Output, null, ct);
            Assert.Equal((1, 1, 1), (result.OldRows, result.NewRows, result.TotalRows));
            using var book = new XLWorkbook(result.OutputPath);
            var sheet = book.Worksheets.First();
            Assert.Equal($"Старое {index}", sheet.Cell(6, 3).GetString());
            Assert.Equal($"Новое {index}", sheet.Cell(6, 9).GetString());
            Assert.Equal($"АБВГ.{index + 1:D3}", sheet.Cell(6, 4).GetString());
            Assert.Equal($"АБВГ.{index + 1:D3}", sheet.Cell(6, 10).GetString());
            Assert.True(sheet.Cell(7, 3).IsEmpty());
            Assert.Equal("не трогать", book.Worksheet("Обвязка").Cell(1, 1).GetString());
            Assert.Equal(f.Hashes, new[] { f.Template, f.Old, f.New }.Select(ConcurrentTasks.Fingerprint).ToArray());
            Assert.Equal(f.Output, Path.GetDirectoryName(result.OutputPath));
            Assert.Single(Directory.GetFiles(f.Output));
            return result.OutputPath;
        });
        Assert.Equal(8, paths.Distinct(StringComparer.Ordinal).Count());
    }

    private sealed class Fixture : IDisposable
    {
        private readonly TempDir _dir = new("concurrent-compare");
        public string Template { get; }
        public string Old { get; }
        public string New { get; }
        public string Output { get; }
        public string[] Hashes { get; }
        public Fixture(int index)
        {
            Template = Path.Combine(_dir.Path, "шаблон.xlsm");
            Old = Path.Combine(_dir.Path, "старое.xlsx");
            New = Path.Combine(_dir.Path, "новое.xlsx");
            Output = Directory.CreateDirectory(Path.Combine(_dir.Path, "результат")).FullName;
            ExcelStand.WriteTemplate(Template);
            ExcelStand.WriteSource(Old, new CompareRow("1", "1.", $"Старое {index}", $"АБВГ.{index + 1:D3}", "1"));
            ExcelStand.WriteSource(New, new CompareRow("1", "1.", $"Новое {index}", $"АБВГ.{index + 1:D3}", "2"));
            Hashes = new[] { Template, Old, New }.Select(ConcurrentTasks.Fingerprint).ToArray();
        }
        public void Dispose() => _dir.Dispose();
    }
}
