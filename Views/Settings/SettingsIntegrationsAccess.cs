using Avalonia.Controls;
using Avalonia.Layout;
using SmartBase.Controls.Dialogs;
using SmartBase.Database;
using SmartBase.Integrations;
using SmartBase.Setup;
using SmartBase.Utils;
using static SmartBase.Controls.Widgets.SettingRows;

namespace SmartBase.Views.Settings;

/// <summary>
/// Вкладка «Доступ извне» — половина про сеть (docs/ROADMAP-API.md, раздел 6.7). Вторая ее
/// половина, папка обмена, живет в <c>SettingsIntegrationsInbox</c>: экран один, потому что
/// вопрос у человека один — «как до нас достучаться».
///
/// Три вещи и ровно в этом порядке: каким способом к нам обращаются, кому это разрешено и что
/// переслать тому, кто будет писать отправителя.
///
/// <b>Выключено по умолчанию.</b> Порт открывают осознанно, а не обнаруживают; сетевой режим при
/// этом не просто галочка с предупреждением — его разрешает политика организации, и строка о нем
/// показана серой с объяснением, к кому идти. Запрет обязан объяснять себя, иначе его считают
/// поломкой.
/// </summary>
public partial class SettingsView
{
    /// <summary>Порт открылся или закрылся — перерисовать строку состояния.</summary>
    public void OnIngressChanged()
    {
        if (_integrationsTab == "inbox") BuildIntegrationsInbox();
    }

    // ── Как к нам обращаются ──────────────────────────────────────────────────

    private Border AccessModeRow()
    {
        var mode = AppSettings.LoadIngressMode();
        var port = AppSettings.LoadIngressPort();

        var block = new StackPanel { Spacing = 6 };

        block.Children.Add(ModeChoice("Выключен", IngressMode.Off, mode,
            "Никого не слушаем. Это состояние по умолчанию."));

        block.Children.Add(ModeChoice("Только с этого компьютера", IngressMode.Loopback, mode,
            $"Адрес {IngressGate.LoopbackHost} — программы, запущенные на этой же машине. "
            + "С соседнего компьютера сюда не достучаться физически, даже зная токен. "
            + "Прав администратора не требуется."));

        // Сетевой режим показан всегда: пустое место на его месте читалось бы как «такого не
        // бывает», а серая строка с причиной — как «есть, но не вам решать».
        var verdict = IngressGate.Check(IngressMode.Network, port);

        var network = ModeChoice("Разрешить по локальной сети", IngressMode.Network, mode,
            verdict.Allowed
                ? "Слушать будем все адреса машины сразу — их бывает несколько. Нужны права "
                + "администратора ОДИН раз: занять адрес и открыть порт в брандмауэре."
                : Capitalize(verdict.Explanation)
                + ". Разрешает его организация, а не пользователь, — обратитесь к "
                + "ответственному за интеграции.");

        network.IsEnabled = verdict.Allowed;
        block.Children.Add(network);

        if (verdict.Allowed && ListenerPermissions.Needed)
            block.Children.Add(PermissionsLine(port));

        block.Children.Add(PortLine(mode, port));

        // Слушаем или нет — в ячейке с данными: это ответ программы, а не пояснение к
        // переключателям, и отличаться он должен так же, как отличается показанное от набранного.
        var state = new List<Control> { Note(StateLine()) };

        if (mode == IngressMode.Network && MachineAddress.All().Count > 1)
            state.Add(Note("Адреса этой машины: " + string.Join(", ", MachineAddress.All())
                           + ". Отправителю называют тот, по которому он до нас дотянется."));

        block.Children.Add(DataBox(state));

        return Row("Как к нам обращаются",
            "Прием по сети нужен тем отправителям, которые стоят на этом же компьютере: "
            + "учетной системе, скрипту, макросу табличного редактора. Всем остальным — папка "
            + "обмена ниже: она работает, даже когда программа закрыта.",
            block);
    }

    /// <summary>Одна строка выбора режима: переключатель и пояснение под ним.</summary>
    private Control ModeChoice(string title, IngressMode value, IngressMode current,
                               string explanation)
    {
        var radio = new RadioButton
        {
            Content   = title,
            GroupName = "ingressMode",
            IsChecked = value == current,
        };

        radio.IsCheckedChanged += (_, _) =>
        {
            if (radio.IsChecked != true || AppSettings.LoadIngressMode() == value) return;

            if (!AllowIntegrations(Core.Models.PermAction.IntegrationsSetup))
            {
                radio.IsChecked = false;
                return;
            }

            AppSettings.SaveIngressMode(value);
            _ingress?.Apply();
            BuildIntegrationsInbox();
        };

        var block = new StackPanel { Spacing = 2 };
        block.Children.Add(radio);

        var note = SubNote(explanation);
        block.Children.Add(note);

        return block;
    }

    /// <summary>
    /// Разрешения системы для сетевого режима: занять адрес и открыть порт в брандмауэре.
    /// Программа не делает этого молча — команды показываются, копируются и выполняются по
    /// кнопке: менять настройки машины за спиной у того, кто за нее отвечает, нельзя.
    /// </summary>
    private Control PermissionsLine(int port)
    {
        var block = new StackPanel { Spacing = 6, Margin = new Avalonia.Thickness(0, 4, 0, 0) };

        block.Children.Add(SubNote(
            "Права администратора нужны один раз на машину. Кнопка ниже выполнит обе команды "
            + "с окном контроля учетных записей; если прав нет — скопируйте их и передайте в "
            + "службу ИТ."));

        block.Children.Add(ButtonLine(
            RowButton("Настроить разрешения…", () => _ = GrantListenerAsync(port)),
            RowButton("Скопировать команды", () =>
                _ = TopLevel.GetTopLevel(this)?.Clipboard?
                    .SetTextAsync(ListenerPermissions.Script(port)))));

        return block;
    }

    private async Task GrantListenerAsync(int port)
    {
        if (VisualRoot is not Window win) return;
        if (!AllowIntegrations(Core.Models.PermAction.IntegrationsSetup)) return;

        var trouble = await ListenerPermissions.GrantAsync(port);

        // Успех не объявляется словами: подтвердит его только поднявшийся слушатель. Поэтому
        // сразу пробуем открыть порт заново и показываем то, чем это кончилось.
        _ingress?.Apply();
        BuildIntegrationsInbox();

        await ConfirmDialog.Info(trouble is null
            ? "Команды выполнены. Состояние приема — строкой ниже переключателей: "
            + "если порт открылся, там стоит адрес."
            : "Разрешения не выданы: " + trouble + ".\n\n"
            + "Команды можно скопировать кнопкой рядом и передать в службу ИТ.")
            .ShowDialog(win);
    }

    private static string Capitalize(string text) =>
        text.Length == 0 ? text : char.ToUpperInvariant(text[0]) + text[1..];

    /// <summary>Номер порта. Правится, только когда прием включен: менять номер двери, которой
    /// нет, незачем.</summary>
    private Control PortLine(IngressMode mode, int port)
    {
        var box = Field(port.ToString(System.Globalization.CultureInfo.InvariantCulture),
                        width: 110);

        box.IsEnabled = mode != IngressMode.Off;

        var apply = RowButton("Применить", () =>
        {
            if (!AllowIntegrations(Core.Models.PermAction.IntegrationsSetup)) return;

            if (!int.TryParse(box.Text, out var wanted) || !IngressGate.ValidPort(wanted))
            {
                _ = ConfirmDialog
                    .Info($"Номер порта годится от {IngressGate.MinPort} до {IngressGate.MaxPort}.")
                    .ShowDialog((Window)VisualRoot!);
                return;
            }

            AppSettings.SaveIngressPort(wanted);
            _ingress?.Apply();
            BuildIntegrationsInbox();
        });

        apply.IsEnabled = mode != IngressMode.Off;

        var label = new TextBlock
        {
            Classes           = { "desc" },
            Text              = "Порт",
            VerticalAlignment = VerticalAlignment.Center,
        };

        var line = ButtonLine(label, box, apply);
        line.Margin = new Avalonia.Thickness(0, 6, 0, -6);

        return line;
    }

    /// <summary>
    /// Слушаем или нет — одной фразой. «Выключено» и «не смогли открыть» выглядят на экране
    /// одинаково, а означают разное, и второе надо назвать вслух: занятый порт человек ищет
    /// часами, если ему об этом не сказали.
    /// </summary>
    private string StateLine()
    {
        if (_ingress is not { } ingress)
            return "Служба приема еще не поднялась — повторите через мгновение.";

        if (ingress.Listening)
            return $"Слушаю {ingress.Address}. Отправитель стучится сюда же.";

        if (ingress.Trouble.Length > 0)
            return "Порт не открылся: " + ingress.Trouble
                 + ". Обычно это занятый номер — попробуйте другой.";

        return "Сейчас не слушаю: прием выключен.";
    }

    // ── Токены доступа ────────────────────────────────────────────────────────

    private Border TokensRow()
    {
        var block = new StackPanel { Spacing = 8 };

        if (Integrations is not { } registry)
        {
            block.Children.Add(DataBox("Список станет доступен, когда откроется база данных."));
            return Row("Токены доступа", TokensHelp(), block);
        }

        var tokens = registry.Tokens();
        var opened = _databases?.Invoke() ?? [];

        if (tokens.Count == 0)
            block.Children.Add(DataBox("Не выдано ни одного токена — постучаться к нам сейчас "
                                       + "некому."));

        foreach (var token in tokens)
        {
            var database = opened.FirstOrDefault(d => d.DbId == token.DbId)?.Name
                        ?? $"база №{token.DbId} (сейчас не открыта)";

            var line = Note($"{token.Name} — {database}"
                + (token.Enabled ? "" : " · выключен")
                + (token.Used is { } used
                    ? $" · последнее обращение {used.ToLocalTime():dd.MM.yyyy HH:mm}"
                    : " · обращений не было"));

            var item = token;

            // Сам токен — в ячейке с данными, действия над ним — рядом, как кнопки строки в
            // «Управлении базой данных»: показанное и нажимаемое не смешиваются.
            var actions = ButtonLine(
                RowButton(item.Enabled ? "Выключить" : "Включить", () =>
                {
                    if (!AllowIntegrations(Core.Models.PermAction.IntegrationsSetup)) return;

                    registry.EnableToken(item.Id, !item.Enabled);
                    BuildIntegrationsInbox();
                }),
                RowButton("Убрать", () => _ = RemoveTokenAsync(registry, item)));

            var box = DataBox(new[] { line });
            box.VerticalAlignment = VerticalAlignment.Center;
            actions.VerticalAlignment = VerticalAlignment.Center;
            actions.Margin = new Avalonia.Thickness(8, 0, 0, -6);

            var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
            Grid.SetColumn(box, 0);
            Grid.SetColumn(actions, 1);
            grid.Children.Add(box);
            grid.Children.Add(actions);

            block.Children.Add(grid);
        }

        block.Children.Add(ButtonLine(
            RowButton("Создать токен", () => _ = IssueTokenAsync(registry))));

        return Row("Токены доступа", TokensHelp(), block);
    }

    private static string TokensHelp() =>
        "Токен — это пользователь-робот в базе данных: он виден администратору в списке "
        + "пользователей, его именем подписаны сделанные им записи, и допуск ему отзывается там "
        + "же, где человеку. По умолчанию роботу разрешено только создавать и изменять записи — "
        + "читать базу через этот канал нельзя вовсе.";

    private async Task IssueTokenAsync(Database.IntegrationsRegistry registry)
    {
        if (VisualRoot is not Window win) return;
        if (!AllowIntegrations(Core.Models.PermAction.IntegrationsSetup)) return;

        var opened = _databases?.Invoke() ?? [];

        if (opened.Count == 0)
        {
            await ConfirmDialog.Info("Сначала откройте базу данных: токен выдается под нее.")
                               .ShowDialog(win);
            return;
        }

        var made  = "";
        var owner = "";

        var dialog = new TokenDialog(
            [.. opened.Select(d => (d.DbId, d.Name))],
            (name, dbId) =>
            {
                var target = opened.FirstOrDefault(d => d.DbId == dbId);
                if (target is null) return "эта база сейчас не открыта";

                var (token, secret) = registry.Issue(name, dbId, Environment.MachineName,
                                                     DateTimeOffset.Now);

                // Робот заводится СРАЗУ: токен, которому не соответствует пользователь, работать
                // не будет, и узнавать об этом из отказа первой посылки — потерянный вечер.
                target.Permissions.EnsureRobot(token.RobotKey, token.Name, DateTimeOffset.Now);

                made  = secret;
                owner = target.Name;
                return null;
            });

        if (await dialog.ShowDialog<bool>(win) != true || made.Length == 0) return;

        BuildIntegrationsInbox();

        // Текст токена показывается ОДИН раз: хранилище, из которого секрет можно прочитать
        // обратно, рано или поздно прочитают. Отсюда и кнопка «Скопировать» рядом.
        await ConfirmDialog.Info(
            $"Токен для базы «{owner}» создан.\n\n{made}\n\n"
            + "Скопируйте его сейчас: показать второй раз программа не сможет — она хранит "
            + "только проверочный отпечаток. Потеряется — выдайте новый.",
            "Скопировать и закрыть").ShowDialog(win);

        await (TopLevel.GetTopLevel(this)?.Clipboard?.SetTextAsync(made) ?? Task.CompletedTask);
    }

    private async Task RemoveTokenAsync(Database.IntegrationsRegistry registry, AccessToken token)
    {
        if (VisualRoot is not Window win) return;
        if (!AllowIntegrations(Core.Models.PermAction.IntegrationsSetup)) return;

        var ok = await new ConfirmDialog(
            $"Убрать токен «{token.Name}»?\n\n"
            + "Отправитель с этим токеном перестанет проходить сразу же. Пользователь-робот в "
            + "базе останется: его именем подписаны уже сделанные записи, и удалить его значило "
            + "бы обезличить историю изменений.").ShowDialog<bool>(win);

        if (!ok) return;

        registry.RemoveToken(token.Id);
        BuildIntegrationsInbox();
    }

    // ── Что можно читать ──────────────────────────────────────────────────────

    /// <summary>
    /// Выдачи чтения (docs/ROADMAP-API.md, шаг 10). Блок стоит СРАЗУ за токенами: токен
    /// отвечает «кто может постучаться», выдача — «что ему позволено вычитать», и читаются
    /// они подряд.
    /// </summary>
    private Border ReadsRow()
    {
        var block = new StackPanel { Spacing = 8 };

        if (Integrations is not { } registry)
        {
            block.Children.Add(DataBox("Список станет доступен, когда откроется база данных."));
            return Row("Что можно читать", ReadsHelp(), block);
        }

        var grants = registry.Grants();
        var tokens = registry.Tokens();
        var now    = DateTimeOffset.Now;

        if (grants.Count == 0)
            block.Children.Add(DataBox(
                "Выдач нет — вычитать данные по сети не может никто. Это состояние по "
                + "умолчанию: чтение разрешают осознанно, а не обнаруживают."));

        foreach (var grant in grants)
        {
            var token = tokens.FirstOrDefault(t => t.Id == grant.TokenId);

            var lines = new List<Control>
            {
                Note($"{token?.Name ?? $"токен №{grant.TokenId}"} → коллекция «{grant.Collection}»"),
                Note($"поля: {grant.Fields} · {grant.Describe()}"),
            };

            if (grant.Trouble(now) is { } trouble)
            {
                var bad = Note("не действует: " + trouble);
                bad.Foreground = Ink("BrushRed", "#FF3B30");
                lines.Add(bad);
            }

            var box = DataBox(lines);
            box.VerticalAlignment = VerticalAlignment.Center;

            var item    = grant;
            var actions = ButtonLine(RowButton("Отозвать", () => _ = RevokeGrantAsync(registry, item)));
            actions.VerticalAlignment = VerticalAlignment.Center;
            actions.Margin            = new Avalonia.Thickness(8, 0, 0, -6);

            var line = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
            Grid.SetColumn(box, 0);
            Grid.SetColumn(actions, 1);
            line.Children.Add(box);
            line.Children.Add(actions);

            block.Children.Add(line);
        }

        var issue = RowButton("Разрешить чтение…", () => _ = IssueGrantAsync(registry));
        issue.IsEnabled = tokens.Count > 0;

        block.Children.Add(ButtonLine(issue));

        if (tokens.Count == 0)
            block.Children.Add(Note("Сначала создайте токен: выдача разрешает чтение ему."));

        return Row("Что можно читать", ReadsHelp(), block);
    }

    private static string ReadsHelp() =>
        "Токен по умолчанию только пишет. Чтение разрешает ВЫДАЧА: одна коллекция, "
        + "поименованные поля, срок — подтверждается мастер-паролем организации, как исходящее "
        + "направление. Каждый ответ ложится в журнал безопасности и занимает суточный потолок "
        + "выдачи; помеченные поля не выдаются вовсе.";

    private async Task IssueGrantAsync(Database.IntegrationsRegistry registry)
    {
        if (VisualRoot is not Window win) return;
        if (!AllowIntegrations(Core.Models.PermAction.IntegrationsOutward)) return;

        var opened = _databases?.Invoke() ?? [];

        // Токены закрытых баз в списке не предлагаются: состав их коллекций взять неоткуда.
        var usable = registry.Tokens()
            .Select(t => (Token: t, Db: opened.FirstOrDefault(d => d.DbId == t.DbId)))
            .Where(p => p.Db is not null)
            .ToList();

        if (usable.Count == 0)
        {
            await ConfirmDialog.Info(
                    "Нет ни одного токена открытой базы данных: выдача разрешает чтение "
                    + "токену, и сначала нужен он.")
                .ShowDialog(win);
            return;
        }

        DatabaseManager DbOf(int tokenId) => usable.First(p => p.Token.Id == tokenId).Db!;

        var dialog = new ReadGrantDialog(
            [.. usable.Select(p => (p.Token.Id, $"{p.Token.Name} — {p.Db!.Name}"))],
            tokenId => [.. DbOf(tokenId).Constructor.GetAllCollections()
                            .Where(c => c.Id is not null)
                            .Select(c => (c.Id!.Value, c.Name ?? ""))],
            (tokenId, collectionId) =>
                DbOf(tokenId).Constructor.GrantColumnsIsolated(collectionId),
            choice =>
            {
                var made = registry.ConfirmGrant(
                    choice.TokenId, choice.CollectionId, choice.Collection,
                    choice.FieldKeys, choice.Fields, choice.Password,
                    CurrentUser.DisplayName, choice.Days, DateTimeOffset.Now, out var trouble);

                if (made is null) return trouble;

                // След — в журнал безопасности, как у направления: это решение о полномочии,
                // и читает его проверяющий. Не записалось — сказать сразу.
                var token = registry.Token(choice.TokenId);

                var written = Database.SecurityJournal.Add(Database.SecurityEntry.New(
                    Database.SecurityEvent.Confirm, allowed: true,
                    who: CurrentUser.DisplayName, scenario: "",
                    address: IngressApi.RecordsPath + "?collection=" + choice.Collection,
                    fields: choice.Fields, rows: 0, bytes: 0, bodyHash: "",
                    explanation: $"выдано чтение коллекции «{choice.Collection}» токену "
                               + $"«{token?.Name ?? choice.TokenId.ToString()}» на {choice.Days} дней",
                    dbId: token?.DbId ?? 0, at: DateTimeOffset.Now,
                    confirmed: CurrentUser.DisplayName));

                return written
                    ? null
                    : "выдача записана, но след в журнале безопасности не лег — сообщите "
                    + "ответственному за интеграции";
            });

        if (await dialog.ShowDialog<bool>(win)) BuildIntegrationsInbox();
    }

    private async Task RevokeGrantAsync(Database.IntegrationsRegistry registry,
                                        ReadGrant grant)
    {
        if (VisualRoot is not Window win) return;
        if (!AllowIntegrations(Core.Models.PermAction.IntegrationsOutward)) return;

        var yes = await new ConfirmDialog(
            $"Отозвать выдачу на коллекцию «{grant.Collection}»? Чтение остановится сразу же, "
            + "а вернуть его можно только мастер-паролем.",
            "Отозвать").ShowDialog<bool>(win);

        if (!yes) return;

        registry.RevokeGrant(grant.Id);

        Database.SecurityJournal.Add(Database.SecurityEntry.New(
            Database.SecurityEvent.Revoke, allowed: true,
            who: CurrentUser.DisplayName, scenario: "",
            address: IngressApi.RecordsPath + "?collection=" + grant.Collection,
            fields: grant.Fields, rows: 0, bytes: 0, bodyHash: "",
            explanation: $"отозвана выдача чтения коллекции «{grant.Collection}»",
            dbId: 0, at: DateTimeOffset.Now));

        BuildIntegrationsInbox();
    }

    // ── Описание API ──────────────────────────────────────────────────────────

    private Border ApiRow()
    {
        var block = new StackPanel { Spacing = 6 };

        var opened = _databases?.Invoke() ?? [];

        if (opened.Count == 0)
        {
            block.Children.Add(DataBox("Описание станет доступно, когда откроется база данных."));
            return Row("Описание для отправителя", ApiHelp(), block);
        }

        var combo = Picker(opened.Select(d => d.Name), 0);

        // Кнопка одна: описание открывается своим окном, а копирование и сохранение живут в
        // нем же. Три кнопки в строке означали три пути к одному и тому же тексту, из которых
        // два показывали его вслепую.
        block.Children.Add(ButtonLine(
            combo,
            RowButton("Показать описание", () => _ = ShowApiAsync(Chosen(combo, opened)))));

        return Row("Описание для отправителя", ApiHelp(), block);
    }

    private static string ApiHelp() =>
        "Собирается из состава коллекций: адрес, вид сообщения и список колонок с типами. Это и "
        + "есть то, что пересылают тому, кто будет писать отправителя, — больше от нас ему "
        + "ничего не нужно. Значений полей в описании нет: отдается схема, а не данные.";

    private static DatabaseManager? Chosen(ComboBox combo, IReadOnlyList<DatabaseManager> opened) =>
        combo.SelectedIndex >= 0 && combo.SelectedIndex < opened.Count
            ? opened[combo.SelectedIndex]
            : null;

    /// <summary>
    /// Собрать описание. Схема читается на отдельном подключении и в фоне: в базе бывают сотни
    /// коллекций, а UI-поток этого не переживет.
    /// </summary>
    private async Task<string> ApiTextAsync(DatabaseManager target)
    {
        var address = _ingress?.Address ?? "";
        var name    = target.Name;
        var repo    = target.Constructor;

        var collections = await Task.Run(repo.ApiSchemaIsolated);

        return ApiDescription.Text(address, name, collections);
    }

    /// <summary>
    /// Показать описание своим окном (<see cref="ApiDescriptionDialog"/>). Раньше это делало окно
    /// сообщения — оно растет по содержимому и режет длинный текст краем экрана; описание на
    /// сотню коллекций прочитать в нем было нечем (docs/ROADMAP-API.md, шаг 14).
    /// </summary>
    private async Task ShowApiAsync(DatabaseManager? target)
    {
        if (VisualRoot is not Window win || target is null) return;

        var text = await ApiTextAsync(target);

        await new ApiDescriptionDialog(text, ApiDescription.FileName(target.Name), ApiHelp())
            .ShowDialog(win);
    }
}
