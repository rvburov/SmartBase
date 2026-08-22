using SmartBase.Extensions;
using SmartBase.Extensions.PdfAnalyzer;

namespace SmartBase.Tests.Extensions.PdfAnalyzer;

/// <summary>
/// Контракт ИМЕННО этого расширения: прибитый Id и факт регистрации. Общие правила живут в
/// Tests/Extensions/Registry — здесь только то, что уходит вместе с расширением при удалении.
///
/// CreateWindow() здесь не зовётся и звать нельзя: Avalonia в тест-хосте не поднята.
/// </summary>
public sealed class PdfAnalyzerExtensionTests
{
    [Fact]
    public void Extension_id_is_frozen() =>
        Assert.Equal("pdf-analyzer", new PdfAnalyzerExtension().Id);

    // Настроек расширение не объявляет: папку и файл отчёта выбирают в самом окне.
    [Fact]
    public void Extension_declares_no_settings() =>
        Assert.Empty(((IExtension)new PdfAnalyzerExtension()).Settings);

    [Fact]
    public void Extension_is_registered()
    {
        var found = ExtensionRegistry.FindById("pdf-analyzer");

        Assert.NotNull(found);
        Assert.IsType<PdfAnalyzerExtension>(found);
    }
}
