using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using MyPlanner.Database.Repositories;

namespace MyPlanner.Controls.Dialogs;

/// <summary>
/// Окно «Отображение записей» — менеджер видимости/уровней строк-записей таблицы
/// (аналог <see cref="DisplayDialog"/> для колонок). Список записей сверху-вниз:
/// № позиции · чекбокс (показать/скрыть в таблице) · бейдж уровня (клик циклит
/// уровень) · название (Title записи). Блок уровней справа сверху массово переключает
/// чекбоксы: выбор уровня N включает видимость записей с уровнем ≤ N, остальные гасит.
/// Сохраняется в БД (record_display); скрытые записи отсекаются на уровне SQL при
/// постраничной загрузке таблицы — виртуализация сохраняется.
/// </summary>
public partial class RecordDisplayDialog : Window
{
    private readonly ConstructorRepository _repository;
    private readonly int _collectionId;

    // Записи в порядке сверху-вниз: id + отображаемое название (Title).
    private readonly List<(int RecordId, string Title)> _records;

    // Максимум уровней (правый блок): дальше «+» недоступен — как «−» при одном уровне.
    // 14, т.к. сверху ещё кнопка «0» (сброс) — всего в верхней зоне до 15 кнопок.
    private const int MaxLevels = 14;

    private readonly Dictionary<int, bool> _visible = [];
    private readonly Dictionary<int, int>  _levels  = [];
    private int _maxLevel = 1;
    private int _activeLevel = 1;

    // Выделенная запись (клик по строке): её двигают кнопки вверх/вниз у поиска.
    private int? _selectedId;

    public RecordDisplayDialog(ConstructorRepository repository, int collectionId,
                               IReadOnlyList<(int RecordId, string Title)> records)
    {
        InitializeComponent();

        _repository   = repository;
        _collectionId = collectionId;
        _records      = records.ToList();

        // Загружаем сохранённое отображение (отсутствие ключа = видима, уровень 1).
        var saved = repository.GetRecordDisplay(collectionId);
        foreach (var (id, _) in _records)
        {
            var (vis, lvl) = saved.TryGetValue(id, out var s) ? s : (true, 1);
            _visible[id] = vis;
            _levels[id]  = Math.Max(1, lvl);
        }
        _maxLevel    = Math.Max(1, _levels.Values.DefaultIfEmpty(1).Max());
        _activeLevel = 0;   // ни один уровень не выбран (видимость — из сохранённого состояния)

        SearchBox.TextChanged += (_, _) => RenderList();
        CancelBtn.Click += (_, _) => Close(false);
        SaveBtn.Click   += (_, _) => Save();

        RenderLevels();
        RenderMoveButtons();
        RenderList();
    }

    private void Save()
    {
        _repository.SaveRecordDisplay(_collectionId,
            _records.Select(r => (r.RecordId, _visible[r.RecordId], _levels[r.RecordId])));
        // Порядок строк в окне = порядок записей сверху-вниз → пишем позиции, чтобы
        // перестановка отразилась и в самой таблице.
        _repository.SaveRecordOrder(_collectionId, _records.Select(r => r.RecordId).ToList());
        Close(true);
    }

    // lucide plus/minus — как у кнопки «Создать» в сайдбаре — и lucide arrow-up/down.
    private static readonly Geometry PlusIcon  = Geometry.Parse("M5 12 H19 M12 5 V19");
    private static readonly Geometry MinusIcon = Geometry.Parse("M5 12 H19");
    private static readonly Geometry ArrowUp   = Geometry.Parse("M5 12 L12 5 L19 12 M12 19 V5");
    private static readonly Geometry ArrowDown = Geometry.Parse("M12 5 V19 M5 12 L12 19 L19 12");

    // Кнопки перемещения выделенной записи (справа от поиска) — в стиле кнопок
    // уровней (с рамкой). Неактивны, если ничего не выделено или запись крайняя.
    private void RenderMoveButtons()
    {
        MoveButtons.Children.Clear();
        var idx = _selectedId is null ? -1 : _records.FindIndex(r => r.RecordId == _selectedId);
        MoveButtons.Children.Add(MakeMoveButton(ArrowUp,   idx > 0,                              () => MoveSelected(-1)));
        MoveButtons.Children.Add(MakeMoveButton(ArrowDown, idx >= 0 && idx < _records.Count - 1, () => MoveSelected(+1)));
    }

    // Кнопка-стрелка в стиле уровней (рамка): обводочная иконка, серая → синяя при
    // наведении; enabled=false — приглушённая.
    private Control MakeMoveButton(Geometry icon, bool enabled, Action onClick)
    {
        var path = new Avalonia.Controls.Shapes.Path
        {
            Data = icon, StrokeThickness = 2,
            StrokeLineCap = PenLineCap.Round, StrokeJoin = PenLineJoin.Round,
        };
        var box = new Viewbox
        {
            Width = 16, Height = 16,
            Child = new Canvas { Width = 24, Height = 24, Children = { path } },
        };
        return MakeLevelButton(box, br => path.Stroke = br, active: false, onClick, enabled);
    }

    // Переставить выделенную запись вверх/вниз (и № слева). К таблице применяется
    // при «Сохранить» (см. SaveRecordOrder).
    private void MoveSelected(int dir)
    {
        if (_selectedId is null) return;
        var i = _records.FindIndex(r => r.RecordId == _selectedId);
        var j = i + dir;
        if (i < 0 || j < 0 || j >= _records.Count) return;
        (_records[i], _records[j]) = (_records[j], _records[i]);
        RenderList();
        RenderMoveButtons();   // граничная доступность могла измениться
    }

    // Блок уровней (активитибар): числа 1..maxLevel сверху, «+»/«−» прибиты к низу.
    private void RenderLevels()
    {
        var fontBase = (double)this.FindResource("FontSizeBase")!;

        LevelPanel.Children.Clear();
        LevelActions.Children.Clear();

        // «0» (перед «1») — сброс всех уровней записей на 1 (с подтверждением).
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
            // Подсветка кумулятивная: выбран уровень N ⇒ синими становятся 1..N
            // (их записи показаны). Клик включает чекбоксы записей с уровнем ≤ N.
            LevelPanel.Children.Add(MakeLevelButton(txt, br => txt.Foreground = br,
                lvl <= _activeLevel, () => ApplyLevel(lvl)));
        }

        // «+» — обводочная иконка плюса (как в сайдбаре), не текст. Лишь добавляет
        // доступный уровень (для присвоения кликом по бейджу); видимость не трогает.
        var path = new Avalonia.Controls.Shapes.Path
        {
            Data = PlusIcon, StrokeThickness = 2,
            StrokeLineCap = PenLineCap.Round, StrokeJoin = PenLineJoin.Round,
        };
        var plusIcon = new Viewbox
        {
            Width = 16, Height = 16,
            Child = new Canvas { Width = 24, Height = 24, Children = { path } },
        };
        LevelActions.Children.Add(MakeLevelButton(plusIcon, br => path.Stroke = br,
            active: false, () => { if (_maxLevel < MaxLevels) { _maxLevel++; RenderLevels(); } },
            enabled: _maxLevel < MaxLevels));

        // «−» — убрать верхний уровень. Неактивна при единственном уровне (1 не удалить).
        var minusPath = new Avalonia.Controls.Shapes.Path
        {
            Data = MinusIcon, StrokeThickness = 2,
            StrokeLineCap = PenLineCap.Round, StrokeJoin = PenLineJoin.Round,
        };
        var minusIcon = new Viewbox
        {
            Width = 16, Height = 16,
            Child = new Canvas { Width = 24, Height = 24, Children = { minusPath } },
        };
        LevelActions.Children.Add(MakeLevelButton(minusIcon, br => minusPath.Stroke = br,
            active: false, RemoveLevel, enabled: _maxLevel > 1));
    }

    // «0» — сброс: после подтверждения все записи возвращаются на уровень 1.
    private async Task ResetLevels()
    {
        var dlg = new ConfirmDialog(
            "Все уровни будут выставлены по умолчанию на первый уровень. Подтвердить?",
            confirmText: "Сбросить");
        if (await dlg.ShowDialog<bool?>(this) != true) return;

        foreach (var k in _levels.Keys.ToList()) _levels[k] = 1;
        RenderList();
    }

    // Убирает верхний уровень: maxLevel уменьшается, у записей с этим (теперь
    // несуществующим) уровнем уровень снижается до нового максимума. Уровень 1 не
    // удаляется — минимум один уровень всегда есть.
    private void RemoveLevel()
    {
        if (_maxLevel <= 1) return;
        _maxLevel--;
        foreach (var key in _levels.Keys.ToList())
            if (_levels[key] > _maxLevel) _levels[key] = _maxLevel;
        if (_activeLevel > _maxLevel) _activeLevel = _maxLevel;
        RenderLevels();
        RenderList();   // бейджи уровней могли измениться
    }

    // Кнопка уровня (на базе сайдбарной navbtn): серый квадрат с рамкой. Внутренний
    // фон всегда серый (без заливки). Активная (выбранная) и наведённая — синий бордер
    // + синее содержимое. child — содержимое (цифра/иконка), setFg красит его цвет.
    // enabled=false — приглушённая, без наведения и клика (например, «−» при одном уровне).
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
            BorderThickness = new Thickness(1.2),
            Background  = secBg,                        // фон всегда серый (без синей заливки)
            BorderBrush = active ? accent : separator,  // активная — синий бордер
            Cursor = new Cursor(enabled ? StandardCursorType.Hand : StandardCursorType.Arrow),
            Opacity = enabled ? 1.0 : 0.4,
            HorizontalAlignment = HorizontalAlignment.Center,
            Child = child,
        };

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
        foreach (var (id, _) in _records)
            _visible[id] = _levels[id] <= level;
        RenderLevels();
        RenderList();
    }

    private void RenderList()
    {
        var secondary   = (IBrush)this.FindResource("BrushSecondaryText")!;
        var accentLight = (IBrush)this.FindResource("BrushAccentLight")!;
        var fontBase    = (double)this.FindResource("FontSizeBase")!;

        var q = SearchBox.Text?.Trim() ?? "";

        ListHost.Children.Clear();
        for (var i = 0; i < _records.Count; i++)
        {
            var (id, title) = _records[i];
            var name = string.IsNullOrWhiteSpace(title) ? "Без названия" : title;
            if (q.Length > 0 && !name.Contains(q, StringComparison.OrdinalIgnoreCase)) continue;

            var pos = i + 1;

            var num = new TextBlock
            {
                Text = pos.ToString(), Width = 22,
                Foreground = secondary, FontSize = fontBase,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment   = VerticalAlignment.Center,
            };

            var cb = new CheckBox
            {
                IsChecked = _visible[id], VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(2, 0, 6, 0),
            };
            cb.IsCheckedChanged += (_, _) => _visible[id] = cb.IsChecked == true;

            // Бейдж уровня — в стиле кнопок правого блока (рамка, синий на наведении).
            // Клик циклически меняет уровень записи 1..maxLevel.
            var lvlText = new TextBlock
            {
                Text = _levels[id].ToString(), FontSize = fontBase,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment   = VerticalAlignment.Center,
            };
            var badge = MakeLevelButton(lvlText, br => lvlText.Foreground = br, active: false,
                () => { _levels[id] = _levels[id] % _maxLevel + 1; RenderList(); }, size: 32);
            badge.VerticalAlignment = VerticalAlignment.Center;
            badge.Margin = new Thickness(0, 0, 8, 0);

            var label = new TextBlock
            {
                Text = name, VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
            };

            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,Auto,Auto,*") };
            Grid.SetColumn(num, 0);   row.Children.Add(num);
            Grid.SetColumn(cb, 1);    row.Children.Add(cb);
            Grid.SetColumn(badge, 2); row.Children.Add(badge);
            Grid.SetColumn(label, 3); row.Children.Add(label);

            // Клик по строке выделяет запись (подсветка во всю ширину, без скругления —
            // как строки сайдбара); её двигают кнопки у поиска. Padding insets контент,
            // но фон-подсветка занимает всю ширину строки (до линии рамки).
            var rowBorder = new Border
            {
                Background = id == _selectedId ? accentLight : Brushes.Transparent,
                Padding    = new Thickness(8, 4),
                Cursor     = new Cursor(StandardCursorType.Hand),
                Child      = row,
            };
            rowBorder.PointerPressed += (_, _) => { _selectedId = id; RenderList(); RenderMoveButtons(); };
            ListHost.Children.Add(rowBorder);
        }
    }
}
