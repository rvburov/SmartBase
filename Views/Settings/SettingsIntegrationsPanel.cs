using Avalonia.Controls;
using Avalonia.Layout;
using SmartBase.Integrations;
using SmartBase.Settings;
using SmartBase.Utils;

namespace SmartBase.Views.Settings;

/// <summary>
/// Раздел «Настройки → Интеграции» (partial SettingsView, без ViewModel — как «Календарь» и
/// «Задачи»). Раздел один, а внутри вкладки; переключатель между ними — выпадающий список в
/// навбаре, как виды календаря.
///
/// Пока вкладка одна — «Безопасность». Она показывает то, что программе уже известно:
/// действующий режим, судьбу каждого источника политики, разрешенные адреса, папки и потолки.
/// Только показывает: политика правится ВНЕ программы, и ни одной кнопки, меняющей права,
/// здесь нет и не будет — в этом весь смысл контура (docs/ROADMAP-API.md, раздел 10).
///
/// Единственное действие — «Перечитать»: администратор поправил файл, а перезапускать
/// программу ради этого незачем.
/// </summary>
public partial class SettingsView
{
    // Какая вкладка открыта. Пустая строка — раздел еще не открывали в этот заход.
    private string _integrationsTab = "";

    // ── Вход в раздел и переключение вкладок ──────────────────────────────────

    private void BuildIntegrationsPanel()
    {
        var tab = IntegrationTabs.Resolve(AppSettings.LoadIntegrationsTab()).Key;

        HeaderBar.SetIntegrationTabs(tab);
        ShowIntegrationTab(tab);
    }

    /// <summary>
    /// Показать вкладку. Выбор запоминается: обмен настраивают в несколько заходов, и
    /// возвращаться каждый раз к первой вкладке утомительно.
    /// </summary>
    private void ShowIntegrationTab(string key)
    {
        _integrationsTab = IntegrationTabs.Resolve(key).Key;
        AppSettings.SaveIntegrationsTab(_integrationsTab);

        IntegrationsConnectionsTab.IsVisible = _integrationsTab == "connections";
        IntegrationsRequestsTab.IsVisible    = _integrationsTab == "requests";
        IntegrationsSecurityTab.IsVisible    = _integrationsTab == "security";

        // Список строится при каждом показе: карточки правятся в окнах, и вернуться на вкладку
        // можно уже с другим их составом.
        if (_integrationsTab == "connections") BuildIntegrationsConnections();
        if (_integrationsTab == "requests")    BuildIntegrationsRequests();
        if (_integrationsTab == "security")    BuildIntegrationsSecurity();
    }

    // ── Вкладка «Безопасность» ────────────────────────────────────────────────

    private void BuildIntegrationsSecurity()
    {
        var rows = IntegrationsSecurityTab.Children;
        rows.Clear();

        var loaded = EgressSetup.Last;
        var policy = loaded.Set;
        var today  = DateOnly.FromDateTime(DateTime.Today);

        // 1. Что вообще можно. Первой строкой и человеческим языком: проверяющий должен
        //    получить ответ здесь, а не собирать его из таблиц ниже.
        Add(rows, Row("Состояние контура", StateText(policy, today)));

        // 2. Откуда взялись ограничения и что стало с каждым источником. Пересечение названо
        //    прямо: без этого таблица источников читается как «хоть один разрешил».
        Add(rows, Row("Источники политики",
            "Разрешено то, что разрешают ВСЕ источники разом. Источник, который не прочитался, "
            + "запирает контур, а не выпадает из расчета — иначе пропажа файла расширяла бы права.",
            SourcesList(loaded)));

        // 3. Куда класть файл. Без этого администратор гадает, а гадание кончается тем, что
        //    политику кладут не туда и считают, что она есть.
        Add(rows, Row("Где программа ищет политику",
            $"Имя файла — {PolicySources.FileName}, подпись рядом с ним "
            + $"({PolicySources.FileName}{PolicySources.SignatureExtension}).",
            PlacesList()));

        // 4. Белый список — по источникам, а не общим списком: пересечение адресов нельзя
        //    показать одной строкой, а сводить его к «объединению» значило бы соврать.
        Add(rows, Row("Разрешенные адреса",
            "Имя должно подойти под список каждого источника, и фактический адрес соединения — тоже.",
            AddressList(policy)));

        Add(rows, Row("Папки для обмена файлами",
            "Куда разрешено класть и откуда брать файлы. Все остальные папки закрыты.",
            FolderList(policy)));

        Add(rows, Row("Ограничения", "", LimitsList(policy)), last: true);

        // 5. Единственное действие раздела.
        var reread = new Button
        {
            Content             = "Перечитать политику",
            Height              = 30,
            Padding             = new Avalonia.Thickness(12, 0),
            HorizontalAlignment = HorizontalAlignment.Left,
            Margin              = new Avalonia.Thickness(0, 12, 0, 0),
        };

        reread.Click += (_, _) =>
        {
            EgressSetup.Init();
            BuildIntegrationsSecurity();
        };

        rows.Add(reread);
    }

    // ── Тексты ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Одна фраза о том, что сейчас можно. Порядок причин тот же, что у шлюза: сборка, потом
    /// наличие политики, потом срок, потом режим, — иначе экран объяснял бы одно, а отказ
    /// приходил бы по другой причине.
    /// </summary>
    private static string StateText(EgressPolicySet policy, DateOnly today)
    {
        if (EgressGate.ClosedLoopBuild)
            return "Сборка для закрытого контура: сетевого клиента в программе нет, "
                 + "исходящие обращения невозможны.";

        if (!policy.Any)
            return "Политика организации не найдена — исходящие обращения запрещены. "
                 + "Это состояние по умолчанию: без разрешения наружу не уходит ничего.";

        if (policy.Expired(today))
            return $"Срок действия политики истек {policy.ValidUntil:dd.MM.yyyy} — "
                 + "исходящие обращения запрещены до обновления файла.";

        return policy.Mode switch
        {
            EgressMode.Internal =>
                "Только сеть организации: адрес должен быть в белом списке, а соединение — "
                + "идти на внутренний адрес. Обращения в интернет запрещены.",

            EgressMode.External =>
                "Внешние узлы из белого списка: разрешены только поименованные в политике адреса.",

            _ => "Изолированный режим: исходящих обращений нет вовсе.",
        };
    }

    private static Control SourcesList(PolicyLoadResult loaded)
    {
        if (loaded.Reads.Count == 0)
            return Note("Ни одного источника не найдено.");

        var list = new StackPanel { Spacing = 6 };

        foreach (var read in loaded.Reads)
            list.Children.Add(Note($"{(read.Accepted ? "принят" : "отклонен")} · {read.Source} — {read.Note}"));

        return list;
    }

    private static Control PlacesList()
    {
        var list = new StackPanel { Spacing = 6 };

        foreach (var place in PolicySources.Places())
            list.Children.Add(Note(
                $"{place.Name} — {place.Where}" + (place.SignatureRequired ? " · подпись обязательна" : "")));

        return list;
    }

    private static Control AddressList(EgressPolicySet policy)
    {
        if (!policy.Any) return Note("Список пуст: разрешенных адресов нет.");

        var list = new StackPanel { Spacing = 6 };

        foreach (var source in policy.Sources)
        {
            list.Children.Add(Note($"{source.Source}:"));
            list.Children.Add(Note("    имена — " + Join(source.Hosts)));
            list.Children.Add(Note("    сети — "  + Join(source.Networks)));
        }

        return list;
    }

    private static Control FolderList(EgressPolicySet policy)
    {
        if (!policy.Any) return Note("Папок не разрешено.");

        var list = new StackPanel { Spacing = 6 };

        foreach (var source in policy.Sources)
            list.Children.Add(Note($"{source.Source} — " + Join(source.Folders)));

        return list;
    }

    private static Control LimitsList(EgressPolicySet policy)
    {
        if (!policy.Any) return Note("Ограничения не заданы: обмен запрещен целиком.");

        var list = new StackPanel { Spacing = 6 };

        list.Children.Add(Note($"строк за один запуск — не больше {policy.MaxRowsPerRun:N0}"));
        list.Children.Add(Note($"объем в сутки — не больше {Size(policy.MaxBytesPerDay)}"));
        list.Children.Add(Note($"запусков в час — не больше {policy.MaxRunsPerHour:N0}"));
        list.Children.Add(Note("срок действия — "
            + (policy.ValidUntil is { } until ? until.ToString("dd.MM.yyyy") : "без срока")));
        list.Children.Add(Note("дописывать адреса самому — "
            + (policy.UserMayAddHosts ? "разрешено" : "запрещено")));

        return list;
    }

    // ── Мелочи оформления ─────────────────────────────────────────────────────

    private static string Join(IReadOnlyList<string> values) =>
        values.Count == 0 ? "не заданы" : string.Join(", ", values);

    private static string Size(long bytes) => bytes switch
    {
        < 1024L * 1024        => $"{bytes / 1024.0:N0} КБ",
        < 1024L * 1024 * 1024 => $"{bytes / 1024.0 / 1024:N1} МБ",
        _                     => $"{bytes / 1024.0 / 1024 / 1024:N2} ГБ",
    };

    private static TextBlock Note(string text) => new()
    {
        Classes   = { "desc" },
        Text      = text,
        TextWrapping = Avalonia.Media.TextWrapping.Wrap,
    };

    /// <summary>Строка раздела: заголовок, пояснение и, если нужно, содержимое под ними.</summary>
    private static Border Row(string title, string description, Control? content = null)
    {
        var block = new StackPanel { Spacing = 6 };
        block.Children.Add(new TextBlock { Classes = { "title" }, Text = title });

        if (description.Length > 0) block.Children.Add(Note(description));
        if (content is not null)
        {
            content.Margin = new Avalonia.Thickness(0, 4, 0, 0);
            block.Children.Add(content);
        }

        return new Border { Classes = { "settingRow" }, Child = block };
    }

    /// <summary>Строка и разделитель под ней — как в разметке остальных разделов.</summary>
    private static void Add(Avalonia.Controls.Controls rows, Border row, bool last = false)
    {
        rows.Add(row);
        if (!last) rows.Add(new Border { Classes = { "separator", "h" } });
    }
}
