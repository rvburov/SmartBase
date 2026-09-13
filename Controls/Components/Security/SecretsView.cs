using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
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
/// <b>Это опись, а не просмотрщик.</b> Экран отвечает на вопрос «что у нас где заведено и что из
/// этого давно мертво». Значения он не показывает — кроме одного случая, где секрет восстановим
/// по построению (секрет соединения: им подписывается каждый запрос, и программа достает его
/// сама при каждой отправке). Прятать от человека то, что программа достает сама, было бы
/// притворством; показывать остальное — враньем: от мастер-пароля, токенов роботов и паролей баз
/// хранится один отпечаток, и показать их нельзя физически, а не «не положено».
///
/// Поэтому у каждой строки, где кнопки «Показать» нет, написано ПОЧЕМУ ее нет
/// (<see cref="SecretEntry.Guard"/>): отсутствие кнопки без объяснения читается как недоделка, и
/// человек идет искать ее в другом месте.
///
/// <b>Главная польза — мертвые секреты.</b> Тем, чем не пользовались три месяца, скорее всего не
/// пользуются вовсе, а дверь открыта. Такие строки помечены и стоят наверху списка: за ними сюда
/// и приходят.
///
/// <b>Показ секрета — событие безопасности.</b> Он идет под мастер-паролем организации и
/// оставляет строку в журнале — и удачная попытка, и неудачная. Своей проверки пароля здесь нет:
/// спрашивается та же политика, что и при подтверждении направления.
///
/// Сами правила описи (что чем является, что мертво, в каком порядке показывать) живут в
/// <see cref="SecretsInventory"/> — там их проверяют тесты. Здесь только показ и действия.
/// </summary>
public sealed class SecretsView : UserControl
{
    private readonly StackPanel _rows = new() { Spacing = 0, Margin = new Thickness(16, 0, 16, 32) };

    private Workspace? _workspace;

    // Показанные секреты: ключ строки → значение. Живут, пока открыт экран, и стираются при
    // уходе из раздела: секрет, оставшийся на экране до завтра, — это тот же секрет на бумажке.
    private readonly Dictionary<string, string> _shown = new(StringComparer.Ordinal);

    // Раскрытая строка (подробности и действия); пусто — ни одной.
    private string _open = "";

    public SecretsView() =>
        Content = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content                     = _rows,
        };

    /// <summary>Показать опись заново — при каждом заходе в раздел.</summary>
    public void Show(Workspace? workspace)
    {
        _workspace = workspace;
        Build();
    }

    /// <summary>
    /// Забыть показанные секреты — при уходе из раздела. Экран, к которому вернулись через час с
    /// открытым секретом, ничем не лучше записки с ним на столе.
    /// </summary>
    public void Forget()
    {
        _shown.Clear();
        _open = "";
    }

    private IntegrationsRegistry? Integrations => _workspace?.Registry.Integrations;

    private IReadOnlyList<DatabaseManager> Databases => _workspace?.Mounted ?? [];

    // ── Опись ─────────────────────────────────────────────────────────────────

    private void Build()
    {
        var rows = _rows.Children;
        rows.Clear();

        var now     = DateTimeOffset.Now;
        var entries = SecretsInventory.Ordered(Collect(), now);
        var dead    = entries.Count(e => SecretsInventory.Stale(e, now));

        Add(rows, Row("Что это за список",
            "Все секреты, заведенные на этой машине: пароли и токены чужих программ, токены "
            + "роботов, пароли баз данных и мастер-пароль организации. Значения здесь не хранятся "
            + "и почти нигде не показываются — список отвечает на вопрос «что где заведено», а не "
            + "«какой там пароль».",
            DataBox(dead == 0
                ? $"Секретов: {entries.Count}. Давно не используемых нет."
                : $"Секретов: {entries.Count}, из них давно не используемых: {dead}. "
                  + $"«Давно» — это больше {SecretsInventory.StaleDays} дней: за такой срок "
                  + "срабатывает и месячный отчет, и квартальная выгрузка.")));

        if (entries.Count == 0)
        {
            Add(rows, Row("Секретов нет",
                "Ни соединений с чужими программами, ни токенов, ни паролей баз пока не "
                + "заведено."), last: true);
            return;
        }

        var list = new StackPanel { Spacing = 0 };

        list.Children.Add(HeaderLine());

        foreach (var entry in entries)
            list.Children.Add(Line(entry, now));

        Add(rows, Row("Секреты",
            "Нажмите на строку — под ней раскроются подробности и действия.",
            list), last: true);
    }

    /// <summary>
    /// Собрать опись из того, что знает программа. Сами строки делает
    /// <see cref="SecretsInventory"/>: здесь только выборка данных.
    /// </summary>
    private List<SecretEntry> Collect()
    {
        var entries = new List<SecretEntry>();

        if (Integrations is { } registry)
        {
            var requests = registry.Requests();

            foreach (var connection in registry.Connections())
            {
                if (!registry.HasSecret(connection.Id)) continue;

                var mine = requests.Where(r => r.ConnectionId == connection.Id).ToList();

                // Последнее пользование секретом — последний запуск любого из запросов этого
                // соединения: секрет подписывает каждый из них, и отдельной отметки ему не надо.
                var last = mine.Select(r => registry.LastRun(r.Id))
                               .Where(m => m is not null)
                               .OrderByDescending(m => m!.Value)
                               .FirstOrDefault();

                entries.Add(SecretsInventory.Connection(
                    connection.Id, connection.Name, connection.Address,
                    mine.Count, last, created: null));
            }

            foreach (var token in registry.Tokens())
                entries.Add(SecretsInventory.RobotToken(
                    token.Id, token.Name,
                    Databases.FirstOrDefault(d => d.DbId == token.DbId)?.Name ?? "",
                    token.Created, token.Used, token.Enabled));
        }

        foreach (var db in Databases)
            if (db.Permissions.HasPassword)
                entries.Add(SecretsInventory.DatabasePassword(db.DbId, db.Name));

        // Пароли серверов PostgreSQL: база может быть и не открыта — пароль живет в реестре
        // машины, и в описи ему место наравне с остальными.
        foreach (var entry in _workspace?.Registry.GetAll() ?? [])
            if (entry.IsPostgres)
                entries.Add(SecretsInventory.PgPassword(entry.Id, entry.Name, PgServer(entry)));

        var policy = EgressSetup.Last.Set;

        entries.Add(SecretsInventory.PolicyMaster(
            policy.HasMaster,
            string.Join(", ", policy.Sources.Where(p => p.HasMaster).Select(p => p.Source))));

        return entries;
    }

    private static string PgServer(Database.Entities.DatabaseRefEntity entry) =>
        entry.PgHost is { Length: > 0 } host
            ? $"{host}:{entry.PgPort}/{entry.PgDatabase}"
            : "";

    // ── Таблица ───────────────────────────────────────────────────────────────

    /// <summary>Ширины колонок общие у шапки и строк — иначе таблица «поедет».</summary>
    private static ColumnDefinitions Columns() => new("*,8,150,8,140,8,96");

    private static Control HeaderLine()
    {
        var grid = new Grid { ColumnDefinitions = Columns() };

        Put(grid, 0, Head("Секрет"));
        Put(grid, 2, Head("Где применяется"));
        Put(grid, 4, Head("Пользовались"));
        Put(grid, 6, Head("Состояние"));

        return TableHead(grid);
    }

    private Control Line(SecretEntry entry, DateTimeOffset now)
    {
        var open  = _open == entry.Key;
        var stale = SecretsInventory.Stale(entry, now);

        var grid = new Grid { ColumnDefinitions = Columns() };

        Put(grid, 0, Cell(entry.Title));
        Put(grid, 2, Cell(entry.Where, secondary: true));
        Put(grid, 4, Cell(UsedText(entry), secondary: true));

        var state = Cell(stale ? "давно не нужен" : "в работе");
        state.Foreground = Ink(stale ? "BrushRed" : "BrushGreen",
                               stale ? "#FF3B30" : "#34C759");
        Put(grid, 6, state);

        return TableRow(grid, open ? Details(entry, stale) : null, open, () =>
        {
            _open = open ? "" : entry.Key;
            Build();
        });
    }

    /// <summary>
    /// Когда пользовались. «Не считаем» и «не пользовались» — разные ответы, и путать их нельзя:
    /// по первому отзывать секрет не за что, по второму — пора.
    /// </summary>
    private static string UsedText(SecretEntry entry) =>
        !entry.UseKnown            ? "не отмечается"
        : entry.Used is { } moment ? moment.ToLocalTime().ToString("dd.MM.yyyy")
        :                            "ни разу";

    private Control Details(SecretEntry entry, bool stale)
    {
        var list = new StackPanel { Spacing = 4 };

        list.Children.Add(Note(entry.What));

        if (entry.Created is { } created)
            list.Children.Add(Note("Заведен: " + created.ToLocalTime().ToString("dd.MM.yyyy HH:mm")));

        if (stale)
            list.Children.Add(Note($"Им не пользовались дольше {SecretsInventory.StaleDays} дней. "
                + "Забытый секрет — открытая дверь, о которой никто не думает: если он больше не "
                + "нужен, удалите его."));

        if (entry.Guard.Length > 0) list.Children.Add(Note(entry.Guard));

        var block = new StackPanel { Spacing = 6, Children = { DataBox(list) } };

        // Показанное значение — своей ячейкой, а не строкой в общей: это единственное место
        // экрана, где на виду лежит сам секрет, и смешивать его с описанием нельзя.
        if (_shown.TryGetValue(entry.Key, out var secret))
        {
            block.Children.Add(CodeBox(secret));
            block.Children.Add(ButtonLine(
                RowButton("Скопировать", () =>
                    _ = TopLevel.GetTopLevel(this)?.Clipboard?.SetTextAsync(secret)),
                RowButton("Скрыть", () => { _shown.Remove(entry.Key); Build(); })));
        }

        if (Actions(entry) is { Count: > 0 } actions)
            block.Children.Add(ButtonLine([.. actions]));

        return block;
    }

    // ── Действия ──────────────────────────────────────────────────────────────

    private List<Control> Actions(SecretEntry entry)
    {
        var buttons = new List<Control>();

        if (entry.Revealable && !_shown.ContainsKey(entry.Key))
            buttons.Add(RowButton("Показать…", () => _ = RevealAsync(entry)));

        switch (entry.Kind)
        {
            case SecretKind.Connection:
                buttons.Add(RowButton("Заменить…", () => _ = ReplaceConnectionAsync(entry)));
                buttons.Add(RowButton("Удалить",   () => _ = ForgetConnectionAsync(entry)));
                break;

            case SecretKind.RobotToken:
                // Заменить токен — значит выдать новый: старый секрет не хранится, и «сменить
                // его» нечем. Выдача живет на своем экране вместе с выбором базы и правами робота.
                buttons.Add(RowButton("Отозвать", () => _ = RemoveTokenAsync(entry)));
                break;

            case SecretKind.DatabasePassword:
                buttons.Add(RowButton("Заменить…", () => _ = ReplaceDatabasePasswordAsync(entry)));
                buttons.Add(RowButton("Удалить",   () => _ = RemoveDatabasePasswordAsync(entry)));
                break;
        }

        return buttons;
    }

    /// <summary>
    /// Показать секрет соединения. Пароль проверяет политика — та же, что подтверждает
    /// направления; след пишется в журнал безопасности В ОБОИХ случаях: и когда показали, и
    /// когда не сошелся пароль. Вторая половина важнее первой — подбор мастер-пароля выглядит
    /// именно так.
    /// </summary>
    private async Task RevealAsync(SecretEntry entry)
    {
        if (VisualRoot is not Window win) return;
        if (Integrations is not { } registry) return;

        var (_, id) = SecretsInventory.Parse(entry.Key);

        var policy = EgressSetup.Last.Set;

        if (!policy.HasMaster)
        {
            await ConfirmDialog.Info(
                    "Мастер-пароль организации не задан политикой, и показать секрет нечем.\n\n"
                    + "Это не поломка: без мастер-пароля программа не подтверждает и исходящие "
                    + "направления — см. раздел «Безопасность».")
                .ShowDialog(win);
            return;
        }

        await PasswordDialog.ForReveal($"«{entry.Title}»", password =>
        {
            var ok = policy.MasterMatches(password);

            // «Не записалось — не показываем»: то же правило, что у отправки и выгрузки. Журнал
            // и есть единственное, что помнит о показанном секрете.
            if (!Trace(entry, ok)) return "След не записался в журнал безопасности — показ отменен";

            if (!ok) return "Мастер-пароль не подошел";

            var secret = registry.Secret(id);

            if (secret.Length == 0)
                return "Секрет не читается: хранилище операционной системы его потеряло";

            _shown[entry.Key] = secret;
            return "";
        }).ShowDialog<bool>(win);

        Build();
    }

    /// <summary>Строка в журнал безопасности о показе секрета; ложь — записать не удалось.</summary>
    private bool Trace(SecretEntry entry, bool allowed) =>
        SecurityJournal.Add(SecurityEntry.New(
            SecurityEvent.Reveal, allowed,
            who:         CurrentUser.DisplayName,
            scenario:    "",
            address:     entry.Title,
            fields:      "",
            rows:        0,
            bytes:       0,
            bodyHash:    "",
            explanation: allowed
                ? "Секрет соединения показан на экране в менеджере паролей."
                : "Показ секрета отклонен: мастер-пароль не подошел.",
            dbId:        0,
            at:          DateTimeOffset.Now));

    private async Task ReplaceConnectionAsync(SecretEntry entry)
    {
        if (VisualRoot is not Window win) return;
        if (Integrations is not { } registry) return;
        if (!AllowIntegrations(win)) return;

        var (_, id) = SecretsInventory.Parse(entry.Key);

        if (registry.Find(id) is not { } connection) return;

        var secret = await PasswordDialog.ForSecret($"«{entry.Title}»").ShowDialog<string?>(win);
        if (secret is null) return;

        registry.Save(connection, secret);
        _shown.Remove(entry.Key);
        Build();
    }

    private async Task ForgetConnectionAsync(SecretEntry entry)
    {
        if (VisualRoot is not Window win) return;
        if (Integrations is not { } registry) return;
        if (!AllowIntegrations(win)) return;

        var (_, id) = SecretsInventory.Parse(entry.Key);

        if (registry.Find(id) is not { } connection) return;

        var yes = await new ConfirmDialog(
                $"Удалить секрет соединения «{connection.Name}»?\n\n"
                + "Само соединение останется, но запросы к нему перестанут проходить: чужая "
                + "программа ответит отказом, пока секрет не введут заново.",
                confirmText: "Удалить")
            .ShowDialog<bool>(win);

        if (!yes) return;

        registry.Save(connection, "");
        _shown.Remove(entry.Key);
        Build();
    }

    private async Task RemoveTokenAsync(SecretEntry entry)
    {
        if (VisualRoot is not Window win) return;
        if (Integrations is not { } registry) return;
        if (!AllowIntegrations(win)) return;

        var (_, id) = SecretsInventory.Parse(entry.Key);

        var yes = await new ConfirmDialog(
                $"Отозвать {entry.Title.ToLowerInvariant()}?\n\n"
                + "Чужая программа, которая им пользуется, сразу получит отказ. Выданные по нему "
                + "разрешения на чтение тоже пропадут. Пользователь-робот останется в базе: его "
                + "имя стоит в истории изменений.",
                confirmText: "Отозвать")
            .ShowDialog<bool>(win);

        if (!yes) return;

        registry.RemoveToken(id);
        Build();
    }

    private async Task ReplaceDatabasePasswordAsync(SecretEntry entry)
    {
        if (VisualRoot is not Window win) return;

        var (_, dbId) = SecretsInventory.Parse(entry.Key);

        if (AdminDatabase(dbId) is not { } db)
        {
            await ConfirmDialog.Info(
                    "Пароль базы данных меняет только ее администратор.").ShowDialog(win);
            return;
        }

        var password = await PasswordDialog.ForSet(db.Name, hasPassword: true)
                                           .ShowDialog<string?>(win);

        if (password is null) return;

        db.Permissions.SetPassword(password);
        Build();
    }

    private async Task RemoveDatabasePasswordAsync(SecretEntry entry)
    {
        if (VisualRoot is not Window win) return;

        var (_, dbId) = SecretsInventory.Parse(entry.Key);

        if (AdminDatabase(dbId) is not { } db)
        {
            await ConfirmDialog.Info(
                    "Пароль базы данных убирает только ее администратор.").ShowDialog(win);
            return;
        }

        var yes = await new ConfirmDialog(
                $"Убрать пароль базы «{db.Name}»?\n\n"
                + "Новые пользователи больше не смогут открыть себе доступ сами — их будет "
                + "допускать администратор.",
                confirmText: "Убрать")
            .ShowDialog<bool>(win);

        if (!yes) return;

        db.Permissions.RemovePassword();
        Build();
    }

    // ── Права ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Право на настройку обмена. Спрашиваются ВСЕ открытые базы разом — тем же правилом, что и
    /// на экранах интеграций: настройки обмена общие на машину, и спроси одну базу — человек,
    /// которому соседняя запретила, обошел бы запрет, открыв ту, где разрешено.
    /// </summary>
    private bool AllowIntegrations(Window win)
    {
        var bases = Databases;

        if (bases.Count > 0 && bases.All(d => d.Permissions.Can(PermAction.IntegrationsSetup)))
            return true;

        PermissionNotice.Show(this, PermAction.IntegrationsSetup);
        return false;
    }

    /// <summary>База по ключу, если текущий пользователь в ней администратор.</summary>
    private DatabaseManager? AdminDatabase(int dbId) =>
        Databases.FirstOrDefault(d => d.DbId == dbId && d.Permissions.IsAdmin);
}
