using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using SmartBase.Controls.Components;
using SmartBase.Controls.Dialogs;
using SmartBase.Database;
using SmartBase.Database.Entities;
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

        // Выбор базы живет в навбаре: у «Истории изменений» свой список, у панели
        // администратора свой — разделы никогда не видны вместе.
        HeaderBar.HistoryDbChanged += (_, _) => OnHistoryDbChanged();
        HeaderBar.AdminDbChanged   += (_, _) => LoadAdminDb();

        // Выгрузка в CSV — одна кнопка навбара на два раздела: что выгружать, решает открытый.
        HeaderBar.ExportRequested += (_, _) =>
        {
            if (_categoryKey == "history")          _ = ExportHistoryToCsv();
            else if (_categoryKey == "securitylog") _ = ExportSecurityLogToCsv();
        };

        HeaderBar.ChainCheckRequested += (_, _) => VerifySecurityChain();

        // Меню «Добавить пароль» живет в навбаре (правый край «Менеджера паролей»), а окна и права
        // — у самой описи: навбар только передает выбранный пункт.
        HeaderBar.AddConnectionRequested += (_, _) => _ = _secrets?.AddConnectionAsync();
        HeaderBar.AddTokenRequested      += (_, _) => _ = _secrets?.AddTokenAsync();
        HeaderBar.MasterHashRequested    += (_, _) => _ = _secrets?.MasterHashAsync();
    }

    /// <summary>Рабочее пространство для панелей администратора и истории.</summary>
    public void Initialize(Workspace workspace) => _workspace = workspace;

    /// <summary>Показать раздел профиля по ключу из ProfileRegistry.</summary>
    public void ShowCategory(string key)
    {
        // Уход из «Общих данных» с полем в фокусе — тоже точка фиксации ФИО.
        if (GeneralPanel.IsVisible && key != "general") CommitProfile();

        _categoryKey = key;
        GeneralPanel.IsVisible     = key == "general";
        AdminPanel.IsVisible       = key == "admin";
        SecurityPanel.IsVisible    = key == "security";
        SecretsPanel.IsVisible     = key == "secrets";
        HistoryPanel.IsVisible     = key == "history";
        SecurityLogPanel.IsVisible = key == "securitylog";
        // Список пользователей открыт «поверх» раздела и сам не спрячется: выбор раздела
        // в сайдбаре (в том числе повторный выбор «Панели администратора») возвращает
        // обычный вид с навбаром.
        CloseUsersPanel();
        // Детали строки живут в отдельной колонке окна и разделом не скрываются — закрываем
        // их вместе с уходом из раздела, которому они принадлежат.
        CloseHistoryDetail();
        CloseLogDetail();
        HeaderBar.Title = (ProfileRegistry.FindByKey(key) ?? ProfileRegistry.All[0]).Label;
        // Действия навбара расставляются по открытому разделу одним местом.
        HeaderBar.ShowSection(key);
        // «Добавить пароль» — только в «Менеджере паролей»; в нем самом видимость уточнит
        // сборка списка (ShowSecrets → SyncSecretsActions).
        SyncSecretsActions();

        if (key == "admin")       RebuildAdminPanel();
        if (key == "security")    ShowSecurityContour();
        if (key == "secrets")     ShowSecrets();
        if (key == "history")     RebuildHistoryPanel();
        if (key == "securitylog") RebuildSecurityLog();
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

    /// <summary>Пункт списка баз: имя и иконка рода хранения — сервер у базы на сервере,
    /// цилиндр у базы-файла. Один источник на навбар и на шапку таблицы пользователей.</summary>
    private static NavbarDb DbChoice(DatabaseManager db) =>
        new(string.IsNullOrWhiteSpace(db.Name) ? "База данных" : db.Name,
            UsersIcon(db.Provider == "postgres" ? "IconServer" : "IconDatabase"));

    // Пересобрать панель: список баз, где текущий пользователь — администратор.
    private void RebuildAdminPanel()
    {
        _adminDbs = _workspace?.Mounted.Where(m => m.Permissions.IsAdmin).ToList() ?? [];

        var hasAny = _adminDbs.Count > 0;
        AdminNoAccessRow.IsVisible = !hasAny;
        AdminContent.IsVisible     = hasAny;
        // Администрировать больше нечего (последнюю базу размонтировали или админство
        // отозвали) — открытая панель пользователей не должна остаться висеть.
        if (!hasAny) { HeaderBar.SetAdminDbs([], -1); CloseUsersPanel(); return; }

        // Список баз — в навбаре, как у «Истории изменений»: выбор области стоит в одном месте
        // на все разделы, а не строкой среди настроек, которыми он же и управляет.
        var keepDbId = _adminDb?.DbId;
        var names = _adminDbs.Select(DbChoice).ToList();
        var idx = keepDbId is int id ? _adminDbs.FindIndex(d => d.DbId == id) : -1;
        HeaderBar.SetAdminDbs(names, idx >= 0 ? idx : 0);
        LoadAdminDb();
    }

    /// <summary>Чужие события пользователей этой базы (присоединение, допуск, права, роль —
    /// принес поллер через MainWindow): перечитать открытую панель пользователей, сохранив
    /// выбранную область, и список баз истории — допуск могли отозвать или вернуть. Панель
    /// закрыта или база ее не касается — не трогаем (откроется свежей).</summary>
    public void OnRemoteUserChanges(int dbId)
    {
        RefreshHistoryAccess();
        // Сосед мог выдать или отозвать админство — опись показывает только свое.
        RefreshSecrets();

        // Строка «Пользователи» показывает, сколько людей ждут допуска: сосед присоединился к базе
        // — число обязано измениться сразу, а не при следующем заходе в раздел.
        if (AdminPanel.IsVisible && _adminDb?.DbId == dbId) RefreshUsersRow();

        // Панель пользователей открыта ВМЕСТО панели администратора, и та в это время скрыта, —
        // смотреть надо на саму панель. Таблица показывает одну базу: правки соседних ее не
        // касаются.
        if (!UsersPanelOpen || _adminDb is null) return;
        if (_adminDb.DbId == dbId) RefreshUsersPanel();
    }

    /// <summary>
    /// Набор смонтированных баз или доступ к ним поменялся (MainWindow.SyncMountedToUi): открытые
    /// разделы следуют за ним сразу — история показывает только базы с допуском, панель
    /// администратора — только базы, где вы администратор.
    /// </summary>
    public void OnDatabasesChanged()
    {
        RefreshHistoryAccess();
        // Опись — только открытые базы: открылась, закрылась, стала сжиматься — список за ней.
        RefreshSecrets();

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
        var i = HeaderBar.AdminDbIndex;
        _adminDb = i >= 0 && i < _adminDbs.Count ? _adminDbs[i] : null;
        CloseUsersPanel();
        if (_adminDb is null) return;

        RefreshPasswordRow();
        RefreshRetentionRow();
        RefreshUsersRow();
    }

    // Программное выставление способа входа не должно писать политику в базу.
    private bool _suppressAccessMode;

    private void RefreshPasswordRow()
    {
        if (_adminDb is null) return;
        var perms = _adminDb.Permissions;
        var has   = perms.HasPassword;

        PasswordStateText.Text       = has ? "установлен" : "не установлен";
        PasswordStateText.Foreground = Brush(has ? "BrushGreen" : "BrushSecondaryText",
                                             has ? "#34C759" : "#8E8E93");

        // Пояснение говорит о том, что с паролем происходит СЕЙЧАС: заданный пароль при входе по
        // подтверждению хранится, но никого не пускает, и молчать об этом нельзя — иначе строка
        // «установлен» обещает больше, чем есть.
        PasswordHintText.Text = !has
            ? "Требуется для входа по паролю."
            : perms.PasswordAccess
                ? "Новый пользователь вводит его и получает допуск сам. В базе хранится хэш."
                : "Не применяется: выбран вход по подтверждению администратора.";

        PasswordSetBtn.Content      = has ? "Сменить пароль" : "Установить пароль";
        PasswordRemoveBtn.IsVisible = has;

        // Список показывает, чем замок открывается СЕЙЧАС: без пароля открывать нечем, даже если
        // вход по паролю не переключали.
        _suppressAccessMode = true;
        AccessModeCombo.SelectedIndex = perms.PasswordAccess ? 1 : 0;
        _suppressAccessMode = false;
    }

    // Сколько людей в базе и сколько ждут допуска — в самой строке: за этим в панель и заходят.
    private void RefreshUsersRow()
    {
        if (_adminDb is null) return;

        List<DbUserEntity> users;
        try { users = _adminDb.Permissions.GetUsers(); }
        catch (Exception error)
        {
            // Файл базы сейчас недоступен (сетевую папку отключили): строка не врет числом, а
            // молчит — таблица пользователей скажет о том же подробнее.
            Serilog.Log.Warning(error, "Пользователи базы {Db:l} не сосчитались", _adminDb.Name);
            UsersCountText.Text = "";
            return;
        }

        // Ждут только те, по кому решения еще не было: отозванный допуск — это решение,
        // и звать администратора разбираться с ним повторно незачем.
        var pending = users.Count(u => !u.IsAdmin && !u.IsApproved && !u.AccessDecided);

        UsersCountText.Text = Plural(users.Count, "пользователь", "пользователя", "пользователей")
                            + (pending > 0
                                ? $" · {pending} {(pending == 1 ? "ждет" : "ждут")} допуска"
                                : "");
        UsersCountText.Foreground = Brush(pending > 0 ? "BrushRed" : "BrushSecondaryText",
                                          pending > 0 ? "#FF3B30" : "#8E8E93");
    }

    private static string Plural(int n, string one, string few, string many)
    {
        var a = n % 10;
        var b = n % 100;
        var word = a == 1 && b != 11 ? one
                 : a >= 2 && a <= 4 && (b < 12 || b > 14) ? few
                 : many;
        return $"{n} {word}";
    }

    // «Способ входа нового пользователя». Переключение на подтверждение администратора пароль не
    // удаляет — вернули вход по паролю, и прежний пароль снова работает. Вход по паролю без
    // пароля никого бы не пустил, поэтому сначала окно установки пароля; отказались от окна —
    // список возвращается к прежнему значению.
    private async void OnAccessModeChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_suppressAccessMode || _adminDb is not { } db || TopLevel.GetTopLevel(this) is not Window owner)
            return;

        var byPassword = AccessModeCombo.SelectedIndex == 1;

        if (byPassword && !db.Permissions.HasPassword)
        {
            var name     = string.IsNullOrWhiteSpace(db.Name) ? "База данных" : db.Name;
            var password = await PasswordDialog.ForSet(name, hasPassword: false).ShowDialog<string?>(owner);

            // Пока окно было открыто, в навбаре могли выбрать другую базу — пароль чужой базе не ставим.
            if (string.IsNullOrEmpty(password) || !ReferenceEquals(db, _adminDb))
            {
                RefreshPasswordRow();
                return;
            }
            if (!RunAdminAction(() => db.Permissions.SetPassword(password))) return;
        }

        if (RunAdminAction(() => db.Permissions.SetPasswordAccess(byPassword))) RefreshPasswordRow();
    }

    // ── Очистка журнала ────────────────────────────────────────────────────────

    // Программное выставление тумблера/периода не должно писать конфиг в БД.
    private bool _suppressRetention;

    // Отразить сохраненную в базе политику автоочистки (RetentionEnabled/Months). Выключенная
    // автоочистка — это «Бессрочно», отдельного переключателя у нее нет: срок и его отсутствие —
    // значения одного и того же выбора.
    private void RefreshRetentionRow()
    {
        if (_adminDb is null) return;
        _suppressRetention = true;

        var months = _adminDb.Permissions.RetentionEnabled
            ? _adminDb.Permissions.RetentionMonths.ToString(System.Globalization.CultureInfo.InvariantCulture)
            : "0";

        var idx = -1;
        for (var i = 0; i < RetentionPeriodCombo.Items.Count; i++)
            if ((RetentionPeriodCombo.Items[i] as ComboBoxItem)?.Tag as string == months) { idx = i; break; }
        RetentionPeriodCombo.SelectedIndex = idx >= 0 ? idx : 0;   // «Бессрочно» по умолчанию
        _suppressRetention = false;
    }

    private void OnRetentionPeriodChanged(object? sender, SelectionChangedEventArgs e) => SaveRetention();

    private void SaveRetention()
    {
        if (_suppressRetention || _adminDb is null) return;

        var months = int.TryParse((RetentionPeriodCombo.SelectedItem as ComboBoxItem)?.Tag as string,
                                  out var m) ? m : 0;

        // «Бессрочно» не забывает прежний срок: в базу уходит выключенная автоочистка с последним
        // назначенным числом месяцев, и вернувшийся к сроку администратор видит свое значение, а
        // не подставленное программой.
        _adminDb.Permissions.SetChangeLogRetention(
            months > 0,
            months > 0 ? months : _adminDb.Permissions.RetentionMonths);
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
        if (tag == "all") { cutoff = null; periodText = "всю историю изменений"; }
        else
        {
            var months = int.Parse(tag);
            cutoff = DateTime.UtcNow.AddMonths(-months);
            periodText = $"записи старше {months} мес.";
        }

        var dbName = string.IsNullOrWhiteSpace(_adminDb.Name) ? "База данных" : _adminDb.Name;

        var confirmed = await new ConfirmDialog(
                $"Очистить {periodText} базы «{dbName}»?\n\nОтменить это нельзя.",
                confirmText: "Очистить")
            .ShowDialog<bool>(owner);
        if (!confirmed) return;

        int deleted;
        try { deleted = _adminDb.Constructor.PurgeChangeLog(cutoff); }
        catch (Exception ex) when (DbErrors.Handle(ex, _adminDb.Constructor.DbPath)) { return; }

        await ConfirmDialog.Info($"Удалено записей истории изменений: {deleted:N0}.").ShowDialog(owner);

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
        if (_adminDb is not { } db || TopLevel.GetTopLevel(this) is not Window owner) return;
        var name = string.IsNullOrWhiteSpace(db.Name) ? "База данных" : db.Name;
        // Вопрос общий с менеджером паролей: при выключенном входе по паролю он не обещает, что
        // пользователи «больше не смогут открыть доступ сами», — они и так не могут.
        var confirmed = await new ConfirmDialog(
                SecretsInventory.RemovePasswordQuestion(name, db.Permissions.PasswordAccessEnabled),
                confirmText: "Убрать")
            .ShowDialog<bool>(owner);
        // Пока окно было открыто, в панели могли выбрать другую базу — чужой базе пароль не снимаем.
        if (!confirmed || !ReferenceEquals(db, _adminDb)) return;

        if (RunAdminAction(() => db.Permissions.RemovePassword())) RefreshPasswordRow();
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
