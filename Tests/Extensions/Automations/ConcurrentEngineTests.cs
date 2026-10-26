using System.Text.Json;
using ClosedXML.Excel;
using SmartBase.Extensions;
using SmartBase.Extensions.Automations;

namespace SmartBase.Tests.Extensions.Automations;

public sealed class ConcurrentEngineTests
{
    [Fact]
    public void Eight_shared_stateless_actions_produce_independent_sums_and_readable_workbooks()
    {
        var automation = new SumByKeyAutomation();
        var sheetAction = new SheetAction();
        var paths = ConcurrentTasks.Run(8, index => new Fixture(index), (f, index, ct) =>
        {
            ct.ThrowIfCancellationRequested();
            var made = automation.Run(new AutomationInput(f.Rows, new Dictionary<string, string>
            {
                [SumByKeyAutomation.KeySetting] = "Ключ", [SumByKeyAutomation.SumSetting] = "Норма",
            }));
            Assert.Equal(2, made.Count);
            Assert.Equal($"Группа {index}", made[0]["Ключ"]);
            Assert.Equal("2", made[0][SumByKeyAutomation.CountColumn]);
            Assert.Equal((index + 11).ToString(System.Globalization.CultureInfo.InvariantCulture), made[0]["Сумма Норма"]);
            var result = sheetAction.Run(new ActionContext(null, 0, 0, new DateTimeOffset(2026, 10, 26, 10, 0, 0, TimeSpan.Zero)),
                new ActionInput(new Dictionary<string, string> { [SheetAction.FileSetting] = $"Свод {index}" },
                    Rows: made.Select(r => r.Values).ToArray()));
            Assert.True(result.Ok, result.Explanation);
            Assert.NotNull(result.File);
            Assert.Equal($"Свод {index}.xlsx", result.FileName);
            var path = Path.Combine(f.Directory.Path, result.FileName!);
            File.WriteAllBytes(path, result.File!);
            using var book = new XLWorkbook(path);
            var sheet = book.Worksheets.First();
            Assert.Equal(3, sheet.LastRowUsed()!.RowNumber());
            Assert.Equal($"Группа {index}", sheet.Cell(2, 1).GetString());
            Assert.Equal(2, sheet.Cell(2, 2).GetDouble());
            Assert.Equal(index + 11, sheet.Cell(2, 3).GetDouble());
            Assert.Equal(2.5, sheet.Cell(3, 3).GetDouble());
            Assert.Equal(f.Hash, ConcurrentTasks.Fingerprint(JsonSerializer.SerializeToUtf8Bytes(f.Rows.Select(r => r.Values).ToArray())));
            Assert.Single(Directory.GetFiles(f.Directory.Path));
            return path;
        });
        Assert.Equal(8, paths.Distinct(StringComparer.Ordinal).Count());
    }

    private sealed class Fixture : IDisposable
    {
        public TempDir Directory { get; } = new("concurrent-automation");
        public AutomationRow[] Rows { get; }
        public string Hash { get; }
        public Fixture(int index)
        {
            Rows = [AutomationRow.Of(("Ключ", $"Группа {index}"), ("Норма", "10")),
                AutomationRow.Of(("Ключ", $"Группа {index}"), ("Норма", (index + 1).ToString(System.Globalization.CultureInfo.InvariantCulture))),
                AutomationRow.Of(("Ключ", $"Другая {index}"), ("Норма", "2.5"))];
            Hash = ConcurrentTasks.Fingerprint(JsonSerializer.SerializeToUtf8Bytes(Rows.Select(r => r.Values).ToArray()));
        }
        public void Dispose() => Directory.Dispose();
    }
}
