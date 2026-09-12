using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Platform.Storage;
using SmartBase.Controls.Dialogs;
using SmartBase.Integrations;
using SmartBase.Settings;
using SmartBase.Utils;

namespace SmartBase.Views.Settings;

/// <summary>
/// Раздел «Настройки → Интеграции» (partial SettingsView, без ViewModel — как «Календарь» и
/// «Задачи»). Раздел один, а внутри вкладки; переключатель между ними — выпадающий список в
/// навбаре, как виды календаря.
///
/// Переключение вкладок и вкладка «Безопасность» живут здесь; списки соединений и запросов —
/// в <c>SettingsIntegrationsLists</c>, обмен по кнопке — в <c>SettingsIntegrationsRun</c>,
/// папка-приемник — в <c>SettingsIntegrationsInbox</c>, сценарии — в
/// <c>SettingsIntegrationsScenarios</c>, журнал запусков — в
/// <c>SettingsIntegrationsJournal</c>.
///
/// «Безопасность» показывает то, что программе уже известно: действующий режим, судьбу каждого
/// источника политики, разрешенные адреса, папки и потолки. Только показывает: политика правится
/// ВНЕ программы, и ни одной кнопки, меняющей права, здесь нет и не будет — в этом весь смысл
/// контура (docs/ROADMAP-API.md, раздел 10).
///
/// Действий у вкладки два, и ни одно ничего не разрешает. «Перечитать политику» — администратор
/// поправил файл, а перезапускать программу ради этого незачем. «Паспорт интеграций» выписывает
/// показанное файлом для службы безопасности (10.16), добавляя к нему то, чего на экране нет:
/// подтвержденные направления и сводку по журналу за месяц.
/// </summary>
public partial class SettingsView
{
    // Какая вкладка открыта. Пустая строка — раздел еще не открывали в этот заход.
    private string _integrationsTab = "";

    // ── Права на обмен (docs/ROADMAP-API.md, 10.8) ────────────────────────────

    /// <summary>
    /// Разрешено ли действие обмена текущему пользователю.
    ///
    /// Спрашиваются ВСЕ открытые базы разом — тем же правилом, что и у политики: «разрешено то,
    /// что разрешают все». Настройки обмена общие на машину, а ответ ложится в ту базу, которую
    /// выберут при запуске; спроси одну — и человек, которому соседняя база настройку запретила,
    /// обошел бы запрет, открыв ту, где она разрешена.
    ///
    /// Ни одной базы не открыто — ответ «нельзя»: спрашивать некого, а обмен, которому некуда
    /// класть ответ, настраивать незачем (то же говорит и сам запуск).
    ///
    /// Права уровня базы, поэтому коллекция не передается: у настройки обмена ее и нет.
    /// </summary>
    private bool CanIntegrations(Core.Models.PermAction action)
    {
        var bases = _databases?.Invoke() ?? [];
        return bases.Count > 0 && bases.All(d => d.Permissions.Can(action));
    }

    /// <summary>
    /// Пустить действие или объяснить отказ окном — как и везде в программе: запрещенная кнопка
    /// остается нажимаемой и называет причину, иначе интерфейс молчит и выглядит сломанным
    /// (docs/developer/architecture/users-permissions.md).
    /// </summary>
    private bool AllowIntegrations(Core.Models.PermAction action)
    {
        if (CanIntegrations(action)) return true;

        PermissionNotice.Show(this, action);
        return false;
    }

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
        IntegrationsScenariosTab.IsVisible   = _integrationsTab == "scenarios";
        IntegrationsInboxTab.IsVisible       = _integrationsTab == "inbox";
        IntegrationsSecurityTab.IsVisible    = _integrationsTab == "security";
        IntegrationsJournalTab.IsVisible     = _integrationsTab == "journal";

        // Список строится при каждом показе: карточки правятся в окнах, и вернуться на вкладку
        // можно уже с другим их составом.
        if (_integrationsTab == "connections") BuildIntegrationsConnections();
        if (_integrationsTab == "requests")    BuildIntegrationsRequests();
        if (_integrationsTab == "scenarios")   BuildIntegrationsScenarios();
        if (_integrationsTab == "inbox")       BuildIntegrationsInbox();
        if (_integrationsTab == "security")    BuildIntegrationsSecurity();
        if (_integrationsTab == "journal")     BuildIntegrationsJournal();
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

        Add(rows, Row("Ограничения", "", LimitsList(policy)));

        // Зеркало — единственное место контура, где наружу от программы уходят САМИ данные, и
        // молчать о нем нельзя: человек должен знать, что копия отправленного где-то лежит.
        Add(rows, Row("Зеркало исходящего",
            "Копия всего, что уходит наружу, в папке аудита организации. В отличие от журнала "
            + "безопасности, копия содержит сами данные — поэтому папку называет организация, "
            + "а не программа и не пользователь.",
            MirrorList(policy)));

        // 5. Кто сейчас может уносить данные наружу. До этой строки экран отвечал только на
        //    вопрос «что разрешено вообще»; проверяющего же занимает «кому разрешено здесь и
        //    сейчас, с чьего подтверждения и до какого числа».
        Add(rows, Row("Действующие направления",
            "Исходящим считается запрос, уносящий данные наружу; читающему подтверждать нечего. "
            + "Подтверждение выдается мастер-паролем организации, на срок и с отсчетом простоя.",
            DirectionsList()));

        // 6. Метки — вторая половина правила «что именно уходит» (10.6): белый список отвечает
        //    «куда», метки — «что». Одно без другого не читается.
        Add(rows, Row("Метки конфиденциальности",
            "Метка базы и метки отдельных свойств. «Закрытый контур» запрещает исходящие "
            + "независимо от политики, режима и прав.",
            LabelsList()));

        // 7. Сколько раз замок сработал. Ноль — тоже ответ: контур либо никому не мешает, либо
        //    в нем не работают вовсе, и отличить одно от другого помогает соседняя вкладка.
        Add(rows, Row("Блокировок за неделю",
            "Отказы, записанные в журнал безопасности за последние семь дней.",
            BlocksList()), last: true);

        // 5. Два действия раздела, и ни одно из них ничего не разрешает: одно перечитывает
        //    политику, второе выписывает то же самое файлом.
        var reread = SmallButton("Перечитать политику", () =>
        {
            EgressSetup.Init();
            BuildIntegrationsSecurity();
        });

        var passport = SmallButton("Паспорт интеграций…", () => _ = ExportPassportAsync());

        rows.Add(new StackPanel
        {
            Orientation         = Orientation.Horizontal,
            Spacing             = 8,
            HorizontalAlignment = HorizontalAlignment.Left,
            Margin              = new Avalonia.Thickness(0, 12, 0, 0),
            Children            = { reread, passport },
        });

        // Сказано вслух, потому что вкладка без единого поля ввода читается как «еще не
        // доделали». Она не не доделана — в этом ее устройство.
        rows.Add(new TextBlock
        {
            Classes      = { "desc" },
            Text         = "Ни одного поля ввода здесь нет и не будет: политику правит "
                         + "ответственный за интеграции, вне программы.",
            TextWrapping = Avalonia.Media.TextWrapping.Wrap,
            Margin       = new Avalonia.Thickness(0, 8, 0, 0),
        });
    }

    // ── Паспорт интеграций ────────────────────────────────────────────────────

    /// <summary>
    /// Выписать паспорт интеграций файлом (docs/ROADMAP-API.md, 10.16) — то, что отдают службе
    /// безопасности: что разрешено, с чем программа соединяется, кому разрешено уносить данные
    /// и что уходило за месяц.
    ///
    /// Собирается на UI-потоке и намеренно: сведения берутся у реестра (общий контекст app.db) и
    /// у открытых баз. Журнал за месяц читается страницами, и это единственное место, где
    /// заметное ожидание вообще возможно — поэтому кнопка гасится на время работы.
    /// </summary>
    private async Task ExportPassportAsync()
    {
        if (VisualRoot is not Window win) return;

        if (Integrations is not { } registry)
        {
            await ConfirmDialog.Info("Настройки интеграций еще не открылись — повторите.")
                               .ShowDialog(win);
            return;
        }

        if (_passporting) return;
        _passporting = true;

        try
        {
            var databases = (_databases?.Invoke() ?? [])
                .Select(m => new Database.PassportDatabase(
                    m.DbId, m.Name,
                    m.Constructor.GetDatabaseLabel(),
                    m.Constructor.LabeledFieldCount()))
                .ToList();

            var text = Database.IntegrationsPassport.Build(
                registry, EgressSetup.Last, Database.SecurityJournal.Current, databases,
                CurrentUser.DisplayName, DateTimeOffset.Now,
                ingress:     AppSettings.LoadIngressMode(),
                ingressPort: AppSettings.LoadIngressPort());

            var file = await win.StorageProvider.SaveFilePickerAsync(
                new Avalonia.Platform.Storage.FilePickerSaveOptions
                {
                    Title               = "Куда сохранить паспорт интеграций",
                    SuggestedFileName   = $"Паспорт интеграций {DateTime.Now:yyyy-MM-dd HH-mm}.txt",
                    DefaultExtension    = "txt",
                    ShowOverwritePrompt = true,
                    FileTypeChoices     =
                    [
                        new Avalonia.Platform.Storage.FilePickerFileType("Текстовый файл")
                        {
                            Patterns = ["*.txt"],
                        },
                    ],
                });

            if (file?.TryGetLocalPath() is not { Length: > 0 } path) return;

            // С меткой порядка байтов: файл открывают Блокнотом, а он без метки читает русский
            // текст как набор знаков вопроса.
            await File.WriteAllTextAsync(path, text, new System.Text.UTF8Encoding(true));

            Serilog.Log.Information("Паспорт интеграций выписан: {Path:l}", path);

            await ConfirmDialog.Info($"Паспорт интеграций сохранен.\n\nФайл: {path}")
                               .ShowDialog(win);
        }
        catch (Exception error)
        {
            Serilog.Log.Warning(error, "Паспорт интеграций не выписался");

            await ConfirmDialog.Info("Сохранить паспорт не удалось.\n\n" + error.Message)
                               .ShowDialog(win);
        }
        finally { _passporting = false; }
    }

    // Паспорт читает журнал за месяц: второй щелчок дал бы вторую работу и второе окно.
    private bool _passporting;

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
        var list = new StackPanel { Spacing = 6 };

        // Момент применения — первой строкой и раньше самих источников. Вопрос звучит как
        // «с какого момента действует то, что я вижу», и ответ на него не время правки файла:
        // файл могли поправить час назад, а «Перечитать политику» никто не нажимал.
        list.Children.Add(Note(EgressSetup.AppliedAt is { } applied
            ? $"Применено {applied.ToLocalTime():dd.MM.yyyy HH:mm}"
              + (loaded.Reads.Count > 1 ? $" · пересечение {loaded.Reads.Count} источников" : "")
            : "Политика еще не читалась за этот заход."));

        if (loaded.Reads.Count == 0)
        {
            list.Children.Add(Note("Ни одного источника не найдено."));
            return list;
        }

        foreach (var read in loaded.Reads)
            list.Children.Add(Note($"{(read.Accepted ? "принят" : "отклонен")} · {read.Source} — {read.Note}"));

        return list;
    }

    /// <summary>
    /// Кто может уносить данные наружу прямо сейчас. Показываются ВСЕ исходящие запросы, а не
    /// только подтвержденные: «заведено, но не подтверждено» — такой же ответ проверяющему, и
    /// без него список выглядел бы полным, когда он пуст просто потому, что подтверждать
    /// забыли.
    /// </summary>
    private Control DirectionsList()
    {
        if (Integrations is not { } registry)
            return Note("Список станет доступен, когда откроется база данных.");

        var outward = registry.Requests().Where(r => r.Outward).ToList();

        if (outward.Count == 0)
            return Note("Запросов, уносящих данные наружу, не заведено ни одного.");

        var now  = DateTimeOffset.Now;
        var list = new StackPanel { Spacing = 6 };

        // Объем за месяц — по рабочему журналу и по тому же ключу направления, по которому
        // считается суточный потолок. Журнал подрезается, поэтому цифра честно «за то, что
        // помнит журнал», а не «за календарный месяц».
        var spent = Database.RunJournal.Current?.SpentByDirection(now.AddDays(-30))
                    ?? [];

        foreach (var request in outward)
        {
            var direction = registry.Direction(request.Id);
            var trouble   = registry.OutwardTrouble(request, now);
            var bytes     = spent.FirstOrDefault(s => s.Direction == request.Id).Bytes;

            list.Children.Add(Note($"{request.Name} — "
                + $"{request.Method.ToString().ToUpperInvariant()} {request.Path}"));

            if (direction is null)
            {
                var idle = Note("    не подтверждено — отправлять не может");
                idle.Foreground = Ink("BrushRed", "#FF3B30");
                list.Children.Add(idle);
                continue;
            }

            list.Children.Add(Note($"    подтвердил {direction.Confirmed} · "
                + $"до {direction.Until.ToLocalTime():dd.MM.yyyy} · "
                + $"за месяц {Size(bytes)}"));

            var state = Note(trouble is null
                ? "    действует"
                : "    не действует: " + trouble);

            if (trouble is not null) state.Foreground = Ink("BrushRed", "#FF3B30");
            list.Children.Add(state);
        }

        return list;
    }

    /// <summary>
    /// Метки открытых баз и счетчик помеченных свойств. Список свойств разворачивается по
    /// нажатию: цифра нужна всегда, а имена — редко, и в базе их бывают сотни.
    /// </summary>
    private Control LabelsList()
    {
        var bases = _databases?.Invoke() ?? [];

        if (bases.Count == 0)
            return Note("Ни одной базы не открыто — показывать нечего.");

        var list   = new StackPanel { Spacing = 6 };
        var fields = 0;

        foreach (var manager in bases)
        {
            var label = manager.Constructor.GetDatabaseLabel();
            fields += manager.Constructor.LabeledFieldCount();

            var line = Note($"База «{manager.Name}» — {Core.Models.Confidentiality.Title(label)}"
                + (label == Core.Models.ConfidentialityLabel.Closed
                    ? ": исходящие запрещены"
                    : ""));

            if (label == Core.Models.ConfidentialityLabel.Closed)
                line.Foreground = Ink("BrushRed", "#FF3B30");

            list.Children.Add(line);
        }

        list.Children.Add(Note($"Полей с метками: {fields:N0}."));

        if (fields == 0) return list;

        list.Children.Add(new StackPanel
        {
            Orientation         = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Left,
            Margin              = new Avalonia.Thickness(0, 4, 0, 0),
            Children            =
            {
                SmallButton(_labeledFieldsShown ? "Скрыть список" : "Показать список", () =>
                {
                    _labeledFieldsShown = !_labeledFieldsShown;
                    BuildIntegrationsSecurity();
                }),
            },
        });

        if (!_labeledFieldsShown) return list;

        foreach (var manager in bases)
            foreach (var (collection, field, label) in manager.Constructor.LabeledFields())
                list.Children.Add(Note(
                    $"    {manager.Name} · {collection} · {field} — "
                    + Core.Models.Confidentiality.Title(label)));

        return list;
    }

    // Развернут ли список помеченных свойств. Живет в пределах захода: это лупа, а не настройка.
    private bool _labeledFieldsShown;

    /// <summary>
    /// Сколько отказов легло в журнал безопасности за неделю и кнопка перехода к ним. Ноль
    /// показывается словами, а не пустотой: пустая строка читается как «не считали».
    /// </summary>
    private Control BlocksList()
    {
        var list = new StackPanel { Spacing = 6 };

        if (Database.SecurityJournal.Current is not { } journal)
        {
            list.Children.Add(Note(
                "Журнал безопасности не открылся, и считать блокировки не по чему. "
                + "Причина записана в разделе «Диагностика»."));
            return list;
        }

        long blocked;
        try
        {
            blocked = journal.Count(new Database.SecurityFilter(
                Event: null, BlockedOnly: true, Since: DateTimeOffset.Now.AddDays(-7)));
        }
        catch (Exception error)
        {
            Serilog.Log.Warning(error, "Блокировки за неделю не сосчитались");
            list.Children.Add(Note("Счет блокировок не удался — см. раздел «Диагностика»."));
            return list;
        }

        list.Children.Add(Note(blocked == 0
            ? "За неделю не заблокировано ни одной попытки."
            : $"Заблокировано попыток: {blocked:N0}."));

        if (blocked == 0) return list;

        list.Children.Add(new StackPanel
        {
            Orientation         = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Left,
            Margin              = new Avalonia.Thickness(0, 4, 0, 0),
            Children            = { SmallButton("В журнал", OpenBlockedInJournal) },
        });

        return list;
    }

    /// <summary>
    /// Открыть журнал безопасности уже с нужным отбором. Переход без отбора заставил бы человека
    /// повторять руками то, что экран уже знает, — и на второй странице он бы отбор забыл.
    /// </summary>
    private void OpenBlockedInJournal()
    {
        _securityLog         = true;
        _securityBlockedOnly = true;
        _securityEvent       = null;
        _securityDays        = 7;
        _securityShown       = 0;
        _securityOpen        = 0;

        HeaderBar.SetIntegrationTabs("journal");
        ShowIntegrationTab("journal");
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

            // Имя и фактический адрес проверяются подряд, поэтому пустой список сетей запирает
            // контур целиком. При заполненном списке имен это выглядит как настроенный обмен, и
            // узнают о нем по отказу «адрес не входит в разрешенные сети» — фразе, которая при
            // разрешенном имени читается как поломка программы, а не как незаполненное поле.
            if (source.Hosts.Count > 0 && source.Networks.Count == 0)
            {
                var alarm = Note(
                    "    сети не заданы — обмен закрыт целиком: адрес проверяется отдельно от "
                    + "имени, и пустой список не пропускает никого. Для узлов организации "
                    + "перечислите ее сети, для внешних узлов — 0.0.0.0/0 и ::/0.");

                alarm.Foreground = Ink("BrushRed", "#FF3B30");
                list.Children.Add(alarm);
            }
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

    /// <summary>Куда складываются копии исходящего; «никуда» — тоже ответ, и он частый.</summary>
    private static Control MirrorList(EgressPolicySet policy)
    {
        var list = new StackPanel { Spacing = 6 };

        if (policy.Mirrors.Count == 0)
        {
            list.Children.Add(Note(
                "Не ведется: политика не назвала папку. Это состояние по умолчанию — копии "
                + "секретных данных не разбрасываются по машине без указания."));
            return list;
        }

        foreach (var folder in policy.Mirrors)
            list.Children.Add(Note(folder));

        list.Children.Add(Note(
            "Копия снимается с того, что ДОШЛО до сети, и только с запросов, уносящих данные. "
            + "Читающие обращения в зеркало не идут."));

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
