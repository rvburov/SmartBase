using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using DataBase.Controls.Widgets;

namespace DataBase.Controls.Components;

public partial class ConstructorNavbar : UserControl
{
    private static readonly string[] ViewNames = ["Таблица", "Список"];
    private static readonly string[] ViewModes = ["table", "list"];

    public event EventHandler<string>? ViewChanged;
    /// <summary>Новое имя коллекции (после blur/Enter). Пусто/whitespace — игнорируется родителем.</summary>
    public event EventHandler<string>? TitleEdited;
    /// <summary>Запрос изменить видимость правой панели записи (true — открыть, false — закрыть).</summary>
    public event EventHandler<bool>?   RecordPanelToggled;

    private string _committedTitle = "";

    public ConstructorNavbar()
    {
        InitializeComponent();

        ViewSelector.ItemsSource   = ViewNames;
        ViewSelector.SelectedIndex = 0;

        ViewSelector.SelectionChanged += (_, _) =>
        {
            var idx = ViewSelector.SelectedIndex;
            if (idx >= 0 && idx < ViewModes.Length)
                ViewChanged?.Invoke(this, ViewModes[idx]);
        };

        TitleDisplay.PointerPressed += (_, _) =>
        {
            if (!TitleBox.IsEnabled) return;
            TitleDisplay.IsVisible = false;
            TitleBox.IsVisible     = true;
            TitleBox.Focus();
            TitleBox.SelectAll();
        };

        TitleBox.LostFocus += (_, _) => { CommitTitle(); ExitTitleEdit(); };
        TitleBox.KeyDown   += (_, e) =>
        {
            if (e.Key == Key.Enter)  { CommitTitle(); ExitTitleEdit(); e.Handled = true; }
            if (e.Key == Key.Escape) { TitleBox.Text = _committedTitle; ExitTitleEdit(); e.Handled = true; }
        };

        RecordPanelBtn.IsCheckedChanged += (_, _) =>
        {
            UpdateRecordPanelIcon();
            RecordPanelToggled?.Invoke(this, RecordPanelBtn.IsChecked == true);
        };
    }

    /// <summary>Показывает/скрывает контролы, осмысленные только при выбранной коллекции
    /// (переключатель вида и кнопка панели записи).</summary>
    public void ShowCollectionControls(bool show)
    {
        ViewSelector.IsVisible   = show;
        RecordPanelBtn.IsVisible = show;
    }

    /// <summary>Синхронизирует визуальное состояние кнопки с фактической видимостью панели записи.</summary>
    public void SetRecordPanelOpen(bool isOpen)
    {
        if (RecordPanelBtn.IsChecked == isOpen) return;
        RecordPanelBtn.IsChecked = isOpen;   // вызовет IsCheckedChanged → UpdateRecordPanelIcon
    }

    private void UpdateRecordPanelIcon()
    {
        var open = RecordPanelBtn.IsChecked == true;
        RecordOpenIcon.IsVisible  = !open;
        RecordCloseIcon.IsVisible = open;
        ToolTip.SetTip(RecordPanelBtn, open ? "Закрыть запись" : "Открыть запись");
    }

    /// <summary>Всплывашка-ошибка у заголовка коллекции (напр. при попытке дублирующего имени).
    /// Через Post — чтобы показать после завершения текущего события commit/focus.</summary>
    public void ShowTitleError(string message)
        => Dispatcher.UIThread.Post(() => Toast.Error(TitleDisplay, message));

    public void SetTitle(string text)
    {
        _committedTitle    = text ?? "";
        TitleBox.Text      = _committedTitle;
        TitleBox.IsEnabled = !string.IsNullOrEmpty(_committedTitle);
        ExitTitleEdit();
    }

    private void ExitTitleEdit()
    {
        TitleBox.IsVisible     = false;
        TitleDisplay.IsVisible = true;
        if (string.IsNullOrEmpty(_committedTitle))
        {
            TitleDisplay.Text       = "Выберите коллекцию";
            TitleDisplay.Foreground = (IBrush)this.FindResource("BrushSecondaryText")!;
        }
        else
        {
            TitleDisplay.Text       = _committedTitle;
            TitleDisplay.Foreground = (IBrush)this.FindResource("BrushPrimaryText")!;
        }
    }

    public string CurrentViewMode =>
        ViewModes[Math.Max(0, Math.Min(ViewSelector.SelectedIndex, ViewModes.Length - 1))];

    /// <summary>Выбрать вид (table/list) без подсветки кнопок — те живут в футере.</summary>
    public void SetViewMode(string viewMode)
    {
        var idx = Array.IndexOf(ViewModes, viewMode);
        if (idx >= 0) ViewSelector.SelectedIndex = idx;
    }

    private void CommitTitle()
    {
        var newName = (TitleBox.Text ?? "").Trim();
        if (newName.Length == 0 || newName == _committedTitle)
        {
            // Откатываем визуально, если попытались очистить или ничего не поменялось.
            TitleBox.Text = _committedTitle;
            return;
        }
        _committedTitle = newName;
        TitleBox.Text   = newName;
        TitleEdited?.Invoke(this, newName);
    }
}
