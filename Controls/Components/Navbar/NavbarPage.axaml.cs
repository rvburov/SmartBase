using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using SmartBase.Core.Models;

namespace SmartBase.Controls.Components;

/// <summary>Навбар панели «Страница записи»: редактируемое имя записи слева + кнопка
/// открыть/закрыть блок коллекции справа. Высота/отступы — как у остальных навбаров (56 / 16).
/// Сохранение имени и тоггл коллекции решает родитель через события.</summary>
public partial class NavbarPage : UserControl
{
    /// <summary>Имя записи отредактировано пользователем (по символу). Аргумент — текущий текст поля.</summary>
    public event EventHandler<string>? TitleChanged;

    /// <summary>Нажата кнопка блока коллекции (true — открыть, false — закрыть).</summary>
    public event EventHandler<bool>? CollectionPanelToggled;

    // Гасит TitleChanged на время служебной установки имени (SetTitle), чтобы её не приняли за правку.
    private bool _suppressTitleChange;

    public NavbarPage()
    {
        InitializeComponent();

        TitleDisplay.PointerPressed += (_, _) =>
        {
            TitleDisplay.IsVisible = false;
            TitleBox.IsVisible     = true;
            TitleBox.Focus();
            TitleBox.SelectAll();
        };

        TitleBox.LostFocus += (_, _) => ExitTitleEdit();
        TitleBox.KeyDown   += (_, e) =>
        {
            if (e.Key is Key.Enter or Key.Escape) { ExitTitleEdit(); e.Handled = true; }
        };

        TitleBox.TextChanged += (_, _) =>
        {
            if (_suppressTitleChange) return;
            TitleChanged?.Invoke(this, TitleBox.Text ?? "");
        };

        CollectionPanelBtn.IsCheckedChanged += (_, _) =>
        {
            UpdateCollectionPanelIcon();
            CollectionPanelToggled?.Invoke(this, CollectionPanelBtn.IsChecked == true);
        };

        ExitTitleEdit();
    }

    /// <summary>True — имя сейчас редактируется (родитель не перетирает его при ReloadFromDb).</summary>
    public bool IsTitleEditing => TitleBox.IsVisible;

    /// <summary>Установить имя без события TitleChanged. Пустая строка → плейсхолдер «Без названия».</summary>
    public void SetTitle(string title)
    {
        _suppressTitleChange = true;
        TitleBox.Text = title ?? "";
        ExitTitleEdit();
        _suppressTitleChange = false;
    }

    /// <summary>Синхронизирует визуальное состояние кнопки с фактической видимостью блока коллекции.</summary>
    public void SetCollectionPanelOpen(bool isOpen)
    {
        if (CollectionPanelBtn.IsChecked == isOpen) return;
        CollectionPanelBtn.IsChecked = isOpen;
    }

    private void ExitTitleEdit()
    {
        TitleBox.IsVisible     = false;
        TitleDisplay.IsVisible = true;
        // Шапка — одна строка: показываем первую (TitleText), как и все остальные блоки с
        // названием записи. Режется только ПОКАЗ — в TitleBox остаётся полный текст, и
        // правка с сохранением идут по нему, ничего не теряя.
        var title = TitleText.FirstLine(TitleBox.Text);
        // Кисти берём из ресурсов приложения (а не this.FindResource) — ExitTitleEdit зовётся
        // в т.ч. из конструктора, когда контрол ещё не в дереве и this.FindResource даёт UnsetValue.
        if (string.IsNullOrEmpty(title))
        {
            TitleDisplay.Text       = "Без названия";
            TitleDisplay.Foreground = (IBrush)Application.Current!.FindResource("BrushSecondaryText")!;
        }
        else
        {
            TitleDisplay.Text       = title;
            TitleDisplay.Foreground = (IBrush)Application.Current!.FindResource("BrushPrimaryText")!;
        }
    }

    private void UpdateCollectionPanelIcon()
    {
        var open = CollectionPanelBtn.IsChecked == true;
        CollectionOpenIcon.IsVisible  = !open;
        CollectionCloseIcon.IsVisible = open;
        ToolTip.SetTip(CollectionPanelBtn, open ? "Закрыть коллекцию" : "Открыть коллекцию");
    }
}
