using Avalonia.Controls;
using SmartBase.Controls.Dialogs;
using SmartBase.Integrations;
using static SmartBase.Controls.Widgets.SettingRows;

namespace SmartBase.Views.Settings;

/// <summary>
/// Вкладки «Соединения» и «Запросы» раздела «Интеграции» (partial SettingsView).
///
/// Здесь только списки и действия — карточки правятся в своих окнах
/// (<see cref="IntegrationConnectionDialog"/>, <see cref="IntegrationRequestDialog"/>), как и
/// подключение баз данных в соседнем разделе. Хранит все <c>IntegrationsRegistry</c>, а секреты
/// не проходят через этот файл вовсе: окно правки получает сохраненный секрет только для кнопки
/// «Проверить связь», а обратно отдает его отдельным полем.
/// </summary>
public partial class SettingsView
{
    /// <summary>
    /// Реестр соединений и запросов; null — база еще не открыта. Экран настроек живет и до
    /// этого, и падать из-за раздела, в который человек просто заглянул, ему незачем.
    /// </summary>
    private Database.IntegrationsRegistry? Integrations => _registry?.Integrations;

    // ── Соединения ────────────────────────────────────────────────────────────

    private void BuildIntegrationsConnections()
    {
        var rows = IntegrationsConnectionsTab.Children;
        rows.Clear();

        if (Integrations is null)
        {
            rows.Add(Row("Соединения", "Список станет доступен, когда откроется база данных."));
            return;
        }

        var registry = Integrations;

        rows.Add(ActionsRow("Добавить", () => _ = EditConnectionAsync(IntegrationConnection.New())));

        var connections = registry.Connections();

        if (connections.Count == 0)
        {
            rows.Add(Row("Соединений пока нет",
                "Соединение — это «куда и под кем ходим»: адрес чужой программы, способ "
                + "авторизации и секрет. Настраивается один раз и служит всем запросам."));
            return;
        }

        foreach (var connection in connections)
        {
            rows.Add(ConnectionRow(registry, connection));
            rows.Add(Separator());
        }
    }

    private Border ConnectionRow(Database.IntegrationsRegistry registry, IntegrationConnection connection)
    {
        var used = registry.RequestsOf(connection.Id).Count;

        var description = $"{connection.Address} · {AuthText(connection.Auth)}"
                        + (registry.HasSecret(connection.Id) ? " · секрет сохранен" : "")
                        + (used > 0 ? $" · запросов: {used}" : "");

        var decision = connection.BaseAddress is null
            ? EgressDecision.Deny(EgressDenyReason.BadAddress, "адрес не разобрался")
            : EgressGate.Check(connection.BaseAddress);

        var policy = decision.Allowed
            ? "Политика: адрес разрешен"
            : "Политика: " + decision.Explanation;

        return CardRow(connection.Name, description, policy,
            edit:   () => _ = EditConnectionAsync(connection),
            delete: () => _ = DeleteConnectionAsync(connection));
    }

    private async Task EditConnectionAsync(IntegrationConnection card)
    {
        if (VisualRoot is not Window win || Integrations is null) return;
        if (!AllowIntegrations(Core.Models.PermAction.IntegrationsSetup)) return;

        var registry = Integrations;
        var hasSecret = card.Id > 0 && registry.HasSecret(card.Id);
        var secret    = hasSecret ? registry.Secret(card.Id) : "";

        var result = await new IntegrationConnectionDialog(card, hasSecret, secret).ShowDialog<
            ConnectionDialogResult?>(win);

        if (result is null) return;

        registry.Save(result.Connection, result.Secret);
        BuildIntegrationsConnections();
    }

    private async Task DeleteConnectionAsync(IntegrationConnection connection)
    {
        if (VisualRoot is not Window win || Integrations is null) return;
        if (!AllowIntegrations(Core.Models.PermAction.IntegrationsSetup)) return;

        var registry = Integrations;
        var used     = registry.RequestsOf(connection.Id);

        // Соединение с запросами не удаляется: унести чужие карточки молча хуже, чем отказать.
        if (used.Count > 0)
        {
            await ConfirmDialog.Info(
                    $"Соединение «{connection.Name}» используют запросы ({used.Count}): "
                    + string.Join(", ", used.Select(r => r.Name))
                    + ".\n\nУдалите их или переключите на другое соединение.")
                .ShowDialog(win);
            return;
        }

        var confirmed = await new ConfirmDialog(
            $"Удалить соединение «{connection.Name}»? Сохраненный секрет тоже будет удален.")
            .ShowDialog<bool>(win);

        if (confirmed != true) return;

        registry.Delete(connection.Id);
        BuildIntegrationsConnections();
    }

    private static string AuthText(ConnectionAuth auth) => auth switch
    {
        ConnectionAuth.Header => "ключ в заголовке",
        ConnectionAuth.Bearer => "токен",
        ConnectionAuth.Basic  => "имя и пароль",
        ConnectionAuth.Query  => "ключ в параметрах",
        _                     => "без авторизации",
    };

    // ── Запросы ───────────────────────────────────────────────────────────────

    private void BuildIntegrationsRequests()
    {
        var rows = IntegrationsRequestsTab.Children;
        rows.Clear();

        if (Integrations is null)
        {
            rows.Add(Row("Запросы", "Список станет доступен, когда откроется база данных."));
            return;
        }

        var registry = Integrations;
        var connections = registry.Connections();

        // Запрос без соединения отправить некуда, и заводить его бессмысленно.
        if (connections.Count == 0)
        {
            rows.Add(Row("Сначала нужно соединение",
                "Запрос уходит через соединение: адрес и секрет берутся оттуда. "
                + "Заведите соединение на соседней вкладке."));
            return;
        }

        rows.Add(ActionsRow("Добавить", () => _ = EditRequestAsync(IntegrationRequest.New()),
                            "Из cURL",   () => _ = ImportCurlAsync(),
                            PresetButton()));

        var requests = registry.Requests();

        if (requests.Count == 0)
        {
            rows.Add(Row("Запросов пока нет",
                "Запрос — это «что дергаем»: метод, путь, заголовки и тело. Самый быстрый "
                + "способ завести его — вставить строку cURL из документации чужой программы."));
            return;
        }

        foreach (var request in requests)
        {
            rows.Add(RequestRow(registry, request, connections));
            rows.Add(Separator());
        }
    }

    /// <summary>
    /// Кнопка «Из заготовки»: список готовых карточек. Заготовка не создает «другой» запрос —
    /// она заполняет тот же самый, и открывается он обычной карточкой, где видно, что именно
    /// заполнили (docs/ROADMAP-API.md, раздел 5).
    ///
    /// Список выпадающий, а не пять кнопок в ряд: заготовок будет больше, а строка действий
    /// одна на весь экран.
    /// </summary>
    private Control PresetButton()
    {
        var button = RowButton("Из заготовки", () => { });

        var menu = new MenuFlyout { Placement = PlacementMode.BottomEdgeAlignedLeft };

        foreach (var preset in RequestPresets.All)
        {
            var item = new MenuItem { Header = preset.Title };
            var made = preset;

            // Подсказка живет в подсказке пункта, а не второй строкой: строка «зачем это берут»
            // длиннее самого названия, и в меню она превратила бы список в простыню.
            ToolTip.SetTip(item, made.Hint);

            item.Click += (_, _) => _ = EditRequestAsync(made.Request);
            menu.Items.Add(item);
        }

        button.Flyout = menu;
        return button;
    }

    private Border RequestRow(Database.IntegrationsRegistry registry,
                              IntegrationRequest request,
                              IReadOnlyList<IntegrationConnection> connections)
    {
        var connection = connections.FirstOrDefault(c => c.Id == request.ConnectionId);

        var description = $"{request.Method.ToString().ToUpperInvariant()} {request.Path}"
                        + $" · {connection?.Name ?? "соединение не найдено"}";

        var extra = request.Paging == RequestPaging.None
            ? request.ItemsPath.Length > 0 ? "Записи в ответе: " + request.ItemsPath : ""
            : $"Разбивка на страницы: {PagingText(request.Paging)} · {request.PagingField}";

        // Когда запрос последний раз отработал — то самое, что подставляется в
        // {{последний_запуск}}. Без этой строки человек не знает, с какой границы пойдет
        // следующая выгрузка, и не понимает, почему пришло меньше, чем он ждал.
        if (registry.LastRun(request.Id) is { } last)
            extra += (extra.Length > 0 ? " · " : "")
                   + "Последний запуск: " + last.ToLocalTime().ToString("dd.MM.yyyy HH:mm");

        // Что запрос уносит из коллекции — в карточке, рядом с остальным. Строка «полей: 4»
        // отвечает на первый вопрос проверяющего и на первый же вопрос человека, вернувшегося к
        // обмену через полгода.
        if (registry.Outgoing(request.Id) is { } outgoing)
            extra += (extra.Length > 0 ? " · " : "") + "Уносит: " + outgoing.Describe();

        // Состояние исходящего направления — прямо в карточке. Человек, у которого обмен встал,
        // должен увидеть причину здесь, а не искать ее в журнале: «срок вышел» и «запрос
        // правили» лечатся по-разному.
        var trouble = request.Outward
            ? registry.OutwardTrouble(request, DateTimeOffset.Now)
            : null;

        if (request.Outward)
            extra += (extra.Length > 0 ? " · " : "") + "Направление: "
                   + (trouble ?? registry.Direction(request.Id)?.Describe() ?? "подтверждено");

        // «Что уйдет» есть у любого запроса — читающий тоже стоит посмотреть глазами перед
        // первым запуском. Подтверждение появляется только у исходящего: читающему подтверждать
        // нечего, и кнопка, которая ничего не делает, читается как поломка.
        var more = new List<(string Text, Action Click)>
        {
            ("Что уйдет", () => _ = PreviewRequestAsync(registry, request, connection)),
        };

        // «Что уносит» — только у исходящего: читающий запрос данные не отдает, и предлагать ему
        // набрать состав полей значило бы обещать отправку, которой не будет.
        if (request.Outward)
        {
            more.Add(("Что уносит", () => _ = EditOutgoingAsync(registry, request)));

            // Кнопка выбирается по тому, ДЕЙСТВУЕТ ли направление, а не по тому, существует ли
            // запись о нем. Запрос правили, вышел срок, направление простаивало — строка в базе
            // осталась, но разрешения нет, и человеку нужна ровно одна кнопка: подтвердить
            // заново. Предлагать в этом состоянии «Отозвать» значило бы просить его отменить то,
            // что уже не работает, и лишь потом разрешить снова.
            more.Add((registry.Direction(request.Id), trouble) switch
            {
                (null, _)  => ("Подтвердить",
                               () => _ = ConfirmDirectionAsync(registry, request)),
                (_, null)  => ("Отозвать",
                               () => _ = RevokeDirectionAsync(registry, request)),
                _          => ("Подтвердить заново",
                               () => _ = ConfirmDirectionAsync(registry, request)),
            });
        }

        return CardRow(request.Name, description, extra,
            edit:   () => _ = EditRequestAsync(request),
            delete: () => _ = DeleteRequestAsync(request),
            first:  ("В коллекцию", () => _ = RunToCollectionAsync(registry, request, connection)),
            more:   more);
    }

    /// <summary>
    /// Сухой прогон: собрать запрос целиком и показать, ничего не отправляя
    /// (docs/ROADMAP-API.md, 10.9).
    /// </summary>
    private async Task PreviewRequestAsync(Database.IntegrationsRegistry registry,
                                           IntegrationRequest request,
                                           IntegrationConnection? connection)
    {
        if (VisualRoot is not Window win) return;

        if (connection is null)
        {
            await ConfirmDialog.Info("У запроса не найдено соединение: адрес брать неоткуда.")
                               .ShowDialog(win);
            return;
        }

        // Тело собирается ровно тем же вызовом, что и при настоящей отправке: сухой прогон,
        // который показывает НЕ то тело, что уйдет, хуже, чем его отсутствие.
        var (payload, cannot) = Database.OutgoingSource.Build(
            registry, request, _databases?.Invoke() ?? []);

        if (cannot is { Length: > 0 })
        {
            await ConfirmDialog.Info($"Показать нечего: {cannot}").ShowDialog(win);
            return;
        }

        // Причина отказа берется у того же замка, что стоит на настоящей отправке, — иначе
        // сухой прогон однажды показал бы «уйдет», там где не уйдет.
        var preview = RequestRunner.Preview(
            connection, request, registry.Secret(connection.Id),
            VariableContext.Now(registry.LastRun(request.Id), connection.Address),
            Database.OutwardGuard.Refusal(registry, request, DateTimeOffset.Now),
            payload);

        await new RequestPreviewDialog(preview).ShowDialog(win);
    }

    /// <summary>
    /// Набрать состав того, что запрос уносит из коллекции (docs/ROADMAP-API.md, раздел 2).
    ///
    /// Право спрашивается как у любой другой настройки обмена: запускать настроенное можно и без
    /// него, а менять состав выгрузки — нельзя. Подтверждение направления сбрасывать отдельно не
    /// надо: состав входит в отпечаток запроса, и правка роняет его сама.
    /// </summary>
    private async Task EditOutgoingAsync(Database.IntegrationsRegistry registry,
                                         IntegrationRequest request)
    {
        if (VisualRoot is not Window win) return;
        if (!AllowIntegrations(Core.Models.PermAction.IntegrationsSetup)) return;

        var databases = _databases?.Invoke() ?? [];

        if (databases.Count == 0)
        {
            await ConfirmDialog.Info(
                    "Нет открытых баз данных: брать записи неоткуда.\n\n"
                    + "Подключите базу в разделе «Управление базой данных» и повторите.")
                .ShowDialog(win);
            return;
        }

        var options = databases
            .Select(m => new DbOption(m.DbId, m.Name, m.Constructor))
            .ToList();

        var choice = await new OutgoingBindingDialog(options, registry.Outgoing(request.Id))
            .ShowDialog<OutgoingChoice?>(win);

        if (choice is null) return;

        registry.SaveOutgoing(request.Id, choice.Binding);
        BuildIntegrationsRequests();
    }

    /// <summary>
    /// Подтвердить исходящее направление мастер-паролем. Пароль проверяет реестр (а он —
    /// политику): экран его не видит и не хранит.
    /// </summary>
    private async Task ConfirmDirectionAsync(Database.IntegrationsRegistry registry,
                                             IntegrationRequest request)
    {
        if (VisualRoot is not Window win) return;
        if (!AllowIntegrations(Core.Models.PermAction.IntegrationsOutward)) return;

        var what = $"{request.Name}: {request.Method.ToString().ToUpperInvariant()} {request.Path}";

        var done = await new DirectionConfirmDialog(what, (password, days) =>
        {
            registry.ConfirmDirection(request, password, Utils.CurrentUser.DisplayName, days,
                                      DateTimeOffset.Now, out var trouble);

            if (trouble.Length > 0) return trouble;

            // След подтверждения — в журнале безопасности, а не в рабочем: это решение о
            // полномочии, и читает его проверяющий. Не записалось — говорим об этом сразу:
            // подтверждение без следа ничем не лучше отправки без следа.
            var written = Database.SecurityJournal.Add(Database.SecurityEntry.New(
                Database.SecurityEvent.Confirm, allowed: true,
                who: Utils.CurrentUser.DisplayName, scenario: "",
                address: request.Path, fields: "", rows: 0, bytes: 0, bodyHash: "",
                explanation: $"подтверждено направление «{request.Name}» на {days} дней",
                dbId: 0, at: DateTimeOffset.Now, confirmed: Utils.CurrentUser.DisplayName));

            return written
                ? null
                : "подтверждение записано, но след в журнале безопасности не лег — сообщите "
                + "ответственному за интеграции";
        }).ShowDialog<bool>(win);

        if (done) BuildIntegrationsRequests();
    }

    /// <summary>Отозвать подтверждение — руками, до срока.</summary>
    private async Task RevokeDirectionAsync(Database.IntegrationsRegistry registry,
                                            IntegrationRequest request)
    {
        if (VisualRoot is not Window win) return;
        if (!AllowIntegrations(Core.Models.PermAction.IntegrationsOutward)) return;

        var yes = await new ConfirmDialog(
            $"Отозвать подтверждение у запроса «{request.Name}»? Он перестанет отправлять данные "
            + "наружу, а вернуть разрешение можно только мастер-паролем.",
            "Отозвать").ShowDialog<bool>(win);

        if (!yes) return;

        registry.RevokeDirection(request.Id);

        Database.SecurityJournal.Add(Database.SecurityEntry.New(
            Database.SecurityEvent.Revoke, allowed: true,
            who: Utils.CurrentUser.DisplayName, scenario: "",
            address: request.Path, fields: "", rows: 0, bytes: 0, bodyHash: "",
            explanation: $"отозвано направление «{request.Name}»",
            dbId: 0, at: DateTimeOffset.Now));

        BuildIntegrationsRequests();
    }

    private async Task EditRequestAsync(IntegrationRequest card)
    {
        if (VisualRoot is not Window win || Integrations is null) return;
        if (!AllowIntegrations(Core.Models.PermAction.IntegrationsSetup)) return;

        var registry    = Integrations;
        var connections = registry.Connections();
        if (connections.Count == 0) return;

        var result = await new IntegrationRequestDialog(card, connections, registry.Secret)
            .ShowDialog<IntegrationRequest?>(win);

        if (result is null) return;

        registry.SaveRequest(result);
        BuildIntegrationsRequests();
    }

    private async Task DeleteRequestAsync(IntegrationRequest request)
    {
        if (VisualRoot is not Window win || Integrations is null) return;
        if (!AllowIntegrations(Core.Models.PermAction.IntegrationsSetup)) return;

        // Запрос с шагом сценария не удаляется — как и соединение с запросами: сценарий с дырой
        // посреди списка шагов хуже отказа.
        var used = Integrations.ScenariosUsing(request.Id);

        if (used.Count > 0)
        {
            await ConfirmDialog.Info(
                    $"Запрос «{request.Name}» выполняют сценарии ({used.Count}): "
                    + string.Join(", ", used.Select(s => s.Name))
                    + ".\n\nУберите шаг из сценария или удалите сценарий.")
                .ShowDialog(win);
            return;
        }

        var confirmed = await new ConfirmDialog($"Удалить запрос «{request.Name}»?")
            .ShowDialog<bool>(win);

        if (confirmed != true) return;

        Integrations!.DeleteRequest(request.Id);
        BuildIntegrationsRequests();
    }

    /// <summary>
    /// Вставка cURL: строка разбирается, под нее подбирается соединение с тем же адресом либо
    /// заводится новое, а карточка запроса открывается на правку. Секрет из строки уходит в
    /// соединение, а не в заголовки запроса.
    /// </summary>
    private async Task ImportCurlAsync()
    {
        if (VisualRoot is not Window win || Integrations is null) return;
        if (!AllowIntegrations(Core.Models.PermAction.IntegrationsSetup)) return;

        var import = await new CurlImportDialog().ShowDialog<CurlImport?>(win);
        if (import is null) return;

        var registry = Integrations;

        // Соединение с тем же адресом уже есть — берем его: плодить одинаковые адреса значит
        // потом менять токен в нескольких местах.
        var existing = registry.Connections().FirstOrDefault(
            c => string.Equals(c.Address.TrimEnd('/'), import.Address.TrimEnd('/'),
                               StringComparison.OrdinalIgnoreCase));

        IntegrationConnection connection;
        try
        {
            connection = existing ?? registry.Save(
                import.ToConnection(NameFor(import.Address)),
                import.Secret.Length > 0 ? import.Secret : "");
        }
        catch (InvalidOperationException error)
        {
            // Разбор дал адрес, который не годится в соединение. Редкий случай, но человек
            // должен увидеть причину, а не молчание в ответ на нажатие.
            await ConfirmDialog.Info("Соединение не заведено: " + error.Message).ShowDialog(win);
            return;
        }

        var request = await new IntegrationRequestDialog(
                import.ToRequest(NameFor(import.Path), connection.Id),
                registry.Connections(),
                registry.Secret)
            .ShowDialog<IntegrationRequest?>(win);

        // Запрос не сохранили — убираем и соединение, если завели его прямо сейчас и им никто
        // не пользуется. Иначе отмена оставляла бы за собой мусор, которого человек не заводил.
        if (request is null)
        {
            if (existing is null && registry.RequestsOf(connection.Id).Count == 0)
                registry.Delete(connection.Id);
        }
        else
        {
            registry.SaveRequest(request);
        }

        BuildIntegrationsConnections();
        BuildIntegrationsRequests();
    }

    /// <summary>Имя по умолчанию — из адреса: человек его тут же поправит, но пустым оставлять нельзя.</summary>
    private static string NameFor(string address)
    {
        var text = address.Replace("https://", "", StringComparison.OrdinalIgnoreCase)
                          .Replace("http://",  "", StringComparison.OrdinalIgnoreCase)
                          .Trim('/');

        return text.Length == 0 ? "Из cURL" : text;
    }

    private static string PagingText(RequestPaging paging) => paging switch
    {
        RequestPaging.Page   => "номер страницы",
        RequestPaging.Offset => "сдвиг от начала",
        RequestPaging.Cursor => "метка следующей порции",
        _                    => "нет",
    };

    // Строки списка (ActionsRow, CardRow) и сами контролы — в Controls/Widgets/SettingRows.cs:
    // вид у всех вкладок раздела один, и держать его в одном месте дешевле, чем
    // сверять шесть.
}
