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
using SmartBase.Database.Repositories;

namespace SmartBase.Controls.Dialogs;

/// <summary>
/// Окно «Отображение записей» — менеджер видимости/уровней/порядка строк-записей таблицы
/// (аналог <see cref="DisplayDialog"/> для колонок). Список записей сверху-вниз:
/// № позиции · чекбокс (показать/скрыть в таблице) · бейдж уровня (клик циклит
/// уровень) · название (Title записи). Блок уровней справа сверху массово переключает
/// чекбоксы: выбор уровня N включает видимость записей с уровнем ≤ N, остальные гасит.
///
/// Список — виртуализирующий <see cref="ListBox"/>: физически рендерятся только видимые
/// строки, поэтому окно открывается мгновенно даже на коллекциях в сотни тысяч записей
/// (раньше строился контрол на КАЖДУЮ запись → зависание). Сохраняется в БД (record_display);
/// скрытые записи отсекаются на уровне SQL при постраничной загрузке таблицы.
/// </summary>
public partial class RecordDisplayDialog : Window
{
    private readonly ConstructorRepository _repository;
    private readonly int _collectionId;

    // Все строки в порядке сверху-вниз (мастер-список). _shown — то, что показано
    // (фильтр поиска); содержит ССЫЛКИ на те же RowVm, поэтому правки видимости/уровня
    // через массовые операции отражаются в обоих.
    private readonly List<RowVm> _all = [];
    private readonly AvaloniaList<RowVm> _shown = [];

    private const int MaxLevels = 14;
    private int _maxLevel = 1;
    private int _activeLevel = 1;

    // Ширина колонки «№» — единая для всех строк (вычисляется под самый длинный номер),
    // чтобы крупные номера не обрезались, а чекбокс справа оставался ровным по горизонтали.
    private double _numWidth = 22;

    // Порядок строк меняли (кнопки вверх/вниз) — только тогда переписываем позиции в БД.
    private bool _orderChanged;

    public RecordDisplayDialog(ConstructorRepository repository, int collectionId,
                               IReadOnlyList<(int RecordId, string Title)> records)
    {
        InitializeComponent();

        _repository   = repository;
        _collectionId = collectionId;

        var saved = repository.GetRecordDisplay(collectionId);
        for (var i = 0; i < records.Count; i++)
        {
            var (id, title) = records[i];
            var (vis, lvl) = saved.TryGetValue(id, out var s) ? s : (true, 1);
            _all.Add(new RowVm(id, title) { Number = i + 1, Visible = vis, Level = Math.Max(1, lvl) });
        }
        _maxLevel    = Math.Max(1, _all.Select(r => r.Level).DefaultIfEmpty(1).Max());
        _activeLevel = 0;   // ни один уровень не выбран (видимость — из сохранённого состояния)
        _numWidth    = NumberColumnWidth();

        ListHost.ItemTemplate = BuildRowTemplate();
        ListHost.ItemsSource   = _shown;
        _shown.AddRange(_all);

        ListHost.SelectionChanged += (_, _) => RenderMoveButtons();
        SearchBox.TextChanged += (_, _) => ApplySearch();
        CancelBtn.Click += (_, _) => Close(false);
        SaveBtn.Click   += (_, _) => Save();

        RenderLevels();
        RenderMoveButtons();
    }

    private void Save()
    {
        _repository.SaveRecordDisplay(_collectionId,
            _all.Select(r => (r.RecordId, r.Visible, r.Level)));
        // Порядок переписываем в БД только если его реально меняли — иначе на больших
        // коллекциях лишний полный реордер (дорого), а при простом скрытии он не нужен.
        if (_orderChanged)
            _repository.SaveRecordOrder(_collectionId, _all.Select(r => r.RecordId).ToList());
        Close(true);
    }

    // Единая ширина колонки «№»: дефолтные 22px для коротких номеров, шире — под самый
    // длинный номер (= числу строк), измеренный реальным шрифтом строки.
    private double NumberColumnWidth()
    {
        var text     = Math.Max(1, _all.Count).ToString(CultureInfo.InvariantCulture);
        var fontBase = (double)this.FindResource("FontSizeBase")!;
        var measured = new FormattedText(text, CultureInfo.InvariantCulture,
                                         FlowDirection.LeftToRight,
                                         new Typeface(this.FontFamily), fontBase, null).Width;
        return Math.Max(22, Math.Ceiling(measured) + 8);
    }

    // ── Шаблон строки (рендерится только для видимых строк) ────────────────────
    private IDataTemplate BuildRowTemplate()
    {
        var secondary = (IBrush)this.FindResource("BrushSecondaryText")!;
        var fontBase  = (double)this.FindResource("FontSizeBase")!;

        return new FuncDataTemplate<RowVm>((_, _) =>
        {
            var num = new TextBlock
            {
                Width = _numWidth, Foreground = secondary, FontSize = fontBase,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment   = VerticalAlignment.Center,
            };
            num.Bind(TextBlock.TextProperty, new Binding(nameof(RowVm.NumberText)));

            var cb = new CheckBox { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(2, 0, 6, 0) };
            cb.Bind(ToggleButton.IsCheckedProperty, new Binding(nameof(RowVm.Visible)) { Mode = BindingMode.TwoWay });

            var lvlText = new TextBlock
            {
                FontSize = fontBase,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment   = VerticalAlignment.Center,
            };
            lvlText.Bind(TextBlock.TextProperty, new Binding(nameof(RowVm.LevelText)));
            // Бейдж уровня в стиле кнопок правого блока. Клик циклит уровень ТЕКУЩЕЙ строки —
            // читаем DataContext (а не замыкание), чтобы корректно работать при переиспользовании
            // контейнеров виртуализацией.
            var badge = MakeLevelButton(lvlText, br => lvlText.Foreground = br, active: false, onClick: () => { });
            badge.PointerPressed += (s, _) =>
            {
                if ((s as Control)?.DataContext is RowVm vm) vm.CycleLevel(_maxLevel);
            };
            badge.VerticalAlignment = VerticalAlignment.Center;
            badge.Margin = new Thickness(0, 0, 8, 0);

            var label = new TextBlock { VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
            label.Bind(TextBlock.TextProperty, new Binding(nameof(RowVm.Title)));

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
            : _all.Where(r => r.Title.Contains(q, StringComparison.OrdinalIgnoreCase)));
        RenderMoveButtons();
    }

    private bool IsSearching => !string.IsNullOrEmpty(SearchBox.Text?.Trim());

    // ── Перемещение выделенной записи ──────────────────────────────────────────
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

    // Перемещение доступно только без активного поиска (двигаем в полном порядке коллекции).
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

    // Переставить выделенную запись на одну позицию вверх/вниз в мастер-списке и в показе.
    private void MoveSelected(int dir)
    {
        if (ListHost.SelectedItem is not RowVm vm) return;
        var i = _all.IndexOf(vm);
        var j = i + dir;
        if (i < 0 || j < 0 || j >= _all.Count) return;

        (_all[i], _all[j]) = (_all[j], _all[i]);
        _all[i].Number = i + 1;
        _all[j].Number = j + 1;
        _shown.Move(i, j);          // без активного поиска _shown зеркалит _all
        _orderChanged = true;

        ListHost.SelectedItem = vm;
        RenderMoveButtons();
    }

    // ── Блок уровней (правый активитибар) ──────────────────────────────────────
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

    // Применяет выбранный уровень: видимость = (уровень записи ≤ выбранного).
    private void ApplyLevel(int level)
    {
        _activeLevel = level;
        foreach (var r in _all) r.Visible = r.Level <= level;
        RenderLevels();
    }

    // ── Модель строки (привязки обновляют только реализованные виртуализацией строки) ──
    private sealed class RowVm : INotifyPropertyChanged
    {
        public int    RecordId { get; }
        public string Title    { get; }

        public RowVm(int recordId, string title)
        {
            RecordId = recordId;
            Title    = string.IsNullOrWhiteSpace(title) ? "Без названия" : title;
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
