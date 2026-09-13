using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using SmartBase.Integrations;

namespace SmartBase.Controls.Dialogs;

/// <summary>
/// Окно настройки шага «преобразовать строки»: таблица «новая колонка = выражение» и проба на
/// придуманной строке (docs/ROADMAP-API.md, шаг 12).
///
/// Имена колонок набираются, а не выбираются из списка, — по той же причине, что в отборе: какие
/// колонки принесет предыдущий шаг, заранее не знает никто, ответ чужой программы меняется без
/// предупреждения. Опечатку ловит запуск и называет словами, какие колонки на самом деле пришли.
///
/// <b>Проба считает здесь и сейчас, ни во что не заглядывая.</b> У выражения нет ни базы, ни
/// сети, ни файлов — все, что ему нужно, это значения строки, и придумать их может человек.
/// Поля под пробные значения появляются сами: имена берутся из самих выражений, и набирать их
/// второй раз не приходится.
///
/// Возвращает <see cref="ScenarioStep"/> или <c>null</c> при отмене.
/// </summary>
public partial class ScenarioComputeDialog : Window
{
    /// <summary>Строка таблицы: имя новой колонки и выражение для нее.</summary>
    private sealed record ColumnRow(TextBox Name, TextBox Expression);

    /// <summary>Строка пробы: колонка из выражения и придуманное для нее значение.</summary>
    private sealed record SampleRow(string Column, TextBox Value);

    private readonly List<ColumnRow> _columns = [];
    private readonly List<SampleRow> _sample  = [];

    public ScenarioComputeDialog() : this(ScenarioStep.OfCompute()) { }

    /// <param name="step">Что правим; шаг без колонок — новое преобразование.</param>
    public ScenarioComputeDialog(ScenarioStep step)
    {
        InitializeComponent();

        foreach (var formula in step.Formulas)
            _columns.Add(NewRow(formula.Column, formula.Expression));

        // Пустой список показывать незачем: колонка нужна хотя бы одна, а строка «нажмите плюс» —
        // работа, переложенная на человека.
        if (_columns.Count == 0) _columns.Add(NewRow("", ""));

        Rebuild();
        ShowHelp();

        AddColumnBtn.Click += (_, _) =>
        {
            if (_columns.Count >= ScenarioStep.MaxFormulas)
            {
                NoticeBanner.Show($"Колонок в одном шаге не больше {ScenarioStep.MaxFormulas}: "
                                + "посчитайте в два приема.");
                return;
            }

            _columns.Add(NewRow("", ""));
            Rebuild();
        };

        TryBtn.Click    += (_, _) => Probe();
        CancelBtn.Click += (_, _) => Close(null);
        SaveBtn.Click   += (_, _) => Save();
        KeyDown         += (_, e) => { if (e.Key == Key.Escape) Close(null); };
    }

    // ── Строки таблицы ────────────────────────────────────────────────────────

    private static ColumnRow NewRow(string name, string expression) => new(
        new TextBox
        {
            Text                     = name,
            Height                   = 30,
            Watermark                = "Итого",
            VerticalContentAlignment = VerticalAlignment.Center,
            CornerRadius             = new CornerRadius(6),
        },
        new TextBox
        {
            Text                     = expression,
            Height                   = 30,
            Watermark                = "{{Цена}} * {{Количество}}",
            FontFamily               = Mono,
            VerticalContentAlignment = VerticalAlignment.Center,
            CornerRadius             = new CornerRadius(6),
        });

    /// <summary>
    /// Моноширинный шрифт для самих выражений: скобки и кавычки в пропорциональном шрифте
    /// сливаются, а ищут в выражении обычно именно их.
    /// </summary>
    private static FontFamily Mono =>
        Application.Current!.TryFindResource("FontFamilyMono", out var found) && found is FontFamily family
            ? family
            : FontFamily.Default;

    /// <summary>
    /// Перерисовать таблицу целиком, а не точечно: кнопка удаления зависит от места строки в
    /// списке, и держать их в согласии правками по одной — верный способ разойтись.
    /// </summary>
    private void Rebuild()
    {
        // У контрола может быть только один родитель, и, пока прежняя строка его держит,
        // положить его в новую нельзя: Avalonia отвечает на это исключением.
        foreach (var row in _columns)
        {
            if (row.Name.Parent       is Panel a) a.Children.Remove(row.Name);
            if (row.Expression.Parent is Panel b) b.Children.Remove(row.Expression);
        }

        ColumnsPanel.Children.Clear();

        for (var i = 0; i < _columns.Count; i++)
        {
            var index = i;
            var row   = _columns[i];

            var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("200,6,Auto,6,*,6,Auto") };

            Put(grid, 0, row.Name);
            Put(grid, 2, new TextBlock
            {
                Text              = "=",
                Classes           = { "desc" },
                VerticalAlignment = VerticalAlignment.Center,
            });
            Put(grid, 4, row.Expression);
            Put(grid, 6, Tiny("✕", () =>
            {
                _columns.RemoveAt(index);
                if (_columns.Count == 0) _columns.Add(NewRow("", ""));
                Rebuild();
            }));

            ColumnsPanel.Children.Add(grid);
        }
    }

    /// <summary>Справка по функциям — рядом с таблицей, а не в отдельной странице docs.</summary>
    private void ShowHelp()
    {
        HelpPanel.Children.Add(new TextBlock
        {
            Text         = "Что умеют выражения",
            FontWeight   = FontWeight.SemiBold,
            TextWrapping = TextWrapping.Wrap,
        });

        foreach (var (signature, what) in Formula.Help)
        {
            var line = new Grid { ColumnDefinitions = new ColumnDefinitions("340,10,*") };

            Put(line, 0, new TextBlock
            {
                Text         = signature,
                FontFamily   = Mono,
                TextWrapping = TextWrapping.Wrap,
            });

            Put(line, 2, new TextBlock
            {
                Text         = what,
                Classes      = { "desc" },
                TextWrapping = TextWrapping.Wrap,
            });

            HelpPanel.Children.Add(line);
        }
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

    // ── Проба ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Посчитать выражения на придуманной строке. Все происходит в окне: ни базы, ни сети, ни
    /// запуска сценария — ровно то же, что сделает шаг, только над одной строкой, которую человек
    /// набрал сам.
    /// </summary>
    private void Probe()
    {
        NoticeBanner.Hide();

        var formulas = Read(out var trouble);

        if (formulas is null)
        {
            Result(trouble, bad: true);
            return;
        }

        var ready = new List<(string Column, Formula Formula)>(formulas.Count);

        foreach (var formula in formulas)
        {
            if (Formula.TryParse(formula.Expression, out var problem) is not { } parsed)
            {
                Result($"Колонка «{formula.Column}»: {problem}.", bad: true);
                return;
            }

            ready.Add((formula.Column, parsed));
        }

        // Какие колонки нужны пробе: все названные в выражениях, кроме тех, что этот же шаг
        // считает выше по списку, — их придумывать не надо, они посчитаются сами.
        var made   = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var needed = new List<string>();

        foreach (var (column, formula) in ready)
        {
            foreach (var name in formula.Columns)
                if (!made.Contains(name) && !needed.Contains(name, StringComparer.OrdinalIgnoreCase))
                    needed.Add(name);

            made.Add(column);
        }

        var appeared = ShowSample(needed);

        // Поля только появились и пусты: считать по ним нечего, а сказанное словами понятнее
        // вороха отказов «пусто, а нужно число».
        if (appeared && _sample.All(s => (s.Value.Text ?? "").Trim().Length == 0))
        {
            Result("Заполните пробные значения колонок и нажмите «Проба» еще раз.", bad: false);
            return;
        }

        var row = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var value in _sample) row[value.Column] = value.Value.Text ?? "";

        var lines = new List<string>(ready.Count);

        foreach (var (column, formula) in ready)
        {
            var value = formula.Run(row, DateTimeOffset.Now, out var problem);

            if (problem.Length > 0)
            {
                Result($"Колонка «{column}»: {problem}.", bad: true);
                return;
            }

            row[column] = value;
            lines.Add($"{column} = {value}");
        }

        Result(string.Join("\n", lines), bad: false);
    }

    /// <summary>
    /// Показать поля под пробные значения. Уже набранное переживает перестройку: человек правит
    /// выражение и жмет «Проба» по нескольку раз подряд, и терять придуманные значения на каждом
    /// нажатии — способ отбить желание пробовать вовсе.
    /// </summary>
    /// <returns>Появились ли поля, которых раньше не было.</returns>
    private bool ShowSample(IReadOnlyList<string> columns)
    {
        var known = _sample.ToDictionary(s => s.Column, s => s.Value.Text ?? "",
                                         StringComparer.OrdinalIgnoreCase);

        var appeared = columns.Any(c => !known.ContainsKey(c));

        _sample.Clear();
        SamplePanel.Children.Clear();

        foreach (var column in columns)
        {
            var box = new TextBox
            {
                Text                     = known.TryGetValue(column, out var had) ? had : "",
                Height                   = 30,
                Watermark                = "значение",
                VerticalContentAlignment = VerticalAlignment.Center,
                CornerRadius             = new CornerRadius(6),
            };

            var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("200,10,*") };

            Put(grid, 0, new TextBlock
            {
                Text              = column,
                TextWrapping      = TextWrapping.Wrap,
                VerticalAlignment = VerticalAlignment.Center,
            });

            Put(grid, 2, box);

            SamplePanel.Children.Add(grid);
            _sample.Add(new SampleRow(column, box));
        }

        SampleBox.IsVisible = columns.Count > 0;

        TryHint.Text = columns.Count > 0
            ? "Проба считает выражения на этой придуманной строке — здесь, не запуская сценарий."
            : "Выражения ни одной колонки не называют — проба считает их как есть.";

        return appeared;
    }

    /// <summary>Ответ пробы: посчитанное — обычным цветом, отказ — красным.</summary>
    private void Result(string text, bool bad)
    {
        ResultText.Text       = text;
        ResultText.Foreground = Brush(bad ? "BrushRed" : "BrushPrimaryText");
        ResultBox.IsVisible   = text.Length > 0;
    }

    private static IBrush Brush(string key) =>
        Application.Current!.TryFindResource(key, out var found) && found is IBrush brush
            ? brush
            : Brushes.Black;

    // ── Сохранение ────────────────────────────────────────────────────────────

    /// <summary>
    /// Собрать колонки из таблицы; <c>null</c> — собирать нечего или набрано наполовину, причина
    /// в <paramref name="trouble"/>.
    /// </summary>
    private List<ScenarioFormula>? Read(out string trouble)
    {
        trouble = "";

        var formulas = new List<ScenarioFormula>();
        var names    = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var row in _columns)
        {
            var name       = (row.Name.Text       ?? "").Trim();
            var expression = (row.Expression.Text ?? "").Trim();

            // Совсем пустая строка — недобранная заготовка, ее можно молча пропустить; половина
            // строки пропускаться не должна: потерялось бы набранное.
            if (name.Length == 0 && expression.Length == 0) continue;

            if (name.Length == 0)
            {
                trouble = "У одного из выражений не названа колонка.";
                return null;
            }

            if (expression.Length == 0)
            {
                trouble = $"У колонки «{name}» не заполнено выражение.";
                return null;
            }

            if (!names.Add(name))
            {
                trouble = $"Колонка «{name}» названа дважды.";
                return null;
            }

            formulas.Add(new ScenarioFormula(name, expression));
        }

        if (formulas.Count == 0)
        {
            trouble = "Не задано ни одной колонки.";
            return null;
        }

        return formulas;
    }

    private void Save()
    {
        var formulas = Read(out var trouble);

        if (formulas is null)
        {
            NoticeBanner.Show(trouble);
            return;
        }

        var step     = ScenarioStep.OfCompute([.. formulas]);
        var problems = step.Problems();

        if (problems.Count > 0)
        {
            NoticeBanner.Show(string.Join("; ", problems) + ".");
            return;
        }

        Close(step);
    }
}
