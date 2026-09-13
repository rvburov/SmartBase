using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Platform.Storage;
using Avalonia.Media;
using SmartBase.Controls.Dialogs;
using SmartBase.Database;
using SmartBase.Integrations;
using SmartBase.Utils;
using static SmartBase.Controls.Widgets.SettingRows;

namespace SmartBase.Controls.Components.Security;

/// <summary>
/// Контур безопасности — что программе разрешено на этой машине, откуда это взялось и кто
/// сейчас может уносить данные наружу (docs/ROADMAP-API.md, раздел 10).
///
/// <b>Только показ.</b> Ни одного поля ввода здесь нет и не будет: политика — подписанный файл,
/// который правит ответственный за интеграции вне программы. Кнопка, меняющая права изнутри,
/// отменила бы весь смысл контура.
///
/// <b>Почему это отдельный контрол, а не вкладка «Настроек».</b> До шага 15 экран жил в
/// «Настройки → Интеграции» — там, где обмен настраивают. Смотрят же его не настройщики, а
/// служба безопасности и администратор, и сидеть ему полагается в панели администратора, рядом
/// с правами и журналом. Контрол ничего не знает о том, кто его показывает: ему дают рабочее
/// пространство, а он строит строки.
///
/// Действий два, и ни одно ничего не разрешает. «Перечитать политику» — администратор поправил
/// файл, а перезапускать программу ради этого незачем. «Паспорт интеграций» выписывает
/// показанное файлом для службы безопасности (10.16), добавляя то, чего на экране нет:
/// подтвержденные направления и сводку по журналу за месяц.
/// </summary>
public sealed class SecurityContourView : UserControl
{
    private readonly StackPanel _rows = new() { Spacing = 0, Margin = new Avalonia.Thickness(16, 0, 16, 32) };

    private Workspace? _workspace;

    // Развернут ли список помеченных свойств. Живет в пределах захода: это лупа, а не настройка.
    private bool _labeledFieldsShown;

    // Паспорт читает журнал за месяц: второй щелчок дал бы вторую работу и второе окно.
    private bool _passporting;

    /// <summary>
    /// Нажата кнопка «В журнал» у счетчика блокировок: показать журнал безопасности с уже
    /// выставленным отбором. Переход делает тот, кто показывает контур, — у панели
    /// администратора и у любого другого хозяина экрана свой способ открыть журнал.
    /// </summary>
    public event EventHandler? BlockedRequested;

    /// <summary>
    /// Показывать ли администраторские действия: переход в журнал безопасности и выписку
    /// паспорта.
    ///
    /// Сам контур читают все — он объясняет, почему отправка не уходит, и это нужнее всего тому,
    /// у кого прав нет. А вот журнал и паспорт — дело администратора: паспорт уносит из программы
    /// сводку по направлениям и объемам, журнал живет в его панели. Кнопка, которая ведет туда,
    /// куда человека не пустят, хуже отсутствующей.
    /// </summary>
    public bool ForAdmin { get; set; }

    public SecurityContourView() =>
        Content = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content                     = _rows,
        };

    /// <summary>Показать контур заново — при открытии экрана и после «Перечитать политику».</summary>
    public void Show(Workspace? workspace, bool forAdmin = false)
    {
        _workspace = workspace;
        ForAdmin   = forAdmin;
        Build();
    }

    private IntegrationsRegistry? Integrations => _workspace?.Registry.Integrations;

    private IReadOnlyList<DatabaseManager> Databases => _workspace?.Mounted ?? [];

    // ── Строки ────────────────────────────────────────────────────────────────

    private void Build()
    {
        var rows = _rows.Children;
        rows.Clear();

        var loaded = EgressSetup.Last;
        var policy = loaded.Set;
        var today  = DateOnly.FromDateTime(DateTime.Today);

        // 1. Что вообще можно. Первой строкой и человеческим языком: проверяющий должен
        //    получить ответ здесь, а не собирать его из таблиц ниже.
        Add(rows, Row("Состояние контура",
            "Что программе разрешено прямо сейчас. Ответ собран в том же порядке, в котором "
            + "спрашивает сам шлюз: сборка, наличие политики, ее срок, режим.",
            DataBox(StateText(policy, today))));

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
        //    в нем не работают вовсе, и отличить одно от другого помогает журнал безопасности.
        Add(rows, Row("Блокировок за неделю",
            "Отказы, записанные в журнал безопасности за последние семь дней.",
            BlocksList()), last: true);

        // «Перечитать политику» доступно всем: оно ничего не разрешает, а перечитывать файл
        // приходится сразу после того, как администратор его поправил, — и первым это замечает
        // тот, у кого встал обмен.
        var buttons = new List<Control>
        {
            RowButton("Перечитать политику", () =>
            {
                EgressSetup.Init();
                Build();
            }),
        };

        if (ForAdmin)
        {
            buttons.Add(RowButton("Паспорт интеграций…", () => _ = ExportPassportAsync()));

            // Подсчет мастер-хэша — единственное, что программа дает СОСТАВИТЕЛЮ политики.
            // Формат хэша свой, и без этой кнопки его приходилось воспроизводить сторонним
            // скриптом: строку masterHash требует любое исходящее направление, а посчитать ее
            // было нечем. Политику кнопка не трогает — считает и отдает.
            buttons.Add(RowButton("Мастер-пароль для политики…", () => _ = MasterHashAsync()));
        }

        var actions = ButtonLine([.. buttons]);
        actions.Margin = new Avalonia.Thickness(0, 12, 0, -6);
        rows.Add(actions);

        // Сказано вслух, потому что экран без единого поля ввода читается как «еще не
        // доделали». Он не не доделан — в этом его устройство.
        rows.Add(new TextBlock
        {
            Classes      = { "desc" },
            Text         = "Ни одного поля ввода здесь нет и не будет: политику правит "
                         + "ответственный за интеграции, вне программы. «Мастер-пароль для "
                         + "политики» ее тоже не трогает — считает строку, которую он впишет "
                         + "в файл сам.",
            TextWrapping = TextWrapping.Wrap,
            Margin       = new Avalonia.Thickness(0, 8, 0, 0),
        });
    }

    // ── Мастер-пароль для политики ────────────────────────────────────────────

    /// <summary>
    /// Открыть окно подсчета проверочного хэша мастер-пароля. Ничего не сохраняет: строка нужна
    /// тому, кто СОСТАВЛЯЕТ политику, и попадает в нее руками (см.
    /// <see cref="MasterHashDialog"/>).
    /// </summary>
    private async Task MasterHashAsync()
    {
        if (VisualRoot is not Window win) return;
        await new MasterHashDialog().ShowDialog(win);
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
            var databases = Databases
                .Select(m => new PassportDatabase(
                    m.DbId, m.Name,
                    m.Constructor.GetDatabaseLabel(),
                    m.Constructor.LabeledFieldCount()))
                .ToList();

            var text = IntegrationsPassport.Build(
                registry, EgressSetup.Last, SecurityJournal.Current, databases,
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
        var lines = new List<Control>();

        // Момент применения — первой строкой и раньше самих источников. Вопрос звучит как
        // «с какого момента действует то, что я вижу», и ответ на него не время правки файла:
        // файл могли поправить час назад, а «Перечитать политику» никто не нажимал.
        lines.Add(Note(EgressSetup.AppliedAt is { } applied
            ? $"Применено {applied.ToLocalTime():dd.MM.yyyy HH:mm}"
              + (loaded.Reads.Count > 1 ? $" · пересечение {loaded.Reads.Count} источников" : "")
            : "Политика еще не читалась за этот заход."));

        if (loaded.Reads.Count == 0)
        {
            lines.Add(Note("Ни одного источника не найдено."));
            return DataBox(lines);
        }

        foreach (var read in loaded.Reads)
            lines.Add(Note($"{(read.Accepted ? "принят" : "отклонен")} · {read.Source} — {read.Note}"));

        return DataBox(lines);
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
            return DataBox("Список станет доступен, когда откроется база данных.");

        var outward = registry.Requests().Where(r => r.Outward).ToList();

        if (outward.Count == 0)
            return DataBox("Запросов, уносящих данные наружу, не заведено ни одного.");

        var now  = DateTimeOffset.Now;
        var list = new StackPanel { Spacing = 6 };

        // Объем за месяц — по рабочему журналу и по тому же ключу направления, по которому
        // считается суточный потолок. Журнал подрезается, поэтому цифра честно «за то, что
        // помнит журнал», а не «за календарный месяц».
        var spent = RunJournal.Current?.SpentByDirection(now.AddDays(-30)) ?? [];

        foreach (var request in outward)
        {
            var direction = registry.Direction(request.Id);
            var trouble   = registry.OutwardTrouble(request, now);
            var bytes     = spent.FirstOrDefault(s => s.Direction == request.Id).Bytes;

            list.Children.Add(Note($"{request.Name} — "
                + $"{request.Method.ToString().ToUpperInvariant()} {request.Path}"));

            if (direction is null)
            {
                var idle = SubNote("не подтверждено — отправлять не может");
                idle.Foreground = Ink("BrushRed", "#FF3B30");
                list.Children.Add(idle);
                continue;
            }

            list.Children.Add(SubNote($"подтвердил {direction.Confirmed} · "
                + $"до {direction.Until.ToLocalTime():dd.MM.yyyy} · "
                + $"за месяц {Size(bytes)}"));

            var state = SubNote(trouble is null
                ? "действует"
                : "не действует: " + trouble);

            if (trouble is not null) state.Foreground = Ink("BrushRed", "#FF3B30");
            list.Children.Add(state);
        }

        return DataBox(list);
    }

    /// <summary>
    /// Метки открытых баз и счетчик помеченных свойств. Список свойств разворачивается по
    /// нажатию: цифра нужна всегда, а имена — редко, и в базе их бывают сотни.
    /// </summary>
    private Control LabelsList()
    {
        var bases = Databases;

        if (bases.Count == 0)
            return DataBox("Ни одной базы не открыто — показывать нечего.");

        var lines  = new List<Control>();
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

            lines.Add(line);
        }

        lines.Add(Note($"Полей с метками: {fields:N0}."));

        var block = new StackPanel { Spacing = 6, Children = { DataBox(lines) } };

        if (fields == 0) return block;

        block.Children.Add(ButtonLine(
            RowButton(_labeledFieldsShown ? "Скрыть список" : "Показать список", () =>
            {
                _labeledFieldsShown = !_labeledFieldsShown;
                Build();
            })));

        if (!_labeledFieldsShown) return block;

        // Раскрытый список — СВОЕЙ ячейкой, а не строками в общей: это отдельные данные, и
        // дописывать их в ячейку со сводкой значило бы смешать «сколько» и «какие именно».
        var named = new List<Control>();

        foreach (var manager in bases)
            foreach (var (collection, field, label) in manager.Constructor.LabeledFields())
                named.Add(Note($"{manager.Name} · {collection} · {field} — "
                               + Core.Models.Confidentiality.Title(label)));

        block.Children.Add(DataBox(named));

        return block;
    }

    /// <summary>
    /// Сколько отказов легло в журнал безопасности за неделю и кнопка перехода к ним. Ноль
    /// показывается словами, а не пустотой: пустая строка читается как «не считали».
    /// </summary>
    private Control BlocksList()
    {
        if (SecurityJournal.Current is not { } journal)
            return DataBox("Журнал безопасности не открылся, и считать блокировки не по чему. "
                           + "Причина записана в разделе «Диагностика».");

        long blocked;
        try
        {
            blocked = journal.Count(new SecurityFilter(
                Event: null, BlockedOnly: true, Since: DateTimeOffset.Now.AddDays(-7)));
        }
        catch (Exception error)
        {
            Serilog.Log.Warning(error, "Блокировки за неделю не сосчитались");
            return DataBox("Счет блокировок не удался — см. раздел «Диагностика».");
        }

        var box = DataBox(blocked == 0
            ? "За неделю не заблокировано ни одной попытки."
            : $"Заблокировано попыток: {blocked:N0}.");

        // Само число видно всем — «замок сработал» это ответ и тому, у кого встал обмен. А
        // читать сами строки ведет только администратора: журнал живет в его панели.
        if (blocked == 0 || !ForAdmin) return box;

        return new StackPanel
        {
            Spacing  = 6,
            Children =
            {
                box,
                ButtonLine(RowButton("В журнал",
                                     () => BlockedRequested?.Invoke(this, EventArgs.Empty))),
            },
        };
    }

    private static Control PlacesList()
    {
        var lines = new List<Control>();

        foreach (var place in PolicySources.Places())
            lines.Add(Note(
                $"{place.Name} — {place.Where}" + (place.SignatureRequired ? " · подпись обязательна" : "")));

        // Сказано ЗАРАНЕЕ, а не в момент отказа. Открытый ключ организации вшивается в сборку, и
        // пустой он означает, что места с обязательной подписью в этой сборке не работают вовсе:
        // файл туда положат, программа его отклонит, а отклоненный источник по правилу
        // пересечения запрет контур целиком. Узнавать об этом после раздачи политики по сети —
        // худший из возможных моментов.
        if (!PolicySignature.KeyPresent && PolicySources.Places().Any(p => p.SignatureRequired))
        {
            var warning = SubNote("В эту сборку открытый ключ организации не вшит — места с "
                                + "обязательной подписью не примут политику. Используйте реестр "
                                + "или папку установки.");
            warning.Foreground = Ink("BrushRed", "#FF3B30");
            lines.Add(warning);
        }

        return DataBox(lines);
    }

    private static Control AddressList(EgressPolicySet policy)
    {
        if (!policy.Any) return DataBox("Список пуст: разрешенных адресов нет.");

        var list = new StackPanel { Spacing = 6 };

        foreach (var source in policy.Sources)
        {
            list.Children.Add(Note($"{source.Source}:"));
            list.Children.Add(SubNote("имена — " + Join(source.Hosts)));
            list.Children.Add(SubNote("сети — "  + Join(source.Networks)));

            // Имя и фактический адрес проверяются подряд, поэтому пустой список сетей запирает
            // контур целиком. При заполненном списке имен это выглядит как настроенный обмен, и
            // узнают о нем по отказу «адрес не входит в разрешенные сети» — фразе, которая при
            // разрешенном имени читается как поломка программы, а не как незаполненное поле.
            if (source.Hosts.Count > 0 && source.Networks.Count == 0)
            {
                var alarm = SubNote(
                    "сети не заданы — обмен закрыт целиком: адрес проверяется отдельно от "
                    + "имени, и пустой список не пропускает никого. Для узлов организации "
                    + "перечислите ее сети, для внешних узлов — 0.0.0.0/0 и ::/0.");

                alarm.Foreground = Ink("BrushRed", "#FF3B30");
                list.Children.Add(alarm);
            }
        }

        return DataBox(list);
    }

    private static Control FolderList(EgressPolicySet policy)
    {
        if (!policy.Any) return DataBox("Папок не разрешено.");

        var lines = new List<Control>();

        foreach (var source in policy.Sources)
            lines.Add(Note($"{source.Source} — " + Join(source.Folders)));

        return DataBox(lines);
    }

    /// <summary>Куда складываются копии исходящего; «никуда» — тоже ответ, и он частый.</summary>
    private static Control MirrorList(EgressPolicySet policy)
    {
        if (policy.Mirrors.Count == 0)
            return DataBox("Не ведется: политика не назвала папку. Это состояние по умолчанию — "
                           + "копии секретных данных не разбрасываются по машине без указания.");

        var lines = new List<Control>();

        foreach (var folder in policy.Mirrors)
            lines.Add(Note(folder));

        lines.Add(Note(
            "Копия снимается с того, что ДОШЛО до сети, и только с запросов, уносящих данные. "
            + "Читающие обращения в зеркало не идут."));

        return DataBox(lines);
    }

    private static Control LimitsList(EgressPolicySet policy)
    {
        if (!policy.Any) return DataBox("Ограничения не заданы: обмен запрещен целиком.");

        return DataBox(new[]
        {
            Note($"строк за один запуск — не больше {policy.MaxRowsPerRun:N0}"),
            Note($"объем в сутки — не больше {Size(policy.MaxBytesPerDay)}"),
            Note($"запусков в час — не больше {policy.MaxRunsPerHour:N0}"),
            Note("срок действия — "
                 + (policy.ValidUntil is { } until ? until.ToString("dd.MM.yyyy") : "без срока")),
            Note("дописывать адреса самому — "
                 + (policy.UserMayAddHosts ? "разрешено" : "запрещено")),
        });
    }

}
