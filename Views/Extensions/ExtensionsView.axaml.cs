using Avalonia.Controls;
using SmartBase.Extensions;
using SmartBase.Settings;

namespace SmartBase.Views.Extensions;

/// <summary>
/// Блок раздела «Расширения» (по стилю блока настроек): навбар с именем расширения и
/// кнопкой запуска + Markdown-описание. Оболочка знает о расширении только через
/// <see cref="IExtension"/> — само расширение лежит самостоятельно под Extensions/.
///
/// Скрытые расширения («Настройки → Расширения → Отображение раздела расширений») сюда не
/// попадают: и запасной выбор, и проверка показываемого идут через видимые пункты каталога.
/// </summary>
public partial class ExtensionsView : UserControl
{
    private IExtension? _current;

    public ExtensionsView()
    {
        InitializeComponent();
        HeaderBar.RunClicked += OnRunClick;
        ShowExtension("");   // пустой Id → первое видимое расширение
    }

    /// <summary>Показать расширение по Id (из выбора в сайдбаре). Пустой, неизвестный или
    /// скрытый → первое видимое.</summary>
    public void ShowExtension(string id)
    {
        _current = ViewCatalog.IsVisible(ViewCatalog.ScopeExtensions, id)
            ? ExtensionRegistry.FindById(id)
            : null;
        _current ??= FirstVisible();

        if (_current is null)
        {
            HeaderBar.Title        = "Расширения";
            DescriptionMd.Markdown = "_Нет доступных расширений._";
            HeaderBar.SetRunState(false, "Нет расширения");
            return;
        }

        HeaderBar.Title        = _current.Name;
        DescriptionMd.Markdown = _current.MarkdownDescription;
        HeaderBar.SetRunState(_current.CanRun, _current.CanRun ? "Запустить расширение" : "Запуск пока недоступен");
    }

    // Первое расширение, которое разрешено показывать. null — расширений нет вовсе
    // (реестр пуст) либо ни одно не нашлось по Id из каталога.
    private static IExtension? FirstVisible() =>
        ViewCatalog.FirstVisible(ViewCatalog.ScopeExtensions) is { } item
            ? ExtensionRegistry.FindById(item.Key)
            : null;

    // Запуск: расширение отдает свое окно (в стиле приложения), оболочка показывает его
    // поверх главного окна. null — расширение окно не открывает (заглушка/надстройка).
    private void OnRunClick(object? sender, EventArgs e)
    {
        if (_current is null || !_current.CanRun) return;

        var window = _current.CreateWindow();
        if (window is null) return;

        if (TopLevel.GetTopLevel(this) is Window owner) window.Show(owner);
        else                                            window.Show();
    }
}
