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
/// (docs/ROADMAP-API.md, раздел 13). Полоса «ЕСЛИ» из плана здесь пока не показана: условиям
/// нужно то, что течет между шагами, а это приезжает вместе с контрактом <c>Actions</c>.
/// Настройка, которая ничего не делает, читается как поломка.
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

    /// <summary>Строка списка шагов: сам шаг и то, чем его правят.</summary>
    private sealed record StepRow(ScenarioStepKind Kind, ComboBox? Requests, TextBox? Text);

    private readonly Scenario _card;

    // Запросы, на которые можно сослаться: ключ, имя и есть ли у него привязка к коллекции.
    private readonly IReadOnlyList<(int Id, string Name, bool Bound)> _requests;

    private readonly List<StepRow> _steps = [];

    /// <param name="card">Что правим; <see cref="Scenario.New"/> — новый сценарий.</param>
    /// <param name="requests">Карточки запросов с пометкой «привязка настроена».</param>
    public ScenarioDialog(Scenario card, IReadOnlyList<(int Id, string Name, bool Bound)> requests)
    {
        InitializeComponent();

        _card     = card;
        _requests = requests;

        Title        = card.Id > 0 ? "Сценарий" : "Новый сценарий";
        NameBox.Text = card.Name;

        EveryCombo.ItemsSource = Intervals.Select(i => i.Text).ToList();
        EveryCombo.SelectedIndex = Math.Max(0, Array.FindIndex(
            Intervals, i => i.Minutes == card.Trigger.Minutes));

        TimeBox.Text = card.Trigger.Kind == ScenarioTriggerKind.Daily
            ? card.Trigger.Time.ToString("HH:mm", CultureInfo.InvariantCulture)
            : "";

        ManualRadio.IsChecked = card.Trigger.Kind == ScenarioTriggerKind.Manual;
        EveryRadio.IsChecked  = card.Trigger.Kind == ScenarioTriggerKind.Every;
        DailyRadio.IsChecked  = card.Trigger.Kind == ScenarioTriggerKind.Daily;

        EnabledSwitch.IsChecked = card.Enabled;

        foreach (var step in card.Steps) AddStep(step);

        ShowHint();

        ManualRadio.IsCheckedChanged   += (_, _) => ShowHint();
        EveryRadio.IsCheckedChanged     += (_, _) => ShowHint();
        DailyRadio.IsCheckedChanged     += (_, _) => ShowHint();
        EnabledSwitch.IsCheckedChanged  += (_, _) => ShowHint();

        AddRequestBtn.Click += (_, _) => AddStep(ScenarioStep.OfRequest(0));
        AddNoticeBtn.Click  += (_, _) => AddStep(ScenarioStep.OfNotice(""));

        // Ссылаться не на что — запросы заводят на соседней вкладке, и сказать об этом надо
        // раньше, чем человек нажмет и получит пустой список.
        AddRequestBtn.IsEnabled = _requests.Count > 0;

        CancelBtn.Click += (_, _) => Close(null);
        SaveBtn.Click   += (_, _) => Save();
        KeyDown         += (_, e) => { if (e.Key == Key.Escape) Close(null); };
    }

    // ── Шаги ──────────────────────────────────────────────────────────────────

    private void AddStep(ScenarioStep step)
    {
        var row = step.Kind == ScenarioStepKind.Request ? RequestRow(step) : NoticeRow(step);

        _steps.Add(row);
        Rebuild();
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

        return new StepRow(ScenarioStepKind.Request, combo, null);
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

        return new StepRow(ScenarioStepKind.Notify, null, box);
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
            if (((Control?)row.Requests ?? row.Text!).Parent is Panel old)
                old.Children.Remove((Control?)row.Requests ?? row.Text!);

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

            Put(grid, 2, (Control?)row.Requests ?? row.Text!);

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
        var manual = ManualRadio.IsChecked == true;

        EnabledHint.Text = manual
            ? "Сценарий без расписания запускается только кнопкой, и включать его незачем — "
              + "выберите периодичность или оставьте тумблер выключенным."
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
            steps.Add(row.Kind == ScenarioStepKind.Request
                ? ScenarioStep.OfRequest(SelectedRequest(row))
                : ScenarioStep.OfNotice((row.Text!.Text ?? "").Trim()));

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

    /// <summary>Триггер из переключателей; null — время набрано не так (причина в problem).</summary>
    private ScenarioTrigger? ReadTrigger(out string problem)
    {
        problem = "";

        if (ManualRadio.IsChecked == true) return ScenarioTrigger.Manual;

        if (EveryRadio.IsChecked == true)
        {
            var index = Math.Max(0, EveryCombo.SelectedIndex);
            return ScenarioTrigger.EveryMinutes(Intervals[index].Minutes);
        }

        if (TimeOnly.TryParseExact((TimeBox.Text ?? "").Trim(), "HH:mm",
                                   CultureInfo.InvariantCulture, DateTimeStyles.None, out var time))
            return ScenarioTrigger.DailyAt(time);

        problem = "Время указывается как ЧЧ:ММ, например 07:00";
        return null;
    }
}
