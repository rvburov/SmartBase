using SmartBase.Extensions;
using SmartBase.Extensions.PdfRename;

namespace SmartBase.Tests.Extensions.PdfRename;

/// <summary>
/// Контракт ИМЕННО этого расширения: прибитый Id и факт регистрации. Общие правила (форма Id,
/// непустые подписи, уникальность) живут в Tests/Extensions/Registry — здесь только то, что
/// уходит вместе с расширением при удалении.
///
/// Id прибит потому, что он часть ХРАНИЛИЩА (ключ «ext.&lt;Id&gt;.…» в app.db): переименование
/// равносильно удалению расширения — сохраненные пользователем значения осиротеют.
///
/// CreateWindow() здесь не зовется и звать нельзя: Avalonia в тест-хосте не поднята.
/// </summary>
public sealed class PdfRenameExtensionTests
{
    [Fact]
    public void Extension_id_is_frozen() =>
        Assert.Equal("pdf-rename", new PdfRenameExtension().Id);

    // Настроек расширение не объявляет — работает дефолт интерфейса (Settings => []).
    // Читаем через IExtension: у дефолтного члена интерфейса нет реализации в самом классе.
    [Fact]
    public void Extension_declares_no_settings() =>
        Assert.Empty(((IExtension)new PdfRenameExtension()).Settings);

    [Fact]
    public void Extension_is_registered()
    {
        var found = ExtensionRegistry.FindById("pdf-rename");

        Assert.NotNull(found);
        Assert.IsType<PdfRenameExtension>(found);
    }
}
