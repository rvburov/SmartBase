using Avalonia.Controls;
using Avalonia.Input;

namespace MyPlanner.Controls.Components;

public partial class ConstructorNavbar : UserControl
{
    private static readonly string[] ViewNames = ["Таблица", "Список"];
    private static readonly string[] ViewModes = ["table", "list"];

    public event EventHandler<string>? ViewChanged;
    /// <summary>Новое имя коллекции (после blur/Enter). Пусто/whitespace — игнорируется родителем.</summary>
    public event EventHandler<string>? TitleEdited;

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

        TitleBox.LostFocus += (_, _) => CommitTitle();
        TitleBox.KeyDown   += (_, e) =>
        {
            if (e.Key == Key.Enter) { CommitTitle(); e.Handled = true; }
            if (e.Key == Key.Escape) { TitleBox.Text = _committedTitle; e.Handled = true; }
        };
    }

    public void SetTitle(string text)
    {
        _committedTitle = text ?? "";
        TitleBox.Text   = _committedTitle;
        TitleBox.IsEnabled = !string.IsNullOrEmpty(_committedTitle);
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
