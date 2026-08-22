using SmartBase.Extensions.ProductCompare;

namespace SmartBase.Tests.Extensions.ProductCompare;

/// <summary>
/// Точки отказа расширения: каждая обязана быть понятной пользователю строкой в статусе окна,
/// а не голым исключением ClosedXML с английским текстом и стеком. Проверки идут до чтения
/// файлов, поэтому стенд здесь почти не нужен.
/// </summary>
public sealed class ProductCompareEngineTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"sbext_pc_{Guid.NewGuid():N}");
    private readonly string _template;
    private readonly string _old;
    private readonly string _new;

    public ProductCompareEngineTests()
    {
        Directory.CreateDirectory(_root);
        _template = Path.Combine(_root, "шаблон.xlsm");
        _old      = Path.Combine(_root, "старый.xlsx");
        _new      = Path.Combine(_root, "новый.xlsx");
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
        catch { /* best-effort */ }
    }

    private ProductCompareException RunExpectingFailure(string template, string oldPath, string newPath, string outDir) =>
        Assert.Throws<ProductCompareException>(
            () => ProductCompareEngine.Run(template, oldPath, newPath, outDir, null, CancellationToken.None));

    [Fact]
    public void Missing_template_is_a_friendly_error()
    {
        ExcelStand.WriteSource(_old);
        ExcelStand.WriteSource(_new);

        var ex = RunExpectingFailure(Path.Combine(_root, "нет.xlsm"), _old, _new, _root);

        Assert.Equal("Файл-шаблон не найден.", ex.Message);
    }

    [Fact]
    public void Missing_old_file_is_a_friendly_error()
    {
        ExcelStand.WriteTemplate(_template);
        ExcelStand.WriteSource(_new);

        var ex = RunExpectingFailure(_template, Path.Combine(_root, "нет.xlsx"), _new, _root);

        Assert.Equal("Старый файл не найден.", ex.Message);
    }

    [Fact]
    public void Missing_new_file_is_a_friendly_error()
    {
        ExcelStand.WriteTemplate(_template);
        ExcelStand.WriteSource(_old);

        var ex = RunExpectingFailure(_template, _old, Path.Combine(_root, "нет.xlsx"), _root);

        Assert.Equal("Новый файл не найден.", ex.Message);
    }

    [Fact]
    public void Missing_output_folder_is_a_friendly_error()
    {
        ExcelStand.WriteTemplate(_template);
        ExcelStand.WriteSource(_old);
        ExcelStand.WriteSource(_new);

        var ex = RunExpectingFailure(_template, _old, _new, Path.Combine(_root, "нет-папки"));

        Assert.Equal("Папка для сохранения не найдена.", ex.Message);
    }

    // Повреждённая или чужая книга не должна выпадать наружу исключением ClosedXML.
    [Fact]
    public void Unreadable_workbook_is_a_friendly_error()
    {
        ExcelStand.WriteTemplate(_template);
        ExcelStand.WriteSource(_new);
        File.WriteAllText(_old, "это не книга Excel");

        var ex = RunExpectingFailure(_template, _old, _new, _root);

        Assert.Contains("Не удалось открыть файл", ex.Message);
        Assert.Contains("старый.xlsx", ex.Message);
    }
}
