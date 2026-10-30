using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using SmartBase.Controls.Components;
using SmartBase.Controls.Dialogs;
using SmartBase.Database;
using SmartBase.Settings;
using SmartBase.Utils;

namespace SmartBase.Views.Profile;

/// <summary>Общие данные пользователя и история изменений открытых баз.</summary>
public partial class ProfileView : UserControl
{
    private Workspace? _workspace;

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

        HeaderBar.HistoryDbChanged += (_, _) => OnHistoryDbChanged();
        HeaderBar.ExportRequested += (_, _) => _ = ExportHistoryToCsv();
    }

    public void Initialize(Workspace workspace) => _workspace = workspace;

    /// <summary>Показать раздел профиля по ключу из ProfileRegistry.</summary>
    public void ShowCategory(string key)
    {
        var section = ProfileRegistry.FindByKey(key) ?? ProfileRegistry.All[0];
        using var slow = Logging.Diag.Measure($"открытие профиля «{section.Label}»", Logging.Diag.UiOpen);
        if (GeneralPanel.IsVisible && section.Key != "general") CommitProfile();
        GeneralPanel.IsVisible = section.Key == "general";
        HistoryPanel.IsVisible = section.Key == "history";
        CloseHistoryDetail();
        HeaderBar.Title = section.Label;
        HeaderBar.ShowSection(section.Key);
        if (section.Key == "history") RebuildHistoryPanel();
    }

    /// <summary>Открытая история следует за подключением и отключением баз.</summary>
    public void OnDatabasesChanged() => RefreshHistoryDatabases();

    // ── Поля профиля (ФИО, подразделение, должность) ──────────────────────────
    //
    // Все — по желанию: пустое значение = «не задано» (ключ из app.db удаляется).
    // Два уровня. Каждая буква (TextChanged) — сразу в app.db через CurrentUser:
    // закрытие приложения без расфокуса ничего не теряет. Точка фиксации
    // (расфокус/Enter/уход из раздела) — нормализация полей + снапшот ИМЕНИ в db_users
    // всех смонтированных баз с событием user.rename: соседи видят новое имя сразу,
    // не «при следующем запуске». Подразделение и должность в базы не уезжают —
    // они справочные и на идентичность не влияют.
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
            db.Users.SyncDisplayName();
    }

    private static NavbarDb DbChoice(DatabaseManager db) =>
        new(string.IsNullOrWhiteSpace(db.Name) ? "База данных" : db.Name,
            Application.Current!.TryFindResource(db.Provider == "postgres" ? "IconServer" : "IconDatabase", out var icon)
                ? icon as Geometry : null);

    // ── Очистка журнала ────────────────────────────────────────────────────────

    // Программное выставление тумблера/периода не должно писать конфиг в БД.
    private bool _suppressRetention;

    // Отразить сохраненную в базе настройку автоочистки. Выключенная
    // автоочистка — это «Бессрочно», отдельного переключателя у нее нет: срок и его отсутствие —
    // значения одного и того же выбора.
    private void RefreshRetentionRow()
    {
        HistoryMaintenance.IsVisible = _historyDb is not null;
        if (_historyDb is null) return;
        _suppressRetention = true;
        try
        {
            var retention = _historyDb.Constructor.GetChangeLogRetention();
            var months = retention.Enabled
                ? retention.Months.ToString(System.Globalization.CultureInfo.InvariantCulture)
                : "0";
            var idx = -1;
            for (var i = 0; i < RetentionPeriodCombo.Items.Count; i++)
                if ((RetentionPeriodCombo.Items[i] as ComboBoxItem)?.Tag as string == months) { idx = i; break; }
            RetentionPeriodCombo.SelectedIndex = idx >= 0 ? idx : 0;
        }
        catch (Exception ex) when (DbErrors.Handle(ex, _historyDb.Constructor.DbPath))
        {
            HistoryMaintenance.IsVisible = false;
        }
        finally { _suppressRetention = false; }
    }

    private void OnRetentionPeriodChanged(object? sender, SelectionChangedEventArgs e) => SaveRetention();

    private void SaveRetention()
    {
        if (_suppressRetention || _historyDb is null) return;

        var months = int.TryParse((RetentionPeriodCombo.SelectedItem as ComboBoxItem)?.Tag as string,
                                  out var m) ? m : 0;

        // «Бессрочно» не забывает прежний срок: в базу уходит выключенная автоочистка с последним
        // назначенным числом месяцев, и вернувшийся к сроку пользователь видит свое значение, а
        // не подставленное программой.
        try
        {
            var previous = _historyDb.Constructor.GetChangeLogRetention();
            _historyDb.Constructor.SetChangeLogRetention(
                months > 0, months > 0 ? months : previous.Months);
        }
        catch (Exception ex) when (DbErrors.Handle(ex, _historyDb.Constructor.DbPath))
        {
            RefreshRetentionRow();
        }
    }

    private async void OnPurgeChangeLog(object? sender, RoutedEventArgs e)
    {
        if (_historyDb is not { } selected || TopLevel.GetTopLevel(this) is not Window owner) return;

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

        var dbName = string.IsNullOrWhiteSpace(selected.Name) ? "База данных" : selected.Name;

        var confirmed = await new ConfirmDialog(
                $"Очистить {periodText} базы «{dbName}»?\n\nОтменить это нельзя.",
                confirmText: "Очистить")
            .ShowDialog<bool>(owner);
        if (!confirmed) return;

        int deleted;
        try { deleted = selected.Constructor.PurgeChangeLog(cutoff); }
        catch (Exception ex) when (DbErrors.Handle(ex, selected.Constructor.DbPath)) { return; }

        await ConfirmDialog.Info($"Удалено записей истории изменений: {deleted:N0}.").ShowDialog(owner);

        // Открыта история этой базы — перечитать, чтобы удаленное исчезло из таблицы.
        if (HistoryPanel.IsVisible && ReferenceEquals(_historyDb, selected))
            ReloadHistoryRows();
    }

}
