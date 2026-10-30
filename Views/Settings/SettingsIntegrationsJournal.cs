using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using SmartBase.Controls.Dialogs;
using SmartBase.Database;
using SmartBase.Integrations;
using static SmartBase.Controls.Widgets.SettingRows;

namespace SmartBase.Views.Settings;

/// <summary>
/// Вкладка «Журнал» раздела «Интеграции» (partial SettingsView): что уже происходило с обменом
/// и почему оно кончилось именно так (docs/ROADMAP-API.md, 10.10).
///
/// Журнал показывает результаты запусков, тела запросов и ответов, ошибки и число записей.
/// Строки читаются страницами, фильтруются и выгружаются в CSV. Рабочий журнал можно очистить.
/// </summary>
public partial class SettingsView
{
    /// <summary>Сколько строк добавляет одно нажатие «Показать еще».</summary>
    private const int JournalPage = 200;

    // Что показываем: канал (null — все) и «только неудачные». Живет в пределах захода в
    // программу и намеренно НЕ запоминается в настройках: это лупа, а не режим работы.
    private RunChannel? _journalChannel;
    private bool        _journalFailedOnly;

    // За какой период показывать; 0 — за все время.
    private int _journalDays = 30;

    // Ключ строки, у которой раскрыты подробности; 0 — ни одной.
    private long _journalOpen;

    // Сколько строк показано сейчас; 0 — первая страница. Живет в пределах захода, как и отбор:
    // «докрутил до сотой строки» — это не настройка, а сегодняшний разбор.
    private int _journalShown;

    // Отбор по колонкам — «как в табличном редакторе»: колонка и выбранные в ее воронке значения. Тоже лупа,
    // а не режим работы, поэтому в настройках не запоминается.
    private readonly Dictionary<string, HashSet<string>> _journalPicked = new(StringComparer.Ordinal);

    /// <summary>
    /// Сценарий отработал сам. Зовет MainWindow из службы сценариев; экран перестраивается,
    /// только если журнал открыт, — иначе это работа впустую.
    /// </summary>
    public void OnScenarioRan()
    {
        if (_integrationsTab == "journal") BuildIntegrationsJournal();

        // И список сценариев: у отработавшего изменилось время последнего запуска, а у
        // соседей могла уйти или появиться строка «не сработает».
        if (_integrationsTab == "scenarios") BuildIntegrationsScenarios();
    }

    private void BuildIntegrationsJournal()
    {
        var rows = IntegrationsJournalTab.Children;
        rows.Clear();

        if (RunJournal.Current is not { } journal)
        {
            // Журнал не открылся при запуске — сам обмен при этом работает: падать из-за того,
            // что не удалось рассказать о работе, значило бы терять сделанную работу.
            Add(rows, Row("Журнал не открылся",
                "Файл журнала не удалось открыть при запуске программы. Обмен от этого не "
                + "останавливается, но история по нему не ведется, а причина записана в "
                + "разделе «Диагностика»."), last: true);
            return;
        }

        // Сужение уходит в SQL, а не в память: отбери «неудачные» из прочитанной пачки — и
        // единственная ночная неудача не найдется, потому что удачные строки вытеснят ее.
        var filter = JournalFilter();

        // Одна строка сверх показанного — так и узнаем, есть ли что дальше, не считая весь файл.
        var page = journal.Page(filter, JournalShown() + 1);
        var more = page.Count > JournalShown();

        var selected = page.Take(JournalShown()).ToList();

        Add(rows, FilterRow());
        Add(rows, RunsRow(selected, more));
        Add(rows, FileRow(journal, selected.Count, filter.Narrow), last: true);
    }

    /// <summary>
    /// Сколько строк показываем: каждое «Показать еще» добавляет страницу. Читается всегда от
    /// свежего конца, а не «следующая страница после последней»: вкладка перестраивается целиком
    /// на каждое нажатие, и хранить накопленные страницы означало бы держать в памяти то, что и
    /// так лежит в файле рядом.
    /// </summary>
    private int JournalShown() => _journalShown > 0 ? _journalShown : JournalPage;

    /// <summary>Что показываем: переключатели сверху плюс воронки в шапке таблицы.</summary>
    private RunFilter JournalFilter()
    {
        var picked = new Dictionary<string, IReadOnlyCollection<string>>(StringComparer.Ordinal);

        foreach (var (column, values) in _journalPicked)
            if (values.Count > 0) picked[column] = values;

        return new RunFilter(_journalChannel, _journalFailedOnly, picked,
                             _journalDays > 0 ? DateTimeOffset.Now.AddDays(-_journalDays) : null);
    }

    /// <summary>Выбранное в воронке колонки; множество живет, пока открыт раздел.</summary>
    private HashSet<string> JournalPicked(string column)
    {
        if (!_journalPicked.TryGetValue(column, out var values))
            _journalPicked[column] = values = new HashSet<string>(StringComparer.Ordinal);

        return values;
    }

    // ── Что показывать ────────────────────────────────────────────────────────

    private Border FilterRow()
    {
        var channel = Picker(
            ["Все каналы", "Запросы по сети", "Файлы из папки", "Сценарии", "Обращения к нам",
             "Потоки событий"],
            _journalChannel switch
            {
                null               => 0,
                RunChannel.Request => 1,
                RunChannel.Inbox   => 2,
                RunChannel.Ingress => 4,
                RunChannel.Stream  => 5,
                _                  => 3,
            });

        // Обработчик вешается ПОСЛЕ выставления значения: иначе первый же показ вкладки
        // перестроил бы сам себя.
        channel.SelectionChanged += (_, _) =>
        {
            _journalChannel = channel.SelectedIndex switch
            {
                1 => RunChannel.Request,
                2 => RunChannel.Inbox,
                3 => RunChannel.Scenario,
                4 => RunChannel.Ingress,
                5 => RunChannel.Stream,
                _ => null,
            };

            _journalOpen  = 0;
            _journalShown = 0;   // сужение начинает чтение заново: страницы были от другой выборки
            BuildIntegrationsJournal();
        };

        var period = Picker(["7 дней", "30 дней", "Год", "Все"], _journalDays switch
        {
            7   => 0,
            30  => 1,
            365 => 2,
            _   => 3,
        }, width: 120);

        period.SelectionChanged += (_, _) =>
        {
            _journalDays = period.SelectedIndex switch
            {
                0 => 7,
                1 => 30,
                2 => 365,
                _ => 0,
            };

            _journalOpen  = 0;
            _journalShown = 0;
            BuildIntegrationsJournal();
        };

        var failed = new CheckBox
        {
            Content           = "Только неудачные",
            IsChecked         = _journalFailedOnly,
            VerticalAlignment = VerticalAlignment.Center,
        };

        failed.IsCheckedChanged += (_, _) =>
        {
            _journalFailedOnly = failed.IsChecked == true;
            _journalOpen       = 0;
            _journalShown      = 0;
            BuildIntegrationsJournal();
        };

        var refresh = RowButton("Обновить", () =>
        {
            _journalOpen = 0;
            BuildIntegrationsJournal();
        });

        var export = RowButton("Выгрузить CSV", () => _ = ExportRunsAsync());

        var line = ButtonLine(channel, period, failed, refresh, export);

        return Row("Что показывать",
            "Разбор «почему записи не появились» начинается с неудачных строк — их тут больше "
            + "всего и ищут. Период сужает выборку В ФАЙЛЕ, а не на экране: единственная неудача "
            + "недельной давности иначе не нашлась бы, вытесненная свежими строками. Выгрузка "
            + "отдает ВСЕ подошедшие строки, а не показанные две сотни.",
            line);
    }

    // ── Выгрузка запусков ─────────────────────────────────────────────────────

    // Считать строки на большом журнале — секунды, а окон в это время еще нет: второй щелчок
    // дал бы вторую выгрузку и второе окно подтверждения.
    private bool _runsExporting;

    /// <summary>
    /// Выгрузить отобранные запуски в CSV.
    ///
    /// Выгружается все, что подошло под условия, а не показанные две сотни строк: человек,
    /// нажавший «Выгрузить», просит журнал, а не картинку экрана. Строки читаются лениво,
    /// страницами, поэтому размер файла ограничивает только диск.
    ///
    /// Тел запроса и ответа в файле нет — они превратили бы таблицу в нечитаемую. Их смотрят на
    /// экране, у одной строки; в CSV идут числа и итоги, ради которых таблицу и открывают.
    /// </summary>
    private async Task ExportRunsAsync()
    {
        if (TopLevel.GetTopLevel(this) is not Window owner) return;
        if (RunJournal.Current is not { } journal) return;
        if (_runsExporting) return;

        // Снимок условий ДО ожидания: их могли поменять, пока считались строки.
        var filter = JournalFilter();

        _runsExporting = true;
        try
        {
            await Controls.Dialogs.CsvExportRunner.Run(owner, new Controls.Dialogs.CsvExportRunner.Job(
                Title:           "Выгрузка журнала обмена",
                What:            "Журнал обмена",
                TotalRows:       journal.Count(filter),
                Filtered:        filter.Narrow,
                DefaultFileName: Controls.Dialogs.CsvExportRunner.FileName(
                                     "Журнал обмена", DateTime.Now),
                Headers:         RunJournal.CsvHeaders,
                Rows:            ct => journal.Stream(filter)
                                              .TakeWhile(_ => !ct.IsCancellationRequested)
                                              .Select(RunJournal.CsvRow)));
        }
        finally { _runsExporting = false; }
    }

    // ── Список запусков ───────────────────────────────────────────────────────

    private Border RunsRow(IReadOnlyList<RunEntry> runs, bool more)
    {
        var list = new StackPanel { Spacing = 0 };

        if (runs.Count == 0)
        {
            list.Children.Add(Note(_journalFailedOnly || _journalChannel is not null
                ? "Под выбранные условия не подошло ни одной строки."
                : "Обмена еще не было: ни одного запроса и ни одного принятого файла."));

            return Row("Запуски", "", list);
        }

        list.Children.Add(HeaderLine());

        foreach (var run in runs)
            list.Children.Add(RunLine(run));

        if (more)
        {
            list.Children.Add(new TextBlock
            {
                Classes      = { "desc" },
                Margin       = new Thickness(0, 8, 0, 0),
                TextWrapping = TextWrapping.Wrap,
                Text         = $"Показаны последние {runs.Count:N0} строк — дальше есть еще.",
            });

            var next = ButtonLine(RowButton("Показать еще", () =>
            {
                _journalShown = JournalShown() + JournalPage;
                BuildIntegrationsJournal();
            }));

            next.Margin = new Thickness(0, 10, 0, -6);
            list.Children.Add(next);
        }

        return Row("Запуски",
            "Строка на каждый запуск, свежие сверху. Нажмите на строку — под ней раскроются "
            + "адрес, объяснение и числа.",
            list);
    }

    /// <summary>
    /// Что стоит в колонке «Шаг». Пометка приходит из сценария целой фразой («шаг 2 из 5 ·
    /// запрос»), но слово «шаг» в колонке с таким заголовком — лишнее: колонка узкая, и место
    /// нужнее номеру и роду.
    /// </summary>
    private static string StepCell(RunEntry run)
    {
        if (run.Step.Length > 0)
            return run.Step.StartsWith("шаг ", StringComparison.Ordinal) ? run.Step[4..] : run.Step;

        // Итоговая строка сценария — не шаг, а сценарий целиком: у нее есть имя сценария, но
        // номера шага нет и быть не может.
        return run.Scenario.Length > 0 ? "целиком" : "—";
    }

    /// <summary>Ширины колонок общие у шапки и строк — иначе таблица «поедет».</summary>
    private static ColumnDefinitions RunColumns() => new("104,8,58,8,*,8,150,8,140,8,58,8,74");

    /// <summary>
    /// Шапка таблицы. У колонок, по которым есть что отбирать, — воронка с отбором «как в
    /// табличном редакторе» (<see cref="Controls.Widgets.ColumnFilter"/>); отбор уходит в SQL, а не отсеивает
    /// показанную пачку. «Время» и «Строк» воронки не получают: у первого значения уникальны и
    /// список из них бесполезен, у второго отбор по числу строк никто не ищет.
    /// </summary>
    private Control HeaderLine()
    {
        var grid = new Grid { ColumnDefinitions = RunColumns() };

        Put(grid, 0,  Head("Время"));
        Put(grid, 2,  Funnel("Channel",  "Канал"));
        Put(grid, 4,  Funnel("Name",     "Что запускали"));
        Put(grid, 6,  Funnel("Scenario", "Сценарий"));
        Put(grid, 8,  Funnel("Step",     "Шаг"));
        Put(grid, 10, Head("Строк"));
        Put(grid, 12, Funnel("Ok",       "Итог"));

        return TableHead(grid);
    }

    /// <summary>
    /// Одна строка журнала. Подробности раскрываются под ней, а не в отдельной панели: панель
    /// деталей требует места, которого в узкой колонке настроек нет, а читают их по одной.
    /// </summary>
    private Control RunLine(RunEntry run)
    {
        var open = _journalOpen == run.Id;

        var grid = new Grid { ColumnDefinitions = RunColumns() };

        Put(grid, 0, Cell(Moment(run), secondary: true));
        Put(grid, 2, Cell(ChannelName(run.Channel), secondary: true));
        Put(grid, 4, Cell(run.Name.Length > 0 ? run.Name : "без названия"));

        // «Руками» — не пустота, а ответ: запуск с экрана сценарию не принадлежит. Прочерк в
        // этой колонке читался бы как «сценарий был, но потерялся».
        Put(grid, 6, Cell(run.Scenario.Length > 0 ? run.Scenario : "руками", secondary: true));
        Put(grid, 8, Cell(StepCell(run), secondary: true));

        Put(grid, 10, Cell(run.Rows > 0 ? $"{run.Rows:N0}" : "—", secondary: true));

        var verdict = Cell(run.Ok ? "удача" : "ошибка");
        verdict.Foreground = Ink(run.Ok ? "BrushGreen" : "BrushRed", run.Ok ? "#34C759" : "#FF3B30");
        Put(grid, 12, verdict);

        return TableRow(grid, open ? Details(run) : null, open, () =>
        {
            _journalOpen = open ? 0 : run.Id;
            BuildIntegrationsJournal();
        });
    }

    /// <summary>
    /// Подробности строки: то, чему не нашлось места в колонках. Лежат в ячейке с данными —
    /// раскрытый блок должен отличаться от строк вокруг, иначе он читается как еще одна строка
    /// таблицы, только без колонок.
    /// </summary>
    private Control Details(RunEntry run)
    {
        var list = new StackPanel { Spacing = 4 };

        // У удачи объяснения не бывает, и строка «Без объяснения» читалась бы как потерянный
        // текст, а не как его отсутствие. У неудачи оно обязано быть — и если его нет, это
        // недоработка, которую и надо показать словами, а не пустым местом.
        if (run.Explanation.Length > 0) list.Children.Add(Note(run.Explanation));
        else if (!run.Ok)               list.Children.Add(Note("Без объяснения."));

        // Куда ходили — только если было куда: у строки сценария адреса нет, и «не записан»
        // в ней читался бы как потеря, а не как «неприменимо».
        if (run.Address.Length > 0)
            list.Children.Add(Note(
                (run.Channel switch
                {
                    RunChannel.Request => "Адрес: ",
                    RunChannel.Stream  => "Адрес: ",
                    RunChannel.Ingress => "Откуда: ",
                    _                  => "Папка: ",
                }) + run.Address));

        list.Children.Add(Note(Numbers(run)));

        list.Children.Add(Note("База данных: " + DatabaseName(run.DbId)));

        // Строка есть, а тел нет — это может значить и «их не было», и «уже не хранятся».
        // Второе надо сказать вслух: молчание читается как потеря.
        if (run.Channel == RunChannel.Request && run.SentBody.Length == 0 && run.GotBody.Length == 0)
            list.Children.Add(Note(
                "Тела запроса и ответа не хранятся: они держатся только у последних "
                + $"{RunJournal.KeepBodies:N0} запусков."));

        // Сценарий и шаг — в подробностях целиком: в колонке они урезаны по ширине, а
        // выписывают отсюда, из подробностей, вместе с остальным.
        if (run.Scenario.Length > 0)
            list.Children.Add(Note($"Сценарий: {run.Scenario}"
                + (run.Step.Length > 0 ? $" · {run.Step}" : " · сценарий целиком")));

        var block = new StackPanel { Spacing = 6, Children = { DataBox(list) } };

        // Тела запроса и ответа — то, ради чего журнал отладки и открывают: без них панель
        // отвечает «не получилось» и не отвечает «почему». Каждое в своей ячейке: у них свой
        // шрифт и своя прокрутка, а внутри общей ячейки они читались бы как продолжение чисел.
        if (run.SentBody.Length > 0) block.Children.Add(Body("Запрос",  run.SentBody));
        if (run.GotBody.Length  > 0) block.Children.Add(Body("Ответ",   run.GotBody));

        var copy = RowButton("Скопировать", () =>
            _ = TopLevel.GetTopLevel(this)?.Clipboard?.SetTextAsync(Plain(run)));

        // Повтор — только у сетевого шага и только когда карточка запроса еще на месте. Файл из
        // папки повторить нельзя: он уже разобран и уехал в «Обработанные», а квитанция отсечет
        // второй заход. Итоговую строку сценария — тоже: повторять надо сценарий, а у него своя
        // кнопка на соседней вкладке.
        block.Children.Add(Repeatable(run) is { } request
            ? ButtonLine(copy, RowButton("Повторить шаг", () => _ = RepeatRunAsync(request)))
            : ButtonLine(copy));

        return block;
    }

    /// <summary>
    /// Карточка запроса, которым можно повторить этот запуск; <c>null</c> — повторять нечем.
    ///
    /// Проверяется не «был ли это запрос», а «жива ли его карточка»: в строке журнала записан
    /// ключ карточки, и она могла уехать вместе с запросом, который человек удалил. Кнопка,
    /// отвечающая «карточки больше нет», хуже отсутствующей кнопки.
    /// </summary>
    private IntegrationRequest? Repeatable(RunEntry run) =>
        run.Channel == RunChannel.Request && run.Direction > 0 && Integrations is { } registry
            ? registry.FindRequest(run.Direction)
            : null;

    /// <summary>
    /// Повторить запуск — тем же путем, которым запрос запускают с карточки: «В коллекцию».
    /// Своего пути повтора нет намеренно: он повторял бы не то, что делает обычный запуск, и
    /// разошелся бы с ним на первой же правке.
    ///
    /// Повтор создает новый запуск с текущими настройками запроса.
    /// </summary>
    private async Task RepeatRunAsync(IntegrationRequest request)
    {
        if (VisualRoot is not Window win || Integrations is not { } registry) return;

        var connection = registry.Find(request.ConnectionId);

        if (connection is null)
        {
            await ConfirmDialog.Info(
                    $"У запроса «{request.Name}» не найдено соединение: повторять нечем.")
                .ShowDialog(win);
            return;
        }

        await RunToCollectionAsync(registry, request, connection);

        _journalOpen = 0;
        BuildIntegrationsJournal();
    }

    /// <summary>
    /// Тело запроса или ответа: заголовок и сам текст в рамке, моноширинным шрифтом и с
    /// прокруткой по высоте. Текст выделяемый — половина разбора кончается тем, что кусок
    /// ответа пересылают той стороне.
    ///
    /// Высота ограничена: ответ сторонней программы бывает на восемь тысяч знаков, и вылезший на
    /// три экрана JSON увел бы из виду сам список запусков.
    /// </summary>
    private static Control Body(string title, string text) => new StackPanel
    {
        Spacing  = 4,
        Children = { Note(title + ":"), CodeBox(text) },
    };

    /// <summary>
    /// Строка текстом — чтобы переслать той стороне. Именно этим кончается почти каждый разбор
    /// неудачного обмена, и перенабирать числа руками незачем. Секрета в тексте нет: адрес в
    /// журнале и так лежит замаскированным.
    /// </summary>
    private string Plain(RunEntry run) =>
        $"{Full(run)} · {ChannelText(run.Channel)} · {run.Name} · {(run.Ok ? "удача" : "ошибка")}\n"
        + $"{run.Address}\n"
        + $"{run.Explanation}\n"
        + $"{Numbers(run)} · база: {DatabaseName(run.DbId)}"
        + (run.SentBody.Length > 0 ? $"\n\nЗапрос:\n{run.SentBody}" : "")
        + (run.GotBody.Length  > 0 ? $"\n\nОтвет:\n{run.GotBody}"   : "");

    /// <summary>
    /// Числа запуска. Пустые не показываются: обмен файлами не меряет ни объем, ни длительность,
    /// и «0 КБ · — » рядом с принятыми записями читается как поломка, а не как «неприменимо».
    /// </summary>
    private static string Numbers(RunEntry run)
    {
        var parts = new List<string> { $"записей: {run.Rows:N0}" };

        if (run.Bytes > 0) parts.Add("объем: " + Size(run.Bytes));
        if (run.Ms    > 0) parts.Add("длительность: " + Duration(run.Ms));

        return string.Join(" · ", parts);
    }

    // ── Файл журнала ──────────────────────────────────────────────────────────

    private Border FileRow(RunJournal journal, int read, bool narrow)
    {
        var block = new StackPanel { Spacing = 6 };

        block.Children.Add(DataBox(new[]
        {
            Note($"Файл: {journal.Path}"),
            Note((narrow ? $"Показано подошедших строк: {read:N0}."
                         : $"Показано последних строк: {read:N0}.")
                 + $" Журнал хранит не больше {RunJournal.Keep:N0} строк и подрезает себя сам "
                 + "при запуске программы."),
        }));

        block.Children.Add(ButtonLine(RowButton("Очистить журнал", () => _ = ClearJournalAsync())));

        return Row("Где это лежит",
            "Журнал общий на все базы данных и лежит рядом с настройками программы, а не внутри "
            + "файла базы: файл базы ездит по сетевой папке, и история обмена раздула бы его на "
            + "всех машинах разом.",
            block);
    }

    private async Task ClearJournalAsync()
    {
        if (VisualRoot is not Window win || RunJournal.Current is not { } journal) return;

        var confirmed = await new ConfirmDialog(
                "Очистить журнал обмена? История запусков будет удалена без возможности "
                + "восстановления.",
                confirmText: "Очистить")
            .ShowDialog<bool>(win);

        if (confirmed != true) return;

        journal.Clear();
        _journalOpen = 0;
        BuildIntegrationsJournal();
    }

    // ── Мелочи оформления ─────────────────────────────────────────────────────

    /// <summary>Канал колонкой — коротко, чтобы влезло.</summary>
    // Слово канала одно на экран и на выгрузку: разойдись они — человек, сверяющий CSV с
    // экраном, решил бы, что смотрит разные журналы.
    /// <summary>Заголовок колонки с воронкой отбора.</summary>
    private Control Funnel(string column, string title) =>
        Controls.Widgets.ColumnFilter.Head(title, () => JournalValues(column),
                                           JournalPicked(column),
                                           () =>
                                           {
                                               // Сужение начинает чтение заново: страницы были
                                               // от другой выборки.
                                               _journalShown = 0;
                                               _journalOpen  = 0;
                                               BuildIntegrationsJournal();
                                           });

    /// <summary>
    /// Какие значения бывают в колонке. Числа переводятся в слова прямо здесь: в журнале канал
    /// лежит числом, а человеку нужен «Запрос», и список отбора не место для расшифровки кодов.
    /// </summary>
    private IReadOnlyList<Controls.Widgets.ColumnFilterValue> JournalValues(string column)
    {
        if (RunJournal.Current is not { } journal) return [];

        var values = journal.Values(column, JournalFilter());
        var made   = new List<Controls.Widgets.ColumnFilterValue>(values.Count);

        foreach (var (value, count) in values)
            made.Add(new Controls.Widgets.ColumnFilterValue(value, ValueText(column, value), count));

        return made;
    }

    /// <summary>Как назвать значение колонки человеку.</summary>
    private static string ValueText(string column, string value) => column switch
    {
        "Channel" => int.TryParse(value, out var channel)
            ? ChannelName((RunChannel)channel)
            : value,

        "Ok" => value == "1" ? "удача" : "ошибка",

        _ => value.Length > 0 ? value : "(пусто)",
    };

    private static string ChannelName(RunChannel channel) => RunJournal.ChannelName(channel);

    /// <summary>Канал в пересылаемом тексте — словами, без экрана рядом.</summary>
    private static string ChannelText(RunChannel channel) => channel switch
    {
        RunChannel.Request => "запрос по сети",
        RunChannel.Inbox   => "файл из папки",
        RunChannel.Ingress => "обращение к нам",
        RunChannel.Stream  => "поток событий",
        _                  => "сценарий",
    };

    /// <summary>Время строки; испорченный правкой файла момент показывается как есть.</summary>
    private static string Moment(RunEntry run) =>
        run.Moment is { } moment ? moment.ToLocalTime().ToString("dd.MM HH:mm:ss") : run.At;

    /// <summary>
    /// То же время с годом — для текста, который уносят из программы. В колонке год лишний
    /// (смотрят вчерашнее), а в пересланной строке без него не разобраться.
    /// </summary>
    private static string Full(RunEntry run) =>
        run.Moment is { } moment ? moment.ToLocalTime().ToString("dd.MM.yyyy HH:mm:ss") : run.At;

    private static string Duration(long ms) => ms switch
    {
        <= 0      => "—",
        < 1000    => $"{ms} мс",
        < 60_000  => $"{ms / 1000.0:N1} с",
        _         => $"{ms / 60_000} мин {ms % 60_000 / 1000} с",
    };

    /// <summary>Имя базы по ключу; закрытая база названа ключом — история переживает базы.</summary>
    private string DatabaseName(int dbId)
    {
        if (dbId <= 0) return "не задета";

        var open = _databases?.Invoke() ?? [];

        return open.FirstOrDefault(d => d.DbId == dbId) is { } found
            ? found.Name
            : $"№{dbId} (сейчас не открыта)";
    }

    // Ячейки таблицы (Head, Cell, Put), строка таблицы и кисти — в Controls/Widgets/SettingRows.cs.
}
