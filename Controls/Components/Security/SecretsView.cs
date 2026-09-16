using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Media;
using SmartBase.Controls.Dialogs;
using SmartBase.Core.Models;
using SmartBase.Database;
using SmartBase.Integrations;
using SmartBase.Utils;
using static SmartBase.Controls.Widgets.SettingRows;

namespace SmartBase.Controls.Components.Security;

/// <summary>
/// Менеджер паролей — опись секретов машины (docs/ROADMAP-API.md, шаг 16).
///
/// <b>Вид — как «Управление базой данных».</b> Строка на секрет: слева иконка рода, посередине
/// название, состояние и путь, справа иконки-кнопки действий. Строки не раскрываются и больше
/// ничего не показывают: для чего секрет и где он хранится — подсказкой при наведении, а за
/// подробностями (когда выдан токен, было ли обращение) — окно действия. Список, в котором каждая
/// строка разворачивается в полэкрана, отвечает на вопрос «что у нас заведено» хуже списка из
/// коротких строк.
///
/// <b>Это опись, а не просмотрщик.</b> Значения экран не показывает — кроме одного случая, где
/// секрет восстановим по построению (секрет соединения: им подписывается каждый запрос, и
/// программа достает его сама при каждой отправке). Показ идет под мастер-паролем организации,
/// оставляет строку в журнале безопасности и открывается отдельным окном: закрыли окно — значения
/// на экране больше нет, и хранить его здесь не приходится.
///
/// <b>Кто что видит и какие у строки состояния</b> — решает <see cref="SecretsInventory"/>: там
/// эти правила проверяют тесты. Здесь только выборка данных, рисование и действия.
/// </summary>
public sealed class SecretsView : UserControl
{
    private readonly StackPanel _rows = new() { Spacing = 0, Margin = new Thickness(16, 0, 16, 32) };

    private Workspace? _workspace;

    public SecretsView() =>
        Content = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content                     = _rows,
        };

    /// <summary>
    /// В списке есть то, что заводит меню «Добавить пароль»: соединения, токены, мастер-пароль
    /// (по последней сборке). По нему хозяин экрана показывает само меню: все три его пункта
    /// спрашивают право на настройку обмена, а кнопка, ведущая туда, куда человека не пустят,
    /// хуже отсутствующей.
    /// </summary>
    public bool ForIntegrations { get; private set; }

    /// <summary>Список перестроен — хозяин экрана сверяет с ним свой навбар.</summary>
    public event EventHandler? Built;

    /// <summary>
    /// База переименована окном настроек базы (шестеренка у пароля сервера PostgreSQL). Имя уже
    /// записано в реестр; переименовать смонтированную базу и перечитать сайдбары — дело главного
    /// окна.
    /// </summary>
    public event EventHandler<(int Id, string Name)>? DatabaseRenamed;

    /// <summary>Показать опись заново — при каждом заходе в раздел и при смене открытых баз.</summary>
    public void Show(Workspace? workspace)
    {
        _workspace = workspace;
        Build();
    }

    private IntegrationsRegistry? Integrations => _workspace?.Registry.Integrations;

    private IReadOnlyList<DatabaseManager> Databases => _workspace?.Mounted ?? [];

    // ── Опись ─────────────────────────────────────────────────────────────────

    private void Build()
    {
        var rows = _rows.Children;
        rows.Clear();

        var listing = SecretsInventory.Build(Collect(), DateTimeOffset.Now);

        ForIntegrations = listing.Integrations;

        // Над списком ничего нет: ни пояснения, ни сводки. Строки говорят сами за себя, а
        // объяснение каждой — в ее подсказке.
        if (!listing.Admin)
            rows.Add(Widgets.SettingRows.Row(SecretsInventory.NoAccessTitle,
                                             SecretsInventory.NoAccessText));
        else
            foreach (var entry in listing.Entries)
                Add(rows, Row(entry, entry.Status, ToneBrush(entry.Tone), Buttons(entry)));

        Built?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Собрать то, из чего складывается опись. Решений здесь нет — только чтение: кто что видит,
    /// решает <see cref="SecretsInventory.Build"/>.
    /// </summary>
    private InventoryFacts Collect()
    {
        var connections  = new List<InventoryConnection>();
        var tokens       = new List<InventoryToken>();
        var integrations = CanIntegrations();

        // Без права на настройку обмена соединения и токены не читаются вовсе. Отсеял бы их и
        // сам SecretsInventory.Build, но незачем поднимать в память сведения о секретах, которые
        // человеку в этом сеансе не покажут ни строкой, ни кнопкой.
        if (integrations && Integrations is { } registry)
        {
            var requests = registry.Requests();

            foreach (var connection in registry.Connections())
            {
                // Последнее пользование секретом — последний запуск любого из запросов этого
                // соединения: секрет подписывает каждый из них.
                var last = requests.Where(r => r.ConnectionId == connection.Id)
                                   .Select(r => registry.LastRun(r.Id))
                                   .Max();

                connections.Add(new InventoryConnection(
                    connection.Id, connection.Name, connection.Address,
                    registry.HasSecret(connection.Id), last));
            }

            foreach (var token in registry.Tokens())
                tokens.Add(new InventoryToken(token.Id, token.Name, token.DbId, token.Enabled,
                                              token.Created, token.Used));
        }

        var registered = _workspace?.Registry.GetAll() ?? [];
        var databases  = new List<InventoryDatabase>();

        // Только ОТКРЫТЫЕ базы: у закрытой, недоступной или сжимаемой пароль лежит в файле,
        // который сейчас не читается, и менять его отсюда нечем.
        foreach (var manager in Databases)
        {
            // Базу отключили, пока она монтировалась: записи в реестре уже нет, и главное окно
            // вот-вот ее размонтирует.
            if (registered.FirstOrDefault(d => d.Id == manager.DbId) is not { } entry) continue;

            var permissions = manager.Permissions;

            databases.Add(new InventoryDatabase(
                manager.DbId, NameOf(manager), permissions.IsAdmin, entry.IsPostgres,
                entry.Path, entry.PgHost ?? "", entry.PgPort ?? PgConnectionInfo.DefaultPort,
                entry.PgDatabase ?? "", !string.IsNullOrEmpty(entry.PgPassword),
                permissions.HasPassword, permissions.PasswordAccessEnabled,
                permissions.LastPasswordUnlock));
        }

        var policy = EgressSetup.Last.Set;

        var master = new InventoryMaster(
            policy.HasMaster,
            [.. policy.Sources.Where(p => p.HasMaster).Select(p => p.Source)]);

        return new InventoryFacts(databases, connections, tokens, master, integrations);
    }

    private static string NameOf(DatabaseManager database) =>
        string.IsNullOrWhiteSpace(database.Name) ? "База данных" : database.Name;

    // ── Строка ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Строка секрета: иконка рода, название, состояние (одним цветом), путь и кнопки, прижатые к
    /// правому краю. Статическая и без рабочего пространства — чтобы раскладку можно было померить
    /// безголовым стендом, не открывая ни одной базы.
    /// </summary>
    /// <param name="entry">Строка описи.</param>
    /// <param name="status">Состояние словами.</param>
    /// <param name="statusBrush">Цвет состояния.</param>
    /// <param name="buttons">Только те кнопки, что у строки есть: пустых мест под отсутствующие
    /// нет, и кнопки всех строк стоят у одного края.</param>
    public static Border Row(SecretEntry entry, string status, IBrush statusBrush,
                               IReadOnlyList<Button> buttons)
    {
        var title = new TextBlock
        {
            Classes      = { "title" },
            Text         = entry.Title,
            TextTrimming = TextTrimming.CharacterEllipsis,
            // Прозрачный фон — чтобы курсор ловился по всей строке названия, а не по самим буквам:
            // иначе подсказка выскакивает только между засечками и кажется случайной.
            Background   = Brushes.Transparent,
        };

        var state = new TextBlock
        {
            Classes      = { "desc" },
            Text         = status,
            Foreground   = statusBrush,
            TextWrapping = TextWrapping.NoWrap,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };

        // Путь бывает длиннее строки — обрезается многоточием, а не переносится: строка списка
        // обязана оставаться строкой.
        var where = new TextBlock
        {
            Classes      = { "desc" },
            Text         = entry.Where,
            TextWrapping = TextWrapping.NoWrap,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };

        // Подсказка висит на НАЗВАНИИ, а не на всей строке: наводя курсор на строку, человек чаще
        // всего целится в кнопку, и плашка во весь абзац перекрывала бы их подписи. Название —
        // единственное место строки, по которому не нажимают.
        ToolTip.SetTip(title, SecretsInventory.Tip(entry));

        return ListRow(Icon(KindIcon(entry.Kind), Ink("BrushSecondaryText", "#8E8E93")),
                       [title, state, where], buttons);
    }

    /// <summary>Иконка рода секрета.</summary>
    public static string KindIcon(SecretKind kind) => kind switch
    {
        SecretKind.DatabasePassword => "IconDatabase",
        SecretKind.PgPassword       => "IconServer",
        SecretKind.Connection       => "IconWorkflow",
        SecretKind.RobotToken       => "IconBot",
        _                           => "IconShieldCheck",
    };

    private static IBrush ToneBrush(SecretTone tone) => tone switch
    {
        SecretTone.Good  => Ink("BrushGreen", "#34C759"),
        SecretTone.Alarm => Ink("BrushRed", "#FF3B30"),
        _                => Ink("BrushSecondaryText", "#8E8E93"),
    };

    /// <summary>
    /// Кнопки строки слева направо: шестеренка, ключ, корзина. Ключ и корзина — только там, где
    /// есть что показать и что убрать: корзина у пустого места удаляла бы ничто.
    /// </summary>
    private List<Button> Buttons(SecretEntry entry)
    {
        var id  = entry.Id;
        var set = entry.State == SecretState.Set;

        var buttons = new List<Button>();

        switch (entry.Kind)
        {
            case SecretKind.DatabasePassword:
                buttons.Add(Gear(set ? "Сменить пароль" : "Установить пароль",
                                 () => _ = SetDatabasePasswordAsync(id)));
                if (set) buttons.Add(Trash("Убрать пароль", () => _ = RemoveDatabasePasswordAsync(id)));
                break;

            case SecretKind.PgPassword:
                // Корзины нет: без пароля сервер пускает только по доверию к машине, и «убрать
                // пароль» здесь значило бы молча сломать подключение.
                buttons.Add(Gear("Настройки базы данных", () => _ = ServerSettingsAsync(id)));
                break;

            case SecretKind.Connection:
                buttons.Add(Gear("Настроить соединение", () => _ = EditConnectionAsync(id)));
                if (set)
                {
                    buttons.Add(IconButton("IconKey", "Показать секрет", () => _ = RevealAsync(id)));
                    buttons.Add(Trash("Удалить секрет", () => _ = ForgetConnectionAsync(id)));
                }
                break;

            case SecretKind.RobotToken:
                buttons.Add(Gear("Настроить токен", () => _ = TokenSettingsAsync(id)));
                buttons.Add(Trash("Отозвать токен", () => _ = RemoveTokenAsync(id)));
                break;

            default:
                buttons.Add(Gear("Мастер-пароль для политики", () => _ = MasterHashAsync()));
                break;
        }

        return buttons;
    }

    private static Button Gear(string tip, Action click) => IconButton("IconSettings", tip, click);

    private static Button Trash(string tip, Action click) =>
        IconButton("IconTrash", tip, click, red: true);

    // ── Пароль базы ───────────────────────────────────────────────────────────

    private async Task SetDatabasePasswordAsync(int dbId)
    {
        if (VisualRoot is not Window win) return;
        if (await AdminDatabaseAsync(win, dbId) is not { } db) return;

        var password = await PasswordDialog.ForSet(NameOf(db), db.Permissions.HasPassword)
                                           .ShowDialog<string?>(win);

        if (!string.IsNullOrEmpty(password))
            await RunAdminAsync(win, db, () => db.Permissions.SetPassword(password));

        Build();
    }

    private async Task RemoveDatabasePasswordAsync(int dbId)
    {
        if (VisualRoot is not Window win) return;
        if (await AdminDatabaseAsync(win, dbId) is not { } db) return;

        var yes = await new ConfirmDialog(
                SecretsInventory.RemovePasswordQuestion(NameOf(db), db.Permissions.PasswordAccessEnabled),
                confirmText: "Убрать")
            .ShowDialog<bool>(win);

        if (yes) await RunAdminAsync(win, db, () => db.Permissions.RemovePassword());

        Build();
    }

    /// <summary>
    /// База, если она еще открыта и текущий пользователь все еще ее администратор. Строка могла
    /// провисеть на экране дольше, чем длилось админство: сосед понизил, базу отключили.
    /// </summary>
    private async Task<DatabaseManager?> AdminDatabaseAsync(Window win, int dbId)
    {
        var db = Databases.FirstOrDefault(d => d.DbId == dbId);

        if (db is not null && db.Permissions.IsAdmin) return db;

        await ConfirmDialog.Info(db is null
                ? "Эта база данных больше не открыта."
                : "Этим распоряжается только администратор базы данных, а вы им больше не являетесь.")
            .ShowDialog(win);

        Build();
        return null;
    }

    /// <summary>
    /// Действие администратора базы. Админство могли отозвать, пока было открыто окно ввода, —
    /// тогда служба прав бросает, и причина показывается словами, как в панели администратора
    /// (<c>ProfileView.RunAdminAction</c>); список после этого перестраивается и строка уходит.
    /// </summary>
    private static async Task RunAdminAsync(Window win, DatabaseManager db, Action action)
    {
        try
        {
            action();
        }
        catch (InvalidOperationException error)
        {
            await ConfirmDialog.Info(error.Message).ShowDialog(win);
        }
        catch (Exception error) when (DbErrors.Handle(error, db.Constructor.DbPath))
        {
            // Сетевая база отвалилась посреди записи: уведомление уже показано.
        }
    }

    // ── Пароль сервера PostgreSQL ─────────────────────────────────────────────

    /// <summary>
    /// Окно настроек базы — то же, что открывает шестеренка в «Управлении базой данных»: пароль
    /// сервера меняется вместе с пробой подключения, и вторая, урезанная форма ввода здесь
    /// разошлась бы с той при первой же правке. Сжатия и сведений о файле у серверной базы нет.
    /// </summary>
    private async Task ServerSettingsAsync(int dbId)
    {
        if (VisualRoot is not Window win || _workspace is not { } workspace) return;
        if (await AdminDatabaseAsync(win, dbId) is not { } db) return;

        if (workspace.Registry.GetAll().FirstOrDefault(d => d.Id == dbId && d.IsPostgres) is not { } entry)
        {
            Build();
            return;
        }

        var result = await new DbSettingsDialog(entry, storage: null, canMaintain: false,
                                                onCompact: null, repo: db.Constructor)
            .ShowDialog<DbSettingsResult?>(win);

        if (result?.NewName is { } newName)
        {
            workspace.Registry.Rename(dbId, newName);
            DatabaseRenamed?.Invoke(this, (dbId, newName));
        }

        // База открыта — переподключать ее не нужно: новый пароль уже проверен пробой в окне и
        // пригодится при следующем открытии.
        if (result?.NewPgPassword is { } password)
            workspace.Registry.UpdatePgPassword(dbId, password);

        Build();
    }

    // ── Соединения ────────────────────────────────────────────────────────────

    /// <summary>Новое соединение — из меню «Добавить пароль» в навбаре.</summary>
    public async Task AddConnectionAsync()
    {
        if (VisualRoot is not Window win || Integrations is not { } registry) return;
        if (!AllowIntegrations(this)) return;

        var result = await new IntegrationConnectionDialog(IntegrationConnection.New())
            .ShowDialog<ConnectionDialogResult?>(win);

        if (result is not null) registry.Save(result.Connection, result.Secret);

        Build();
    }

    /// <summary>Карточка соединения — то же окно, что в «Настройки → Интеграции».</summary>
    private async Task EditConnectionAsync(int id)
    {
        if (VisualRoot is not Window win || Integrations is not { } registry) return;
        if (!AllowIntegrations(this)) return;

        if (registry.Find(id) is not { } card)
        {
            Build();
            return;
        }

        var hasSecret = registry.HasSecret(id);
        var secret    = hasSecret ? registry.Secret(id) : "";

        var result = await new IntegrationConnectionDialog(card, hasSecret, secret)
            .ShowDialog<ConnectionDialogResult?>(win);

        if (result is not null) registry.Save(result.Connection, result.Secret);

        Build();
    }

    /// <summary>
    /// Показать секрет соединения. Пароль проверяет политика — та же, что подтверждает
    /// направления; след пишется в журнал безопасности В ОБОИХ исходах: и когда показали, и
    /// когда не сошелся пароль. Вторая половина важнее первой — подбор мастер-пароля выглядит
    /// именно так.
    ///
    /// Показанное значение живет только в окне показа: закрыли его — на экране и в этом контроле
    /// секрета нет, и возвращаться к нему через час некуда.
    /// </summary>
    private async Task RevealAsync(int id)
    {
        if (VisualRoot is not Window win || Integrations is not { } registry) return;
        if (!AllowIntegrations(this)) return;

        if (registry.Find(id) is not { } connection)
        {
            Build();
            return;
        }

        var policy = EgressSetup.Last.Set;

        if (!policy.HasMaster)
        {
            await ConfirmDialog.Info(
                    "Мастер-пароль организации не задан политикой, и показать секрет нечем.\n\n"
                    + "Это не поломка: без мастер-пароля программа не подтверждает и исходящие "
                    + "направления. Строку для политики посчитает окно «Мастер-пароль для "
                    + "политики»: пункт «Мастер-пароль» в меню «Добавить пароль» этого раздела "
                    + "или кнопка в разделе «Безопасность» у администратора. Вписывает ее в политику "
                    + "ответственный за интеграции.")
                .ShowDialog(win);
            return;
        }

        var shown = "";

        var revealed = await PasswordDialog.ForReveal($"соединения «{connection.Name}»", password =>
        {
            if (!policy.MasterMatches(password))
                return Trace(connection.Name, allowed: false,
                             "Показ секрета отклонен: мастер-пароль не подошел.")
                    ? "Мастер-пароль не подошел"
                    : "След не записался в журнал безопасности — показ отменен";

            var secret = registry.Secret(id);

            // Пароль сошелся, а секрета нет: хранилище ОС его потеряло (app.db приехал с другой
            // машины, профиль пересоздан). Показа не было, но мастер-пароль предъявили верно — и
            // это событие журнала: иначе удачные предъявления оставались бы невидимыми.
            if (secret.Length == 0)
            {
                Trace(connection.Name, allowed: false,
                      "Показ секрета не состоялся: мастер-пароль подошел, но секрет не читается "
                      + "из хранилища операционной системы.");
                return "Секрет не читается: хранилище операционной системы его потеряло";
            }

            // «Не записалось — не показываем»: то же правило, что у отправки и выгрузки. Журнал
            // и есть единственное, что помнит о показанном секрете.
            if (!Trace(connection.Name, allowed: true,
                       "Секрет соединения показан на экране в менеджере паролей."))
                return "След не записался в журнал безопасности — показ отменен";

            shown = secret;
            return "";
        }).ShowDialog<bool>(win);

        if (revealed && shown.Length > 0)
            await new SecretRevealDialog(connection.Name, shown).ShowDialog(win);

        shown = "";
        Build();
    }

    /// <summary>
    /// Строка в журнал безопасности о показе секрета; ложь — записать не удалось. Исходов у
    /// показа три, а не два: показали, не подошел пароль и «пароль подошел, а секрет не читается»,
    /// — поэтому текст решения приходит готовым, а не выводится из одного признака.
    /// </summary>
    private static bool Trace(string connection, bool allowed, string explanation) =>
        SecurityJournal.Add(SecurityEntry.New(
            SecurityEvent.Reveal, allowed,
            who:         CurrentUser.DisplayName,
            scenario:    "",
            address:     $"Соединение «{connection}»",
            fields:      "",
            rows:        0,
            bytes:       0,
            bodyHash:    "",
            explanation: explanation,
            dbId:        0,
            at:          DateTimeOffset.Now));

    private async Task ForgetConnectionAsync(int id)
    {
        if (VisualRoot is not Window win || Integrations is not { } registry) return;
        if (!AllowIntegrations(this)) return;

        if (registry.Find(id) is not { } connection)
        {
            Build();
            return;
        }

        var yes = await new ConfirmDialog(
                $"Удалить секрет соединения «{connection.Name}»?\n\n"
                + "Само соединение останется, но запросы к нему перестанут проходить: чужая "
                + "программа ответит отказом, пока секрет не введут заново.",
                confirmText: "Удалить")
            .ShowDialog<bool>(win);

        if (yes) registry.Save(connection, "");

        Build();
    }

    // ── Токены ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Новый токен — из меню «Добавить пароль». Базы в окне выдачи — только те, где человек
    /// администратор: права роботу потом выдает администратор базы, и выдать токен туда, где
    /// распоряжаться роботом будет некому из присутствующих, значило бы завести робота-сироту.
    /// </summary>
    public async Task AddTokenAsync()
    {
        if (VisualRoot is not Window win || Integrations is not { } registry) return;
        if (!AllowIntegrations(this)) return;

        await TokenIssue.RunAsync(win, registry,
                                  [.. Databases.Where(d => d.Permissions.IsAdmin)],
                                  issued: Build);
        Build();
    }

    /// <summary>
    /// Окно токена: пояснение, тумблер «Включен» и отзыв. Тумблер действует сразу — выключить
    /// токен нужно в ту минуту, когда стало ясно, что он утек, а не по кнопке «Сохранить».
    /// </summary>
    private async Task TokenSettingsAsync(int id)
    {
        if (VisualRoot is not Window win || Integrations is not { } registry) return;
        if (!AllowIntegrations(this)) return;

        if (registry.Token(id) is not { } token)
        {
            Build();
            return;
        }

        var database = Databases.FirstOrDefault(d => d.DbId == token.DbId) is { } owner
            ? NameOf(owner)
            : "";

        await new TokenSettingsDialog(
                token.Name, database, token.Created, token.Used, token.Enabled,
                enable: (dialog, on) =>
                {
                    if (!AllowIntegrations(dialog)) return false;

                    registry.EnableToken(id, on);
                    return true;
                },
                revoke: dialog => RevokeAsync(dialog, registry, token))
            .ShowDialog(win);

        Build();
    }

    private async Task RemoveTokenAsync(int id)
    {
        if (VisualRoot is not Window win || Integrations is not { } registry) return;
        if (!AllowIntegrations(this)) return;

        if (registry.Token(id) is { } token) await RevokeAsync(win, registry, token);

        Build();
    }

    /// <summary>
    /// Отозвать токен после подтверждения — одно на корзину в строке и на кнопку в окне токена.
    /// Истина — отозван.
    /// </summary>
    private async Task<bool> RevokeAsync(Window owner, IntegrationsRegistry registry, AccessToken token)
    {
        if (!AllowIntegrations(owner)) return false;

        var yes = await new ConfirmDialog(
                $"Отозвать токен «{token.Name}»?\n\n"
                + "Чужая программа, которая им пользуется, сразу получит отказ. Выданные по нему "
                + "разрешения на чтение тоже пропадут. Пользователь-робот останется в базе: его "
                + "имя стоит в истории изменений.",
                confirmText: "Отозвать")
            .ShowDialog<bool>(owner);

        if (!yes) return false;

        registry.RemoveToken(token.Id);
        return true;
    }

    // ── Мастер-пароль ─────────────────────────────────────────────────────────

    /// <summary>
    /// Подсчет строки мастер-пароля для политики. Ничего не сохраняет: пароль задается в политике,
    /// а программа только считает строку, которую туда впишут (<see cref="MasterHashDialog"/>).
    /// </summary>
    public async Task MasterHashAsync()
    {
        if (VisualRoot is not Window win) return;

        await new MasterHashDialog().ShowDialog(win);
    }

    // ── Права ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Есть ли право на настройку обмена. Спрашиваются ВСЕ открытые базы разом — тем же правилом,
    /// что и на экранах интеграций: настройки обмена общие на машину, и спроси одну базу —
    /// человек, которому соседняя запретила, обошел бы запрет, открыв ту, где разрешено.
    ///
    /// Этим же правом решается, ПОКАЗЫВАТЬ ли соединения, токены и мастер-пароль
    /// (<see cref="InventoryFacts.IntegrationsAllowed"/>): показ и действие обязаны идти по одному
    /// правилу, иначе человек видит строки, до которых его кнопки не допустят.
    /// </summary>
    private bool CanIntegrations()
    {
        // Спрашиваются ДОПУЩЕННЫЕ базы, а не все открытые: у базы, куда человека еще не пустили,
        // права не спрашивают — там их нет ни на что, и такая база, стоящая в очереди на допуск,
        // запирала бы обмен целиком. Тот же набор, что у экранов интеграций (MainWindow.GrantedDbs).
        var bases = Databases.Where(d => d.Permissions.AccessGranted).ToList();

        return bases.Count > 0 && bases.All(d => d.Permissions.Can(PermAction.IntegrationsSetup));
    }

    /// <summary>Пустить действие или объяснить отказ окном.</summary>
    /// <param name="anchor">От чьего окна показать отказ: из окна токена — поверх него, а не за
    /// ним.</param>
    private bool AllowIntegrations(Visual anchor)
    {
        if (CanIntegrations()) return true;

        PermissionNotice.Show(anchor, PermAction.IntegrationsSetup);
        return false;
    }
}
