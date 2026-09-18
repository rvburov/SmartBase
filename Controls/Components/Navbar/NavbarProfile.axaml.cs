using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;

namespace SmartBase.Controls.Components;

/// <summary>
/// База в списке навбара: имя и иконка рода хранения — цилиндр у базы-файла, сервер у базы на
/// сервере. Иконку кладет вызывающая сторона (она знает провайдер базы), навбар только рисует:
/// подпись списка стоит снаружи, и без иконки в самом списке было бы не видно, где какая база
/// лежит, — а имена у баз-копий часто совпадают.
/// </summary>
public sealed record NavbarDb(string Name, Geometry? Icon);

/// <summary>
/// Навбар раздела «Профиль»: заголовок выбранного раздела слева, действия раздела справа.
///
/// Действий три рода, и у каждого свой раздел: выбор базы («Панель администратора» и «История
/// изменений»), проверка цепочки и выгрузка («Журнал безопасности», выгрузка — еще и у истории),
/// меню «Добавить пароль» («Менеджер паролей»). Кто из них виден, решает ОДИН метод
/// <see cref="ShowSection"/>, а не набор отдельных переключателей: подпись списка баз и кнопка
/// выгрузки общие у двух разделов, и два независимых переключателя гасили бы их по очереди.
///
/// Высота и отступы — как у остальных навбаров (56 / 16).
/// </summary>
public partial class NavbarProfile : UserControl
{
    /// <summary>Пользователь выбрал другую базу журнала (программное наполнение
    /// списка через <see cref="SetHistoryDbs"/> событие не поднимает).</summary>
    public event EventHandler? HistoryDbChanged;

    /// <summary>Пользователь выбрал другую базу в панели администратора.</summary>
    public event EventHandler? AdminDbChanged;

    /// <summary>Нажата кнопка выгрузки в CSV; что выгружать — решает открытый раздел.</summary>
    public event EventHandler? ExportRequested;

    /// <summary>Нажата «Проверить цепочку» в журнале безопасности.</summary>
    public event EventHandler? ChainCheckRequested;

    /// <summary>Пункты меню «Добавить пароль» (обработку ведет ProfileView).</summary>
    public event EventHandler? AddConnectionRequested;
    public event EventHandler? AddTokenRequested;
    public event EventHandler? MasterHashRequested;

    private bool _syncingHistoryDbs;
    private bool _syncingAdminDbs;

    public NavbarProfile()
    {
        InitializeComponent();

        HistoryDbCombo.SelectionChanged += (_, _) =>
        {
            if (!_syncingHistoryDbs && HistoryDbCombo.SelectedIndex >= 0)
                HistoryDbChanged?.Invoke(this, EventArgs.Empty);
        };

        AdminDbCombo.SelectionChanged += (_, _) =>
        {
            if (!_syncingAdminDbs && AdminDbCombo.SelectedIndex >= 0)
                AdminDbChanged?.Invoke(this, EventArgs.Empty);
        };

        AddSecretCombo.SelectionChanged += (_, _) =>
        {
            var idx = AddSecretCombo.SelectedIndex;
            if (idx < 0) return;   // сброс выбора ниже — не действие

            // Сброс — СРАЗУ, в этом же такте. Отложенный (в Post) успевал показать выбранный
            // пункт на месте плейсхолдера, и надпись «Добавить пароль» моргала выбранным
            // словом. Присваивание входит в этот же обработчик с idx = -1 и гасится проверкой
            // выше.
            AddSecretCombo.SelectedIndex = -1;

            // А вот действие — отложенно: модальное окно не должно открываться внутри обработки
            // выбора, иначе выпадашка остается открытой за ним.
            Dispatcher.UIThread.Post(() =>
            {
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

    /// <summary>
    /// Расставить действия под открытый раздел (ключ из <c>ProfileRegistry</c>). Меню «Добавить
    /// пароль» этот метод не трогает: его видимость зависит еще и от прав, и уточняет ее
    /// <see cref="ShowSecretsActions"/> сразу следом.
    /// </summary>
    public void ShowSection(string key)
    {
        var history = key == "history";
        var admin   = key == "admin";
        var log     = key == "securitylog";

        DbLabel.IsVisible        = history || admin;
        HistoryDbCombo.IsVisible = history;
        AdminDbCombo.IsVisible   = admin;
        ChainBtn.IsVisible       = log;
        ExportBtn.IsVisible      = history || log;
    }

    /// <summary>Показать/скрыть «Добавить пароль» (виден только в «Менеджере паролей» и только
    /// тому, кому разрешена настройка обмена: все три пункта спрашивают это право).</summary>
    public bool ShowSecretsActions { set => AddSecretCombo.IsVisible = value; }

    private void OnExportClick(object? sender, RoutedEventArgs e) =>
        ExportRequested?.Invoke(this, EventArgs.Empty);

    private void OnChainClick(object? sender, RoutedEventArgs e) =>
        ChainCheckRequested?.Invoke(this, EventArgs.Empty);

    /// <summary>Наполнить список баз журнала и выставить выбор без события
    /// <see cref="HistoryDbChanged"/>.</summary>
    public void SetHistoryDbs(IReadOnlyList<NavbarDb> names, int selectedIndex)
    {
        _syncingHistoryDbs = true;
        HistoryDbCombo.ItemsSource   = names;
        HistoryDbCombo.SelectedIndex = selectedIndex;
        _syncingHistoryDbs = false;
    }

    /// <summary>Индекс выбранной базы журнала (-1 — допущенных баз нет).</summary>
    public int HistoryDbIndex => HistoryDbCombo.SelectedIndex;

    /// <summary>Наполнить список баз панели администратора без события
    /// <see cref="AdminDbChanged"/>.</summary>
    public void SetAdminDbs(IReadOnlyList<NavbarDb> names, int selectedIndex)
    {
        _syncingAdminDbs = true;
        AdminDbCombo.ItemsSource   = names;
        AdminDbCombo.SelectedIndex = selectedIndex;
        _syncingAdminDbs = false;
    }

    /// <summary>
    /// Индекс выбранной базы панели администратора (-1 — администрировать нечего). Присваивание
    /// события <see cref="AdminDbChanged"/> не поднимает: базу меняют еще и списком в шапке
    /// таблицы пользователей, и навбар за ней просто следует — иначе смена базы в таблице
    /// вернула бы панель администратора на место таблицы.
    /// </summary>
    public int AdminDbIndex
    {
        get => AdminDbCombo.SelectedIndex;
        set
        {
            if (AdminDbCombo.SelectedIndex == value) return;
            _syncingAdminDbs = true;
            AdminDbCombo.SelectedIndex = value;
            _syncingAdminDbs = false;
        }
    }
}
