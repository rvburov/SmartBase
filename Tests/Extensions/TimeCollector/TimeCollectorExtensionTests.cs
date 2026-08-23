using SmartBase.Extensions;
using SmartBase.Extensions.TimeCollector;

namespace SmartBase.Tests.Extensions.TimeCollector;

/// <summary>
/// Контракт ИМЕННО этого расширения: прибитые Id и ключи настроек плюс факт регистрации.
/// Общие правила (форма Id, непустые подписи, уникальность в реестре) живут в
/// Tests/Extensions/Registry — здесь только то, что уходит вместе с расширением при удалении.
///
/// Прибитые строки нужны потому, что и Id, и ключи настроек — часть ХРАНИЛИЩА: значения лежат
/// в app.db под ключом «ext.&lt;Id&gt;.&lt;Key&gt;», и переименование любой из них молча теряет
/// сохраненные пользователем пути (см. docs/developer/features/extensions.md).
///
/// CreateWindow() здесь не зовется и звать нельзя: Avalonia в тест-хосте не поднята
/// (нет AppBuilder, Dispatcher и стилей Application.Current) — конструктор окна упадет.
/// </summary>
public sealed class TimeCollectorExtensionTests
{
    [Fact]
    public void Extension_id_is_frozen()
    {
        Assert.Equal("time-collector", new TimeCollectorExtension().Id);
        Assert.Equal("time-collector", TimeCollectorExtension.ExtensionId);
    }

    // Порядок важен: по нему раздел «Настройки → Расширения» строит поля сверху вниз.
    [Fact]
    public void Setting_keys_are_frozen()
    {
        var keys = new TimeCollectorExtension().Settings.Select(s => s.Key).ToArray();

        Assert.Equal(["folder", "template", "output"], keys);
    }

    [Fact]
    public void Extension_is_registered()
    {
        var found = ExtensionRegistry.FindById("time-collector");

        Assert.NotNull(found);
        Assert.IsType<TimeCollectorExtension>(found);
    }
}
