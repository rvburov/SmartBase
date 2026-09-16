using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;

namespace SmartBase.Controls.Components;

/// <summary>Навбар раздела «Профиль»: заголовок выбранного раздела слева; в «Истории
/// изменений» справа — выбор базы журнала (наполняет ProfileView) и кнопка выгрузки
/// журнала в CSV; в «Менеджере паролей» — меню «Добавить пароль». Высота/отступы — как у
/// остальных навбаров (56 / 16).</summary>
public partial class NavbarProfile : UserControl
{
    /// <summary>Пользователь выбрал другую базу журнала (программное наполнение
    /// списка через <see cref="SetHistoryDbs"/> событие не поднимает).</summary>
    public event EventHandler? HistoryDbChanged;

    /// <summary>Нажата кнопка выгрузки журнала в CSV.</summary>
    public event EventHandler? ExportHistoryRequested;

    /// <summary>Пункты меню «Добавить пароль» (обработку ведет ProfileView).</summary>
    public event EventHandler? AddConnectionRequested;
    public event EventHandler? AddTokenRequested;
    public event EventHandler? MasterHashRequested;

    private bool _syncingHistoryDbs;

    public NavbarProfile()
    {
        InitializeComponent();

        HistoryDbCombo.SelectionChanged += (_, _) =>
        {
            if (!_syncingHistoryDbs && HistoryDbCombo.SelectedIndex >= 0)
                HistoryDbChanged?.Invoke(this, EventArgs.Empty);
        };

        AddSecretCombo.SelectionChanged += (_, _) =>
        {
            var idx = AddSecretCombo.SelectedIndex;
            if (idx < 0) return;   // сброс выбора ниже — не действие
            // Действие и сброс — после закрытия выпадашки (Post), чтобы модальное окно
            // не открывалось внутри обработки выбора и плейсхолдер вернулся на место.
            Dispatcher.UIThread.Post(() =>
            {
                AddSecretCombo.SelectedIndex = -1;
                switch (idx)
                {
                    case 0: AddConnectionRequested?.Invoke(this, EventArgs.Empty); break;
                    case 1: AddTokenRequested?.Invoke(this, EventArgs.Empty);      break;
                    case 2: MasterHashRequested?.Invoke(this, EventArgs.Empty);    break;
                }
            });
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

    /// <summary>Показать/скрыть «Добавить пароль» (виден только в «Менеджере паролей» и только
    /// администратору хотя бы одной открытой базы).</summary>
    public bool ShowSecretsActions { set => AddSecretCombo.IsVisible = value; }

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
