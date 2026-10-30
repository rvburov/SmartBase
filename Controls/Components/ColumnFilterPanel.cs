using System.ComponentModel;
using Avalonia;
using Avalonia.Collections;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using SmartBase.Controls.Behaviors;
using SmartBase.Core.Models;
using Path = Avalonia.Controls.Shapes.Path;

namespace SmartBase.Controls.Components;

/// <summary>Что окно фильтра знает о колонке служебной таблицы и что ему делать с итогом.</summary>
public sealed class ColumnFilterSpec
{
    /// <summary>Ключ колонки — им таблица узнает свою колонку в итоге.</summary>
    public required string Key { get; init; }

    /// <summary>Пункты сортировки: «От А до Я» / «От Я до А», у дат — «Сначала старые» / «Сначала новые».</summary>
    public required string AscLabel  { get; init; }
    public required string DescLabel { get; init; }

    /// <summary>Действующая сортировка этой колонки: true — по возрастанию, null — сортировка не по ней.</summary>
    public bool? Sort { get; init; }

    /// <summary>
    /// Можно ли сортировать по этой колонке. Ложь — пунктов сортировки в окне нет вовсе.
    ///
    /// Полезно для колонок, по которым источник данных не поддерживает сортировку.
    /// </summary>
    public bool Sortable { get; init; } = true;

    /// <summary>Действующий фильтр колонки (сырые значения); null — фильтра нет.</summary>
    public IReadOnlySet<string>? Filter { get; init; }

    /// <summary>Сырое значение → подпись в списке: те же слова, что в ячейках таблицы.</summary>
    public required Func<string, string> Display { get; init; }

    /// <summary>Сырое значение — день «yyyy-MM-dd»: вместо плоского списка дерево «год → месяц → день».</summary>
    public bool IsDate { get; init; }

    /// <summary>
    /// Значения колонки со счетчиками — по строкам, прошедшим фильтры ОСТАЛЬНЫХ колонок (каскад,
    /// <see cref="ColumnCascade"/>). Зовется один раз при открытии: окно показывается сразу с
    /// «Загрузка…», список приходит по готовности.
    /// </summary>
    public required Func<Task<List<(string Value, int Count)>>> LoadValues { get; init; }

    /// <summary>Значения не прочитались — куда об этом сказать, решает таблица (у каждой свой журнал).</summary>
    public Action<Exception>? LoadFailed { get; init; }

    /// <summary>«Готово» или Enter в поиске: сортировка и фильтр применяются разом.</summary>
    public required Action<ColumnFilterResult> Apply { get; init; }
}

/// <summary>Итог окна фильтра колонки.</summary>
/// <param name="Key">Колонка.</param>
/// <param name="Sort">Выбранная сортировка; null — снять сортировку с этой колонки.</param>
/// <param name="Filter">Новый фильтр; null — фильтра нет (отмечено все, что есть в колонке).</param>
/// <param name="FilterKnown">Значения успели загрузиться. Нет — фильтр колонки не трогается:
/// пустой набор галочек означал бы «ничего не отмечено», хотя человек просто не дождался списка.</param>
public sealed record ColumnFilterResult(string Key, bool? Sort, HashSet<string>? Filter, bool FilterKnown)
{
    /// <summary>Записать итог в состояние таблицы: сортировка одна на таблицу, фильтр — на колонку.</summary>
    public void ApplyTo(Dictionary<string, HashSet<string>> filters, ref string? sortKey, ref bool sortAsc)
    {
        if (Sort is bool asc) { sortKey = Key; sortAsc = asc; }
        else if (sortKey == Key) sortKey = null;

        if (!FilterKnown) return;

        if (Filter is null) filters.Remove(Key);
        else                filters[Key] = Filter;
    }
}

/// <summary>
/// Окно фильтра и сортировки колонки служебных таблиц — «История изменений», «Диагностика» и
/// «Пользователи» панели администратора. Ведет себя в точности как окно таблицы конструктора
/// (<c>TableView.BuildFilterPanel</c>), и правила галочек у них общие — <see cref="FilterSelection"/>:
///  • список значений — по строкам, прошедшим фильтры других колонок (каскад); свой фильтр его не режет;
///  • ввод в поиске сам отмечает найденное, очистка поиска возвращает прежние галочки;
///  • «Готово» и Enter в поиске применяют сортировку и фильтр разом; при поиске в фильтр идут
///    отмеченные среди найденного, отмечено все, что есть в колонке, — фильтр снимается;
///  • при повторном открытии отобранные значения стоят первыми.
///
/// <b>Почему одно окно на три таблицы.</b> Раньше у каждой была своя копия, и копии разошлись с
/// конструктором: каскада не было ни в одной, найденное отмечали руками, Enter не применял. Три
/// копии одного поведения расходятся снова при первой же правке одной из них.
///
/// Отличие от конструктора одно и сознательное: даты без выбранной сортировки идут новыми сверху —
/// служебные таблицы и сами показывают свежие строки первыми.
/// </summary>
public static class ColumnFilterPanel
{
    /// <summary>Открыть окно у шеврона колонки. <paramref name="host"/> — вид таблицы: от него
    /// берутся токены оформления. Возвращает флайаут: зовущему это нужно, чтобы подсветить
    /// свой якорь на время показа (<c>Closed</c>) — сам он о закрытии иначе не узнает.</summary>
    public static Flyout Show(Control host, Control anchor, ColumnFilterSpec spec)
    {
        var flyout = PopupPlate.Filter();
        // Флайаут рендерится в отдельном попап-окне вне зума приложения — контент
        // оборачивается в зум-трансформ (плашка-презентер остается вне зума).
        flyout.Content = WindowZoom.WrapPopupContent(Build(host, spec, flyout.Hide));
        // Окно закроет панель, если потеряет фокус (см. TransientPopups).
        TransientPopups.Track(flyout);
        flyout.ShowAt(anchor);
        return flyout;
    }

    private static Control Build(Control host, ColumnFilterSpec spec, Action close)
    {
        var secondary = (IBrush)host.FindResource("BrushSecondaryText")!;
        var fontBase  = (double)host.FindResource("FontSizeBase")!;
        var radius    = (CornerRadius)host.FindResource("RadiusBase")!;
        var family    = host.TryFindResource("FontFamilyBase", out var f) ? f as FontFamily : null;
        // Ширины строк и закрепленный справа счетчик — см. FilterValueMetrics.
        var metrics   = new FilterValueMetrics();

        var panel = new StackPanel
        {
            // Общий стандарт всплывающих списков (Controls.axaml).
            Width   = (double)host.FindResource("PopupContentWidth")!,
            Margin  = new Thickness(12),
            Spacing = 2,
            Cursor  = new Cursor(StandardCursorType.Arrow),
        };

        // ── Данные (грузятся в фоне; окно открывается мгновенно) ──
        var baseCounts = new List<(string Value, int Count)>();
        var domain     = new HashSet<string>();
        var loaded     = false;

        // Отмеченные сырые значения — общий набор строк списка, дерева дат и правил галочек.
        var checkedSet = new HashSet<string>();
        var selection  = new FilterSelection(checkedSet);
        // Значения действующего фильтра — при открытии они идут первыми. Снимок на момент
        // открытия: от щелчков по галочкам строки не прыгают.
        var initialChosen = spec.Filter is null ? null : new HashSet<string>(spec.Filter);

        // ── Сортировка: копится в pendingSort, применяется вместе с фильтром ──
        bool? pendingSort = spec.Sort;

        var sortSection = new StackPanel { Spacing = 2 };
        panel.Children.Add(sortSection);

        Action rebuildListRef = () => { };

        List<(string Raw, int Count)> counts = [];
        void ReloadCounts() =>
            counts = FilterSelection.ChosenFirst(Order(baseCounts, spec, pendingSort ?? spec.Sort),
                                                 c => c.Raw, initialChosen);

        void RenderSort()
        {
            sortSection.Children.Clear();
            if (!spec.Sortable) return;
            sortSection.Children.Add(FlatItem(host, spec.AscLabel,
                () => { pendingSort = true;  RenderSort(); ReloadCounts(); rebuildListRef(); },
                Icon("IconSortAz"), active: pendingSort == true));
            sortSection.Children.Add(FlatItem(host, spec.DescLabel,
                () => { pendingSort = false; RenderSort(); ReloadCounts(); rebuildListRef(); },
                Icon("IconSortZa"), active: pendingSort == false));
            sortSection.Children.Add(FlatItem(host, "Снять сортировку",
                () => { pendingSort = null;  RenderSort(); ReloadCounts(); rebuildListRef(); },
                Icon("IconClose")));
        }
        RenderSort();

        if (spec.Sortable) panel.Children.Add(Separator(host));

        // ── Поиск по значениям ──
        var search = new TextBox
        {
            Watermark    = "Поиск значений...",
            Height       = 30,
            CornerRadius = radius,
            Background   = Brushes.White,
            Margin       = new Thickness(0, 0, 0, 4),
            VerticalContentAlignment = VerticalAlignment.Center,
        };
        panel.Children.Add(search);

        var syncingSelectAll = false;

        // Подпись меняется по режиму: при поиске — «(Выделить все найденное)».
        var selectAll = new CheckBox
        {
            Content   = "(Выделить все)",
            FontSize  = fontBase,
            IsChecked = spec.Filter is null,
            Cursor    = new Cursor(StandardCursorType.Hand),
        };

        // Виртуализирующий ListBox — все уникальные значения без лимита и зависаний.
        var shown   = new AvaloniaList<ValueVm>();
        var vmCache = new Dictionary<string, ValueVm>();
        ValueVm Vm(string raw, int count) =>
            vmCache.TryGetValue(raw, out var v) ? v
                : vmCache[raw] = new ValueVm(raw, spec.Display(raw), count, checkedSet, () => SyncSelectAll());

        var valuesList = new ListBox
        {
            // Высоты нет: список занимает остаток карточки фиксированной высоты и сам
            // ужимается, когда снизу появляется горизонтальная полоса.
            Background      = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Padding         = new Thickness(0),
            ItemsSource     = shown,
            ItemTemplate    = ValueTemplate(fontBase, secondary, metrics),
        };
        valuesList.Styles.Add(new Style(x => x.OfType<ListBoxItem>())
        {
            Setters = { new Setter(ListBoxItem.PaddingProperty, new Thickness(0, 2)),
                        new Setter(ListBoxItem.MinHeightProperty, 0.0) },
        });

        // Строки без подсветки наведения/выбора — взаимодействуем только с чекбоксом.
        void FlatRow(params string[] pseudo) =>
            valuesList.Styles.Add(new Style(x =>
            {
                Selector s = x.OfType<ListBoxItem>();
                foreach (var p in pseudo) s = s.Class(p);
                return s.Template().OfType<ContentPresenter>().Name("PART_ContentPresenter");
            })
            {
                Setters = { new Setter(ContentPresenter.BackgroundProperty, Brushes.Transparent) },
            });
        FlatRow();
        FlatRow(":pointerover");
        FlatRow(":selected");
        FlatRow(":selected", ":pointerover");

        // «Выделить все» — закрепленная шапка списка; полоса прокрутки стоит на ее уровне.
        // gap=2: вместе с верхним отступом первой строки дает под шапкой тот же просвет,
        // что между значениями.
        var valuesBox = ScrollBarLink.HeaderedList(selectAll, valuesList, gap: 2, scroller: metrics.Track);

        var loadingText = new TextBlock
        {
            Text                = "Загрузка…",
            Foreground          = secondary,
            FontSize            = fontBase,
            TextWrapping        = TextWrapping.Wrap,
            TextAlignment       = TextAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment   = VerticalAlignment.Center,
        };
        var valuesHost = new Border
        {
            Background   = Brushes.White,
            BorderBrush  = (IBrush)host.FindResource("BrushSeparator")!,
            CornerRadius = radius,
            // Padding НУЛЕВОЙ: полосы прокрутки прилегают к краям карточки, воздух вокруг
            // содержимого задает ScrollBarLink.HeaderedList. ClipToBounds срезает углы полос.
            Padding      = new Thickness(0),
            ClipToBounds = true,
            // Фикс высота (стандарт) — список не «прыгает» при замене «Загрузка…».
            Height       = (double)host.FindResource("PopupListHeight")!,
            Child        = loadingText,
        };
        valuesHost.Bind(Border.BorderThicknessProperty, valuesHost.GetResourceObservable("BorderLine"));
        panel.Children.Add(valuesHost);

        // Колонка-дата — дерево «Год → Месяц → День» (DateFilterTreeBox), тот же чеклист, что
        // у свойств-дат конструктора. Появляется вместе с данными.
        DateFilterTreeBox? tree = null;
        var treeNewestFirst = false;

        void MountTree(bool newestFirst)
        {
            treeNewestFirst = newestFirst;
            tree = DateFilterTreeBox.ForIsoDates(counts, checkedSet, fontBase, secondary, newestFirst);
            tree.SelectionChanged += (_, _) => SyncSelectAll();
            // «(Выделить все)» — общий чекбокс обоих режимов, а шапку HeaderedList он получает во
            // владение: снимаем его с прежнего родителя, иначе «already has a visual parent».
            (selectAll.Parent as Panel)?.Children.Remove(selectAll);
            valuesHost.Child = EmptyListHint.Wrap(tree.List, search, secondary, fontBase,
                                                  content: ScrollBarLink.HeaderedList(selectAll, tree.List, gap: 2));
            tree.Search(search.Text);
        }

        List<(string Raw, int Count)> Matched()
        {
            var q = search.Text?.Trim() ?? "";
            return q.Length == 0
                ? counts
                : counts.Where(c => spec.Display(c.Raw).Contains(q, StringComparison.OrdinalIgnoreCase)).ToList();
        }

        // Показанные сейчас сырые значения — общий язык обоих режимов: по ним считаются
        // «(Выделить все)», автоотметка найденного и режим «Заменить».
        IEnumerable<string> MatchedRaws() =>
            tree is not null ? tree.VisibleRaws : shown.Select(v => v.Raw);

        void RebuildList()
        {
            if (spec.IsDate)
            {
                if (tree is null) return;   // значения еще грузятся
                // Сменили сортировку — дерево пересобирается в новом порядке.
                var newestFirst = pendingSort != true;
                if (treeNewestFirst != newestFirst) MountTree(newestFirst);
                else tree.Search(search.Text);
                return;
            }

            shown.Clear();
            shown.AddRange(Matched().Select(c => Vm(c.Raw, c.Count)));
            // Ширина строки фиксируется по текущему набору — иначе экстент горизонтальной
            // прокрутки считался бы только по видимым строкам и полоса прыгала бы.
            metrics.Measure(shown.Select(v => v.Display), shown.Select(v => v.CountText), fontBase, family);
        }
        rebuildListRef = RebuildList;

        void SyncSelectAll()
        {
            syncingSelectAll    = true;
            selectAll.Content   = selection.Searching ? "(Выделить все найденное)" : "(Выделить все)";
            selectAll.IsChecked = tree is not null
                ? tree.AllVisibleChecked
                : shown.All(v => checkedSet.Contains(v.Raw));
            syncingSelectAll    = false;
        }

        // Галочки поменяли не через строки (поиск отметил найденное, очистка вернула прежние,
        // «(Выделить все)») — строкам и дереву надо перечитать их: контейнеры списка
        // переиспользуются, и без события привязка не узнает о правке общего набора.
        void RefreshChecks()
        {
            if (tree is not null) tree.RefreshChecked();
            else foreach (var vm in shown) vm.Refresh();
        }

        void OnSearchChanged()
        {
            RebuildList();
            selection.OnSearch(search.Text, MatchedRaws());
            RefreshChecks();
            SyncSelectAll();
        }

        search.TextChanged += (_, _) => OnSearchChanged();
        selectAll.IsCheckedChanged += (_, _) =>
        {
            if (syncingSelectAll) return;
            var on = selectAll.IsChecked == true;
            if (tree is not null) { tree.SetAllVisible(on); return; }

            foreach (var (raw, _) in Matched())
            {
                if (on) checkedSet.Add(raw);
                else    checkedSet.Remove(raw);
            }
            RebuildList();
            RefreshChecks();
        };

        panel.Children.Add(Separator(host));

        // ── «Готово» и Enter в поиске: сортировка и фильтр разом ──
        var okBtn = new Button
        {
            Content             = "Готово",
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin              = new Thickness(0, 4, 0, 0),
        };
        okBtn.Classes.Add("dialog");

        void Apply()
        {
            var filter = loaded ? selection.Effective(MatchedRaws(), domain) : null;
            close();
            spec.Apply(new ColumnFilterResult(spec.Key, pendingSort, filter, FilterKnown: loaded));
        }
        okBtn.Click += (_, _) => Apply();
        search.KeyDown += (_, e) => { if (e.Key == Key.Enter) { Apply(); e.Handled = true; } };
        panel.Children.Add(okBtn);

        // ── Фоновая загрузка значений ──
        Task<List<(string Value, int Count)>> load;
        try { load = spec.LoadValues(); }
        catch (Exception error) { load = Task.FromException<List<(string Value, int Count)>>(error); }

        load.ContinueWith(t => Dispatcher.UIThread.Post(() =>
        {
            if (t.IsFaulted || t.IsCanceled)
            {
                // Окно без этой надписи так и висело бы с «Загрузка…».
                loadingText.Text = "Не удалось прочитать значения";
                if (t.Exception?.GetBaseException() is { } error) spec.LoadFailed?.Invoke(error);
                return;
            }

            baseCounts.Clear();
            baseCounts.AddRange(t.Result);
            domain.Clear();
            foreach (var c in t.Result) domain.Add(c.Value);

            // Галочки — из действующего фильтра, иначе все. Значения фильтра, которых сейчас нет
            // в списке (их строки спрятал фильтр другой колонки), остаются отмеченными: снимут
            // чужой фильтр — они снова в отборе.
            checkedSet.Clear();
            if (spec.Filter is not null) checkedSet.UnionWith(spec.Filter);
            else                         checkedSet.UnionWith(domain);

            ReloadCounts();

            if (spec.IsDate) MountTree(newestFirst: pendingSort != true);
            // «Загрузка…» → список; пустой список показывает «Список пуст» / «Ничего не найдено».
            else valuesHost.Child = EmptyListHint.Wrap(valuesList, search, secondary, fontBase, content: valuesBox);

            // Галочки заполнены заново — снимок «до поиска» (если печатали до загрузки) устарел.
            selection.Reset();
            OnSearchChanged();
            loaded = true;
        }));

        return panel;
    }

    // Порядок значений: выбранная сортировка колонки; без нее даты новыми сверху (сырое ISO
    // сортируется хронологически), остальное — по подписи. «(Пусто)» всегда в конце.
    private static List<(string Raw, int Count)> Order(List<(string Value, int Count)> source,
                                                       ColumnFilterSpec spec, bool? asc)
    {
        var filled = source.Where(c => c.Value.Length > 0).Select(c => (Raw: c.Value, c.Count));
        var empty  = source.Where(c => c.Value.Length == 0).Select(c => (Raw: c.Value, c.Count));

        filled = spec.IsDate
            ? asc == true
                ? filled.OrderBy(c => c.Raw, StringComparer.Ordinal)
                : filled.OrderByDescending(c => c.Raw, StringComparer.Ordinal)
            : asc == false
                ? filled.OrderByDescending(c => spec.Display(c.Raw), StringComparer.CurrentCultureIgnoreCase)
                : filled.OrderBy(c => spec.Display(c.Raw), StringComparer.CurrentCultureIgnoreCase);

        return [.. filled, .. empty];
    }

    // ── Оформление ────────────────────────────────────────────────────────────

    private static Geometry Icon(string key) =>
        Application.Current!.TryFindResource(key, out var res) && res is Geometry g
            ? g
            : throw new KeyNotFoundException($"Icon resource '{key}' not found in Icons.axaml");

    private static double Size(string key, double fallback) =>
        Application.Current!.TryFindResource(key, out var res) && res is double d ? d : fallback;

    // Иконка в стандарте сайдбара: Viewbox «Средний» поверх сетки 24×24.
    private static Viewbox StrokeIcon(Geometry data, IBrush stroke) => new()
    {
        Width             = Size("IconSizeMedium", 18),
        Height            = Size("IconSizeMedium", 18),
        VerticalAlignment = VerticalAlignment.Center,
        Child             = new Canvas
        {
            Width    = 24,
            Height   = 24,
            Children =
            {
                new Path
                {
                    Data            = data,
                    Stroke          = stroke,
                    StrokeThickness = Size("IconStrokeMedium", 1.2),
                    StrokeLineCap   = PenLineCap.Round,
                    StrokeJoin      = PenLineJoin.Round,
                },
            },
        },
    };

    // Пункт сортировки: иконка, подпись и галочка у выбранного.
    private static Button FlatItem(Control host, string text, Action onClick, Geometry icon, bool active = false)
    {
        var fg = (IBrush)host.FindResource(active ? "BrushAccent" : "BrushPrimaryText")!;

        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto") };

        var iconBox = StrokeIcon(icon, fg);
        Grid.SetColumn(iconBox, 0);
        row.Children.Add(iconBox);

        var label = new TextBlock
        {
            Text                = text,
            Foreground          = fg,
            Margin              = new Thickness(8, 0, 0, 0),
            VerticalAlignment   = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        Grid.SetColumn(label, 1);
        row.Children.Add(label);

        if (active)
        {
            var check = StrokeIcon(Icon("IconCheck"), fg);
            Grid.SetColumn(check, 2);
            row.Children.Add(check);
        }

        var btn = new Button
        {
            Content                    = row,
            Background                 = Brushes.Transparent,
            BorderThickness            = new Thickness(0),
            Padding                    = new Thickness(8, 6),
            FontSize                   = (double)host.FindResource("FontSizeBase")!,
            Foreground                 = fg,
            CornerRadius               = (CornerRadius)host.FindResource("RadiusBase")!,
            HorizontalAlignment        = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
        };
        btn.Click += (_, _) => onClick();
        return btn;
    }

    // Разделитель в попапе (контент обернут в зум-трансформ — токен обычный).
    private static Border Separator(Control host)
    {
        var sep = new Border
        {
            Background = (IBrush)host.FindResource("BrushSeparator")!,
            Margin     = new Thickness(0, 6),
        };
        sep.Bind(Layoutable.HeightProperty, sep.GetResourceObservable("SeparatorThickness"));
        sep.Bind(Layoutable.UseLayoutRoundingProperty, sep.GetResourceObservable("LineUseLayoutRounding"));
        RenderOptions.SetEdgeMode(sep, EdgeMode.Aliased);
        return sep;
    }

    // Строка списка значений (чекбокс + количество) для виртуализированного ListBox:
    // привязки следуют за DataContext при переиспользовании контейнеров.
    private static IDataTemplate ValueTemplate(double fontBase, IBrush secondary, FilterValueMetrics metrics) =>
        new FuncDataTemplate<ValueVm>((_, _) =>
        {
            var name = new TextBlock { TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
            name.Bind(TextBlock.TextProperty, new Binding(nameof(ValueVm.Display)));
            // Ширина ВСЕХ строк одинаковая (по самому широкому значению) — иначе экстент
            // горизонтальной прокрутки считался бы по видимым строкам и полоса дергалась.
            name.Bind(Layoutable.MinWidthProperty,
                      new Binding(nameof(FilterValueMetrics.TextMinWidth)) { Source = metrics });

            var cb = new CheckBox
            {
                Content                    = name,
                FontSize                   = fontBase,
                HorizontalAlignment        = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Left,
                Margin                     = new Thickness(0),
                Cursor                     = new Cursor(StandardCursorType.Hand),
            };
            cb.Bind(ToggleButton.IsCheckedProperty, new Binding(nameof(ValueVm.Checked)) { Mode = BindingMode.TwoWay });

            var cnt = new TextBlock { Foreground = secondary, VerticalAlignment = VerticalAlignment.Center };
            cnt.Bind(TextBlock.TextProperty, new Binding(nameof(ValueVm.CountText)));

            // Счетчик ЗАКРЕПЛЕН у правого края видимой области: сдвиг компенсирует горизонтальную
            // прокрутку, фон непрозрачный — длинный текст уезжает под него.
            var pin = new TranslateTransform();
            pin.Bind(TranslateTransform.XProperty, new Binding(nameof(FilterValueMetrics.Pin)) { Source = metrics });
            var cntHost = new Border
            {
                Background          = Brushes.White,
                Padding             = new Thickness(FilterValueMetrics.Gap, 0, 0, 0),
                HorizontalAlignment = HorizontalAlignment.Right,
                Child               = cnt,
                RenderTransform     = pin,
            };

            // Прозрачное МЕСТО под счетчик одинаковой ширины во всех строках.
            var cntSlot = new Border { Child = cntHost };
            cntSlot.Bind(Layoutable.MinWidthProperty,
                         new Binding(nameof(FilterValueMetrics.CountMinWidth)) { Source = metrics });

            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
            Grid.SetColumn(cb, 0);      row.Children.Add(cb);
            Grid.SetColumn(cntSlot, 1); row.Children.Add(cntSlot);
            return row;
        });

    /// <summary>Строка списка значений: галочка читает и пишет общий набор отмеченных.</summary>
    private sealed class ValueVm : INotifyPropertyChanged
    {
        private readonly HashSet<string> _checked;
        private readonly Action _onChanged;

        public ValueVm(string raw, string display, int count, HashSet<string> checkedSet, Action onChanged)
        {
            Raw       = raw;
            Display   = display;
            CountText = count.ToString("N0");
            _checked  = checkedSet;
            _onChanged = onChanged;
        }

        public string Raw       { get; }
        public string Display   { get; }
        public string CountText { get; }

        public bool Checked
        {
            get => _checked.Contains(Raw);
            set
            {
                if (value) _checked.Add(Raw);
                else       _checked.Remove(Raw);
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Checked)));
                _onChanged();
            }
        }

        /// <summary>Перечитать галочку из общего набора — ее поменяли не через строку.</summary>
        public void Refresh() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Checked)));

        public event PropertyChangedEventHandler? PropertyChanged;
    }
}
