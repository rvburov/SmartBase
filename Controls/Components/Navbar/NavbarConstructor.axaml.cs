using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using SmartBase.Controls.Widgets;
using SmartBase.Core.Models;
using SmartBase.Settings;
using SmartBase.Utils;

namespace SmartBase.Controls.Components;

public partial class NavbarConstructor : UserControl
{
    // Только виды, не скрытые в «Оформлении» (ключи и подписи — в ViewCatalog).
    private readonly ViewSwitcher _views;

    public event EventHandler<string>? ViewChanged;
    /// <summary>Новое имя коллекции (после blur/Enter). Пусто/whitespace — игнорируется родителем.</summary>
    public event EventHandler<string>? TitleEdited;
    /// <summary>Запрос изменить видимость правой панели записи (true — открыть, false — закрыть).</summary>
    public event EventHandler<bool>?   RecordPanelToggled;

    private string _committedTitle = "";

    public NavbarConstructor()
    {
        InitializeComponent();

        // Вид по умолчанию — настройка constructorView: на него коллекция уходит со скрытого вида.
        _views = new ViewSwitcher(ViewSelector, ViewCatalog.ScopeConstructor, AppSettings.LoadConstructorView);
        _views.Changed += mode => ViewChanged?.Invoke(this, mode);

        TitleDisplay.PointerPressed += (_, e) =>
        {
            if (!TitleBox.IsEnabled) return;
            Behaviors.TitleEditing.Begin(TitleDisplay, TitleBox, e);
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
            // Шапка — одна строка: показываем первую строку имени коллекции (TitleText).
            // Полное имя остается в TitleBox — переименование идет по нему.
            TitleDisplay.Text       = TitleText.FirstLine(_committedTitle);
            TitleDisplay.Foreground = (IBrush)this.FindResource("BrushPrimaryText")!;
        }
    }

    public string CurrentViewMode => _views.Current;

    /// <summary>
    /// Выбрать вид (table/list) без ViewChanged: вид открывает сам раздел. Скрытый не
    /// показывается — вместо него вид по умолчанию. Возвращает показанный.
    /// </summary>
    public string SetViewMode(string viewMode) => _views.Show(viewMode);

    /// <summary>Перечитать «Отображаемые виды» (их сменили в настройках). Скрыли открытый
    /// вид — переключатель уходит на вид по умолчанию и сообщает об этом ViewChanged.</summary>
    public void ApplyViewVisibility() => _views.Refresh();

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
