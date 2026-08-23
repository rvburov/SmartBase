using SmartBase.Core.Exporters;
using SmartBase.Core.Importers;

namespace SmartBase.Tests.Unit;

/// <summary>
/// Чистая логика планировщиков импорта/экспорта: распознавание форматов по расширению,
/// санитайз имен файлов/листов и уникализация путей.
/// </summary>
public sealed class PlannerTests
{
    // ── ImportPlanner ────────────────────────────────────────────────────────

    [Theory]
    [InlineData("data.xlsx", true)]
    [InlineData("data.xlsm", true)]
    [InlineData("data.xlsb", true)]
    [InlineData("data.xls", true)]
    [InlineData("data.csv", true)]
    [InlineData("data.json", true)]
    [InlineData("data.CSV", true)]  // регистронезависимо
    [InlineData("data.txt", false)]
    [InlineData("data.pdf", false)]
    [InlineData("data", false)]
    public void ImportPlanner_IsSupported(string path, bool expected)
    {
        Assert.Equal(expected, ImportPlanner.IsSupported(path));
    }

    [Theory]
    [InlineData("data.xlsx", true)]
    [InlineData("data.xls", true)]
    [InlineData("data.xlsm", true)]
    [InlineData("data.xlsb", true)]
    [InlineData("data.csv", false)]
    [InlineData("data.json", false)]
    public void ImportPlanner_IsExcel(string path, bool expected)
    {
        Assert.Equal(expected, ImportPlanner.IsExcel(path));
    }

    // ── ExportPlanner: формат ──────────────────────────────────────────────────

    [Theory]
    [InlineData(ExportFormat.Xlsx, ".xlsx")]
    [InlineData(ExportFormat.Xlsm, ".xlsm")]
    [InlineData(ExportFormat.Csv, ".csv")]
    [InlineData(ExportFormat.Json, ".json")]
    public void ExportPlanner_Extension(ExportFormat format, string ext)
    {
        Assert.Equal(ext, ExportPlanner.Extension(format));
    }

    [Theory]
    [InlineData(ExportFormat.Xlsx, true)]
    [InlineData(ExportFormat.Xlsm, true)]
    [InlineData(ExportFormat.Csv, false)]
    [InlineData(ExportFormat.Json, false)]
    public void ExportPlanner_IsExcel(ExportFormat format, bool expected)
    {
        Assert.Equal(expected, ExportPlanner.IsExcel(format));
    }

    // ── ExportPlanner: санитайз имен ────────────────────────────────────────────

    [Fact]
    public void SanitizeFileName_replaces_invalid_and_trims()
    {
        // '/' и управляющие символы недопустимы в именах файлов на всех платформах
        // (набор Path.GetInvalidFileNameChars() отличается между Windows и Unix — ':'
        // на macOS/Linux допустим, поэтому его не проверяем).
        Assert.Equal("a_b", ExportPlanner.SanitizeFileName("a/b"));
        Assert.Equal("a_b", ExportPlanner.SanitizeFileName("a\tb"));
        Assert.Equal("имя", ExportPlanner.SanitizeFileName("  .имя.  "));
    }

    [Fact]
    public void SanitizeFileName_empty_falls_back_to_default()
    {
        Assert.Equal("Коллекция", ExportPlanner.SanitizeFileName("   "));
    }

    [Fact]
    public void SanitizeFileName_reserved_device_name_gets_prefix()
    {
        Assert.Equal("_CON", ExportPlanner.SanitizeFileName("CON"));
    }

    [Fact]
    public void SanitizeFileName_truncates_to_100()
    {
        Assert.True(ExportPlanner.SanitizeFileName(new string('x', 250)).Length <= 100);
    }

    [Fact]
    public void SanitizeSheetName_replaces_invalid_and_limits_length()
    {
        Assert.Equal("a_b_c", ExportPlanner.SanitizeSheetName("a[b]c"));
        Assert.Equal("Лист1", ExportPlanner.SanitizeSheetName("   "));
        Assert.True(ExportPlanner.SanitizeSheetName(new string('y', 50)).Length <= 31);
    }

    // ── ExportPlanner: уникализация путей и разбор исключений ────────────────────

    [Fact]
    public void UniquePath_appends_counter_when_taken()
    {
        using var dir = new TempDir();
        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var first  = ExportPlanner.UniquePath(dir.Path, "Отчет", ".csv", taken);
        var second = ExportPlanner.UniquePath(dir.Path, "Отчет", ".csv", taken);

        Assert.Equal(Path.Combine(dir.Path, "Отчет.csv"), first);
        Assert.Equal(Path.Combine(dir.Path, "Отчет (2).csv"), second);
    }

    [Fact]
    public void ParseColumnExclusions_splits_and_is_case_insensitive()
    {
        var set = ExportPlanner.ParseColumnExclusions("Имя, Возраст; Город");
        Assert.Equal(3, set.Count);
        Assert.Contains("возраст", set); // регистронезависимо
        Assert.Empty(ExportPlanner.ParseColumnExclusions(null));
    }
}
