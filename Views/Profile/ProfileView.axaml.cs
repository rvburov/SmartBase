using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using SmartBase.Controls.Dialogs;
using SmartBase.Database;
using SmartBase.Database.Repositories;
using SmartBase.Settings;
using SmartBase.Utils;

namespace SmartBase.Views.Profile;

/// <summary>
/// Модуль «Профиль»: общие данные пользователя (имя компьютера — автоматически, ФИО,
/// подразделение и должность — вручную и по желанию),
/// панель администратора (допуск и права пользователей, пароль базы) и история изменений.
/// Разделы переключает сайдбар (SidebarList в MainWindow) через <see cref="ShowCategory"/> —
/// зеркало SettingsView. Данные админ-панели перечитываются при каждом открытии раздела.
/// </summary>
public partial class ProfileView : UserControl
{
    private Workspace? _workspace;

    // Открытый раздел (ключ ProfileRegistry) — панель пользователей по нему понимает,
    // куда возвращаться при закрытии.
    private string _categoryKey = "general";

    // ── Состояние панели администратора ──────────────────────────────────────
    // Пользователи, роли и права выбранной базы живут в отдельной панели, которая
    // открывается на месте этой, — ProfileAdminUsers.cs (тот же partial-класс).
    private List<DatabaseManager> _adminDbs = [];
    private DatabaseManager? _adminDb;

    public ProfileView()
    {
        InitializeComponent();

        // ── Общие данные ─────────────────────────────────────────────────────
        MachineNameText.Text = Environment.MachineName;
        UserKeyText.Text     = CurrentUser.Key;

        LastNameBox.Text   = CurrentUser.LastName;
        FirstNameBox.Text  = CurrentUser.FirstName;
        MiddleNameBox.Text = CurrentUser.MiddleName;
        DepartmentBox.Text = CurrentUser.Department;
        PositionBox.Text   = CurrentUser.Position;
        DisplayNameText.Text = CurrentUser.DisplayName;

        // Фиксация полей профиля не должна зависеть от LostFocus (клик по пустому месту
        // фокус не забирает — ввод терялся бы молча): каждая буква сразу в app.db, Enter —
        // полная фиксация, клик мимо полей снимает фокус (LostFocus дозакрепляет).
        foreach (var box in new[] { LastNameBox, FirstNameBox, MiddleNameBox, DepartmentBox, PositionBox })
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

        // Выбор базы журнала живет в навбаре (правый край «Истории изменений»).
        HeaderBar.HistoryDbChanged      += (_, _) => OnHistoryDbChanged();
        HeaderBar.ExportHistoryRequested += (_, _) => _ = ExportHistoryToCsv();
    }

    /// <summary>Рабочее пространство для панелей администратора и истории.</summary>
    public void Initialize(Workspace workspace) => _workspace = workspace;

    /// <summary>Показать раздел профиля по ключу из ProfileRegistry.</summary>
    public void ShowCategory(string key)
    {
        // Уход из «Общих данных» с полем в фокусе — тоже точка фиксации ФИО.
        if (GeneralPanel.IsVisible && key != "general") CommitProfile();

        _categoryKey = key;
        GeneralPanel.IsVisible  = key == "general";
        AdminPanel.IsVisible    = key == "admin";
        SecurityPanel.IsVisible = key == "security";
        SecretsPanel.IsVisible  = key == "secrets";
        HistoryPanel.IsVisible  = key == "history";
        // Список пользователей открыт «поверх» раздела и сам не спрячется: выбор раздела
        // в сайдбаре (в том числе повторный выбор «Панели администратора») возвращает
        // обычный вид с навбаром. То же с контуром безопасности и его журналом.
        CloseUsersPanel();
        CloseSecurityPanels();
        // Показанный секрет живет, пока человек на его экране: уйти и вернуться через час
        // к открытому паролю — то же, что оставить его на бумажке.
        if (key != "secrets") _secrets?.Forget();
        // Детали строки журнала живут в отдельной колонке окна и разделом не скрываются —
        // закрываем их вместе с уходом из «Истории изменений».
        CloseHistoryDetail();
        HeaderBar.Title = (ProfileRegistry.FindByKey(key) ?? ProfileRegistry.All[0]).Label;
        // Действия истории (выбор базы, очистка журнала) — только для истории.
        HeaderBar.ShowHistoryActions = key == "history";

        if (key == "admin")    RebuildAdminPanel();
        if (key == "security") ShowSecurityContour();
        if (key == "secrets")  ShowSecrets();
        if (key == "history")  RebuildHistoryPanel();
    }

    // ── Поля профиля (ФИО, подразделение, должность) ──────────────────────────
    //
    // Все — по желанию: пустое значение = «не задано» (ключ из app.db удаляется).
    // Два уровня. Каждая буква (TextChanged) — сразу в app.db через CurrentUser:
    // закрытие приложения без расфокуса ничего не теряет. Точка фиксации
    // (расфокус/Enter/уход из раздела) — нормализация полей + снапшот ИМЕНИ в db_users
    // всех смонтированных баз с событием user.rename: соседи видят новое имя сразу,
    // не «при следующем запуске». Подразделение и должность в базы не уезжают —
    // они справочные, на идентичность и права не влияют.
    private void SaveProfileFields()
    {
        CurrentUser.LastName   = LastNameBox.Text   ?? "";
        CurrentUser.FirstName  = FirstNameBox.Text  ?? "";
        CurrentUser.MiddleName = MiddleNameBox.Text ?? "";
        CurrentUser.Department = DepartmentBox.Text ?? "";
        CurrentUser.Position   = PositionBox.Text   ?? "";
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
        DepartmentBox.Text = CurrentUser.Department;
        PositionBox.Text   = CurrentUser.Position;
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
        // Администрировать больше нечего (последнюю базу размонтировали или админство
        // отозвали) — открытые панели не должны остаться висеть.
        if (!hasAny) { CloseUsersPanel(); CloseSecurityPanels(); return; }

        // Сохранить выбранную базу, если она еще в списке.
        var keepDbId = _adminDb?.DbId;
        AdminDbCombo.ItemsSource = _adminDbs
            .Select(d => string.IsNullOrWhiteSpace(d.Name) ? "База данных" : d.Name)
            .ToList();
        var idx = keepDbId is int id ? _adminDbs.FindIndex(d => d.DbId == id) : -1;
        AdminDbCombo.SelectedIndex = idx >= 0 ? idx : 0;   // SelectionChanged → LoadAdminDb
    }

    private void OnAdminDbChanged(object? sender, SelectionChangedEventArgs e) => LoadAdminDb();

    /// <summary>Чужие события пользователей этой базы (присоединение, допуск, права, роль —
    /// принес поллер через MainWindow): перечитать открытую панель пользователей, сохранив
    /// выбранную область, и список баз истории — допуск могли отозвать или вернуть. Панель
    /// закрыта или база ее не касается — не трогаем (откроется свежей).</summary>
    public void OnRemoteUserChanges(int dbId)
    {
        RefreshHistoryAccess();

        // Панель пользователей открыта ВМЕСТО панели администратора, и та в это время скрыта, —
        // смотреть надо на саму панель. В области «все базы» ее касается любая из админских баз.
        if (!UsersPanelOpen || _adminDb is null) return;
        if (_adminDb.DbId == dbId || (AcrossDatabases && _adminDbs.Any(d => d.DbId == dbId)))
            RefreshUsersPanel();
    }

    /// <summary>
    /// Набор смонтированных баз или доступ к ним поменялся (MainWindow.SyncMountedToUi): открытые
    /// разделы следуют за ним сразу — история показывает только базы с допуском, панель
    /// администратора — только базы, где вы администратор.
    /// </summary>
    public void OnDatabasesChanged()
    {
        RefreshHistoryAccess();

        if (!AdminPanel.IsVisible && !UsersPanelOpen) return;
        var admins = _workspace?.Mounted.Where(m => m.Permissions.IsAdmin).ToList() ?? [];
        if (admins.SequenceEqual(_adminDbs)) return;
        CloseUsersPanel();
        RebuildAdminPanel();
    }

    // Загрузить выбранную базу: пароль, автоочистка. Панель пользователей относится
    // к конкретной базе — при смене базы закрывается (открывается заново кнопкой).
    private void LoadAdminDb()
    {
        var i = AdminDbCombo.SelectedIndex;
        _adminDb = i >= 0 && i < _adminDbs.Count ? _adminDbs[i] : null;
        CloseUsersPanel();
        if (_adminDb is null) return;

        RefreshPasswordRow();
        RefreshRetentionRow();
    }

    // Программное выставление тумблера доступа по паролю не должно писать политику в базу.
    private bool _suppressPasswordAccess;

    private void RefreshPasswordRow()
    {
        if (_adminDb is null) return;
        var perms = _adminDb.Permissions;
        var has   = perms.HasPassword;
        PasswordStateText.Text     = has ? "Пароль установлен." : "Пароль не установлен.";
        PasswordSetBtn.Content     = has ? "Сменить пароль" : "Установить пароль";
        PasswordRemoveBtn.IsVisible = has;

        // Тумблер показывает, открывает ли замок пароль сейчас: без пароля открывать нечем, даже
        // если вход паролем не выключали.
        _suppressPasswordAccess = true;
        PasswordAccessToggle.IsChecked = perms.PasswordAccess;
        _suppressPasswordAccess = false;
    }

    // «Доступ других пользователей по паролю». Выключение пароль не удаляет — включили обратно, и
    // прежний пароль снова работает. Включение без пароля — сначала придумать пароль: доступ по
    // паролю без пароля никого бы не пустил. Отказались от окна — тумблер возвращается.
    private async void OnPasswordAccessToggled(object? sender, RoutedEventArgs e)
    {
        if (_suppressPasswordAccess || _adminDb is not { } db || TopLevel.GetTopLevel(this) is not Window owner)
            return;

        var on = PasswordAccessToggle.IsChecked == true;

        if (on && !db.Permissions.HasPassword)
        {
            var name     = string.IsNullOrWhiteSpace(db.Name) ? "База данных" : db.Name;
            var password = await PasswordDialog.ForSet(name, hasPassword: false).ShowDialog<string?>(owner);

            // Пока окно было открыто, в панели могли выбрать другую базу — пароль чужой базе не ставим.
            if (string.IsNullOrEmpty(password) || !ReferenceEquals(db, _adminDb))
            {
                RefreshPasswordRow();
                return;
            }
            if (!RunAdminAction(() => db.Permissions.SetPassword(password))) return;
        }

        if (RunAdminAction(() => db.Permissions.SetPasswordAccess(on))) RefreshPasswordRow();
    }

    // ── Очистка журнала ────────────────────────────────────────────────────────

    // Программное выставление тумблера/периода не должно писать конфиг в БД.
    private bool _suppressRetention;

    // Отразить сохраненную в базе политику автоочистки (RetentionEnabled/Months).
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

        // Открыта история этой базы — перечитать, чтобы удаленное исчезло из таблицы.
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

    // ── Хелперы ресурсов (fallback — как в SidebarRow) ────────────────────────
    // Используются и панелью пользователей (ProfileAdminUsers.cs — тот же класс).

    private static IBrush Brush(string key, string fallbackHex) =>
        Application.Current!.TryFindResource(key, out var res) && res is IBrush b
            ? b
            : new SolidColorBrush(Color.Parse(fallbackHex));

    // Имя FontSizeToken, а не FontSize: короткое имя скрыло бы унаследованное
    // TemplatedControl.FontSize (CS0108).
    private static double FontSizeToken(string key, double fallback) =>
        Application.Current!.TryFindResource(key, out var res) && res is double d ? d : fallback;

    private static FontWeight WeightToken(string key, FontWeight fallback) =>
        Application.Current!.TryFindResource(key, out var res) && res is FontWeight w ? w : fallback;
}
