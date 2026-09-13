using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using SmartBase.Integrations;

namespace SmartBase.Controls.Dialogs;

/// <summary>
/// Окно настройки шага «экспорт выборки»: коллекция, формат, папка и отбор строк
/// (docs/ROADMAP-API.md, шаг 11).
///
/// Читает шаг КОЛЛЕКЦИЮ, а не поток предыдущих шагов: выборка существует и тогда, когда
/// сценарию никто ничего не принес, — «каждый вечер выгрузить неоплаченное». Папку выбирают
/// строкой, а не окном, по той же причине, что у папки обмена: путь на общем диске приходит
/// от администратора и вставляется целиком, а разрешает его политика, и проверка живет на
/// запуске.
///
/// Возвращает <see cref="ScenarioStep"/> или <c>null</c> при отмене.
/// </summary>
public partial class ScenarioExportDialog : Window
{
    /// <summary>Подписи форматов — по словарю модели, чтобы список нельзя было рассинхронизировать.</summary>
    private static readonly (string Format, string Text)[] Formats =
    [
        ("csv",  "CSV"),
        ("json", "JSON"),
        ("xlsx", "Excel"),
    ];

    /// <summary>Вопросы в том порядке, в каком их выбирают чаще, — как у шага «отбор».</summary>
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

    private readonly IReadOnlyList<CollectionChoice> _places;
    private readonly List<TestRow>                   _tests = [];

    public ScenarioExportDialog() : this(ScenarioStep.OfExport(0, 0, "", "csv"), []) { }

    /// <param name="step">Что правим; шаг с пустой коллекцией — новый.</param>
    /// <param name="places">Коллекции открытых баз.</param>
    public ScenarioExportDialog(ScenarioStep step, IReadOnlyList<CollectionChoice> places)
    {
        InitializeComponent();

        _places = places;

        PlaceCombo.ItemsSource = _places.Select(p => p.Name + " — " + p.Database).ToList();
        PlaceCombo.SelectedIndex = Math.Max(0, _places.ToList().FindIndex(
            p => p.DbId == step.DbId && p.CollectionId == step.CollectionId));

        FormatCombo.ItemsSource = Formats.Select(f => f.Text).ToList();
        FormatCombo.SelectedIndex = Math.Max(0, Array.FindIndex(
            Formats, f => f.Format == step.Format.Trim().ToLowerInvariant()));

        FolderBox.Text = step.Folder;

        foreach (var test in step.Tests) _tests.Add(NewRow(test));

        Rebuild();

        AddTestBtn.Click += (_, _) =>
        {
            if (_tests.Count >= ScenarioStep.MaxConditions)
            {
                NoticeBanner.Show($"Условий не больше {ScenarioStep.MaxConditions}: "
                                + "выгрузите в два приема.");
                return;
            }

            _tests.Add(NewRow(null));
            Rebuild();
        };

        if (_places.Count == 0)
            NoticeBanner.Show("Нет открытых баз данных: выгружать нечего.");

        SaveBtn.IsEnabled = _places.Count > 0;

        CancelBtn.Click += (_, _) => Close(null);
        SaveBtn.Click   += (_, _) => Save();
        KeyDown         += (_, e) => { if (e.Key == Key.Escape) Close(null); };
    }

    // ── Строки условий (тот же прием, что у окна отбора) ──────────────────────

    private TestRow NewRow(ScenarioCondition? test)
    {
        var column = new TextBox
        {
            Text                     = test?.Column ?? "",
            Height                   = 30,
            Watermark                = "Статус",
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
            Watermark                = "Не оплачен",
            VerticalContentAlignment = VerticalAlignment.Center,
            CornerRadius             = new CornerRadius(6),
        };

        var row = new TestRow(column, what, value);

        what.SelectionChanged += (_, _) => ShowValue(row);
        ShowValue(row);

        return row;
    }

    private static void ShowValue(TestRow row)
    {
        var needs = Chosen(row).NeedsValue;

        row.Value.IsEnabled = needs;
        row.Value.Watermark = needs ? "Не оплачен" : "образец не нужен";

        if (!needs) row.Value.Text = "";
    }

    private static ScenarioCondition Chosen(TestRow row) =>
        new((row.Column.Text ?? "").Trim(),
            Tests[Math.Clamp(row.Test.SelectedIndex, 0, Tests.Length - 1)],
            (row.Value.Text ?? "").Trim());

    private void Rebuild()
    {
        foreach (var row in _tests)
            foreach (var control in Controls(row))
                if (control.Parent is Panel old) old.Children.Remove(control);

        TestsPanel.Children.Clear();

        if (_tests.Count == 0)
        {
            TestsPanel.Children.Add(new TextBlock
            {
                Classes      = { "desc" },
                TextWrapping = Avalonia.Media.TextWrapping.Wrap,
                Text         = "Условий нет — уходит вся коллекция.",
            });

            return;
        }

        for (var i = 0; i < _tests.Count; i++)
        {
            var index = i;
            var row   = _tests[i];

            var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,6,Auto,6,*,6,Auto") };

            if (i > 0) TestsPanel.Children.Add(new TextBlock { Text = "и", Classes = { "desc" } });

            Put(grid, 0, row.Column);
            Put(grid, 2, row.Test);
            Put(grid, 4, row.Value);
            Put(grid, 6, Tiny("✕", () => { _tests.RemoveAt(index); Rebuild(); }));

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
        if (PlaceCombo.SelectedIndex < 0 || PlaceCombo.SelectedIndex >= _places.Count)
        {
            NoticeBanner.Show("Выберите коллекцию для выгрузки.");
            return;
        }

        var place  = _places[PlaceCombo.SelectedIndex];
        var format = Formats[Math.Clamp(FormatCombo.SelectedIndex, 0, Formats.Length - 1)].Format;

        var step = ScenarioStep.OfExport(place.DbId, place.CollectionId,
                                         (FolderBox.Text ?? "").Trim(), format,
                                         [.. _tests.Select(Chosen)]);

        var problems = step.Problems();
        if (problems.Count > 0)
        {
            NoticeBanner.Show(string.Join("; ", problems) + ".");
            return;
        }

        Close(step);
    }
}
