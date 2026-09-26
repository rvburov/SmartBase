using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using SmartBase.Controls.Behaviors;

namespace SmartBase.Controls.Widgets;

/// <summary>Одно значение в списке отбора: что отбираем, как это показать и сколько таких строк.</summary>
/// <param name="Value">Значение, как оно лежит в журнале, — оно и уходит в условие отбора.</param>
/// <param name="Text">Как назвать его человеку: «Запрос» вместо «1», «удача» вместо «true».</param>
/// <param name="Count">Сколько строк с таким значением.</param>
public sealed record ColumnFilterValue(string Value, string Text, int Count);

/// <summary>
/// Отбор по колонке в шапке таблицы — «как в табличном редакторе» (docs/ROADMAP-API.md, шаг 14).
///
/// Виджет общий и нарочно маленький: заголовок колонки, воронка, а во всплывающем списке —
/// поиск, «выделить все» и значения с числом строк. Ничего о своих данных он не знает; какие
/// значения бывают и что делать с выбранным, ему говорит таблица.
///
/// <b>Почему это не тот же фильтр, что в «Диагностике».</b> Тот умеет еще и сортировку колонки,
/// живет в шапке рисованной сетки и читает значения из <c>logs.db</c> страницами вместе с ними; его
/// устройство следует из этого, а не из общей идеи отбора. Журналам обмена нужна ровно половина
/// — отбор по значению, — и переписывать ради нее рабочий экран диагностики значило бы взять
/// риск без выгоды для человека. Поэтому здесь свой, короткий виджет, а не вынутый оттуда.
///
/// <b>Отбор уходит в условие таблицы, а не отсеивает показанное.</b> Журнал копится годами и
/// читается страницами: отсей программа уже прочитанную пачку — единственная ночная неудача не
/// нашлась бы, потому что удачные строки вытеснили бы ее из пачки.
/// </summary>
public static class ColumnFilter
{
    /// <summary>Сколько значений показываем в списке; больше — предложение уточнить поиском.</summary>
    public const int MaxValues = 200;

    /// <summary>
    /// Заголовок колонки с воронкой. <paramref name="chosen"/> — выбранные значения: пустое
    /// множество означает «все», и воронка тогда не горит.
    /// </summary>
    /// <param name="title">Имя колонки.</param>
    /// <param name="values">Какие значения бывают — спрашивается при открытии списка, а не
    /// заранее: считать их на каждую перерисовку шапки означало бы проход по журналу на каждый
    /// щелчок.</param>
    /// <param name="chosen">Что отобрано сейчас; виджет меняет это множество на месте.</param>
    /// <param name="apply">Что сделать после «Готово» — перестроить таблицу.</param>
    public static Control Head(string                                title,
                               Func<IReadOnlyList<ColumnFilterValue>> values,
                               ISet<string>                          chosen,
                               Action                                apply)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,4,Auto") };

        var text = new TextBlock
        {
            Text              = title,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming      = TextTrimming.CharacterEllipsis,
            FontWeight        = FontWeight.SemiBold,
            Foreground        = Brush("BrushSecondaryText"),
        };

        var button = new Button
        {
            Width               = 22,
            Height              = 22,
            Padding             = new Thickness(0),
            Background          = Brushes.Transparent,
            BorderThickness     = new Thickness(0),
            VerticalAlignment   = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Right,
            Cursor              = new Cursor(StandardCursorType.Hand),
            Content             = Funnel(chosen.Count > 0),
            // Отбор по колонке ищут именно на воронке, и подсказка называет его словами: значок
            // без подписи читается как «что-то настроить».
            [ToolTip.TipProperty] = chosen.Count > 0
                ? $"Отбор по колонке «{title}»: выбрано значений {chosen.Count}"
                : $"Отбор по колонке «{title}»",
        };

        button.Click += (_, _) => Show(button, title, values, chosen, apply);

        Grid.SetColumn(text, 0);
        Grid.SetColumn(button, 2);
        grid.Children.Add(text);
        grid.Children.Add(button);

        return grid;
    }

    // ── Всплывающий список ────────────────────────────────────────────────────

    private static void Show(Control                               anchor,
                             string                                title,
                             Func<IReadOnlyList<ColumnFilterValue>> values,
                             ISet<string>                          chosen,
                             Action                                apply)
    {
        var flyout = PopupPlate.Filter();

        flyout.Content = WindowZoom.WrapPopupContent(
            Panel(title, values(), chosen, () => { flyout.Hide(); apply(); }, flyout.Hide));

        // Окно закроет панель, если потеряет фокус (см. TransientPopups).
        TransientPopups.Track(flyout);
        flyout.ShowAt(anchor);
    }

    private static Control Panel(string                            title,
                                 IReadOnlyList<ColumnFilterValue>  values,
                                 ISet<string>                      chosen,
                                 Action                            done,
                                 Action                            cancel)
    {
        var width = Resource("PopupContentWidth", 240d);

        var panel = new StackPanel
        {
            Width   = width,
            Margin  = new Thickness(12),
            Spacing = 6,
            Cursor  = new Cursor(StandardCursorType.Arrow),
        };

        // Набранное живет здесь и уезжает в отбор только по «Готово»: список перестраивается на
        // каждую букву поиска, и применяй он выбор сразу — таблица моргала бы под руками.
        var picked = new HashSet<string>(chosen, StringComparer.Ordinal);

        var search = new TextBox
        {
            Watermark                = "Поиск значений",
            Height                   = 30,
            VerticalContentAlignment = VerticalAlignment.Center,
            CornerRadius             = new CornerRadius(6),
        };

        var list = new StackPanel { Spacing = 2 };

        var all = new CheckBox
        {
            Content   = "(Выделить все)",
            IsChecked = picked.Count == 0,
            Cursor    = new Cursor(StandardCursorType.Hand),
        };

        var quiet = false;   // перестройка списка сама щелкает галочками

        void Rebuild()
        {
            quiet = true;
            list.Children.Clear();

            var needle = (search.Text ?? "").Trim();

            var shown = needle.Length == 0
                ? values
                : [.. values.Where(v => v.Text.Contains(needle, StringComparison.CurrentCultureIgnoreCase))];

            if (shown.Count == 0)
            {
                list.Children.Add(new TextBlock
                {
                    Text         = "Ничего не нашлось",
                    TextWrapping = TextWrapping.Wrap,
                    Foreground   = Brush("BrushSecondaryText"),
                });

                quiet = false;
                return;
            }

            foreach (var value in shown.Take(MaxValues))
            {
                var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,6,Auto") };

                var box = new CheckBox
                {
                    Content   = value.Text,
                    IsChecked = picked.Count == 0 || picked.Contains(value.Value),
                    Cursor    = new Cursor(StandardCursorType.Hand),
                };

                box.IsCheckedChanged += (_, _) =>
                {
                    if (quiet) return;

                    // Пустое множество означает «все», поэтому первая же снятая галочка
                    // разворачивает его в поименный список: иначе «все, кроме одного» было бы
                    // нечем записать.
                    if (picked.Count == 0)
                        foreach (var known in values) picked.Add(known.Value);

                    if (box.IsChecked == true) picked.Add(value.Value);
                    else                       picked.Remove(value.Value);

                    // Отмечено все — это снова «все»: так отбор не остается в базе списком из
                    // сорока значений, который завтра не совпадет с журналом.
                    if (picked.Count == values.Count) picked.Clear();

                    quiet = true;
                    all.IsChecked = picked.Count == 0;
                    quiet = false;
                };

                var count = new TextBlock
                {
                    Text              = value.Count.ToString("N0", System.Globalization.CultureInfo.CurrentCulture),
                    VerticalAlignment = VerticalAlignment.Center,
                    Foreground        = Brush("BrushSecondaryText"),
                };

                Grid.SetColumn(box, 0);
                Grid.SetColumn(count, 2);
                row.Children.Add(box);
                row.Children.Add(count);

                list.Children.Add(row);
            }

            if (shown.Count > MaxValues)
                list.Children.Add(new TextBlock
                {
                    Text         = $"Показаны первые {MaxValues} значений из {shown.Count} — "
                                 + "уточните поиском.",
                    TextWrapping = TextWrapping.Wrap,
                    Foreground   = Brush("BrushSecondaryText"),
                });

            quiet = false;
        }

        all.IsCheckedChanged += (_, _) =>
        {
            if (quiet) return;

            if (all.IsChecked == true) picked.Clear();
            else                       foreach (var value in values) picked.Remove(value.Value);

            Rebuild();
        };

        search.TextChanged += (_, _) => Rebuild();

        Rebuild();

        panel.Children.Add(new TextBlock
        {
            Text         = title,
            FontWeight   = FontWeight.SemiBold,
            TextWrapping = TextWrapping.Wrap,
        });

        panel.Children.Add(search);
        panel.Children.Add(all);

        panel.Children.Add(new ScrollViewer
        {
            MaxHeight                     = 260,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility   = ScrollBarVisibility.Auto,
            Content                       = list,
        });

        var buttons = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto") };

        var reset = new Button
        {
            Content = "Сбросить",
            Height  = 28,
            Padding = new Thickness(10, 0),
            Cursor  = new Cursor(StandardCursorType.Hand),
        };

        reset.Click += (_, _) =>
        {
            picked.Clear();
            chosen.Clear();
            done();
        };

        var ok = new Button
        {
            Content = "Готово",
            Height  = 28,
            Padding = new Thickness(10, 0),
            Cursor  = new Cursor(StandardCursorType.Hand),
        };

        ok.Click += (_, _) =>
        {
            chosen.Clear();
            foreach (var value in picked) chosen.Add(value);
            done();
        };

        Grid.SetColumn(reset, 0);
        Grid.SetColumn(ok, 2);
        buttons.Children.Add(reset);
        buttons.Children.Add(ok);

        panel.Children.Add(buttons);

        // Escape закрывает список, ничего не применяя: человек передумал.
        panel.KeyDown += (_, e) => { if (e.Key == Key.Escape) cancel(); };

        return panel;
    }

    // ── Мелочи ────────────────────────────────────────────────────────────────

    /// <summary>Воронка: горит цветом, когда отбор по колонке стоит.</summary>
    private static Control Funnel(bool active) => new Viewbox
    {
        Width  = 13,
        Height = 13,
        Child  = new Avalonia.Controls.Shapes.Path
        {
            Data            = Geometry("IconFilter"),
            Fill            = active ? Brush("BrushAccent") : Brush("BrushSecondaryText"),
            Stretch         = Stretch.Uniform,
        },
    };

    private static Geometry? Geometry(string key) =>
        Application.Current!.TryFindResource(key, out var found) && found is Geometry geometry
            ? geometry
            : null;

    private static IBrush Brush(string key) =>
        Application.Current!.TryFindResource(key, out var found) && found is IBrush brush
            ? brush
            : Brushes.Gray;

    private static T Resource<T>(string key, T fallback) =>
        Application.Current!.TryFindResource(key, out var found) && found is T value ? value : fallback;
}
