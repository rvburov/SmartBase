using SmartBase.Extensions.AssemblyCalc;

namespace SmartBase.Tests.Extensions.AssemblyCalc;

/// <summary>
/// Точки отказа расширения: каждая обязана быть понятной пользователю строкой в статусе окна,
/// а не голым исключением ClosedXML. Отдельно проверяется, что тексты «нет листа» у ИСТОЧНИКА
/// и у ШАБЛОНА разные: иначе по сообщению не понять, какой файл выбран неверно.
/// </summary>
public sealed class AssemblyCalcEngineTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"sbext_ac_{Guid.NewGuid():N}");
    private readonly string _source;
    private readonly string _template;

    public AssemblyCalcEngineTests()
    {
        Directory.CreateDirectory(_root);
        _source   = Path.Combine(_root, "исходный.xlsx");
        _template = Path.Combine(_root, "шаблон.xlsm");
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
        catch { /* best-effort */ }
    }

    private AssemblyCalcException RunExpectingFailure(string source, string template, string outDir) =>
        Assert.Throws<AssemblyCalcException>(
            () => AssemblyCalcEngine.Run(source, template, outDir, null, CancellationToken.None));

    [Fact]
    public void Missing_source_is_a_friendly_error()
    {
        ExcelStand.WriteTemplate(_template);

        var ex = RunExpectingFailure(Path.Combine(_root, "нет.xlsx"), _template, _root);

        Assert.Equal("Исходный файл не найден.", ex.Message);
    }

    [Fact]
    public void Missing_template_is_a_friendly_error()
    {
        ExcelStand.WriteSource(_source);

        var ex = RunExpectingFailure(_source, Path.Combine(_root, "нет.xlsm"), _root);

        Assert.Equal("Файл-шаблон не найден.", ex.Message);
    }

    [Fact]
    public void Missing_output_folder_is_a_friendly_error()
    {
        ExcelStand.WriteSource(_source);
        ExcelStand.WriteTemplate(_template);

        var ex = RunExpectingFailure(_source, _template, Path.Combine(_root, "нет-папки"));

        Assert.Equal("Папка для сохранения не найдена.", ex.Message);
    }

    [Fact]
    public void Source_without_the_data_sheet_is_a_friendly_error()
    {
        ExcelStand.WriteWithoutDataSheet(_source);
        ExcelStand.WriteTemplate(_template);

        var ex = RunExpectingFailure(_source, _template, _root);

        Assert.Equal("Лист «Исходные данные» не найден в исходном файле.", ex.Message);
    }

    [Fact]
    public void Template_without_the_data_sheet_is_a_friendly_error()
    {
        ExcelStand.WriteSource(_source);
        ExcelStand.WriteWithoutDataSheet(_template);

        var ex = RunExpectingFailure(_source, _template, _root);

        Assert.Equal("Лист «Исходные данные» не найден в шаблоне.", ex.Message);
    }

    [Fact]
    public void Unreadable_workbook_is_a_friendly_error()
    {
        File.WriteAllText(_source, "это не книга Excel");
        ExcelStand.WriteTemplate(_template);

        var ex = RunExpectingFailure(_source, _template, _root);

        Assert.Contains("Не удалось открыть исходный файл", ex.Message);
        Assert.Contains("исходный.xlsx", ex.Message);
    }
}
