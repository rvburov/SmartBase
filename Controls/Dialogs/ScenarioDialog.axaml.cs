using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using SmartBase.Integrations;

namespace SmartBase.Controls.Dialogs;

/// <summary>
/// Окно правки сценария: «когда» и «что делать».
///
/// Три полосы вместо графа узлов — сценарии линейные, а редактор графа это отдельный проект
/// (docs/ROADMAP-API.md, раздел 13). Полосы «ЕСЛИ» в шапке нет: условия стали ШАГОМ
/// (<see cref="ScenarioFilterDialog"/>), потому что им нужны данные, а данные появляются только
/// после того, как их кто-то принес. Полоса в шапке обязана была бы угадывать, к какому шагу
/// относится, — а отбор, поставленный между «разобрать» и «зарегистрировать», не гадает ни о чем.
///
/// Два решения окна стоит назвать.
///
/// <list type="number">
///   <item><b>Периодичность выбирается из списка, а не набирается.</b> Опечатка «5» вместо «50»
///   превращает обмен в опрос и выбирает часовой потолок политики до обеда; набирать тут
///   нечего — готовых интервалов десяток, и все осмысленные.</item>
///   <item><b>Шаг «запрос» выбирает только КАРТОЧКУ запроса.</b> Раскладку колонок он берет у
///   нее же — ту, которую человек однажды настроил кнопкой «В коллекцию» и проверил глазами.
///   Окно говорит об этом прямо: запрос без привязки помечен, и сценарий его не выполнит.</item>
///   <item><b>Шаг «действие» правится своим окном.</b> У действия есть и коллекция, и список
///   полей, которого окно не знает наперед, — в строку списка это не помещается, а класть
///   вглубь то, что помещается (запрос, сообщение), значило бы прятать частое ради редкого.
///   Строка показывает настроенное словами и открывает <see cref="ScenarioActionDialog"/>
///   нажатием.</item>
/// </list>
///
/// Возвращает <see cref="Scenario"/> или null при отмене.
/// </summary>
public partial class ScenarioDialog : Window
{
    /// <summary>Готовые интервалы. Все, что чаще пяти минут, — уже опрос, а не расписание.</summary>
    private static readonly (int Minutes, string Text)[] Intervals =
    [
        (5,    "5 минут"),
        (10,   "10 минут"),
        (15,   "15 минут"),
        (30,   "30 минут"),
        (60,   "1 час"),
        (120,  "2 часа"),
        (180,  "3 часа"),
        (360,  "6 часов"),
        (720,  "12 часов"),
        (1440, "сутки"),
    ];

    /// <summary>
    /// Сколько ждать тишины после последней правки. Список короче, чем у периодичности, и
    /// намеренно: это не «как часто ходить», а «сколько дать человеку доредактировать», и
    /// ответов тут по-настоящему немного.
    /// </summary>
    private static readonly (int Minutes, string Text)[] Settles =
    [
        (5,  "5 минут тишины"),
        (10, "10 минут тишины"),
        (15, "15 минут тишины"),
        (30, "30 минут тишины"),
        (60, "час тишины"),
    ];

    /// <summary>
    /// Упреждение календарного триггера — за сколько до начала события заводить сценарий.
    /// Ноль минут — «к началу» — законный ответ: это не частота опроса, а фора.
    /// </summary>
    private static readonly (int Minutes, string Text)[] Leads =
    [
        (0,    "к началу события"),
        (5,    "за 5 минут"),
        (10,   "за 10 минут"),
        (15,   "за 15 минут"),
        (30,   "за 30 минут"),
        (60,   "за час"),
        (120,  "за 2 часа"),
        (720,  "за 12 часов"),
        (1440, "за сутки"),
    ];

    /// <summary>Строка списка шагов: сам шаг и то, чем его правят.</summary>
    private sealed class StepRow
    {
        public required ScenarioStepKind Kind { get; init; }

        public ComboBox? Requests { get; init; }
        public TextBox?  Text     { get; init; }
        public Button?   Card     { get; init; }

        /// <summary>
        /// Настроенный шаг — у тех видов, которые правятся своим окном (действие и отбор). У
        /// остальных не используется: им хватает одного контрола в строке.
        /// </summary>
        public ScenarioStep Action { get; set; } = ScenarioStep.OfAction("", "", 0, 0);

        /// <summary>Чем правят шаг — один контрол на строку.</summary>
        public Control Editor => (Control?)Requests ?? Text ?? (Control)Card!;
    }

    private readonly Scenario _card;

    // Запросы, на которые можно сослаться: ключ, имя и есть ли у него привязка к коллекции.
    private readonly IReadOnlyList<(int Id, string Name, bool Bound)> _requests;

    // Действия расширений и коллекции открытых баз — все, что нужно шагу «действие».
    private readonly IReadOnlyList<ActionChoice>     _actions;
    private readonly IReadOnlyList<CollectionChoice> _places;

    private readonly List<StepRow> _steps = [];

    /// <param name="card">Что правим; <see cref="Scenario.New"/> — новый сценарий.</param>
    /// <param name="requests">Карточки запросов с пометкой «привязка настроена».</param>
    /// <param name="actions">Действия расширений, которые можно поставить шагом.</param>
    /// <param name="places">Коллекции открытых баз — куда действие может писать.</param>
    public ScenarioDialog(Scenario                                        card,
                          IReadOnlyList<(int Id, string Name, bool Bound)> requests,
                          IReadOnlyList<ActionChoice>                     actions,
                          IReadOnlyList<CollectionChoice>                 places)
    {
        InitializeComponent();

        _card     = card;
        _requests = requests;
        _actions  = actions;
        _places   = places;

        Title        = card.Id > 0 ? "Сценарий" : "Новый сценарий";
        NameBox.Text = card.Name;

        EveryCombo.ItemsSource = Intervals.Select(i => i.Text).ToList();
        EveryCombo.SelectedIndex = Math.Max(0, Array.FindIndex(
            Intervals, i => i.Minutes == card.Trigger.Minutes));

        TimeBox.Text = card.Trigger.Kind == ScenarioTriggerKind.Daily
            ? card.Trigger.Time.ToString("HH:mm", CultureInfo.InvariantCulture)
            : "";

        ChangedCombo.ItemsSource = _places.Select(p => $"{p.Database} · {p.Name}").ToList();
        ChangedCombo.SelectedIndex = _places.Count == 0 ? -1 : Math.Max(0, _places.ToList().FindIndex(
            p => p.DbId == card.Trigger.DbId && p.CollectionId == card.Trigger.CollectionId));

        SettleCombo.ItemsSource = Settles.Select(s => s.Text).ToList();
        SettleCombo.SelectedIndex = Math.Max(0, Array.FindIndex(
            Settles, s => s.Minutes == card.Trigger.Minutes));

        CalendarCombo.ItemsSource = _places.Select(p => $"{p.Database} · {p.Name}").ToList();
        CalendarCombo.SelectedIndex = _places.Count == 0 ? -1 : Math.Max(0, _places.ToList().FindIndex(
            p => p.DbId == card.Trigger.DbId && p.CollectionId == card.Trigger.CollectionId));

        LeadCombo.ItemsSource = Leads.Select(l => l.Text).ToList();
        LeadCombo.SelectedIndex = card.Trigger.Kind == ScenarioTriggerKind.Calendar
            ? Math.Max(0, Array.FindIndex(Leads, l => l.Minutes == card.Trigger.Minutes))
            : Array.FindIndex(Leads, l => l.Minutes == 30);   // осмысленный дефолт: за полчаса

        ManualRadio.IsChecked   = card.Trigger.Kind == ScenarioTriggerKind.Manual;
        EveryRadio.IsChecked    = card.Trigger.Kind == ScenarioTriggerKind.Every;
        DailyRadio.IsChecked    = card.Trigger.Kind == ScenarioTriggerKind.Daily;
        ChangedRadio.IsChecked  = card.Trigger.Kind == ScenarioTriggerKind.Changed;
        CalendarRadio.IsChecked = card.Trigger.Kind == ScenarioTriggerKind.Calendar;

        // Следить не за чем: баз нет или в них нет коллекций. Отключенный переключатель честнее
        // пустого списка — он говорит «сейчас нельзя», а не «выбирайте из ничего».
        ChangedRadio.IsEnabled  = _places.Count > 0;
        CalendarRadio.IsEnabled = _places.Count > 0;

        EnabledSwitch.IsChecked = card.Enabled;

        foreach (var step in card.Steps) AddStep(step);

        ShowHint();

        ManualRadio.IsCheckedChanged    += (_, _) => ShowHint();
        EveryRadio.IsCheckedChanged     += (_, _) => ShowHint();
        DailyRadio.IsCheckedChanged     += (_, _) => ShowHint();
        ChangedRadio.IsCheckedChanged   += (_, _) => ShowHint();
        CalendarRadio.IsCheckedChanged  += (_, _) => ShowHint();
        EnabledSwitch.IsCheckedChanged  += (_, _) => ShowHint();

        AddRequestBtn.Click += (_, _) => AddStep(ScenarioStep.OfRequest(0));
        AddNoticeBtn.Click  += (_, _) => AddStep(ScenarioStep.OfNotice(""));
        AddIntakeBtn.Click  += (_, _) => AddStep(ScenarioStep.OfIntake(""));
        AddActionBtn.Click  += async (_, _) => await AddActionAsync();
        AddFilterBtn.Click  += async (_, _) => await AddFilterAsync();
        AddRecordBtn.Click  += async (_, _) => await AddRecordAsync();
        AddExportBtn.Click  += async (_, _) => await AddExportAsync();

        // Ссылаться не на что — запросы заводят на соседней вкладке, и сказать об этом надо
        // раньше, чем человек нажмет и получит пустой список.
        AddRequestBtn.IsEnabled = _requests.Count > 0;
        AddActionBtn.IsEnabled  = _actions.Count > 0 && _places.Count > 0;
        AddRecordBtn.IsEnabled  = _places.Count > 0;
        AddExportBtn.IsEnabled  = _places.Count > 0;

        CancelBtn.Click += (_, _) => Close(null);
        SaveBtn.Click   += (_, _) => Save();
        KeyDown         += (_, e) => { if (e.Key == Key.Escape) Close(null); };
    }

    // ── Шаги ──────────────────────────────────────────────────────────────────

    private void AddStep(ScenarioStep step)
    {
        var row = step.Kind switch
        {
            ScenarioStepKind.Request      => RequestRow(step),
            ScenarioStepKind.Action       => ActionRow(step),
            ScenarioStepKind.Intake       => IntakeRow(step),
            ScenarioStepKind.Filter       => FilterRow(step),
            ScenarioStepKind.CreateRecord => RecordRow(step),
            ScenarioStepKind.Export       => ExportRow(step),
            _                             => NoticeRow(step),
        };

        _steps.Add(row);
        Rebuild();
    }

    /// <summary>
    /// Завести шаг-действие. Окно настройки открывается СРАЗУ, а пустая строка в списке не
    /// появляется вовсе: шаг без действия и коллекции все равно не сохранится, а строка
    /// «настройте меня» — работа, переложенная на человека.
    /// </summary>
    private async Task AddActionAsync()
    {
        var made = await new ScenarioActionDialog(ScenarioStep.OfAction("", "", 0, 0),
                                                  _actions, _places).ShowDialog<ScenarioStep?>(this);

        if (made is not null) AddStep(made);
    }

    private StepRow RequestRow(ScenarioStep step)
    {
        var combo = new ComboBox
        {
            ItemsSource              = _requests.Select(Label).ToList(),
            Height                   = 30,
            HorizontalAlignment      = HorizontalAlignment.Stretch,
            VerticalContentAlignment = VerticalAlignment.Center,
        };

        var index = _requests.ToList().FindIndex(r => r.Id == step.RequestId);
        combo.SelectedIndex = index >= 0 ? index : (_requests.Count > 0 ? 0 : -1);

        return new StepRow { Kind = ScenarioStepKind.Request, Requests = combo };
    }

    /// <summary>Строка шага-действия: настроенное словами, нажатие открывает окно настройки.</summary>
    private StepRow ActionRow(ScenarioStep step)
    {
        var card = new Button
        {
            Height                     = 30,
            Padding                    = new Thickness(10, 0),
            HorizontalAlignment        = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Left,
            VerticalContentAlignment   = VerticalAlignment.Center,
        };

        var row = new StepRow { Kind = ScenarioStepKind.Action, Card = card, Action = step };

        card.Content = CardText(step);
        card.Click  += async (_, _) => await EditActionAsync(row);

        return row;
    }

    private async Task EditActionAsync(StepRow row)
    {
        var made = await new ScenarioActionDialog(row.Action, _actions, _places)
                             .ShowDialog<ScenarioStep?>(this);

        if (made is null) return;

        row.Action        = made;
        row.Card!.Content = CardText(made);
    }

    /// <summary>Завести шаг «создать запись» — окно сразу, как у действия и отбора.</summary>
    private async Task AddRecordAsync()
    {
        var made = await new ScenarioRecordDialog(
                ScenarioStep.OfRecord(0, 0, new Dictionary<string, string>()), _places)
            .ShowDialog<ScenarioStep?>(this);

        if (made is not null) AddStep(made);
    }

    /// <summary>Строка шага «создать запись»: настроенное словами, нажатие открывает окно.</summary>
    private StepRow RecordRow(ScenarioStep step)
    {
        var card = new Button
        {
            Height                     = 30,
            Padding                    = new Thickness(10, 0),
            HorizontalAlignment        = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Left,
            VerticalContentAlignment   = VerticalAlignment.Center,
        };

        var row = new StepRow { Kind = ScenarioStepKind.CreateRecord, Card = card, Action = step };

        card.Content = PlacedText(step);
        card.Click  += async (_, _) =>
        {
            var made = await new ScenarioRecordDialog(row.Action, _places)
                                 .ShowDialog<ScenarioStep?>(this);
            if (made is null) return;

            row.Action   = made;
            card.Content = PlacedText(made);
        };

        return row;
    }

    /// <summary>Завести шаг «экспорт выборки» — окно сразу, как у остальных сложных шагов.</summary>
    private async Task AddExportAsync()
    {
        var made = await new ScenarioExportDialog(ScenarioStep.OfExport(0, 0, "", "csv"), _places)
            .ShowDialog<ScenarioStep?>(this);

        if (made is not null) AddStep(made);
    }

    /// <summary>Строка шага «экспорт выборки»: настроенное словами, нажатие открывает окно.</summary>
    private StepRow ExportRow(ScenarioStep step)
    {
        var card = new Button
        {
            Height                     = 30,
            Padding                    = new Thickness(10, 0),
            HorizontalAlignment        = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Left,
            VerticalContentAlignment   = VerticalAlignment.Center,
        };

        var row = new StepRow { Kind = ScenarioStepKind.Export, Card = card, Action = step };

        card.Content = PlacedText(step);
        card.Click  += async (_, _) =>
        {
            var made = await new ScenarioExportDialog(row.Action, _places)
                                 .ShowDialog<ScenarioStep?>(this);
            if (made is null) return;

            row.Action   = made;
            card.Content = PlacedText(made);
        };

        return row;
    }

    /// <summary>Подпись шага, чья суть — коллекция: имя берется из списка мест.</summary>
    private string PlacedText(ScenarioStep step) =>
        step.Describe(_places.FirstOrDefault(p => p.DbId == step.DbId
                                                && p.CollectionId == step.CollectionId)?.Name ?? "");

    /// <summary>
    /// Завести шаг-отбор. Окно открывается сразу, по той же причине, что и у действия: отбор
    /// без условий не сохранится, а пустая строка «настройте меня» — переложенная работа.
    /// </summary>
    private async Task AddFilterAsync()
    {
        var made = await new ScenarioFilterDialog(ScenarioStep.OfFilter())
                             .ShowDialog<ScenarioStep?>(this);

        if (made is not null) AddStep(made);
    }

    /// <summary>Строка шага-отбора: условия словами, нажатие открывает окно правки.</summary>
    private StepRow FilterRow(ScenarioStep step)
    {
        var card = new Button
        {
            Height                     = 30,
            Padding                    = new Thickness(10, 0),
            HorizontalAlignment        = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Left,
            VerticalContentAlignment   = VerticalAlignment.Center,
            Content                    = step.Describe(),
        };

        var row = new StepRow { Kind = ScenarioStepKind.Filter, Card = card, Action = step };

        card.Click += async (_, _) =>
        {
            var made = await new ScenarioFilterDialog(row.Action).ShowDialog<ScenarioStep?>(this);
            if (made is null) return;

            row.Action   = made;
            card.Content = made.Describe();
        };

        return row;
    }

    /// <summary>Что настроено в шаге-действии — одной строкой на кнопке.</summary>
    private string CardText(ScenarioStep step)
    {
        if (step.Extension.Length == 0) return "Действие не выбрано";

        var action = _actions.FirstOrDefault(a => a.ExtensionId == step.Extension
                                               && a.Action.Key  == step.Action);

        var place = _places.FirstOrDefault(p => p.DbId         == step.DbId
                                             && p.CollectionId == step.CollectionId);

        var text = step.Describe(action?.Action.Name ?? "");

        return place is null ? text : text + " → " + place.Name;
    }

    /// <summary>
    /// Имя запроса и, если ее нет, пометка о привязке. Сценарий такой запрос не выполнит, и
    /// узнать об этом надо здесь, а не из журнала через неделю.
    /// </summary>
    private static string Label((int Id, string Name, bool Bound) request) =>
        request.Bound ? request.Name : request.Name + " — без привязки к коллекции";

    private static StepRow NoticeRow(ScenarioStep step)
    {
        var box = new TextBox
        {
            Text                     = step.Text,
            Height                   = 30,
            Watermark                = "Заказы обновлены",
            MaxLength                = ScenarioStep.MaxTextLength,
            VerticalContentAlignment = VerticalAlignment.Center,
            CornerRadius             = new CornerRadius(6),
        };

        return new StepRow { Kind = ScenarioStepKind.Notify, Text = box };
    }

    /// <summary>
    /// Строка шага «файл из папки»: путь к корню обмена. Выбор папки кнопкой тут не нужен —
    /// путь на общем диске приходит от администратора строкой и вставляется целиком, а обход
    /// сетевых дисков окном выбора на выключенном сервере отвечает минутным ожиданием.
    /// </summary>
    private static StepRow IntakeRow(ScenarioStep step)
    {
        var box = new TextBox
        {
            Text                     = step.Folder,
            Height                   = 30,
            Watermark                = @"P:\Обмен\КД",
            VerticalContentAlignment = VerticalAlignment.Center,
            CornerRadius             = new CornerRadius(6),
        };

        return new StepRow { Kind = ScenarioStepKind.Intake, Text = box };
    }

    /// <summary>
    /// Перерисовать список шагов. Целиком, а не точечно: номера и кнопки переноса зависят от
    /// места в списке, и держать их в согласии правками по одной — верный способ разойтись.
    /// </summary>
    private void Rebuild()
    {
        // Управление шага переживает перерисовку — в нем набранный текст и выбранный запрос.
        // Но у контрола может быть только один родитель, и, пока прежняя строка его держит,
        // положить его в новую нельзя: Avalonia отвечает на это исключением.
        foreach (var row in _steps)
            if (row.Editor.Parent is Panel old)
                old.Children.Remove(row.Editor);

        StepsPanel.Children.Clear();

        if (_steps.Count == 0)
        {
            StepsPanel.Children.Add(new TextBlock
            {
                Classes      = { "desc" },
                TextWrapping = Avalonia.Media.TextWrapping.Wrap,
                Text         = "Шагов пока нет. Сценарий без шагов не сохраняется: делать ему нечего.",
            });

            return;
        }

        for (var i = 0; i < _steps.Count; i++)
        {
            var index = i;
            var row   = _steps[i];

            var grid = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("Auto,6,*,6,Auto,4,Auto,4,Auto"),
            };

            Put(grid, 0, new TextBlock
            {
                Text              = $"{i + 1}.",
                Classes           = { "desc" },
                VerticalAlignment = VerticalAlignment.Center,
            });

            Put(grid, 2, row.Editor);

            Put(grid, 4, Tiny("↑", () => Move(index, -1), enabled: i > 0));
            Put(grid, 6, Tiny("↓", () => Move(index,  1), enabled: i < _steps.Count - 1));
            Put(grid, 8, Tiny("✕", () => { _steps.RemoveAt(index); Rebuild(); }));

            StepsPanel.Children.Add(grid);
        }
    }

    private void Move(int index, int shift)
    {
        var to = index + shift;
        if (to < 0 || to >= _steps.Count) return;

        (_steps[index], _steps[to]) = (_steps[to], _steps[index]);
        Rebuild();
    }

    private static Button Tiny(string text, Action click, bool enabled = true)
    {
        var button = new Button
        {
            Content   = text,
            Width     = 30,
            Height    = 30,
            Padding   = new Thickness(0),
            IsEnabled = enabled,
            HorizontalContentAlignment = HorizontalAlignment.Center,
        };

        button.Click += (_, _) => click();
        return button;
    }

    private static void Put(Grid grid, int column, Control child)
    {
        Grid.SetColumn(child, column);
        grid.Children.Add(child);
    }

    // ── Подсказка ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Что означает нынешний набор переключателей — словами. Главное здесь: включенный
    /// сценарий без расписания не сработает никогда, а человек будет считать, что обмен идет.
    /// </summary>
    private void ShowHint()
    {
        var manual   = ManualRadio.IsChecked   == true;
        var changed  = ChangedRadio.IsChecked  == true;
        var calendar = CalendarRadio.IsChecked == true;

        // Уточнение к правкам показывается только вместе с ними: строка «через 5 минут тишины»
        // под выбранным «ежедневно в 07:00» читается как часть расписания.
        SettleLabel.IsVisible  = changed;
        SettleCombo.IsVisible  = changed;
        ChangedCombo.IsEnabled = changed;

        LeadLabel.IsVisible     = calendar;
        LeadCombo.IsVisible     = calendar;
        CalendarCombo.IsEnabled = calendar;

        EnabledHint.Text = manual
            ? "Сценарий без расписания запускается только кнопкой, и включать его незачем — "
              + "выберите периодичность или оставьте тумблер выключенным."
            : changed
                ? "Сценарий пойдет, когда правки УТИХНУТ на выбранное время: правка десяти строк "
                  + "подряд даст один запуск, а не десять. Считаются правки только на этом "
                  + "компьютере."
                : calendar
                    ? "Сценарий пойдет к началу каждого события коллекции, с выбранной форой. "
                      + "Наступившие события приходят первому шагу строками: Название, Начало, "
                      + "Конец, Весь день, Коллекция."
                    : EnabledSwitch.IsChecked == true
                        ? "Сценарий будет запускаться сам. Пока выбранная база данных недоступна, запуск "
                          + "пропускается и догоняется, когда она вернется."
                        : "Пока выключен: запускается только кнопкой «Запустить» в списке.";
    }

    // ── Сохранение ────────────────────────────────────────────────────────────

    private void Save()
    {
        var trigger = ReadTrigger(out var problem);

        if (trigger is null)
        {
            NoticeBanner.Show(problem);
            return;
        }

        var steps = new List<ScenarioStep>();

        foreach (var row in _steps)
            steps.Add(row.Kind switch
            {
                ScenarioStepKind.Request      => ScenarioStep.OfRequest(SelectedRequest(row)),
                ScenarioStepKind.Action       => row.Action,
                ScenarioStepKind.Filter       => row.Action,
                ScenarioStepKind.CreateRecord => row.Action,
                ScenarioStepKind.Export       => row.Action,
                ScenarioStepKind.Intake       => ScenarioStep.OfIntake((row.Text!.Text ?? "").Trim()),
                _                             => ScenarioStep.OfNotice((row.Text!.Text ?? "").Trim()),
            });

        var result = _card with
        {
            Name    = (NameBox.Text ?? "").Trim(),
            Enabled = EnabledSwitch.IsChecked == true,
            Trigger = trigger,
            Steps   = steps,
        };

        var problems = result.Problems();

        if (problems.Count > 0)
        {
            NoticeBanner.Show(string.Join("; ", problems));
            return;
        }

        Close(result);
    }

    private int SelectedRequest(StepRow row) =>
        row.Requests is { SelectedIndex: var index } && index >= 0 && index < _requests.Count
            ? _requests[index].Id
            : 0;

    /// <summary>Триггер из переключателей; null — выбор неполон (причина в problem).</summary>
    private ScenarioTrigger? ReadTrigger(out string problem)
    {
        problem = "";

        if (ManualRadio.IsChecked == true) return ScenarioTrigger.Manual;

        if (EveryRadio.IsChecked == true)
        {
            var index = Math.Max(0, EveryCombo.SelectedIndex);
            return ScenarioTrigger.EveryMinutes(Intervals[index].Minutes);
        }

        if (ChangedRadio.IsChecked == true)
        {
            if (ChangedCombo.SelectedIndex < 0 || ChangedCombo.SelectedIndex >= _places.Count)
            {
                problem = "Выберите коллекцию, за изменениями которой следить";
                return null;
            }

            var place  = _places[ChangedCombo.SelectedIndex];
            var settle = Settles[Math.Clamp(SettleCombo.SelectedIndex, 0, Settles.Length - 1)].Minutes;

            return ScenarioTrigger.OnChange(place.DbId, place.CollectionId, settle);
        }

        if (CalendarRadio.IsChecked == true)
        {
            if (CalendarCombo.SelectedIndex < 0 || CalendarCombo.SelectedIndex >= _places.Count)
            {
                problem = "Выберите коллекцию, события которой заводят сценарий";
                return null;
            }

            var place = _places[CalendarCombo.SelectedIndex];
            var lead  = Leads[Math.Clamp(LeadCombo.SelectedIndex, 0, Leads.Length - 1)].Minutes;

            return ScenarioTrigger.BeforeEvent(place.DbId, place.CollectionId, lead);
        }

        if (TimeOnly.TryParseExact((TimeBox.Text ?? "").Trim(), "HH:mm",
                                   CultureInfo.InvariantCulture, DateTimeStyles.None, out var time))
            return ScenarioTrigger.DailyAt(time);

        problem = "Время указывается как ЧЧ:ММ, например 07:00";
        return null;
    }
}
