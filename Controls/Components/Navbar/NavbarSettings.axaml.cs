using Avalonia.Controls;
using Avalonia.Threading;

namespace SmartBase.Controls.Components;

/// <summary>Навбар раздела «Настройки»: заголовок выбранного раздела слева; в разделе
/// «Управление базой данных» справа — «Добавить базу данных» (создать SQLite /
/// подключить SQLite / подключить PostgreSQL). Высота/отступы — как у остальных
/// навбаров (56 / 16). Выпадашка — стандартный ComboBox приложения, но работает
/// как меню действий: выбор пункта поднимает событие и тут же сбрасывается.</summary>
public partial class NavbarSettings : UserControl
{
    /// <summary>Пункты меню «Добавить базу данных» (обработку ведёт SettingsView).</summary>
    public event EventHandler? CreateSqliteRequested;
    public event EventHandler? ConnectSqliteRequested;
    public event EventHandler? ConnectPostgresRequested;

    public NavbarSettings()
    {
        InitializeComponent();

        AddDatabaseCombo.SelectionChanged += (_, _) =>
        {
            var idx = AddDatabaseCombo.SelectedIndex;
            if (idx < 0) return;   // сброс выбора ниже — не действие
            // Действие и сброс — после закрытия выпадашки (Post), чтобы модальное окно
            // не открывалось внутри обработки выбора и плейсхолдер вернулся на место.
            Dispatcher.UIThread.Post(() =>
            {
                AddDatabaseCombo.SelectedIndex = -1;
                switch (idx)
                {
                    case 0: CreateSqliteRequested?.Invoke(this, EventArgs.Empty); break;
                    case 1: ConnectSqliteRequested?.Invoke(this, EventArgs.Empty); break;
                    case 2: ConnectPostgresRequested?.Invoke(this, EventArgs.Empty); break;
                }
            });
        };
    }

    /// <summary>Заголовок раздела настроек.</summary>
    public string Title { set => TitleText.Text = value; }

    /// <summary>Показать/скрыть «Добавить базу данных» (видна только в разделе БД).</summary>
    public bool ShowDatabaseActions { set => AddDatabaseCombo.IsVisible = value; }
}
