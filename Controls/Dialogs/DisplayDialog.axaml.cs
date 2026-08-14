using System.ComponentModel;
using System.Globalization;
using Avalonia;
using Avalonia.Collections;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using SmartBase.Controls.Components;
using SmartBase.Database.Repositories;

namespace SmartBase.Controls.Dialogs;

/// <summary>
/// Окно «Отображение» — менеджер видимости колонок таблицы. Список свойств в порядке
/// слева-направо: № позиции · чекбокс (показать/скрыть в таблице) · бейдж уровня
/// (клик циклит уровень) · название. Блок уровней справа сверху массово переключает
/// чекбоксы: выбор уровня N включает видимость колонок с уровнем ≤ N, остальные гасит.
///
/// Список — виртуализирующий <see cref="ListBox"/>: рендерятся только видимые строки,
/// поэтому окно открывается мгновенно даже на коллекциях с большим числом свойств.
/// Сохраняется в БД (column_display); применяется к таблице при обновлении.
/// </summary>
public partial class DisplayDialog : Window
{
    private readonly ConstructorRepository _repository;
    private readonly int _collectionId;

    private readonly List<RowVm> _all = [];
    private readonly AvaloniaList<RowVm> _shown = [];

    private const int MaxLevels = 14;
    private int _maxLevel = 1;
    private int _activeLevel = 1;
    private bool _orderChanged;

    // Ширина названия — единая для всех строк, по самому длинному. Без неё экстент
    // горизонтальной прокрутки виртуализирующая панель считала бы по видимым строкам, и
    // полоса снизу дёргалась бы при вертикальной прокрутке (см. FilterValueMetrics).
    private readonly FilterValueMetrics _metrics = new();

    public DisplayDialog(ConstructorRepository repository, int collectionId,
                         IReadOnlyList<(string Key, string Name)> columns)
    {
        InitializeComponent();

        _repository   = repository;
        _collectionId = collectionId;

        var saved = repository.GetColumnDisplay(collectionId);
        for (var i = 0; i < columns.Count; i++)
        {
            var (key, name) = columns[i];
            var (vis, lvl) = saved.TryGetValue(key, out var s) ? s : (true, 1);
            _all.Add(new RowVm(key, name) { Number = i + 1, Visible = vis, Level = Math.Max(1, lvl) });
        }
        _maxLevel    = Math.Max(1, _all.Select(r => r.Level).DefaultIfEmpty(1).Max());
        _activeLevel = 0;

        ListHost.ItemTemplate = BuildRowTemplate();
        ListHost.ItemsSource   = _shown;
        _shown.AddRange(_all);
        MeasureRows();

        ListHost.SelectionChanged += (_, _) => RenderMoveButtons();
        SearchBox.TextChanged += (_, _) => ApplySearch();
        CancelBtn.Click += (_, _) => Close(false);
        SaveBtn.Click   += (_, _) => Save();

        RenderLevels();
        RenderMoveButtons();
    }

    private void Save()
    {
        _repository.SaveColumnDisplay(_collectionId,
            _all.Select(r => (r.Key, r.Visible, r.Level)));
        if (_orderChanged)
            _repository.SaveColumnOrder(_collectionId, _all.Select(r => r.Key).ToList());
        Close(true);
    }

    // ── Шаблон строки ──────────────────────────────────────────────────────────
    private IDataTemplate BuildRowTemplate()
    {
        var secondary = (IBrush)this.FindResource("BrushSecondaryText")!;
        var fontBase  = (double)this.FindResource("FontSizeBase")!;

        return new FuncDataTemplate<RowVm>((_, _) =>
        {
            // Ширины у номера НЕТ: колонка «№» подстраивается под своё число, поэтому зазор
            // до чекбокса отсчитывается от каждого числа и одинаков в любой строке. Общая
            // ширина колонки (под самый длинный номер) давала бы «1» огромный отступ, а
            // «546346» — никакого. Плата: чекбокс и всё правее сдвигаются по строкам.
            var num = new TextBlock
            {
                Foreground = secondary, FontSize = fontBase,
                VerticalAlignment = VerticalAlignment.Center,
            };
            num.Bind(TextBlock.TextProperty, new Binding(nameof(RowVm.NumberText)));

            // Слева 8 — просвет между номером и чекбоксом. Номер прижат вправо, поэтому
            // весь зазор задаётся здесь и одинаков в любой строке.
            var cb = new CheckBox { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 6, 0) };
            cb.Bind(ToggleButton.IsCheckedProperty, new Binding(nameof(RowVm.Visible)) { Mode = BindingMode.TwoWay });

            var lvlText = new TextBlock
            {
                FontSize = fontBase,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment   = VerticalAlignment.Center,
            };
            lvlText.Bind(TextBlock.TextProperty, new Binding(nameof(RowVm.LevelText)));
            var badge = MakeLevelButton(lvlText, br => lvlText.Foreground = br, active: false, onClick: () => { });
            badge.PointerPressed += (s, _) =>
            {
                if ((s as Control)?.DataContext is RowVm vm) vm.CycleLevel(_maxLevel);
            };
            badge.VerticalAlignment = VerticalAlignment.Center;
            badge.Margin = new Thickness(0, 0, 8, 0);

            var label = new TextBlock { VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
            label.Bind(TextBlock.TextProperty, new Binding(nameof(RowVm.Name)));
            // Ширина ВСЕХ строк одинаковая — иначе экстент горизонтальной прокрутки
            // считался бы по видимым строкам и полоса дёргалась.
            label.Bind(Layoutable.MinWidthProperty,
                       new Binding(nameof(FilterValueMetrics.TextMinWidth)) { Source = _metrics });

            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,Auto,Auto,*") };
            Grid.SetColumn(num, 0);   row.Children.Add(num);
            Grid.SetColumn(cb, 1);    row.Children.Add(cb);
            Grid.SetColumn(badge, 2); row.Children.Add(badge);
            Grid.SetColumn(label, 3); row.Children.Add(label);

            return new Border { Padding = new Thickness(8, 4), Background = Brushes.Transparent, Child = row };
        });
    }

    // ── Поиск ──────────────────────────────────────────────────────────────────
    private void ApplySearch()
    {
        var q = SearchBox.Text?.Trim() ?? "";
        _shown.Clear();
        _shown.AddRange(q.Length == 0
            ? _all
            : _all.Where(r => r.Name.Contains(q, StringComparison.OrdinalIgnoreCase)));
        MeasureRows();
        RenderMoveButtons();
    }

    // Ширина названия — по самому длинному в ТЕКУЩЕМ наборе (после поиска он другой).
    private void MeasureRows() =>
        _metrics.Measure(_shown.Select(r => r.Name),
                         (double)this.FindResource("FontSizeBase")!,
                         this.TryFindResource("FontFamilyBase", out var f) ? f as FontFamily : null);

    private bool IsSearching => !string.IsNullOrEmpty(SearchBox.Text?.Trim());

    // ── Перемещение выделенного свойства ───────────────────────────────────────
    // Иконки — из общего словаря Assets/Styles/Icons.axaml по ключу x:Key.
    // Имя IconGeometry, а не Icon: короткое Icon скрыло бы унаследованное Window.Icon (CS0108).
    private static Geometry IconGeometry(string key) =>
        Application.Current!.TryFindResource(key, out var res) && res is Geometry g
            ? g
            : throw new KeyNotFoundException($"Icon resource '{key}' not found in Icons.axaml");

    // Размер иконки (сторона Viewbox) — из токенов Icons.axaml.
    private static double IconSize(string key) =>
        Application.Current!.TryFindResource(key, out var r) && r is double d ? d : 16;

    private static Geometry PlusIcon  => IconGeometry("IconPlus");
    private static Geometry MinusIcon => IconGeometry("IconMinus");
    private static Geometry ArrowUp   => IconGeometry("IconArrowUp");
    private static Geometry ArrowDown => IconGeometry("IconArrowDown");

    private void RenderMoveButtons()
    {
        MoveButtons.Children.Clear();
        var i = ListHost.SelectedItem is RowVm vm && !IsSearching ? _all.IndexOf(vm) : -1;
        MoveButtons.Children.Add(MakeMoveButton(ArrowUp,   i > 0,                       () => MoveSelected(-1)));
        MoveButtons.Children.Add(MakeMoveButton(ArrowDown, i >= 0 && i < _all.Count - 1, () => MoveSelected(+1)));
    }

    private Control MakeMoveButton(Geometry icon, bool enabled, Action onClick)
    {
        var path = new Avalonia.Controls.Shapes.Path
        {
            Data = icon, StrokeThickness = IconSize("IconStrokeSmallBold"),
            StrokeLineCap = PenLineCap.Round, StrokeJoin = PenLineJoin.Round,
        };
        var box = new Viewbox { Width = IconSize("IconSizeSmall"), Height = IconSize("IconSizeSmall"), Child = new Canvas { Width = 24, Height = 24, Children = { path } } };
        return MakeLevelButton(box, br => path.Stroke = br, active: false, onClick, enabled);
    }

    private void MoveSelected(int dir)
    {
        if (ListHost.SelectedItem is not RowVm vm) return;
        var i = _all.IndexOf(vm);
        var j = i + dir;
        if (i < 0 || j < 0 || j >= _all.Count) return;

        (_all[i], _all[j]) = (_all[j], _all[i]);
        _all[i].Number = i + 1;
        _all[j].Number = j + 1;
        _shown.Move(i, j);
        _orderChanged = true;

        ListHost.SelectedItem = vm;
        RenderMoveButtons();
    }

    // ── Блок уровней ───────────────────────────────────────────────────────────
    private void RenderLevels()
    {
        var fontBase = (double)this.FindResource("FontSizeBase")!;

        LevelPanel.Children.Clear();
        LevelActions.Children.Clear();

        var zeroText = new TextBlock
        {
            Text = "0", FontSize = fontBase,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment   = VerticalAlignment.Center,
        };
        LevelPanel.Children.Add(MakeLevelButton(zeroText, br => zeroText.Foreground = br,
            active: false, () => _ = ResetLevels()));

        for (var n = 1; n <= _maxLevel; n++)
        {
            var lvl = n;
            var txt = new TextBlock
            {
                Text = lvl.ToString(), FontSize = fontBase,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment   = VerticalAlignment.Center,
            };
            LevelPanel.Children.Add(MakeLevelButton(txt, br => txt.Foreground = br,
                lvl <= _activeLevel, () => ApplyLevel(lvl)));
        }

        var path = new Avalonia.Controls.Shapes.Path
        {
            Data = PlusIcon, StrokeThickness = IconSize("IconStrokeSmallBold"),
            StrokeLineCap = PenLineCap.Round, StrokeJoin = PenLineJoin.Round,
        };
        var plusIcon = new Viewbox { Width = IconSize("IconSizeSmall"), Height = IconSize("IconSizeSmall"), Child = new Canvas { Width = 24, Height = 24, Children = { path } } };
        LevelActions.Children.Add(MakeLevelButton(plusIcon, br => path.Stroke = br,
            active: false, () => { if (_maxLevel < MaxLevels) { _maxLevel++; RenderLevels(); } },
            enabled: _maxLevel < MaxLevels));

        var minusPath = new Avalonia.Controls.Shapes.Path
        {
            Data = MinusIcon, StrokeThickness = IconSize("IconStrokeSmallBold"),
            StrokeLineCap = PenLineCap.Round, StrokeJoin = PenLineJoin.Round,
        };
        var minusIcon = new Viewbox { Width = IconSize("IconSizeSmall"), Height = IconSize("IconSizeSmall"), Child = new Canvas { Width = 24, Height = 24, Children = { minusPath } } };
        LevelActions.Children.Add(MakeLevelButton(minusIcon, br => minusPath.Stroke = br,
            active: false, RemoveLevel, enabled: _maxLevel > 1));
    }

    private async Task ResetLevels()
    {
        var dlg = new ConfirmDialog(
            "Все уровни будут выставлены по умолчанию на первый уровень. Подтвердить?",
            confirmText: "Сбросить");
        if (await dlg.ShowDialog<bool?>(this) != true) return;

        foreach (var r in _all) r.Level = 1;
    }

    private void RemoveLevel()
    {
        if (_maxLevel <= 1) return;
        _maxLevel--;
        foreach (var r in _all) if (r.Level > _maxLevel) r.Level = _maxLevel;
        if (_activeLevel > _maxLevel) _activeLevel = _maxLevel;
        RenderLevels();
    }

    private Border MakeLevelButton(Control child, Action<IBrush> setFg, bool active, Action onClick,
                                   bool enabled = true, double size = 32)
    {
        var accent    = (IBrush)this.FindResource("BrushAccent")!;
        var secondary = (IBrush)this.FindResource("BrushSecondaryText")!;
        var secBg     = (IBrush)this.FindResource("BrushSecondaryBg")!;
        var separator = (IBrush)this.FindResource("BrushSeparator")!;
        var radius    = (CornerRadius)this.FindResource("RadiusMedium")!;

        setFg(active ? accent : secondary);

        var b = new Border
        {
            Width = size, Height = size, CornerRadius = radius,
            Background  = secBg,
            BorderBrush = active ? accent : separator,
            Cursor = new Cursor(enabled ? StandardCursorType.Hand : StandardCursorType.Arrow),
            Opacity = enabled ? 1.0 : 0.4,
            HorizontalAlignment = HorizontalAlignment.Center,
            Child = child,
        };
        // Рамка кнопки — стандартная линия (была 1.2), живой токен окна.
        b.Bind(Border.BorderThicknessProperty, b.GetResourceObservable("BorderLine"));

        if (!enabled) return b;

        b.PointerEntered += (_, _) => { b.BorderBrush = accent; setFg(accent); };
        b.PointerExited  += (_, _) =>
        {
            b.BorderBrush = active ? accent : separator;
            setFg(active ? accent : secondary);
        };
        b.PointerPressed += (_, _) => onClick();
        return b;
    }

    private void ApplyLevel(int level)
    {
        _activeLevel = level;
        foreach (var r in _all) r.Visible = r.Level <= level;
        RenderLevels();
    }

    // ── Модель строки ──────────────────────────────────────────────────────────
    private sealed class RowVm : INotifyPropertyChanged
    {
        public string Key  { get; }
        public string Name { get; }

        public RowVm(string key, string name)
        {
            Key  = key;
            Name = string.IsNullOrWhiteSpace(name) ? "Без названия" : name;
        }

        private int _number;
        public int Number
        {
            get => _number;
            set { if (_number != value) { _number = value; Raise(nameof(Number)); Raise(nameof(NumberText)); } }
        }
        public string NumberText => _number.ToString();

        private bool _visible = true;
        public bool Visible
        {
            get => _visible;
            set { if (_visible != value) { _visible = value; Raise(nameof(Visible)); } }
        }

        private int _level = 1;
        public int Level
        {
            get => _level;
            set { if (_level != value) { _level = value; Raise(nameof(Level)); Raise(nameof(LevelText)); } }
        }
        public string LevelText => _level.ToString();

        public void CycleLevel(int max) => Level = Level % Math.Max(1, max) + 1;

        public event PropertyChangedEventHandler? PropertyChanged;
        private void Raise(string n) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
    }
}
