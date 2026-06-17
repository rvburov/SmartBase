using Avalonia.Controls;

namespace DataBase.Controls.Dialogs;

/// <summary>
/// Выбор целевой БД (например, при создании события календаря, когда смонтировано
/// несколько БД). Возвращает выбранный DbId или null при отмене.
/// </summary>
public partial class SelectDatabaseDialog : Window
{
    private List<int> _ids = [];

    public SelectDatabaseDialog()
    {
        InitializeComponent();
        CancelBtn.Click += (_, _) => Close((int?)null);
        OkBtn.Click     += (_, _) =>
        {
            var idx = DbCombo.SelectedIndex;
            Close(idx >= 0 && idx < _ids.Count ? _ids[idx] : (int?)null);
        };
    }

    public SelectDatabaseDialog(IReadOnlyList<(int DbId, string Name)> databases) : this()
    {
        _ids = databases.Select(d => d.DbId).ToList();
        DbCombo.ItemsSource   = databases.Select(d => d.Name).ToList();
        DbCombo.SelectedIndex = 0;
    }
}
