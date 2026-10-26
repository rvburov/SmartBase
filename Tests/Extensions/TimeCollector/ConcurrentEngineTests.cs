using ClosedXML.Excel;
using SmartBase.Extensions.TimeCollector;

namespace SmartBase.Tests.Extensions.TimeCollector;

public sealed class ConcurrentEngineTests
{
    [Fact]
    public void Eight_time_collections_keep_their_rows_numbers_and_source_workbooks()
    {
        var outputs = ConcurrentTasks.Run(8, index => new Fixture(index), (f, index, ct) =>
        {
            var result = TimeCollectorEngine.Run(f.Input, false, f.Template, f.Output, null, ct);
            Assert.Equal((1, 1, 1), (result.TotalFiles, result.ProcessedFiles, result.RowCount));
            Assert.Empty(result.Issues);
            using var book = new XLWorkbook(result.OutputPath);
            var sheet = book.Worksheet(TimeCollectorEngine.TargetSheet);
            Assert.Equal($"Деталь {index}", sheet.Cell(4, 2).GetString());
            Assert.Equal($"АБВГ.301234.{index + 1:D3}", sheet.Cell(4, 3).GetString());
            Assert.Equal(index + 0.25, sheet.Cell(4, 4).GetDouble());
            Assert.Equal(new DateTime(2026, 10, 26), sheet.Cell(4, 5).GetDateTime());
            Assert.True(sheet.Cell(5, 2).IsEmpty());
            Assert.Equal($"Настройка {index}", book.Worksheet("Настройки").Cell(1, 2).GetString());
            Assert.Equal(f.SourceHash, ConcurrentTasks.Fingerprint(f.Source));
            Assert.Equal(f.TemplateHash, ConcurrentTasks.Fingerprint(f.Template));
            Assert.Equal(f.Output, Path.GetDirectoryName(result.OutputPath));
            Assert.Single(Directory.GetFiles(f.Output));
            return result.OutputPath;
        });
        Assert.Equal(8, outputs.Distinct(StringComparer.Ordinal).Count());
    }

    private sealed class Fixture : IDisposable
    {
        private readonly TempDir _dir = new("concurrent-time");
        public string Input { get; }
        public string Source { get; }
        public string Template { get; }
        public string Output { get; }
        public string SourceHash { get; }
        public string TemplateHash { get; }
        public Fixture(int index)
        {
            Input = Directory.CreateDirectory(Path.Combine(_dir.Path, "расчеты")).FullName;
            Output = Directory.CreateDirectory(Path.Combine(_dir.Path, "результат")).FullName;
            Template = Path.Combine(_dir.Path, "шаблон.xlsm");
            Source = Path.Combine(Input, $"Проект{index}_2026.10.26_Исполнитель{index}.xlsm");
            using (var book = new XLWorkbook())
            {
                book.AddWorksheet(TimeCollectorEngine.TargetSheet).Cell(4, 2).Value = "Старые данные";
                book.AddWorksheet("Настройки").Cell(1, 2).Value = $"Настройка {index}";
                book.SaveAs(Template);
            }
            using (var book = new XLWorkbook())
            {
                var sheet = book.AddWorksheet("Расшифровка");
                string[] headers = ["Уровень", "Индекс", "Наименование", "Обозначение КД", "Обозначение ТД",
                    "Кол-во", "Норма", "Норма общая", "База", "Норма расчетная", "Норма фикс."];
                for (var column = 0; column < headers.Length; column++) sheet.Cell(1, column + 1).Value = headers[column];
                sheet.Cell(2, 2).Value = "Изделие";
                sheet.Cell(2, 3).Value = $"Деталь {index}";
                sheet.Cell(2, 4).Value = $"АБВГ.301234.{index + 1:D3}";
                sheet.Cell(2, 7).Value = index + 0.25;
                book.SaveAs(Source);
            }
            SourceHash = ConcurrentTasks.Fingerprint(Source);
            TemplateHash = ConcurrentTasks.Fingerprint(Template);
        }
        public void Dispose() => _dir.Dispose();
    }
}
