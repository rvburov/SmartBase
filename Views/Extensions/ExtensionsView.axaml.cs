using Avalonia.Controls;
using Avalonia.Interactivity;
using DataBase.Extensions;

namespace DataBase.Views.Extensions;

/// <summary>
/// Блок раздела «Расширения» (по стилю блока настроек): навбар с именем расширения и
/// кнопкой запуска + Markdown-описание. Оболочка знает о расширении только через
/// <see cref="IExtension"/> — само расширение лежит самостоятельно под Extensions/.
/// </summary>
public partial class ExtensionsView : UserControl
{
    private IExtension? _current;

    public ExtensionsView()
    {
        InitializeComponent();
        ShowExtension(ExtensionRegistry.All.FirstOrDefault()?.Id ?? "");
    }

    /// <summary>Показать расширение по Id (из выбора в сайдбаре). Пустой/неизвестный → первое.</summary>
    public void ShowExtension(string id)
    {
        _current = ExtensionRegistry.FindById(id) ?? ExtensionRegistry.All.FirstOrDefault();

        if (_current is null)
        {
            TitleText.Text         = "Расширения";
            DescriptionMd.Markdown = "_Нет доступных расширений._";
            RunBtn.IsEnabled       = false;
            ToolTip.SetTip(RunBtn, "Нет расширения");
            return;
        }

        TitleText.Text         = _current.Name;
        DescriptionMd.Markdown = _current.MarkdownDescription;
        RunBtn.IsEnabled       = _current.CanRun;
        ToolTip.SetTip(RunBtn, _current.CanRun ? "Запустить расширение" : "Запуск пока недоступен");
    }

    // Запуск: расширение отдаёт своё окно (в стиле приложения), оболочка показывает его
    // поверх главного окна. null — расширение окно не открывает (заглушка/надстройка).
    private void OnRunClick(object? sender, RoutedEventArgs e)
    {
        if (_current is null || !_current.CanRun) return;

        var window = _current.CreateWindow();
        if (window is null) return;

        if (TopLevel.GetTopLevel(this) is Window owner) window.Show(owner);
        else                                            window.Show();
    }
}
