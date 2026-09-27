using System.Collections;
using System.Collections.Specialized;
using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using SmartBase.Controls.Components;
using SmartBase.Core.Models;
using SmartBase.Database.Repositories;

namespace SmartBase.Controls.Dialogs;

/// <summary>
/// Окно «Отображение записей» — менеджер видимости/уровней/порядка строк-записей таблицы
/// (аналог <see cref="DisplayDialog"/> для колонок). Список записей сверху-вниз:
/// № позиции · чекбокс (показать/скрыть в таблице) · бейдж уровня (клик циклит
/// уровень) · название (Title записи). Блок уровней справа сверху массово переключает
/// чекбоксы: выбор уровня N включает видимость записей с уровнем ≤ N, остальные гасит.
///
/// Окно рассчитано на коллекции в миллионы записей: в памяти только id записей в порядке таблицы
/// (<see cref="_order"/>), названия подгружаются порциями для видимых строк виртуализирующего
/// <see cref="ListBox"/>, состояние хранится только у записей, отличных от умолчания («видима,
/// уровень 1»), — так же, как в базе (record_display). Поиск — запросом к базе. «Сохранить» пишет
/// только отличия и повторяет перестановки по одной (<c>MoveRecord</c>): прежнее окно на 10 млн
/// записей создавало строку на каждую запись, открывалось полминуты и занимало гигабайт, а при
/// сохранении переписывало состояние и порядок всех записей.
/// </summary>
public partial class RecordDisplayDialog : Window
{
    private readonly ConstructorRepository _repository;
    private readonly int _collectionId;

    // Все записи коллекции в порядке таблицы (id). Перестановки окна меняют и его.
    private readonly int[] _order;
    // Найденные поиском — индексы в _order по возрастанию; null — поиска нет.
    private int[]? _found;
    private CancellationTokenSource? _searchCts;

    // Состояние записей, отличных от умолчания (видима, уровень 1): из базы и правки окна.
    private readonly Dictionary<int, (bool Visible, int Level)> _state;
    // Перестановки по порядку (запись, вверх ли) — «Сохранить» повторяет их в базе.
    private readonly List<(int RecordId, bool Up)> _moves = [];

    // Названия видимых порций и строки списка, созданные для них (по id записи).
    private readonly Dictionary<int, string> _titles = [];
    private readonly Dictionary<int, RowVm> _vms = [];
    private readonly RowList _rows;

    private const int TitlePage   = 256;     // названий за один запрос
    private const int TitleCache  = 20_000;  // сколько названий держать, дальше кеш сбрасывается
    private const int SearchCap   = 100_000; // найденных поиском не больше
    private const int MaxLevels   = 14;
    private int _maxLevel = 1;
    private int _activeLevel = 1;

    // Ширина названия — единая для всех строк, по самому длинному из подгруженных. Без нее экстент
    // горизонтальной прокрутки виртуализирующая панель считала бы по видимым строкам, и полоса
    // снизу дергалась бы при вертикальной прокрутке (см. FilterValueMetrics).
    private readonly FilterValueMetrics _metrics = new();
    private string _widest = "";

    public RecordDisplayDialog(ConstructorRepository repository, int collectionId, int[] orderedRecordIds)
    {
        InitializeComponent();

        _repository   = repository;
        _collectionId = collectionId;
        _order        = orderedRecordIds;
        _state        = repository.GetRecordDisplay(collectionId)
                                  .Where(kv => !kv.Value.Visible || kv.Value.Level > 1)
                                  .ToDictionary(kv => kv.Key, kv => (kv.Value.Visible, Math.Max(1, kv.Value.Level)));
        _maxLevel    = Math.Max(1, _state.Values.Select(s => s.Level).DefaultIfEmpty(1).Max());
        _activeLevel = 0;   // ни один уровень не выбран (видимость — из сохраненного состояния)

        _rows = new RowList(this);
        ListHost.ItemTemplate = BuildRowTemplate();
        ListHost.ItemsSource  = _rows;

        ListHost.SelectionChanged += (_, _) => RenderMoveButtons();
        SearchBox.TextChanged += (_, _) => ApplySearch();
        CancelBtn.Click += (_, _) => Close(false);
        SaveBtn.Click   += (_, _) => Save();

        RenderLevels();
        RenderMoveButtons();
    }

    private void Save()
    {
        // Отличия от умолчания — остальные записи SaveRecordDisplay сам возвращает к умолчанию.
        _repository.SaveRecordDisplay(_collectionId, _state.Select(kv => (kv.Key, kv.Value.Visible, kv.Value.Level)));
        foreach (var (recordId, up) in _moves) _repository.MoveRecord(_collectionId, recordId, up);
        Close(true);
    }

    // ── Строки ─────────────────────────────────────────────────────────────────

    private int Count => _found?.Length ?? _order.Length;

    // Индекс записи в _order по индексу строки списка.
    private int OrderIndex(int row) => _found is { } f ? f[row] : row;

    private (bool Visible, int Level) StateOf(int recordId) =>
        _state.TryGetValue(recordId, out var s) ? s : (true, 1);

    private void SetState(int recordId, bool visible, int level)
    {
        if (visible && level <= 1) _state.Remove(recordId);
        else _state[recordId] = (visible, Math.Max(1, level));
    }

    private RowVm RowAt(int row)
    {
        var index = OrderIndex(row);
        var id    = _order[index];
        if (!_vms.TryGetValue(id, out var vm))
        {
            if (!_titles.ContainsKey(id)) LoadTitles(row);
            var (vis, lvl) = StateOf(id);
            vm = _vms[id] = new RowVm(id, _titles.GetValueOrDefault(id, ""), this);
            vm.SyncState(vis, lvl);
        }
        vm.Index  = row;
        vm.Number = index + 1;
        return vm;
    }

    // Названия порции строк вокруг row одним запросом; кеш ограничен — при переполнении сбрасывается.
    private void LoadTitles(int row)
    {
        if (_titles.Count > TitleCache) { _titles.Clear(); _vms.Clear(); }
        var from = row / TitlePage * TitlePage;
        var ids  = new List<int>(TitlePage);
        for (var r = from; r < Math.Min(Count, from + TitlePage); r++) ids.Add(_order[OrderIndex(r)]);
        foreach (var (id, title) in _repository.GetRecordTitles(ids))
        {
            _titles[id] = title;
            var shown = TitleText.FirstLine(string.IsNullOrWhiteSpace(title) ? "Без названия" : title);
            if (shown.Length > _widest.Length) _widest = shown;
        }
        foreach (var id in ids) _titles.TryAdd(id, "");
        _metrics.Measure([_widest], (double)this.FindResource("FontSizeBase")!,
                         this.TryFindResource("FontFamilyBase", out var f) ? f as FontFamily : null);
    }

    // Состояние поменяли массово (уровни) — перечитать созданные строки, в том числе показанные
    // после сброса кеша названий.
    private void RefreshRealized()
    {
        var shown = ListHost.GetRealizedContainers().Select(c => c.DataContext).OfType<RowVm>();
        foreach (var vm in _vms.Values.Concat(shown).Distinct())
        {
            var (vis, lvl) = StateOf(vm.RecordId);
            vm.SyncState(vis, lvl);
        }
    }

    // ── Шаблон строки (рендерится только для видимых строк) ────────────────────
    private IDataTemplate BuildRowTemplate()
    {
        var secondary = (IBrush)this.FindResource("BrushSecondaryText")!;
        var fontBase  = (double)this.FindResource("FontSizeBase")!;

        return new FuncDataTemplate<RowVm>((_, _) =>
        {
            // Ширины у номера НЕТ: колонка «№» подстраивается под свое число, поэтому зазор
            // до чекбокса отсчитывается от каждого числа и одинаков в любой строке. Общая
            // ширина колонки (под самый длинный номер) давала бы «1» огромный отступ, а
            // «546346» — никакого. Плата: чекбокс и все правее сдвигаются по строкам.
            var num = new TextBlock
            {
                Foreground = secondary, FontSize = fontBase,
                VerticalAlignment = VerticalAlignment.Center,
            };
            num.Bind(TextBlock.TextProperty, new Binding(nameof(RowVm.NumberText)));

            // Слева 8 — просвет между номером и чекбоксом. Номер прижат вправо, поэтому
            // весь зазор задается здесь и одинаков в любой строке.
            var cb = new CheckBox { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 6, 0) };
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
            label.Bind(TextBlock.TextProperty, new Binding(nameof(RowVm.DisplayTitle)));
            // Ширина ВСЕХ строк одинаковая — иначе экстент горизонтальной прокрутки
            // считался бы по видимым строкам и полоса дергалась.
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

    // ── Поиск — по названиям в базе, с паузой на набор ────────────────────────
    private void ApplySearch()
    {
        _searchCts?.Cancel();
        var q = SearchBox.Text?.Trim() ?? "";
        if (q.Length == 0)
        {
            ShowFound(null);
            return;
        }
        var cts = _searchCts = new CancellationTokenSource();
        _ = SearchAsync();

        async Task SearchAsync()
        {
            try
            {
                await Task.Delay(250, cts.Token);
                var ids  = await Task.Run(() => _repository.SearchRecordIdsIsolated(_collectionId, q, SearchCap, cts.Token), cts.Token);
                var set  = ids.Take(SearchCap).ToHashSet();
                var idx  = await Task.Run(() =>
                {
                    var list = new List<int>(set.Count);
                    for (var i = 0; i < _order.Length; i++) if (set.Contains(_order[i])) list.Add(i);
                    return list.ToArray();
                }, cts.Token);
                if (!cts.IsCancellationRequested) ShowFound(idx);
            }
            catch (OperationCanceledException) { }
        }
    }

    private void ShowFound(int[]? found)
    {
        _found = found;
        ListHost.SelectedItem = null;
        _rows.Reset();
        RenderMoveButtons();
    }

    private bool IsSearching => _found is not null;

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
        var i = ListHost.SelectedItem is RowVm vm && !IsSearching ? vm.Index : -1;
        MoveButtons.Children.Add(MakeMoveButton(ArrowUp,   i > 0,                         () => MoveSelected(-1)));
        MoveButtons.Children.Add(MakeMoveButton(ArrowDown, i >= 0 && i < _order.Length - 1, () => MoveSelected(+1)));
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

    // Переставить выделенную запись на одну позицию вверх/вниз: в списке сразу, в базе — по «Сохранить».
    private void MoveSelected(int dir)
    {
        if (ListHost.SelectedItem is not RowVm vm || IsSearching) return;
        var i = vm.Index;
        var j = i + dir;
        if (i < 0 || j < 0 || j >= _order.Length || _order[i] != vm.RecordId) return;

        (_order[i], _order[j]) = (_order[j], _order[i]);
        _moves.Add((vm.RecordId, dir < 0));
        _rows.Replaced(i);
        _rows.Replaced(j);

        ListHost.SelectedItem = RowAt(j);
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

    // Все уровни — первый (видимость не трогается). Записи без отличий в состоянии не хранятся,
    // поэтому меняются только отличные от умолчания.
    private async Task ResetLevels()
    {
        var dlg = new ConfirmDialog(
            "Все уровни будут выставлены по умолчанию на первый уровень. Подтвердить?",
            confirmText: "Сбросить");
        if (await dlg.ShowDialog<bool?>(this) != true) return;

        foreach (var (id, (vis, _)) in _state.ToList()) SetState(id, vis, 1);
        RefreshRealized();
    }

    private void RemoveLevel()
    {
        if (_maxLevel <= 1) return;
        _maxLevel--;
        foreach (var (id, (vis, lvl)) in _state.ToList())
            if (lvl > _maxLevel) SetState(id, vis, _maxLevel);
        if (_activeLevel > _maxLevel) _activeLevel = _maxLevel;
        RefreshRealized();
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

    // Применяет выбранный уровень: видимость = (уровень записи ≤ выбранного). У записей по
    // умолчанию уровень 1, и они видимы при любом уровне — меняются только отличные.
    private void ApplyLevel(int level)
    {
        _activeLevel = level;
        foreach (var (id, (_, lvl)) in _state.ToList()) SetState(id, lvl <= level, lvl);
        RefreshRealized();
        RenderLevels();
    }

    // ── Виртуальный список строк для ListBox ───────────────────────────────────
    // Счет — все записи (или найденные), строка создается, когда виртуализация ее запрашивает.
    private sealed class RowList(RecordDisplayDialog owner) : IList, INotifyCollectionChanged
    {
        public event NotifyCollectionChangedEventHandler? CollectionChanged;

        public int Count => owner.Count;
        public object? this[int index]
        {
            get => owner.RowAt(index);
            set => throw new NotSupportedException();
        }
        public int IndexOf(object? value) =>
            value is RowVm vm && vm.Index >= 0 && vm.Index < Count && owner._order[owner.OrderIndex(vm.Index)] == vm.RecordId
                ? vm.Index : -1;
        public bool Contains(object? value) => IndexOf(value) >= 0;

        public void Reset() =>
            CollectionChanged?.Invoke(this, new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));

        // Строка index сменила запись (перестановка): старую строку списка — новой.
        public void Replaced(int index)
        {
            var id = owner._order[owner.OrderIndex(index)];
            var vm = owner.RowAt(index);
            CollectionChanged?.Invoke(this, new NotifyCollectionChangedEventArgs(
                NotifyCollectionChangedAction.Replace, vm, new RowVm(-id, "", owner), index));
        }

        public bool IsFixedSize => false;
        public bool IsReadOnly => true;
        public bool IsSynchronized => false;
        public object SyncRoot => this;
        public int Add(object? value) => throw new NotSupportedException();
        public void Clear() => throw new NotSupportedException();
        public void Insert(int index, object? value) => throw new NotSupportedException();
        public void Remove(object? value) => throw new NotSupportedException();
        public void RemoveAt(int index) => throw new NotSupportedException();
        public void CopyTo(Array array, int index) => throw new NotSupportedException();
        public IEnumerator GetEnumerator()
        {
            for (var i = 0; i < Count; i++) yield return owner.RowAt(i);
        }
    }

    // ── Модель строки (привязки обновляют только реализованные виртуализацией строки) ──
    private sealed class RowVm(int recordId, string title, RecordDisplayDialog owner) : INotifyPropertyChanged
    {
        public int    RecordId { get; } = recordId;
        /// <summary>Первая строка названия — строка списка одна, показываем ее (см. TitleText).</summary>
        public string DisplayTitle { get; } = TitleText.FirstLine(string.IsNullOrWhiteSpace(title) ? "Без названия" : title);
        /// <summary>Строка списка, на которой запись показана сейчас.</summary>
        public int Index { get; set; }

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
            set
            {
                if (_visible == value) return;
                _visible = value;
                owner.SetState(RecordId, _visible, _level);
                Raise(nameof(Visible));
            }
        }

        private int _level = 1;
        public int Level
        {
            get => _level;
            set
            {
                if (_level == value) return;
                _level = value;
                owner.SetState(RecordId, _visible, _level);
                Raise(nameof(Level)); Raise(nameof(LevelText));
            }
        }
        public string LevelText => _level.ToString();

        public void CycleLevel(int max) => Level = Level % Math.Max(1, max) + 1;

        // Состояние поменяли массово (уровни) — показать, не записывая обратно.
        public void SyncState(bool visible, int level)
        {
            if (_visible != visible) { _visible = visible; Raise(nameof(Visible)); }
            if (_level != level) { _level = level; Raise(nameof(Level)); Raise(nameof(LevelText)); }
        }

        public override bool Equals(object? obj) => obj is RowVm other && other.RecordId == RecordId;
        public override int GetHashCode() => RecordId;

        public event PropertyChangedEventHandler? PropertyChanged;
        private void Raise(string n) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
    }
}
