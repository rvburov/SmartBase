using Avalonia.Controls;
using Avalonia.Interactivity;

namespace SmartBase.Controls.Components;

/// <summary>Навбар раздела «Профиль»: заголовок выбранного раздела слева; в «Истории
/// изменений» справа — выбор базы журнала (наполняет ProfileView) и кнопка выгрузки
/// журнала в CSV. Высота/отступы — как у остальных навбаров (56 / 16).</summary>
public partial class NavbarProfile : UserControl
{
    /// <summary>Пользователь выбрал другую базу журнала (программное наполнение
    /// списка через <see cref="SetHistoryDbs"/> событие не поднимает).</summary>
    public event EventHandler? HistoryDbChanged;

    /// <summary>Нажата кнопка выгрузки журнала в CSV.</summary>
    public event EventHandler? ExportHistoryRequested;

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

    /// <summary>Заголовок раздела профиля.</summary>
    public string Title { set => TitleText.Text = value; }

    /// <summary>Показать/скрыть действия журнала — выбор базы и выгрузку в CSV
    /// (видны только в «Истории изменений»).</summary>
    public bool ShowHistoryActions
    {
        set
        {
            HistoryDbCombo.IsVisible   = value;
            ExportHistoryBtn.IsVisible = value;
        }
    }

    private void OnExportHistoryClick(object? sender, RoutedEventArgs e) =>
        ExportHistoryRequested?.Invoke(this, EventArgs.Empty);

    /// <summary>Наполнить список баз журнала и выставить выбор без события
    /// <see cref="HistoryDbChanged"/>.</summary>
    public void SetHistoryDbs(IReadOnlyList<string> names, int selectedIndex)
    {
        _syncingHistoryDbs = true;
        HistoryDbCombo.ItemsSource   = names;
        HistoryDbCombo.SelectedIndex = selectedIndex;
        _syncingHistoryDbs = false;
    }

    /// <summary>Индекс выбранной базы журнала (-1 — допущенных баз нет).</summary>
    public int HistoryDbIndex => HistoryDbCombo.SelectedIndex;
}
