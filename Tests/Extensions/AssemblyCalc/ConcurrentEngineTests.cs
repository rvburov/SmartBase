using ClosedXML.Excel;
using SmartBase.Extensions.AssemblyCalc;

namespace SmartBase.Tests.Extensions.AssemblyCalc;

public sealed class ConcurrentEngineTests
{
    [Fact]
    public void Eight_independent_calculations_keep_their_inputs_and_mapped_results()
    {
        var paths = ConcurrentTasks.Run(8,
            index => new Fixture(index),
            (f, index, ct) =>
            {
                var result = AssemblyCalcEngine.Run(f.Source, f.Template, f.Output, null, ct);
                Assert.Equal(1, result.RowCount);
                using var book = new XLWorkbook(result.OutputPath);
                var sheet = book.Worksheet(ExcelStand.SheetName);
                Assert.Equal($"Деталь {index}", sheet.Cell(6, 3).GetString());
                Assert.Equal($"АБВГ.301234.{index + 1:D3}", sheet.Cell(6, 4).GetString());
                Assert.Equal($"ВЭ {index}", sheet.Cell(6, 13).GetString());
                Assert.True(sheet.Cell(7, 3).IsEmpty());
                Assert.Equal("не трогать", book.Worksheet("Обвязка").Cell(1, 1).GetString());
                Assert.Equal(f.SourceHash, ConcurrentTasks.Fingerprint(f.Source));
                Assert.Equal(f.TemplateHash, ConcurrentTasks.Fingerprint(f.Template));
                Assert.Equal(f.Output, Path.GetDirectoryName(result.OutputPath));
                Assert.Single(Directory.GetFiles(f.Output));
                return result.OutputPath;
            });
        Assert.Equal(8, paths.Distinct(StringComparer.Ordinal).Count());
    }

    private sealed class Fixture : IDisposable
    {
        private readonly TempDir _dir = new("concurrent-assembly");
        public string Source { get; }
        public string Template { get; }
        public string Output { get; }
        public string SourceHash { get; }
        public string TemplateHash { get; }
        public Fixture(int index)
        {
            Source = Path.Combine(_dir.Path, "источник.xlsx");
            Template = Path.Combine(_dir.Path, "шаблон.xlsm");
            Output = Directory.CreateDirectory(Path.Combine(_dir.Path, "результат")).FullName;
            ExcelStand.WriteTemplate(Template);
            ExcelStand.WriteSource(Source, (1, [(5, $"Деталь {index}"),
                (11, $"АБВГ.301234.{index + 1:D3}"), (248, $"ВЭ {index}")]));
            SourceHash = ConcurrentTasks.Fingerprint(Source);
            TemplateHash = ConcurrentTasks.Fingerprint(Template);
        }
        public void Dispose() => _dir.Dispose();
    }
}
