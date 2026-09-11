using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using SmartBase.Integrations;

namespace SmartBase.Controls.Dialogs;

/// <summary>
/// Окно правки шага «отбор строк» — то самое «ЕСЛИ» из плана (docs/ROADMAP-API.md, раздел 2).
///
/// В плане условия нарисованы отдельной полосой карточки сценария, между «когда» и «что делать».
/// Здесь они стали ШАГОМ, и причина не в удобстве: условию нужны данные, а данные появляются
/// только после того, как их кто-то принес. Полоса в шапке карточки обязана была бы угадывать, к
/// какому шагу относится, — а поставленный между «разобрать сообщение» и «зарегистрировать»,
/// отбор не гадает ни о чем.
///
/// Имя колонки набирается, а не выбирается из списка: какие колонки принесет предыдущий шаг,
/// заранее не знает никто — ответ чужой программы меняется без предупреждения. Тот же прием, что
/// и у подстановок <c>{{колонка}}</c> в шаге-действии; ошибка в имени останавливает шаг словами.
///
/// Возвращает <see cref="ScenarioStep"/> или <c>null</c> при отмене.
/// </summary>
public partial class ScenarioFilterDialog : Window
{
    /// <summary>Вопросы в том порядке, в каком их выбирают чаще.</summary>
    private static readonly ScenarioTest[] Tests =
    [
        ScenarioTest.Filled,
        ScenarioTest.Empty,
        ScenarioTest.Equals,
        ScenarioTest.NotEquals,
        ScenarioTest.Contains,
        ScenarioTest.Matches,
    ];

    /// <summary>Строка списка: три поля одного условия.</summary>
    private sealed record TestRow(TextBox Column, ComboBox Test, TextBox Value);

    private readonly List<TestRow> _tests = [];

    public ScenarioFilterDialog() : this(ScenarioStep.OfFilter()) { }

    /// <param name="step">Что правим; шаг без условий — новый отбор.</param>
    public ScenarioFilterDialog(ScenarioStep step)
    {
        InitializeComponent();

        foreach (var test in step.Tests) _tests.Add(NewRow(test));

        // Пустой список показывать незачем: условие в отборе нужно хотя бы одно, и строка
        // «нажмите плюс» — работа, переложенная на человека.
        if (_tests.Count == 0) _tests.Add(NewRow(null));

        Rebuild();

        AddTestBtn.Click += (_, _) =>
        {
            if (_tests.Count >= ScenarioStep.MaxConditions)
            {
                NoticeBanner.Show($"Условий в одном отборе не больше {ScenarioStep.MaxConditions}: "
                                + "отберите в два приема.");
                return;
            }

            _tests.Add(NewRow(null));
            Rebuild();
        };

        CancelBtn.Click += (_, _) => Close(null);
        SaveBtn.Click   += (_, _) => Save();
    }

    // ── Строки ────────────────────────────────────────────────────────────────

    private TestRow NewRow(ScenarioCondition? test)
    {
        var column = new TextBox
        {
            Text                     = test?.Column ?? "",
            Height                   = 30,
            Watermark                = "Проект",
            VerticalContentAlignment = VerticalAlignment.Center,
            CornerRadius             = new CornerRadius(6),
        };

        var what = new ComboBox
        {
            ItemsSource              = Tests.Select(ScenarioCondition.Name).ToList(),
            SelectedIndex            = Math.Max(0, Array.IndexOf(Tests, test?.Test ?? ScenarioTest.Filled)),
            Width                    = 110,
            Height                   = 30,
            VerticalContentAlignment = VerticalAlignment.Center,
        };

        var value = new TextBox
        {
            Text                     = test?.Value ?? "",
            Height                   = 30,
            Watermark                = "Оплачен",
            VerticalContentAlignment = VerticalAlignment.Center,
            CornerRadius             = new CornerRadius(6),
        };

        var row = new TestRow(column, what, value);

        // Образец у «заполнено» и «пусто» не спрашивается, но место под него остается: поле,
        // исчезающее при смене вопроса, дергает всю строку — а вопрос меняют часто.
        what.SelectionChanged += (_, _) => ShowValue(row);
        ShowValue(row);

        return row;
    }

    private static void ShowValue(TestRow row)
    {
        var needs = Chosen(row).NeedsValue;

        row.Value.IsEnabled = needs;
        row.Value.Watermark = needs ? "Оплачен" : "образец не нужен";

        if (!needs) row.Value.Text = "";
    }

    private static ScenarioCondition Chosen(TestRow row) =>
        new((row.Column.Text ?? "").Trim(),
            Tests[Math.Clamp(row.Test.SelectedIndex, 0, Tests.Length - 1)],
            (row.Value.Text ?? "").Trim());

    /// <summary>
    /// Перерисовать список целиком, а не точечно: кнопка удаления зависит от места строки в
    /// списке, и держать их в согласии правками по одной — верный способ разойтись.
    /// </summary>
    private void Rebuild()
    {
        // У контрола может быть только один родитель, и, пока прежняя строка его держит,
        // положить его в новую нельзя: Avalonia отвечает на это исключением.
        foreach (var row in _tests)
            foreach (var control in Controls(row))
                if (control.Parent is Panel old) old.Children.Remove(control);

        TestsPanel.Children.Clear();

        for (var i = 0; i < _tests.Count; i++)
        {
            var index = i;
            var row   = _tests[i];

            var grid = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("*,6,Auto,6,*,6,Auto"),
            };

            // Союз между условиями — отдельной строкой: «и» сбоку от полей потерялось бы, а
            // именно оно объясняет, почему строка обязана пройти их все.
            if (i > 0) TestsPanel.Children.Add(new TextBlock { Text = "и", Classes = { "desc" } });

            Put(grid, 0, row.Column);
            Put(grid, 2, row.Test);
            Put(grid, 4, row.Value);
            Put(grid, 6, Tiny("✕", () =>
            {
                _tests.RemoveAt(index);
                if (_tests.Count == 0) _tests.Add(NewRow(null));
                Rebuild();
            }));

            TestsPanel.Children.Add(grid);
        }
    }

    private static IEnumerable<Control> Controls(TestRow row)
    {
        yield return row.Column;
        yield return row.Test;
        yield return row.Value;
    }

    private static Button Tiny(string text, Action click)
    {
        var button = new Button
        {
            Content                    = text,
            Width                      = 30,
            Height                     = 30,
            Padding                    = new Thickness(0),
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

    // ── Сохранение ────────────────────────────────────────────────────────────

    private void Save()
    {
        var step = ScenarioStep.OfFilter([.. _tests.Select(Chosen)]);

        var problems = step.Problems();
        if (problems.Count > 0)
        {
            NoticeBanner.Show(string.Join("; ", problems) + ".");
            return;
        }

        Close(step);
    }
}
