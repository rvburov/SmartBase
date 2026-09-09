using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;

namespace SmartBase.Controls.Components;

/// <summary>Навбар раздела «Настройки»: заголовок выбранного раздела слева; в разделе
/// «Управление базой данных» справа — «Добавить базу данных» (создать SQLite /
/// подключить SQLite / подключить PostgreSQL). Высота/отступы — как у остальных
/// навбаров (56 / 16). Выпадашка — стандартный ComboBox приложения, но работает
/// как меню действий: выбор пункта поднимает событие и тут же сбрасывается.</summary>
public partial class NavbarSettings : UserControl
{
    /// <summary>Пункты меню «Добавить базу данных» (обработку ведет SettingsView).</summary>
    public event EventHandler? CreateSqliteRequested;
    public event EventHandler? ConnectSqliteRequested;
    public event EventHandler? ConnectPostgresRequested;
    public event EventHandler? CreatePostgresRequested;

    /// <summary>Нажата кнопка выгрузки диагностики в CSV.</summary>
    public event EventHandler? ExportLogsRequested;

    /// <summary>Выбрана вкладка раздела «Интеграции»; в аргументе — ее ключ.</summary>
    public event EventHandler<string>? IntegrationTabRequested;

    // Гасит событие, пока список наполняется кодом: смена ItemsSource дергает
    // SelectionChanged, и без флага открытие раздела перезаписывало бы сохраненную вкладку.
    private bool _integrationsBuilding;

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
                    case 3: CreatePostgresRequested?.Invoke(this, EventArgs.Empty); break;
                }
            });
        };

        IntegrationsCombo.SelectionChanged += (_, _) =>
        {
            if (_integrationsBuilding) return;

            var index = IntegrationsCombo.SelectedIndex;
            if (index < 0 || index >= Settings.IntegrationTabs.All.Count) return;

            IntegrationTabRequested?.Invoke(this, Settings.IntegrationTabs.All[index].Key);
        };
    }

    /// <summary>Заголовок раздела настроек.</summary>
    public string Title { set => TitleText.Text = value; }

    /// <summary>
    /// Наполнить переключатель вкладок «Интеграций» и выбрать нужную. Подписи берутся из
    /// <see cref="Settings.IntegrationTabs"/> — единственного места, где они перечислены.
    /// </summary>
    public void SetIntegrationTabs(string selectedKey)
    {
        _integrationsBuilding = true;

        IntegrationsCombo.ItemsSource   = Settings.IntegrationTabs.All.Select(t => t.Label).ToList();
        IntegrationsCombo.SelectedIndex = Settings.IntegrationTabs.All.ToList()
            .FindIndex(t => t.Key == Settings.IntegrationTabs.Resolve(selectedKey).Key);

        _integrationsBuilding = false;
    }

    /// <summary>Показать/скрыть переключатель вкладок (виден только в «Интеграциях»).</summary>
    public bool ShowIntegrationTabs { set => IntegrationsCombo.IsVisible = value; }

    /// <summary>Показать/скрыть «Добавить базу данных» (видна только в разделе БД).</summary>
    public bool ShowDatabaseActions { set => AddDatabaseCombo.IsVisible = value; }

    /// <summary>Показать/скрыть выгрузку в CSV (видна только в «Диагностике»).</summary>
    public bool ShowLogsActions { set => ExportLogsBtn.IsVisible = value; }

    private void OnExportLogsClick(object? sender, RoutedEventArgs e) =>
        ExportLogsRequested?.Invoke(this, EventArgs.Empty);
}
