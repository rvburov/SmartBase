using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using SmartBase.Integrations;

namespace SmartBase.Controls.Widgets;

/// <summary>
/// Список условий отбора — «колонка · вопрос · образец», по строке на условие
/// (docs/ROADMAP-API.md, раздел 2: «событие → условие → действие»).
///
/// <b>Почему виджет.</b> Один и тот же список правится в трех местах: шаг «отбор», шаг «экспорт
/// выборки» и состав исходящей привязки. Первые два были посимвольными копиями друг друга —
/// строка из трех полей, кнопка «✕», союз «и» между строками, потолок числа условий, — и третье
/// место стало бы третьей копией.
///
/// <b>Колонки бывают известны, а бывают нет, и это не мелочь.</b> У шага отбора колонки приносит
/// предыдущий шаг — чужая выгрузка, состав которой меняется без предупреждения, — и там имя
/// НАБИРАЕТСЯ. У исходящей привязки колонки свои, это свойства нашей коллекции, и там имя
/// ВЫБИРАЕТСЯ: набирать его руками означало бы приглашать опечатку туда, где ее можно не
/// допустить вовсе. Виджет умеет оба вида, потому что различие настоящее, а не оформительское.
///
/// Своих правил виджет не знает: что считается годным условием, решает
/// <see cref="ScenarioCondition.Problems"/> у того, кто его читает.
/// </summary>
public sealed class ConditionRows : StackPanel
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

    /// <summary>
    /// Строка списка: три поля одного условия. Поле колонки — либо ввод, либо выбор.
    /// <paramref name="ValueHint"/> хранится отдельно от самого поля: подсказка в нем
    /// подменяется на «образец не нужен» и обратно, и взять исходную было бы уже неоткуда.
    /// </summary>
    private sealed record Row(Control Column, ComboBox Test, TextBox Value, string ValueHint);

    private readonly List<Row> _rows = [];

    private IReadOnlyList<string> _columns    = [];
    private string                _columnHint = "Проект";
    private string                _valueHint  = "Оплачен";
    private bool                  _atLeastOne = true;
    private string                _emptyNote  = "";

    public ConditionRows() => Spacing = 6;

    /// <summary>Сколько условий сейчас в списке.</summary>
    public int Count => _rows.Count;

    /// <summary>
    /// Заполнить список.
    /// </summary>
    /// <param name="tests">Что правим; пусто — новый отбор.</param>
    /// <param name="columns">Известные имена колонок; пусто — имя набирается руками.</param>
    /// <param name="columnHint">Пример имени колонки в пустом поле.</param>
    /// <param name="valueHint">Пример образца в пустом поле.</param>
    /// <param name="atLeastOne">Держать хотя бы одну строку: у отбора условие нужно всегда, а у
    /// исходящей привязки список бывает пустым — это и значит «уходит вся коллекция».</param>
    /// <param name="emptyNote">Что написать вместо пустого списка; пусто — не писать ничего.
    /// Нужно там, где отбора может не быть: пустая рамка не отличима от недорисованной.</param>
    public void Load(IEnumerable<ScenarioCondition> tests,
                     IReadOnlyList<string>?         columns    = null,
                     string                         columnHint = "Проект",
                     string                         valueHint  = "Оплачен",
                     bool                           atLeastOne = true,
                     string                         emptyNote  = "")
    {
        _columns    = columns ?? [];
        _columnHint = columnHint;
        _valueHint  = valueHint;
        _atLeastOne = atLeastOne;
        _emptyNote  = emptyNote;

        _rows.Clear();
        foreach (var test in tests) _rows.Add(NewRow(test));

        // Пустой список показывать незачем там, где условие нужно хотя бы одно: строка
        // «нажмите плюс» — работа, переложенная на человека.
        if (_atLeastOne && _rows.Count == 0) _rows.Add(NewRow(null));

        Rebuild();
    }

    /// <summary>Добавить пустую строку.</summary>
    public void Add()
    {
        _rows.Add(NewRow(null));
        Rebuild();
    }

    /// <summary>
    /// Что набрано. Пустые строки отбрасываются: человек нажал «плюс» и передумал — это не
    /// повод отказывать в сохранении.
    /// </summary>
    public IReadOnlyList<ScenarioCondition> Read() =>
        [.. _rows.Select(Chosen).Where(c => c.Column.Length > 0 || c.Value.Length > 0)];

    // ── Строки ────────────────────────────────────────────────────────────────

    private Row NewRow(ScenarioCondition? test)
    {
        var name   = test?.Column ?? "";
        Control column = _columns.Count > 0 ? KnownColumn(name) : TypedColumn(name);

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
            Watermark                = _valueHint,
            VerticalContentAlignment = VerticalAlignment.Center,
            CornerRadius             = new CornerRadius(6),
        };

        var row = new Row(column, what, value, _valueHint);

        // Образец у «заполнено» и «пусто» не спрашивается, но место под него остается: поле,
        // исчезающее при смене вопроса, дергает всю строку — а вопрос меняют часто.
        what.SelectionChanged += (_, _) => ShowValue(row);
        ShowValue(row);

        return row;
    }

    private TextBox TypedColumn(string name) => new()
    {
        Text                     = name,
        Height                   = 30,
        Watermark                = _columnHint,
        VerticalContentAlignment = VerticalAlignment.Center,
        CornerRadius             = new CornerRadius(6),
    };

    /// <summary>
    /// Выбор из известных колонок. Сохраненное имя, которого в списке больше нет (свойство
    /// переименовали или удалили), добавляется в список ОТДЕЛЬНОЙ строкой, а не теряется молча:
    /// иначе открытие окна тихо стирало бы условие, по которому обмен работал вчера.
    /// </summary>
    private ComboBox KnownColumn(string name)
    {
        var items = _columns.ToList();

        if (name.Length > 0 && !items.Contains(name, StringComparer.OrdinalIgnoreCase))
            items.Insert(0, name);

        return new ComboBox
        {
            ItemsSource              = items,
            SelectedItem             = name.Length > 0 ? items.FirstOrDefault(
                                           i => string.Equals(i, name, StringComparison.OrdinalIgnoreCase))
                                     : null,
            PlaceholderText          = "выберите свойство",
            HorizontalAlignment      = HorizontalAlignment.Stretch,
            Height                   = 30,
            VerticalContentAlignment = VerticalAlignment.Center,
        };
    }

    private static void ShowValue(Row row)
    {
        var needs = Chosen(row).NeedsValue;

        row.Value.IsEnabled = needs;
        row.Value.Watermark = needs ? row.ValueHint : "образец не нужен";

        if (!needs) row.Value.Text = "";
    }

    private static ScenarioCondition Chosen(Row row) =>
        new(ColumnOf(row.Column),
            Tests[Math.Clamp(row.Test.SelectedIndex, 0, Tests.Length - 1)],
            (row.Value.Text ?? "").Trim());

    private static string ColumnOf(Control column) => column switch
    {
        TextBox  box  => (box.Text ?? "").Trim(),
        ComboBox list => (list.SelectedItem as string ?? "").Trim(),
        _             => "",
    };

    /// <summary>
    /// Перерисовать список целиком, а не точечно: кнопка удаления зависит от места строки в
    /// списке, и держать их в согласии правками по одной — верный способ разойтись.
    /// </summary>
    private void Rebuild()
    {
        // У контрола может быть только один родитель, и, пока прежняя строка его держит,
        // положить его в новую нельзя: Avalonia отвечает на это исключением.
        foreach (var row in _rows)
            foreach (var control in new Control[] { row.Column, row.Test, row.Value })
                if (control.Parent is Panel old) old.Children.Remove(control);

        Children.Clear();

        if (_rows.Count == 0 && _emptyNote.Length > 0)
        {
            Children.Add(new TextBlock
            {
                Classes      = { "desc" },
                TextWrapping = Avalonia.Media.TextWrapping.Wrap,
                Text         = _emptyNote,
            });

            return;
        }

        for (var i = 0; i < _rows.Count; i++)
        {
            var index = i;
            var row   = _rows[i];

            var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,6,Auto,6,*,6,Auto") };

            // Союз между условиями — отдельной строкой: «и» сбоку от полей потерялось бы, а
            // именно оно объясняет, почему строка обязана пройти их все.
            if (i > 0) Children.Add(new TextBlock { Text = "и", Classes = { "desc" } });

            Put(grid, 0, row.Column);
            Put(grid, 2, row.Test);
            Put(grid, 4, row.Value);
            Put(grid, 6, Tiny("✕", () =>
            {
                _rows.RemoveAt(index);
                if (_atLeastOne && _rows.Count == 0) _rows.Add(NewRow(null));
                Rebuild();
            }));

            Children.Add(grid);
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
}
