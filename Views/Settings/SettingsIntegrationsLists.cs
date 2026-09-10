using Avalonia.Controls;
using Avalonia.Layout;
using SmartBase.Controls.Dialogs;
using SmartBase.Integrations;

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

        rows.Add(Actions("Добавить", () => _ = EditConnectionAsync(IntegrationConnection.New())));

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
            rows.Add(new Border { Classes = { "separator", "h" } });
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

        rows.Add(Actions("Добавить", () => _ = EditRequestAsync(IntegrationRequest.New()),
                         "Из cURL",   () => _ = ImportCurlAsync()));

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
            rows.Add(new Border { Classes = { "separator", "h" } });
        }
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

        return CardRow(request.Name, description, extra,
            edit:   () => _ = EditRequestAsync(request),
            delete: () => _ = DeleteRequestAsync(request),
            first:  ("В коллекцию", () => _ = RunToCollectionAsync(registry, request, connection)));
    }

    private async Task EditRequestAsync(IntegrationRequest card)
    {
        if (VisualRoot is not Window win || Integrations is null) return;

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

    // ── Строки списка ─────────────────────────────────────────────────────────

    /// <summary>Строка со списком действий сверху вкладки.</summary>
    private static Border Actions(string first, Action onFirst, string? second = null, Action? onSecond = null)
    {
        var buttons = new StackPanel
        {
            Orientation         = Orientation.Horizontal,
            Spacing             = 8,
            HorizontalAlignment = HorizontalAlignment.Left,
        };

        buttons.Children.Add(SmallButton(first, onFirst));
        if (second is not null && onSecond is not null) buttons.Children.Add(SmallButton(second, onSecond));

        return new Border { Classes = { "settingRow" }, Child = buttons };
    }

    /// <summary>
    /// Строка карточки: имя, пояснение, вторая строка мелким и кнопки справа.
    /// <paramref name="first"/> — главное действие карточки, если оно есть; стоит перед
    /// «Изменить», потому что нажимают его чаще, чем правят саму карточку.
    /// </summary>
    /// <paramref name="lead"/> — управление слева от имени (тумблер сценария): выключить обмен
    /// нужно чаще, чем править его, и ради этого не должно открываться окно.
    private static Border CardRow(string title, string description, string extra,
                                  Action edit, Action delete,
                                  (string Text, Action Click)? first = null,
                                  Control? lead = null)
    {
        var text = new StackPanel { Spacing = 4, VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock { Classes = { "title" }, Text = title });
        text.Children.Add(Note(description));
        if (extra.Length > 0) text.Children.Add(Note(extra));

        Control left = text;

        if (lead is not null)
        {
            var line = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,12,*") };
            Grid.SetColumn(lead, 0);
            Grid.SetColumn(text, 2);
            line.Children.Add(lead);
            line.Children.Add(text);
            left = line;
        }

        var buttons = new StackPanel
        {
            Orientation       = Orientation.Horizontal,
            Spacing           = 8,
            VerticalAlignment = VerticalAlignment.Center,
        };

        if (first is { } main) buttons.Children.Add(SmallButton(main.Text, main.Click));
        buttons.Children.Add(SmallButton("Изменить", edit));
        buttons.Children.Add(SmallButton("Удалить", delete));

        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto"),
        };

        left.Margin = new Avalonia.Thickness(0, 0, 32, 0);
        Grid.SetColumn(left, 0);
        Grid.SetColumn(buttons, 1);
        grid.Children.Add(left);
        grid.Children.Add(buttons);

        return new Border { Classes = { "settingRow" }, Child = grid };
    }

    private static Button SmallButton(string text, Action click)
    {
        var button = new Button
        {
            Content = text,
            Height  = 30,
            Padding = new Avalonia.Thickness(12, 0),
        };

        button.Click += (_, _) => click();
        return button;
    }
}
