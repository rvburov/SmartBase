using System.ComponentModel;
using Avalonia;
using Avalonia.Collections;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Shapes;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using SmartBase.Core.Models;

namespace SmartBase.Controls.Components;

/// <summary>
/// Чеклист дат в окне фильтра колонки — дерево «Год → Месяц → День» вместо плоского
/// списка значений. Общий для всех фильтров, где колонка хранит дату: свойства-даты
/// таблицы конструктора, «Время» в «Диагностике», «Дата и время» в «Истории изменений».
///
/// Что делает: держит вид-модели узлов, плоский список ВИДИМЫХ строк для
/// виртуализирующего ListBox (свернутые ветки в него не попадают), каскад галочек
/// (отметил год — отметились все его дни; снял один день — галочка ветки пропала, как у
/// «(Выделить все)»). Отмечается день; сам фильтр работает СЫРЫМИ значениями колонки
/// (<see cref="DateFilterNode.Raws"/>) — в SQL уходит тот же <c>IN (...)</c>.
///
/// Дерево и его разбор — <see cref="DateFilterTree"/> (чистая логика, покрыта тестами);
/// здесь только показ и взаимодействие.
/// </summary>
public sealed class DateFilterTreeBox
{
    /// <summary>Шаг отступа уровня и ширина слота стрелки — одно значение, как
    /// <c>IndentStep</c> в дереве сайдбара: только тогда лесенка чекбоксов ровная.</summary>
    private const double IndentStep = 22;

    private readonly HashSet<string> _checked;
    private readonly IReadOnlyList<DateFilterNode> _roots;
    private readonly AvaloniaList<DateNodeVm> _shown = [];
    private List<DateNodeVm> _visibleRoots = [];

    /// <summary>Список для карточки значений: ItemsSource уже привязан к видимым строкам.</summary>
    public ListBox List { get; }

    /// <summary>Галочки изменились — хозяин обновляет «(Выделить все)».</summary>
    public event EventHandler? SelectionChanged;

    public DateFilterTreeBox(IReadOnlyList<DateFilterNode> roots, HashSet<string> checkedRaws,
                             double fontBase, IBrush secondary)
    {
        _roots   = roots;
        _checked = checkedRaws;

        List = new ListBox
        {
            Background      = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Padding         = new Thickness(0),
            ItemsSource     = _shown,
            ItemTemplate    = BuildTemplate(fontBase, secondary),
        };
        List.Styles.Add(new Style(x => x.OfType<ListBoxItem>())
        {
            // Тот же воздух между строками, что у плоского списка значений.
            Setters = { new Setter(ListBoxItem.PaddingProperty, new Thickness(0, 2)),
                        new Setter(ListBoxItem.MinHeightProperty, 0.0) },
        });
        // Строки без подсветки наведения/выбора: взаимодействуем с чекбоксом и шевроном.
        foreach (var pseudo in new[] { Array.Empty<string>(), [":pointerover"], [":selected"],
                                       new[] { ":selected", ":pointerover" } })
            List.Styles.Add(new Style(x =>
            {
                Selector s = x.OfType<ListBoxItem>();
                foreach (var p in pseudo) s = s.Class(p);
                return s.Template().OfType<ContentPresenter>().Name("PART_ContentPresenter");
            })
            {
                Setters = { new Setter(ContentPresenter.BackgroundProperty, Brushes.Transparent) },
            });

        Search(null);
    }

    /// <summary>
    /// Дерево по значениям таблицы конструктора: сырое значение — хранимый вид
    /// <see cref="DateValue"/> («2026-08-02T10:00/2026-08-02T11:00»), днем считается начало.
    /// </summary>
    public static DateFilterTreeBox ForStorageDates(
        IEnumerable<(string Raw, int Count)> values, HashSet<string> checkedRaws,
        double fontBase, IBrush secondary, bool newestFirst) =>
        new(DateFilterTree.Build(values,
                raw => DateValue.TryParseStorage(raw, out var v) ? v.Start : null, newestFirst),
            checkedRaws, fontBase, secondary);

    /// <summary>
    /// Дерево по значениям журналов («Диагностика», «История изменений»): сырое значение —
    /// сама дата «2026-08-23» (время в фильтр не попадает, его отрезают при сборе значений).
    /// </summary>
    public static DateFilterTreeBox ForIsoDates(
        IEnumerable<(string Raw, int Count)> values, HashSet<string> checkedRaws,
        double fontBase, IBrush secondary, bool newestFirst) =>
        new(DateFilterTree.Build(values,
                raw => DateTime.TryParse(raw, System.Globalization.CultureInfo.InvariantCulture,
                                         System.Globalization.DateTimeStyles.None, out var d)
                       ? d : null, newestFirst),
            checkedRaws, fontBase, secondary);

    /// <summary>Сырые значения, показанные сейчас (с учетом поиска) — по ним хозяин строит
    /// «(Выделить все)» и Excel-режим «Заменить», где фильтр = отмеченные СРЕДИ найденного.</summary>
    public IReadOnlyList<string> VisibleRaws { get; private set; } = [];

    /// <summary>Все ли показанные значения отмечены (состояние «(Выделить все)»).</summary>
    public bool AllVisibleChecked =>
        VisibleRaws.Count > 0 && VisibleRaws.All(_checked.Contains);

    /// <summary>Отметить/снять все показанные значения — «(Выделить все)».</summary>
    public void SetAllVisible(bool on)
    {
        foreach (var raw in VisibleRaws)
            if (on) _checked.Add(raw); else _checked.Remove(raw);
        RefreshStates();
    }

    /// <summary>Пересобрать дерево под строку поиска (пустая — все ветки).</summary>
    public void Search(string? query)
    {
        var matched = DateFilterTree.Search(_roots, query);
        VisibleRaws = DateFilterTree.Flatten(matched)
            .Where(n => n.Kind is DateNodeKind.Day or DateNodeKind.Undated)
            .SelectMany(n => n.Raws)
            .ToList();

        // При поиске ветки раскрыты (иначе найденный день не виден под свернутым годом);
        // без поиска раскрываем, только пока ветка одна — длинный список остается обозримым.
        var expandAll = !string.IsNullOrWhiteSpace(query);
        _visibleRoots = matched.Select(n => Create(n, 0, expandAll)).ToList();
        Rebuild();
    }

    private DateNodeVm Create(DateFilterNode node, int level, bool expandAll)
    {
        var vm = new DateNodeVm(node, level, _checked, OnToggled, Rebuild);
        vm.SetChildren(node.Children.Select(c => Create(c, level + 1, expandAll)).ToList());
        vm.IsExpanded = expandAll || (node.Children.Count == 1 && level == 0);
        return vm;
    }

    // Плоский список видимых строк: свернутые ветки в ListBox не попадают вовсе,
    // поэтому виртуализация работает как на обычном списке.
    private void Rebuild()
    {
        var flat = new List<DateNodeVm>();
        void Walk(IEnumerable<DateNodeVm> nodes)
        {
            foreach (var vm in nodes)
            {
                flat.Add(vm);
                if (vm.IsExpanded) Walk(vm.Children);
            }
        }
        Walk(_visibleRoots);

        _shown.Clear();
        _shown.AddRange(flat);
    }

    // Галочку переключили: состояние родителей и потомков пересчитывается из общего
    // набора отмеченных — хранить его в самих строках нельзя, при виртуализации
    // контейнеры переиспользуются. Событие наверх поднимает сам RefreshStates.
    private void OnToggled() => RefreshStates();

    private void RefreshStates()
    {
        foreach (var vm in AllVms(_visibleRoots)) vm.RaiseCheckedChanged();
        SelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    private static IEnumerable<DateNodeVm> AllVms(IEnumerable<DateNodeVm> nodes)
    {
        foreach (var vm in nodes)
        {
            yield return vm;
            foreach (var child in AllVms(vm.Children)) yield return child;
        }
    }

    // Строка: [шеврон или отступ] [чекбокс с подписью] [счетчик справа].
    private static Avalonia.Controls.Templates.IDataTemplate BuildTemplate(double fontBase, IBrush secondary) =>
        new Avalonia.Controls.Templates.FuncDataTemplate<DateNodeVm>((_, _) =>
        {
            // Шеврон-раскрытие: только у года и месяца; у дня на его месте пустое поле
            // такой же ширины, поэтому подписи дней стоят в одну колонку.
            //
            // Свернутое/раскрытое состояние меняет САМУ иконку («›» ↔ «⌄»), а не угол
            // поворота: RotateTransform — не контрол, DataContext у него нет, и привязка
            // угла падает «Cannot find a DataContext to bind to» прямо в проходе разметки.
            // Размер стрелки — как в дереве сайдбара: IconSizeSmall с IconStrokeSmall.
            var arrow = new Avalonia.Controls.Shapes.Path
            {
                Stroke            = secondary,
                StrokeThickness   = Res("IconStrokeSmall", 1.8),
                StrokeLineCap     = PenLineCap.Round,
                StrokeJoin        = PenLineJoin.Round,
                VerticalAlignment = VerticalAlignment.Center,
            };
            arrow.Bind(Avalonia.Controls.Shapes.Path.DataProperty,
                       new Binding(nameof(DateNodeVm.ArrowIcon)));

            var arrowSize = Res("IconSizeSmall", 16);
            var arrowBox = new Viewbox
            {
                Width = arrowSize, Height = arrowSize,
                Child = new Canvas { Width = 24, Height = 24, Children = { arrow } },
                VerticalAlignment   = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center,
            };

            var chevron = new Border
            {
                Background        = Brushes.Transparent,   // иначе клик мимо линий не ловится
                VerticalAlignment = VerticalAlignment.Stretch,
                Child             = arrowBox,
                Cursor            = new Cursor(StandardCursorType.Hand),
            };
            chevron.Bind(Visual.IsVisibleProperty, new Binding(nameof(DateNodeVm.HasChildren)));
            chevron.PointerPressed += (s, e) =>
            {
                if (s is Border { DataContext: DateNodeVm vm }) vm.ToggleExpanded();
                e.Handled = true;
            };

            // Слот стрелки шириной ровно в шаг отступа (как ChevronSlot в сайдбаре): у дня
            // стрелки нет, но слот остается — иначе лесенка чекбоксов ломается на листьях.
            // Шаг отступа = ширине слота, поэтому чекбокс каждого уровня отстоит от
            // предыдущего на одну и ту же величину, а стрелка стоит вплотную перед ним.
            var chevronSlot = new Panel { Width = IndentStep, Children = { chevron } };

            var name = new TextBlock
            {
                TextTrimming      = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center,
            };
            name.Bind(TextBlock.TextProperty, new Binding(nameof(DateNodeVm.Label)));

            var cb = new CheckBox
            {
                Content    = name,
                FontSize   = fontBase,
                HorizontalContentAlignment = HorizontalAlignment.Left,
                Margin     = new Thickness(0),
                Cursor     = new Cursor(StandardCursorType.Hand),
            };
            cb.Bind(ToggleButton.IsCheckedProperty,
                    new Binding(nameof(DateNodeVm.Checked)) { Mode = BindingMode.TwoWay });

            var cnt = new TextBlock
            {
                Foreground        = secondary,
                VerticalAlignment = VerticalAlignment.Center,
                Margin            = new Thickness(8, 0, 0, 0),
            };
            cnt.Bind(TextBlock.TextProperty, new Binding(nameof(DateNodeVm.CountText)));

            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto") };
            Grid.SetColumn(chevronSlot, 0); row.Children.Add(chevronSlot);
            Grid.SetColumn(cb, 1);          row.Children.Add(cb);
            Grid.SetColumn(cnt, 2);         row.Children.Add(cnt);
            // Отступ уровня — на всей строке: год у края, месяц правее, день еще правее.
            row.Bind(Layoutable.MarginProperty, new Binding(nameof(DateNodeVm.Indent)));
            return row;
        });

    // Иконки шеврона кешируются: геттер ArrowIcon дергается на каждое переиспользование
    // строки виртуализированным списком, искать ресурс каждый раз незачем.
    private static Geometry? _chevronRight;
    private static Geometry? _chevronDown;

    private static Geometry? Chevron(bool expanded) => expanded
        ? _chevronDown  ??= Icon("IconChevronDown")
        : _chevronRight ??= Icon("IconChevronRight");

    private static Geometry? Icon(string key) =>
        Application.Current!.TryFindResource(key, out var g) && g is Geometry geom ? geom : null;

    // Числовой токен из ресурсов (размер и толщина стрелки — те же, что в сайдбаре).
    private static double Res(string key, double fallback) =>
        Application.Current!.TryFindResource(key, out var r) && r is double d ? d : fallback;

    /// <summary>Строка дерева: подпись, счетчик, раскрытие и галочка с каскадом.</summary>
    public sealed class DateNodeVm(DateFilterNode node, int level, HashSet<string> checkedRaws,
                                   Action onToggled, Action onExpanded) : INotifyPropertyChanged
    {
        private bool _expanded;

        public string    Label       { get; } = node.Label;
        public string    CountText   { get; } = node.Count.ToString();
        public bool      HasChildren { get; } = node.Children.Count > 0;
        /// <summary>Отступ уровня: шаг равен ширине слота стрелки, поэтому чекбоксы года,
        /// месяца и дня идут ровной лесенкой (тот же прием, что IndentMargin в сайдбаре).</summary>
        public Thickness Indent      { get; } = new(level * IndentStep, 0, 0, 0);

        public List<DateNodeVm> Children { get; private set; } = [];

        public void SetChildren(List<DateNodeVm> children) => Children = children;

        public bool IsExpanded
        {
            get => _expanded;
            set
            {
                if (_expanded == value) return;
                _expanded = value;
                Raise(nameof(IsExpanded));
                Raise(nameof(ArrowIcon));
            }
        }

        /// <summary>Иконка шеврона: «›» у свернутой ветки, «⌄» у раскрытой.</summary>
        public Geometry? ArrowIcon => Chevron(_expanded);

        public void ToggleExpanded()
        {
            IsExpanded = !IsExpanded;
            onExpanded();
        }

        /// <summary>
        /// Галочка узла — ровно как «(Выделить все)», только для своей ветки: стоит, когда
        /// отмечено ВСЕ поддерево; снят хоть один день — у месяца и года галочки нет.
        /// Запись каскадом кладет или убирает все сырые значения поддерева.
        /// </summary>
        public bool Checked
        {
            get => DateFilterTree.IsFullyChecked(node, checkedRaws);
            set
            {
                // Решение принимаем по фактическому состоянию, а не по присланному значению:
                // отмечено все → снять ветку, иначе → отметить целиком.
                var on = !DateFilterTree.IsFullyChecked(node, checkedRaws);
                foreach (var raw in node.Raws)
                    if (on) checkedRaws.Add(raw); else checkedRaws.Remove(raw);
                onToggled();
            }
        }

        /// <summary>Перечитать галочку из общего набора (соседний узел или «Выделить все»).</summary>
        public void RaiseCheckedChanged() => Raise(nameof(Checked));

        public event PropertyChangedEventHandler? PropertyChanged;

        private void Raise(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
