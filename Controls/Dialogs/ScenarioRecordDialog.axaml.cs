using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using SmartBase.Integrations;

namespace SmartBase.Controls.Dialogs;

/// <summary>
/// Окно настройки шага «создать запись»: коллекция и таблица «поле → выражение»
/// (docs/ROADMAP-API.md, шаг 11).
///
/// Имя поля НАБИРАЕТСЯ, а не выбирается из списка, — тем же приемом, что колонка в отборе:
/// окно не ходит в базу, а проверку с живым составом коллекции делает запуск и отказывает
/// словами, называя, какие поля есть. Значение — обычный текст с подстановками
/// <c>{{колонка}}</c>, одно поле на постоянное и подставленное разом, как у действия.
///
/// Возвращает <see cref="ScenarioStep"/> или <c>null</c> при отмене.
/// </summary>
public partial class ScenarioRecordDialog : Window
{
    /// <summary>Строка списка: поле и его выражение.</summary>
    private sealed record FieldRow(TextBox Field, TextBox Value);

    private readonly IReadOnlyList<CollectionChoice> _places;
    private readonly List<FieldRow>                  _fields = [];

    public ScenarioRecordDialog() : this(ScenarioStep.OfRecord(0, 0,
        new Dictionary<string, string>()), []) { }

    /// <param name="step">Что правим; шаг с пустой коллекцией — новый.</param>
    /// <param name="places">Коллекции открытых баз.</param>
    public ScenarioRecordDialog(ScenarioStep step, IReadOnlyList<CollectionChoice> places)
    {
        InitializeComponent();

        _places = places;

        PlaceCombo.ItemsSource = _places.Select(p => p.Name + " — " + p.Database).ToList();
        PlaceCombo.SelectedIndex = Math.Max(0, _places.ToList().FindIndex(
            p => p.DbId == step.DbId && p.CollectionId == step.CollectionId));

        foreach (var (field, value) in step.Fields) _fields.Add(NewRow(field, value));

        // Пустой список показывать незачем: поле нужно хотя бы одно, и строка «нажмите плюс» —
        // работа, переложенная на человека.
        if (_fields.Count == 0) _fields.Add(NewRow("", ""));

        Rebuild();

        AddFieldBtn.Click += (_, _) => { _fields.Add(NewRow("", "")); Rebuild(); };

        if (_places.Count == 0)
            NoticeBanner.Show("Нет открытых баз данных: запись будет некуда писать.");

        SaveBtn.IsEnabled = _places.Count > 0;

        CancelBtn.Click += (_, _) => Close(null);
        SaveBtn.Click   += (_, _) => Save();
        KeyDown         += (_, e) => { if (e.Key == Key.Escape) Close(null); };
    }

    // ── Строки ────────────────────────────────────────────────────────────────

    private static FieldRow NewRow(string field, string value) => new(
        new TextBox
        {
            Text                     = field,
            Height                   = 30,
            Watermark                = "Название",
            VerticalContentAlignment = VerticalAlignment.Center,
            CornerRadius             = new CornerRadius(6),
        },
        new TextBox
        {
            Text                     = value,
            Height                   = 30,
            Watermark                = "{{Название}} — напоминание",
            VerticalContentAlignment = VerticalAlignment.Center,
            CornerRadius             = new CornerRadius(6),
        });

    /// <summary>
    /// Перерисовать список целиком, а не точечно: кнопка удаления зависит от места строки в
    /// списке, и держать их в согласии правками по одной — верный способ разойтись.
    /// </summary>
    private void Rebuild()
    {
        // У контрола может быть только один родитель, и, пока прежняя строка его держит,
        // положить его в новую нельзя: Avalonia отвечает на это исключением.
        foreach (var row in _fields)
        {
            if (row.Field.Parent is Panel a) a.Children.Remove(row.Field);
            if (row.Value.Parent is Panel b) b.Children.Remove(row.Value);
        }

        FieldsPanel.Children.Clear();

        for (var i = 0; i < _fields.Count; i++)
        {
            var index = i;
            var row   = _fields[i];

            var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,6,Auto,6,*,6,Auto") };

            Put(grid, 0, row.Field);
            Put(grid, 2, new TextBlock
            {
                Text              = "=",
                Classes           = { "desc" },
                VerticalAlignment = VerticalAlignment.Center,
            });
            Put(grid, 4, row.Value);
            Put(grid, 6, Tiny("✕", () =>
            {
                _fields.RemoveAt(index);
                if (_fields.Count == 0) _fields.Add(NewRow("", ""));
                Rebuild();
            }));

            FieldsPanel.Children.Add(grid);
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

    // ── Сохранение ────────────────────────────────────────────────────────────

    private void Save()
    {
        if (PlaceCombo.SelectedIndex < 0 || PlaceCombo.SelectedIndex >= _places.Count)
        {
            NoticeBanner.Show("Выберите коллекцию, в которую писать запись.");
            return;
        }

        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var row in _fields)
        {
            var field = (row.Field.Text ?? "").Trim();
            var value = (row.Value.Text ?? "").Trim();

            // Совсем пустая строка — недобранная заготовка, ее можно молча пропустить; поле
            // без имени при заполненном значении пропускать нельзя — потерялось бы набранное.
            if (field.Length == 0 && value.Length == 0) continue;

            if (field.Length == 0)
            {
                NoticeBanner.Show("У одного из значений не названо поле.");
                return;
            }

            if (!values.TryAdd(field, value))
            {
                NoticeBanner.Show($"Поле «{field}» указано дважды.");
                return;
            }
        }

        var place = _places[PlaceCombo.SelectedIndex];
        var step  = ScenarioStep.OfRecord(place.DbId, place.CollectionId, values);

        var problems = step.Problems();
        if (problems.Count > 0)
        {
            NoticeBanner.Show(string.Join("; ", problems) + ".");
            return;
        }

        Close(step);
    }
}
