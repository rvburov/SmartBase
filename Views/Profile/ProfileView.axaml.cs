using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using DataBase.Controls.Dialogs;
using DataBase.Core.Models;
using DataBase.Database;
using DataBase.Database.Entities;
using DataBase.Settings;
using DataBase.Utils;

namespace DataBase.Views.Profile;

/// <summary>
/// Модуль «Профиль»: общие данные пользователя (имя компьютера — автоматически, ФИО — вручную),
/// панель администратора (допуск и права пользователей, пароль базы) и история изменений.
/// Разделы переключает сайдбар (SidebarList в MainWindow) через <see cref="ShowCategory"/> —
/// зеркало SettingsView. Данные админ-панели перечитываются при каждом открытии раздела.
/// </summary>
public partial class ProfileView : UserControl
{
    private Workspace? _workspace;

    // ── Состояние панели администратора ──────────────────────────────────────
    private List<DatabaseManager> _adminDbs = [];
    private DatabaseManager? _adminDb;
    private List<DbUserEntity> _users = [];
    private DbUserEntity? _selectedUser;
    // Области прав в порядке ScopeCombo: null = вся база, иначе id коллекции.
    private List<int?> _scopes = [];
    // Программное выставление чекбоксов не должно писать маски в БД.
    private bool _suppressChecks;
    private readonly List<(CheckBox Box, PermAction Action)> _checkBoxes = [];

    public ProfileView()
    {
        InitializeComponent();

        // ── Общие данные ─────────────────────────────────────────────────────
        MachineNameText.Text = Environment.MachineName;
        UserKeyText.Text     = CurrentUser.Key;

        LastNameBox.Text   = CurrentUser.LastName;
        FirstNameBox.Text  = CurrentUser.FirstName;
        MiddleNameBox.Text = CurrentUser.MiddleName;
        DisplayNameText.Text = CurrentUser.DisplayName;
    }

    /// <summary>Рабочее пространство для панелей администратора и истории.</summary>
    public void Initialize(Workspace workspace) => _workspace = workspace;

    /// <summary>Показать раздел профиля по ключу из ProfileRegistry.</summary>
    public void ShowCategory(string key)
    {
        GeneralPanel.IsVisible = key == "general";
        AdminPanel.IsVisible   = key == "admin";
        HistoryPanel.IsVisible = key == "history";
        HeaderBar.Title = (ProfileRegistry.FindByKey(key) ?? ProfileRegistry.All[0]).Label;

        if (key == "admin")   RebuildAdminPanel();
        if (key == "history") RebuildHistoryPanel();
    }

    // ── ФИО ──────────────────────────────────────────────────────────────────
    //
    // Автосохранение при уходе фокуса (как путь обновления в настройках): пустое
    // значение = «не задано» (ключ удаляется из app.db). Отображаемое имя обновляется
    // сразу — оно же уйдёт снапшотом в db_users открытых баз при следующем запуске.
    private void OnProfileFieldChanged(object? sender, RoutedEventArgs e)
    {
        CurrentUser.LastName   = LastNameBox.Text   ?? "";
        CurrentUser.FirstName  = FirstNameBox.Text  ?? "";
        CurrentUser.MiddleName = MiddleNameBox.Text ?? "";

        // Показать нормализованные значения (обрезка пробелов) и свежее отображаемое имя.
        LastNameBox.Text   = CurrentUser.LastName;
        FirstNameBox.Text  = CurrentUser.FirstName;
        MiddleNameBox.Text = CurrentUser.MiddleName;
        DisplayNameText.Text = CurrentUser.DisplayName;
    }

    // ── Панель администратора ─────────────────────────────────────────────────

    // Пересобрать панель: список баз, где текущий пользователь — администратор.
    private void RebuildAdminPanel()
    {
        _adminDbs = _workspace?.Mounted.Where(m => m.Permissions.IsAdmin).ToList() ?? [];

        var hasAny = _adminDbs.Count > 0;
        AdminNoAccessRow.IsVisible = !hasAny;
        AdminContent.IsVisible     = hasAny;
        if (!hasAny) return;

        // Сохранить выбранную базу, если она ещё в списке.
        var keepDbId = _adminDb?.DbId;
        AdminDbCombo.ItemsSource = _adminDbs
            .Select(d => string.IsNullOrWhiteSpace(d.Name) ? "База данных" : d.Name)
            .ToList();
        var idx = keepDbId is int id ? _adminDbs.FindIndex(d => d.DbId == id) : -1;
        AdminDbCombo.SelectedIndex = idx >= 0 ? idx : 0;   // SelectionChanged → LoadAdminDb
    }

    private void OnAdminDbChanged(object? sender, SelectionChangedEventArgs e) => LoadAdminDb();

    // Загрузить выбранную базу: пароль, пользователи; блок прав сворачивается.
    private void LoadAdminDb()
    {
        var i = AdminDbCombo.SelectedIndex;
        _adminDb = i >= 0 && i < _adminDbs.Count ? _adminDbs[i] : null;
        _selectedUser = null;
        RightsBlock.IsVisible = false;
        if (_adminDb is null) return;

        RefreshPasswordRow();
        RebuildUsers();
    }

    private void RefreshPasswordRow()
    {
        if (_adminDb is null) return;
        var has = _adminDb.Permissions.HasPassword;
        PasswordStateText.Text     = has ? "Пароль установлен." : "Пароль не установлен.";
        PasswordSetBtn.Content     = has ? "Сменить пароль" : "Установить пароль";
        PasswordRemoveBtn.IsVisible = has;
    }

    private async void OnPasswordSet(object? sender, RoutedEventArgs e)
    {
        if (_adminDb is null || TopLevel.GetTopLevel(this) is not Window owner) return;
        var name = string.IsNullOrWhiteSpace(_adminDb.Name) ? "База данных" : _adminDb.Name;
        var password = await PasswordDialog
            .ForSet(name, _adminDb.Permissions.HasPassword)
            .ShowDialog<string?>(owner);
        if (string.IsNullOrEmpty(password)) return;

        _adminDb.Permissions.SetPassword(password);
        RefreshPasswordRow();
    }

    private async void OnPasswordRemove(object? sender, RoutedEventArgs e)
    {
        if (_adminDb is null || TopLevel.GetTopLevel(this) is not Window owner) return;
        var confirmed = await new ConfirmDialog(
                "Убрать пароль базы данных? Новые пользователи смогут получить доступ " +
                "только после вашего подтверждения.", confirmText: "Убрать")
            .ShowDialog<bool>(owner);
        if (!confirmed) return;

        _adminDb.Permissions.RemovePassword();
        RefreshPasswordRow();
    }

    // ── Список пользователей ──────────────────────────────────────────────────

    private void RebuildUsers()
    {
        UsersHost.Children.Clear();
        if (_adminDb is null) return;

        _users = _adminDb.Permissions.GetUsers();
        foreach (var user in _users)
            UsersHost.Children.Add(BuildUserRow(user));
    }

    // Строка пользователя: имя + ключ + статус слева, действия справа. Клик по строке
    // выбирает пользователя для настройки прав (не-администраторов).
    private Control BuildUserRow(DbUserEntity user)
    {
        var isSelf = user.UserKey == CurrentUser.Key;

        var info = new StackPanel { Spacing = 3, VerticalAlignment = VerticalAlignment.Center };
        info.Children.Add(new TextBlock { Text = user.DisplayName + (isSelf ? " (вы)" : "") });
        info.Children.Add(new TextBlock
        {
            Text = user.UserKey,
            FontSize = FontSize("FontSizeSmall", 11),
            Foreground = Brush("BrushSecondaryText", "#8E8E93"),
        });

        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            VerticalAlignment = VerticalAlignment.Center,
        };
        if (user.IsAdmin)
        {
            actions.Children.Add(new TextBlock
            {
                Text = "Администратор",
                VerticalAlignment = VerticalAlignment.Center,
                FontSize = FontSize("FontSizeSmall", 11),
                Foreground = Brush("BrushSecondaryText", "#8E8E93"),
            });
        }
        else if (!user.IsApproved)
        {
            actions.Children.Add(new TextBlock
            {
                Text = "Ожидает допуска",
                VerticalAlignment = VerticalAlignment.Center,
                FontSize = FontSize("FontSizeSmall", 11),
                Foreground = Brush("BrushRed", "#FF3B30"),
            });
            var approve = MakeRowButton("Допустить");
            approve.Click += (_, _) => { _adminDb?.Permissions.SetApproved(user.Id, true); RebuildUsers(); };
            actions.Children.Add(approve);
        }
        else
        {
            var revoke = MakeRowButton("Отозвать допуск");
            revoke.Click += (_, _) =>
            {
                _adminDb?.Permissions.SetApproved(user.Id, false);
                if (_selectedUser?.Id == user.Id) { _selectedUser = null; RightsBlock.IsVisible = false; }
                RebuildUsers();
            };
            actions.Children.Add(revoke);
        }

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        Grid.SetColumn(info, 0);
        Grid.SetColumn(actions, 1);
        grid.Children.Add(info);
        grid.Children.Add(actions);

        var row = new Border
        {
            Padding = new Thickness(10, 8),
            CornerRadius = new CornerRadius(6),
            Background = _selectedUser?.Id == user.Id
                ? Brush("BrushAccentLight", "#E5F0FF")
                : Brushes.Transparent,
            Child = grid,
        };

        // Права настраиваются у не-администраторов; клик по строке выбирает пользователя.
        if (!user.IsAdmin)
        {
            row.Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand);
            row.PointerPressed += (_, _) => SelectUser(user);
        }
        return row;
    }

    // Кнопка строки пользователя: вид dialog, но ширина по тексту (стиль фиксирует
    // Width=110, а «Отозвать допуск» шире — Width=NaN возвращает авторазмер).
    private static Button MakeRowButton(string content)
    {
        var btn = new Button
        {
            Content  = content,
            Width    = double.NaN,
            MinWidth = 110,
            Padding  = new Thickness(14, 0),
        };
        btn.Classes.Add("dialog");
        return btn;
    }

    private void SelectUser(DbUserEntity user)
    {
        _selectedUser = user;
        RebuildUsers();   // подсветка выбранной строки

        RightsTitle.Text = $"Права пользователя: {user.DisplayName}";
        RightsBlock.IsVisible = true;
        RebuildScopes();
    }

    // Область прав: «Вся база данных» + коллекции выбранной базы.
    private void RebuildScopes()
    {
        if (_adminDb is null) return;
        var collections = _adminDb.Constructor.GetAllCollections();

        _scopes = [null];
        var labels = new List<string> { "Вся база данных" };
        foreach (var c in collections.Where(c => c.Id.HasValue))
        {
            _scopes.Add(c.Id);
            labels.Add(c.Name);
        }
        ScopeCombo.ItemsSource   = labels;
        ScopeCombo.SelectedIndex = 0;   // SelectionChanged → LoadChecks
        LoadChecks();
    }

    private void OnScopeChanged(object? sender, SelectionChangedEventArgs e) => LoadChecks();

    private int? CurrentScope =>
        ScopeCombo.SelectedIndex >= 0 && ScopeCombo.SelectedIndex < _scopes.Count
            ? _scopes[ScopeCombo.SelectedIndex]
            : null;

    // Сетка чекбоксов: три группы действий. Строится один раз, дальше только значения.
    private void EnsureChecksGrid()
    {
        if (ChecksGrid.Children.Count > 0) return;

        AddCheckGroup(0, "Коллекции",
        [
            PermAction.ViewCollection, PermAction.EditCollection,
            PermAction.DeleteCollection, PermAction.DuplicateCollection,
            PermAction.CreateCollection,
        ]);
        AddCheckGroup(1, "Записи",
        [
            PermAction.CreateRecord, PermAction.EditRecord,
            PermAction.DeleteRecord, PermAction.DuplicateRecord,
        ]);
        AddCheckGroup(2, "Свойства",
        [
            PermAction.CreateField, PermAction.EditField, PermAction.DeleteField,
        ]);
    }

    private void AddCheckGroup(int column, string title, PermAction[] actions)
    {
        var panel = new StackPanel { Spacing = 8, Margin = new Thickness(0, 0, 24, 0) };
        panel.Children.Add(new TextBlock
        {
            Text = title,
            FontWeight = Avalonia.Media.FontWeight.SemiBold,
            Margin = new Thickness(0, 0, 0, 2),
        });
        foreach (var action in actions)
        {
            var box = new CheckBox { Content = PermActionLabels.Label(action) };
            box.IsCheckedChanged += (_, _) => OnCheckChanged();
            _checkBoxes.Add((box, action));
            panel.Children.Add(box);
        }
        Grid.SetColumn(panel, column);
        ChecksGrid.Children.Add(panel);
    }

    // Показать действующую маску выбранного пользователя в выбранной области.
    private void LoadChecks()
    {
        if (_adminDb is null || _selectedUser is null) return;
        EnsureChecksGrid();

        var mask = _adminDb.Permissions.GetMask(_selectedUser.Id, CurrentScope);
        _suppressChecks = true;
        foreach (var (box, action) in _checkBoxes)
        {
            box.IsChecked = (mask & (int)action) == (int)action;
            // «Создавать коллекции» имеет смысл только на уровне базы.
            box.IsEnabled = action != PermAction.CreateCollection || CurrentScope is null;
        }
        _suppressChecks = false;
    }

    // Любой чекбокс: собрать маску и записать для выбранной области.
    private void OnCheckChanged()
    {
        if (_suppressChecks || _adminDb is null || _selectedUser is null) return;

        var mask = 0;
        foreach (var (box, action) in _checkBoxes)
            if (box.IsChecked == true)
                mask |= (int)action;

        _adminDb.Permissions.SetMask(_selectedUser.Id, CurrentScope, mask);
    }

    // ── История изменений ─────────────────────────────────────────────────────
    //
    // Журнал per-база: выпадающий список допущенных баз + фильтр по типу объекта.
    // Строки — keyset-страницами по 200 (Id DESC, «Показать ещё»), читаются на
    // отдельном подключении (GetChangeLogPageAsync) — миллионы строк не фризят UI.

    private const int HistoryPageSize = 200;
    private List<DatabaseManager> _historyDbs = [];
    private DatabaseManager? _historyDb;
    private long? _historyLastId;
    private bool _historyLoading;

    private void RebuildHistoryPanel()
    {
        _historyDbs = _workspace?.Mounted.Where(m => m.Permissions.AccessGranted).ToList() ?? [];

        var keepDbId = _historyDb?.DbId;
        HistoryDbCombo.ItemsSource = _historyDbs
            .Select(d => string.IsNullOrWhiteSpace(d.Name) ? "База данных" : d.Name)
            .ToList();
        if (HistoryTypeCombo.SelectedIndex < 0) HistoryTypeCombo.SelectedIndex = 0;
        var idx = keepDbId is int id ? _historyDbs.FindIndex(d => d.DbId == id) : -1;
        HistoryDbCombo.SelectedIndex = idx >= 0 ? idx : (_historyDbs.Count > 0 ? 0 : -1);
        ResetHistory();
    }

    private void OnHistoryFilterChanged(object? sender, SelectionChangedEventArgs e) => ResetHistory();

    // Фильтр типов объектов журнала по выбранному пункту (null — все).
    private List<string>? HistoryTypes() =>
        (HistoryTypeCombo.SelectedItem as ComboBoxItem)?.Tag as string switch
        {
            "structure" => ["collection", "group"],
            "records"   => ["record", "records"],
            "fields"    => ["field"],
            _           => null,
        };

    private void ResetHistory()
    {
        var i = HistoryDbCombo.SelectedIndex;
        _historyDb     = i >= 0 && i < _historyDbs.Count ? _historyDbs[i] : null;
        _historyLastId = null;
        HistoryHost.Children.Clear();
        HistoryMoreBtn.IsVisible   = false;
        HistoryEmptyText.IsVisible = false;
        _ = LoadMoreHistoryAsync();
    }

    private void OnHistoryMore(object? sender, RoutedEventArgs e) => _ = LoadMoreHistoryAsync();

    private async Task LoadMoreHistoryAsync()
    {
        if (_historyDb is null || _historyLoading) return;
        _historyLoading = true;
        var requestDb = _historyDb;
        try
        {
            var page = await requestDb.Constructor.GetChangeLogPageAsync(
                _historyLastId, HistoryPageSize, HistoryTypes());

            // Пока читали страницу, могли переключить базу/фильтр — результат уже не наш.
            if (!ReferenceEquals(requestDb, _historyDb)) return;

            foreach (var row in page)
                HistoryHost.Children.Add(BuildHistoryRow(row));

            if (page.Count > 0) _historyLastId = page[^1].Id;
            HistoryMoreBtn.IsVisible   = page.Count == HistoryPageSize;
            HistoryEmptyText.IsVisible = HistoryHost.Children.Count == 0;
        }
        finally { _historyLoading = false; }
    }

    // Строка журнала: «{Имя} {действие}» слева, дата-время справа, серая линия снизу.
    private Control BuildHistoryRow(Database.Repositories.ConstructorRepository.ChangeLogRow row)
    {
        var text = new TextBlock
        {
            Text = $"{row.UserName} {HistoryPhrase(row)}",
            TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 16, 0),
        };
        var when = new TextBlock
        {
            Text = FormatHistoryTs(row.Ts),
            VerticalAlignment = VerticalAlignment.Center,
            FontSize = FontSize("FontSizeSmall", 11),
            Foreground = Brush("BrushSecondaryText", "#8E8E93"),
        };

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        Grid.SetColumn(text, 0);
        Grid.SetColumn(when, 1);
        grid.Children.Add(text);
        grid.Children.Add(when);

        var stack = new StackPanel();
        stack.Children.Add(new Border { Padding = new Thickness(0, 10), Child = grid });
        var sep = new Border();
        sep.Classes.Add("separator");
        sep.Classes.Add("h");
        stack.Children.Add(sep);
        return stack;
    }

    // «создал коллекцию «X»», «удалил 10 000 записей в коллекции «Y»» и т.п.
    private static string HistoryPhrase(Database.Repositories.ConstructorRepository.ChangeLogRow r)
    {
        var verb = r.Action switch
        {
            "create"    => "создал",
            "update"    => "изменил",
            "delete"    => "удалил",
            "duplicate" => "дублировал",
            "import"    => "импортировал",
            "export"    => "экспортировал",
            "move"      => "перенёс",
            _           => r.Action,
        };
        var obj = r.TargetType switch
        {
            "collection" => $"коллекцию «{r.TargetName}»",
            "group"      => $"группу «{r.TargetName}»",
            "field"      => $"свойство «{r.TargetName}»",
            "record"     => r.TargetName.Length > 0 ? $"запись «{r.TargetName}»" : "запись",
            "records"    => RecordsCount(r.Count ?? 0),
            _            => $"«{r.TargetName}»",
        };

        var text = $"{verb} {obj}";
        if (r.TargetType is "record" or "records" or "field" && !string.IsNullOrEmpty(r.CollectionName))
            text += $" в коллекции «{r.CollectionName}»";
        if (r.TargetType is "collection" or "group" && r.Count is > 0)
            text += $" ({RecordsCount(r.Count.Value)})";
        return text;
    }

    // Русское склонение: 1 запись, 2 записи, 10 000 записей.
    private static string RecordsCount(long n)
    {
        var word = (n % 100 is >= 11 and <= 14) ? "записей" : (n % 10) switch
        {
            1        => "запись",
            2 or 3 or 4 => "записи",
            _        => "записей",
        };
        return $"{n:N0} {word}";
    }

    // ISO UTC → локальное «дд.MM.гггг HH:mm».
    private static string FormatHistoryTs(string iso) =>
        DateTime.TryParse(iso, null, System.Globalization.DateTimeStyles.RoundtripKind, out var dt)
            ? dt.ToLocalTime().ToString("dd.MM.yyyy HH:mm")
            : iso;

    // ── Хелперы ресурсов (fallback — как в SidebarRow) ────────────────────────

    private static IBrush Brush(string key, string fallbackHex) =>
        Application.Current!.TryFindResource(key, out var res) && res is IBrush b
            ? b
            : new SolidColorBrush(Color.Parse(fallbackHex));

    private static double FontSize(string key, double fallback) =>
        Application.Current!.TryFindResource(key, out var res) && res is double d ? d : fallback;
}
