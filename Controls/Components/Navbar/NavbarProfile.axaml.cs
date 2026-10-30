using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;

namespace SmartBase.Controls.Components;

/// <summary>Название базы и иконка провайдера в списке навбара.</summary>
public sealed record NavbarDb(string Name, Geometry? Icon);

/// <summary>Заголовок профиля, выбор базы истории изменений и выгрузка в CSV.</summary>
public partial class NavbarProfile : UserControl
{
    public event EventHandler? HistoryDbChanged;
    public event EventHandler? ExportRequested;
    private bool _syncingHistoryDbs;

    public NavbarProfile()
    {
        InitializeComponent();
        HistoryDbCombo.SelectionChanged += (_, _) =>
        {
            if (!_syncingHistoryDbs && HistoryDbCombo.SelectedIndex >= 0)
                HistoryDbChanged?.Invoke(this, EventArgs.Empty);
        };
    }

    public string Title { set => TitleText.Text = value; }

    public void ShowSection(string key)
    {
        var history = key == "history";
        DbLabel.IsVisible = history;
        HistoryDbCombo.IsVisible = history;
        ExportBtn.IsVisible = history;
    }

    private void OnExportClick(object? sender, RoutedEventArgs e) =>
        ExportRequested?.Invoke(this, EventArgs.Empty);

    public void SetHistoryDbs(IReadOnlyList<NavbarDb> names, int selectedIndex)
    {
        _syncingHistoryDbs = true;
        HistoryDbCombo.ItemsSource = names;
        HistoryDbCombo.SelectedIndex = selectedIndex;
        _syncingHistoryDbs = false;
    }

    public int HistoryDbIndex => HistoryDbCombo.SelectedIndex;
}
