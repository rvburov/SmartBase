using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using SmartBase.Controls.Dialogs;
using SmartBase.Core.Models;
using SmartBase.Database;
using SmartBase.Database.Entities;
using SmartBase.Database.Repositories;
using SmartBase.Settings;
using SmartBase.Utils;

namespace SmartBase.Views.Profile;

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

        // Фиксация ФИО не должна зависеть от LostFocus (клик по пустому месту фокус
        // не забирает — ввод терялся бы молча): каждая буква сразу в app.db, Enter —
        // полная фиксация, клик мимо полей снимает фокус (LostFocus дозакрепляет).
        foreach (var box in new[] { LastNameBox, FirstNameBox, MiddleNameBox })
        {
            box.TextChanged += (_, _) => SaveProfileFields();
            box.KeyDown += (_, e) =>
            {
                if (e.Key != Avalonia.Input.Key.Enter) return;
                CommitProfile();
                TopLevel.GetTopLevel(this)?.FocusManager?.ClearFocus();
                e.Handled = true;
            };
        }
        GeneralPanel.PointerPressed += (_, e) =>
        {
            // Клик по самим полям сюда не доходит (TextBox гасит событие) — страховка.
            if (e.Source is Control c &&
                Avalonia.VisualTree.VisualExtensions.FindAncestorOfType<TextBox>(c, includeSelf: true) is not null)
                return;
            TopLevel.GetTopLevel(this)?.FocusManager?.ClearFocus();
        };

        // Выбор базы журнала живёт в навбаре (правый край «Истории изменений»).
        HeaderBar.HistoryDbChanged += (_, _) => OnHistoryDbChanged();
    }

    /// <summary>Рабочее пространство для панелей администратора и истории.</summary>
    public void Initialize(Workspace workspace) => _workspace = workspace;

    /// <summary>Показать раздел профиля по ключу из ProfileRegistry.</summary>
    public void ShowCategory(string key)
    {
        // Уход из «Общих данных» с полем в фокусе — тоже точка фиксации ФИО.
        if (GeneralPanel.IsVisible && key != "general") CommitProfile();

        GeneralPanel.IsVisible = key == "general";
        AdminPanel.IsVisible   = key == "admin";
        HistoryPanel.IsVisible = key == "history";
        LogsPanel.IsVisible    = key == "logs";
        // Панель деталей журнала живёт в корне (во всю высоту) — при уходе из «Диагностики»
        // её нужно закрыть явно (она не вложена в LogsPanel).
        if (key != "logs") CloseLogsDetail();
        HeaderBar.Title = (ProfileRegistry.FindByKey(key) ?? ProfileRegistry.All[0]).Label;
        // Действия истории (выбор базы, очистка журнала) — только для истории.
        HeaderBar.ShowHistoryActions = key == "history";

        if (key == "admin")   RebuildAdminPanel();
        if (key == "history") RebuildHistoryPanel();
        if (key == "logs")    RebuildLogsPanel();
    }

    // ── ФИО ──────────────────────────────────────────────────────────────────
    //
    // Два уровня. Каждая буква (TextChanged) — сразу в app.db через CurrentUser
    // (пустое значение = «не задано», ключ удаляется): закрытие приложения без
    // расфокуса ничего не теряет. Точка фиксации (расфокус/Enter/уход из раздела) —
    // нормализация полей + снапшот имени в db_users всех смонтированных баз с
    // событием user.rename: соседи видят новое имя сразу, не «при следующем запуске».
    private void SaveProfileFields()
    {
        CurrentUser.LastName   = LastNameBox.Text   ?? "";
        CurrentUser.FirstName  = FirstNameBox.Text  ?? "";
        CurrentUser.MiddleName = MiddleNameBox.Text ?? "";
        DisplayNameText.Text   = CurrentUser.DisplayName;
    }

    private void OnProfileFieldChanged(object? sender, RoutedEventArgs e) => CommitProfile();

    private void CommitProfile()
    {
        SaveProfileFields();

        // Показать нормализованные значения (обрезка пробелов) и свежее отображаемое имя.
        LastNameBox.Text   = CurrentUser.LastName;
        FirstNameBox.Text  = CurrentUser.FirstName;
        MiddleNameBox.Text = CurrentUser.MiddleName;
        DisplayNameText.Text = CurrentUser.DisplayName;

        if (_workspace is null) return;
        foreach (var db in _workspace.Mounted)
            db.Permissions.SyncDisplayName();
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

    /// <summary>Чужие события пользователей этой базы (присоединение, допуск, права —
    /// принёс поллер через MainWindow): перечитать список открытой панели администратора,
    /// сохранив выбор. Панель закрыта или база другая — не трогаем (откроется свежей).</summary>
    public void OnRemoteUserChanges(int dbId)
    {
        if (!AdminPanel.IsVisible || _adminDb is null || _adminDb.DbId != dbId) return;

        RebuildUsers();
        // Выбранная строка могла исчезнуть/смениться — пересинхронизировать блок прав.
        _selectedUser = _selectedUser is null
            ? null
            : _users.FirstOrDefault(u => u.Id == _selectedUser.Id && !u.IsAdmin);
        if (_selectedUser is null) RightsBlock.IsVisible = false;
        else if (RightsBlock.IsVisible) LoadChecks();
    }

    // Загрузить выбранную базу: пароль, автоочистка, пользователи; блок прав сворачивается.
    private void LoadAdminDb()
    {
        var i = AdminDbCombo.SelectedIndex;
        _adminDb = i >= 0 && i < _adminDbs.Count ? _adminDbs[i] : null;
        _selectedUser = null;
        RightsBlock.IsVisible = false;
        if (_adminDb is null) return;

        RefreshPasswordRow();
        RefreshRetentionRow();
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

    // ── Очистка журнала ────────────────────────────────────────────────────────

    // Программное выставление тумблера/периода не должно писать конфиг в БД.
    private bool _suppressRetention;

    // Отразить сохранённую в базе политику автоочистки (RetentionEnabled/Months).
    private void RefreshRetentionRow()
    {
        if (_adminDb is null) return;
        _suppressRetention = true;
        RetentionToggle.IsChecked = _adminDb.Permissions.RetentionEnabled;
        var months = _adminDb.Permissions.RetentionMonths.ToString();
        var idx = -1;
        for (var i = 0; i < RetentionPeriodCombo.Items.Count; i++)
            if ((RetentionPeriodCombo.Items[i] as ComboBoxItem)?.Tag as string == months) { idx = i; break; }
        RetentionPeriodCombo.SelectedIndex = idx >= 0 ? idx : 2;   // 12 месяцев по умолчанию
        _suppressRetention = false;
    }

    private void OnRetentionToggled(object? sender, RoutedEventArgs e) => SaveRetention();
    private void OnRetentionPeriodChanged(object? sender, SelectionChangedEventArgs e) => SaveRetention();

    private void SaveRetention()
    {
        if (_suppressRetention || _adminDb is null) return;
        var enabled = RetentionToggle.IsChecked == true;
        var months  = int.TryParse((RetentionPeriodCombo.SelectedItem as ComboBoxItem)?.Tag as string, out var m)
            ? m : PermissionService.DefaultRetentionMonths;
        _adminDb.Permissions.SetChangeLogRetention(enabled, months);
    }

    private async void OnPurgeChangeLog(object? sender, RoutedEventArgs e)
    {
        if (_adminDb is null || TopLevel.GetTopLevel(this) is not Window owner) return;

        var tag = (PurgePeriodCombo.SelectedItem as ComboBoxItem)?.Tag as string;
        if (tag is null)
        {
            await ConfirmDialog.Info("Выберите период очистки.").ShowDialog(owner);
            return;
        }

        DateTime? cutoff;
        string    periodText;
        if (tag == "all") { cutoff = null; periodText = "весь журнал"; }
        else
        {
            var months = int.Parse(tag);
            cutoff = DateTime.UtcNow.AddMonths(-months);
            periodText = $"записи старше {months} мес.";
        }

        var confirmed = await new ConfirmDialog(
                $"Очистить {periodText} в истории изменений выбранной базы данных? " +
                "Это действие нельзя отменить.", confirmText: "Очистить")
            .ShowDialog<bool>(owner);
        if (!confirmed) return;

        int deleted;
        try { deleted = _adminDb.Constructor.PurgeChangeLog(cutoff); }
        catch (Exception ex) when (DbErrors.Handle(ex, _adminDb.Constructor.DbPath)) { return; }

        await ConfirmDialog.Info($"Удалено записей журнала: {deleted:N0}.").ShowDialog(owner);

        // Открыта история этой базы — перечитать, чтобы удалённое исчезло из таблицы.
        if (HistoryPanel.IsVisible && ReferenceEquals(_historyDb, _adminDb))
            ReloadHistoryRows();
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
            CornerRadius = Radius("RadiusBase", 6),
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

    // ── Хелперы ресурсов (fallback — как в SidebarRow) ────────────────────────

    private static IBrush Brush(string key, string fallbackHex) =>
        Application.Current!.TryFindResource(key, out var res) && res is IBrush b
            ? b
            : new SolidColorBrush(Color.Parse(fallbackHex));

    private static double FontSize(string key, double fallback) =>
        Application.Current!.TryFindResource(key, out var res) && res is double d ? d : fallback;

    // Скругление из токена Radius* (fallback — как в Brush/FontSize).
    private static CornerRadius Radius(string key, double fallback) =>
        Application.Current!.TryFindResource(key, out var res) && res is CornerRadius r
            ? r : new CornerRadius(fallback);
}
