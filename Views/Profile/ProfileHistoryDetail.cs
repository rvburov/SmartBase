using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using ChangeLogRow = SmartBase.Database.Repositories.ConstructorRepository.ChangeLogRow;
using Path = Avalonia.Controls.Shapes.Path;

namespace SmartBase.Views.Profile;

/// <summary>
/// Панель «Детали» строки журнала — зеркало панели деталей «Настройки → Диагностика»
/// (SettingsLogsTable): клик по строке открывает справа во всю высоту карточки полей,
/// клик по карточке раскрывает полный текст поля снизу, крестик закрывает панель.
///
/// Зачем: в таблице длинные значения обрезаются «…» — «Объект» с «до → после» почти
/// всегда шире колонки. Тултип годится посмотреть, но не выделить и не скопировать;
/// панель дает полный текст с кнопкой копирования.
///
/// Поля карточек совпадают с колонками таблицы (те же подписи и иконки) — строка
/// раскрывается «как есть», без изобретения новых названий.
/// </summary>
public partial class ProfileView
{
    private bool   _historySelectionHooked;
    private string _historyDetailFieldKey = "object";   // какое поле открыто снизу (помнится между строками)
    private Border? _historyDetailSelectedCard;
    private readonly Dictionary<string, Border> _historyDetailCards = [];

    // Ширина панели деталей (px) — тянется ручкой HistoryDetailResizeGrip; помнится в пределах сессии.
    private double _historyDetailWidth = 440;
    private const double HistoryDetailWidthMin = 320;
    private const double HistoryDetailWidthMax = 900;

    // Общая ширина колонки названий у всех карточек — тянется ручкой ⋮ (как в записи).
    private readonly List<ColumnDefinition> _historyNameCols = [];
    private double _historyNameColWidth = 120;
    private const double HistoryNameColMin = 70;

    // Строка карточек в HistoryDetailGrid (Auto,*,Auto,*): индекс 1.
    private RowDefinition HistoryCardsRow => HistoryDetailGrid.RowDefinitions[1];

    private void HookHistoryDetail()
    {
        if (_historySelectionHooked) return;

        HistoryGrid.SelectionChanged   += OnHistorySelectionChanged;
        HistoryDetailCloseBtn.Click    += (_, _) => CloseHistoryDetail();
        HistoryDetailCopyBtn.Click     += (_, _) => CopyHistoryDetailField();
        HistoryPropsResizeGrip.PointerPressed  += OnHistoryGripPressed;
        HistoryPropsResizeGrip.PointerMoved    += OnHistoryGripMoved;
        HistoryPropsResizeGrip.PointerReleased += OnHistoryGripReleased;
        HistoryDetailResizeGrip.PointerPressed  += OnHistoryDetailGripPressed;
        HistoryDetailResizeGrip.PointerMoved    += OnHistoryDetailGripMoved;
        HistoryDetailResizeGrip.PointerReleased += OnHistoryDetailGripReleased;
        _historySelectionHooked = true;
    }

    private void OnHistorySelectionChanged(object? sender, EventArgs e)
    {
        if (HistoryGrid.SelectedItem is ChangeLogRow row) OpenHistoryDetail(row);
    }

    private void OpenHistoryDetail(ChangeLogRow row)
    {
        BuildHistoryDetailCards(row);
        ApplyHistoryCardsHeight();
        HistoryDetailPanel.Width        = _historyDetailWidth;
        DetailDivider.IsVisible  = true;
        HistoryDetailPanel.IsVisible    = true;
        HistoryDetailResizeGrip.IsVisible = true;
    }

    private void CloseHistoryDetail()
    {
        HistoryDetailPanel.IsVisible      = false;
        DetailDivider.IsVisible    = false;
        HistoryDetailResizeGrip.IsVisible = false;
        HistoryDetailCardsHost.Children.Clear();
        _historyDetailCards.Clear();
        _historyNameCols.Clear();
        _historyDetailSelectedCard = null;
        HistoryDetailFullText.Text = "";
        HistoryGrid.ClearSelection();
    }

    private void CopyHistoryDetailField()
    {
        var text = HistoryDetailFullText.Text;
        if (string.IsNullOrEmpty(text)) return;
        _ = TopLevel.GetTopLevel(this)?.Clipboard?.SetTextAsync(text);
    }

    // Построить карточки полей выбранной строки; открыть снизу ранее выбранное поле
    // (или «Объект» по умолчанию — там длинные тексты «до → после»).
    private void BuildHistoryDetailCards(ChangeLogRow row)
    {
        HistoryDetailCardsHost.Children.Clear();
        _historyDetailCards.Clear();
        _historyNameCols.Clear();
        _historyDetailSelectedCard = null;

        foreach (var (key, header, iconKey) in HistoryColumns)
        {
            var card = BuildHistoryDetailCard(key, header, iconKey, row);
            _historyDetailCards[key] = card;
            HistoryDetailCardsHost.Children.Add(card);
        }

        var openKey = HistoryColumns.Any(c => c.Key == _historyDetailFieldKey)
            ? _historyDetailFieldKey : "object";
        OpenHistoryDetailField(openKey, row);
    }

    // Карточка поля 1-в-1 как карточка свойства записи и строки журнала диагностики:
    // [иконка типа + подпись] · [ручка ⋮] · [значение-превью]. Клик выделяет карточку.
    private Border BuildHistoryDetailCard(string key, string name, string iconKey, ChangeLogRow row)
    {
        var nameLabel = new TextBlock
        {
            Text              = name,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming      = TextTrimming.CharacterEllipsis,
            Margin            = new Thickness(8, 0, 4, 0),
            FontSize          = (double)this.FindResource("FontSizeBase")!,
        };
        nameLabel.Classes.Add("detailName");

        var nameRow = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*"),
            VerticalAlignment = VerticalAlignment.Center,
            ClipToBounds      = true,
        };
        var icon = MakeHistoryCardIcon(iconKey);
        Grid.SetColumn(icon, 0);      nameRow.Children.Add(icon);
        Grid.SetColumn(nameLabel, 1); nameRow.Children.Add(nameLabel);

        var full    = HistoryCellText(key, row);
        var preview = new TextBlock
        {
            Text              = full.Length == 0 ? "—" : full.ReplaceLineEndings(" ").Trim(),
            Foreground        = (IBrush)this.FindResource(full.Length == 0
                                    ? "BrushSecondaryText" : "BrushPrimaryText")!,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming      = TextTrimming.CharacterEllipsis,
            Margin            = new Thickness(2.5, 0, 0, 0),
        };

        // 3 колонки: [название (общая ширина, ресайз ⋮)] · [ручка ⋮] · [значение *].
        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions
            {
                new ColumnDefinition(new GridLength(_historyNameColWidth, GridUnitType.Pixel)),
                new ColumnDefinition(GridLength.Auto),
                new ColumnDefinition(new GridLength(1, GridUnitType.Star)),
            },
        };
        _historyNameCols.Add(grid.ColumnDefinitions[0]);

        var grip = BuildHistoryColumnGrip();
        Grid.SetColumn(nameRow, 0); grid.Children.Add(nameRow);
        Grid.SetColumn(grip,    1); grid.Children.Add(grip);
        Grid.SetColumn(preview, 2); grid.Children.Add(preview);

        var card = new Border { Classes = { "detailRow" }, Child = grid };
        // Нажатие на ручку ⋮ ее обработчик помечает Handled (пузырьком, раньше карточки) —
        // поэтому drag ручки карточку не выделяет.
        card.PointerPressed += (_, _) => OpenHistoryDetailField(key, row);
        return card;
    }

    // Раскрыть поле снизу: подсветить карточку, заполнить крошку и текст.
    private void OpenHistoryDetailField(string key, ChangeLogRow row)
    {
        _historyDetailFieldKey = key;

        _historyDetailSelectedCard?.Classes.Remove("selected");
        if (_historyDetailCards.TryGetValue(key, out var card))
        {
            card.Classes.Add("selected");
            _historyDetailSelectedCard = card;
        }

        HistoryDetailFieldName.Text = HistoryColumns.First(c => c.Key == key).Header;
        HistoryDetailFullText.Text  = HistoryCellText(key, row);
    }

    // Обводочная иконка 16×16 из реестра Icons.axaml с классом detailIcon
    // (синеет в выбранной карточке).
    private Control MakeHistoryCardIcon(string key)
    {
        var path = new Path
        {
            Data            = HistoryIcon(key),
            StrokeThickness = HistoryIconSize("IconStrokeSmall"),
            StrokeLineCap   = PenLineCap.Round,
            StrokeJoin      = PenLineJoin.Round,
        };
        path.Classes.Add("detailIcon");

        return new Viewbox
        {
            Width             = 16,
            Height            = 16,
            VerticalAlignment = VerticalAlignment.Center,
            Child             = new Canvas { Width = 24, Height = 24, Children = { path } },
        };
    }

    // Ручка ⋮ карточки: три вертикальные точки; drag меняет общую ширину колонки названий.
    private Border BuildHistoryColumnGrip()
    {
        var dots = new StackPanel
        {
            Orientation         = Orientation.Vertical,
            Spacing             = 3,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment   = VerticalAlignment.Center,
        };
        for (var i = 0; i < 3; i++)
            dots.Children.Add(new Ellipse
            {
                Width  = 3,
                Height = 3,
                Fill   = (IBrush)this.FindResource("BrushSecondaryText")!,
            });

        var grip = new Border { Classes = { "detailGrip" }, Child = dots };
        grip.PointerPressed  += OnHistoryColGripPressed;
        grip.PointerMoved    += OnHistoryColGripMoved;
        grip.PointerReleased += OnHistoryColGripReleased;
        return grip;
    }

    // ── Ручка-разделитель: делит карточки (сверху) и полный текст (снизу) ────────
    // По умолчанию строки делят место поровну (обе 1*). Перетаскивание задает строке
    // карточек пиксельную высоту; выбранная высота помнится в пределах сессии.
    private bool    _historyGripDragging;
    private double  _historyGripStartY;
    private double  _historyGripStartHeight;
    private double? _historyCardsHeight;   // высота блока карточек (null = поровну, 1*)

    private const double HistoryCardsHeightMin = 48;

    private void ApplyHistoryCardsHeight() =>
        HistoryCardsRow.Height = _historyCardsHeight is double h
            ? new GridLength(h, GridUnitType.Pixel)
            : new GridLength(1, GridUnitType.Star);

    private void OnHistoryGripPressed(object? sender, PointerPressedEventArgs e)
    {
        _historyGripDragging    = true;
        _historyGripStartY      = e.GetPosition(HistoryDetailPanel).Y;
        _historyGripStartHeight = HistoryCardsRow.ActualHeight;
        e.Pointer.Capture((IInputElement?)sender);
        e.Handled = true;
    }

    private void OnHistoryGripMoved(object? sender, PointerEventArgs e)
    {
        if (!_historyGripDragging) return;

        var dy  = e.GetPosition(HistoryDetailPanel).Y - _historyGripStartY;
        // Не даем карточкам съесть весь текст: оставляем нижнему блоку запас.
        var max = Math.Max(HistoryCardsHeightMin, HistoryDetailPanel.Bounds.Height - 180);
        var newHeight = Math.Clamp(_historyGripStartHeight + dy, HistoryCardsHeightMin, max);

        HistoryCardsRow.Height = new GridLength(newHeight, GridUnitType.Pixel);
        _historyCardsHeight = newHeight;
    }

    private void OnHistoryGripReleased(object? sender, PointerReleasedEventArgs e)
    {
        _historyGripDragging = false;
        e.Pointer.Capture(null);
        e.Handled = true;
    }

    // ── Ручка ширины панели деталей ─────────────────────────────────────────────
    // Тянем за левый край панели: движение вправо сужает панель (она справа).
    private bool   _historyDetailGripDragging;
    private double _historyDetailGripStartX;
    private double _historyDetailGripStartWidth;

    private void OnHistoryDetailGripPressed(object? sender, PointerPressedEventArgs e)
    {
        _historyDetailGripDragging   = true;
        _historyDetailGripStartX     = e.GetPosition(this).X;
        _historyDetailGripStartWidth = _historyDetailWidth;
        e.Pointer.Capture((IInputElement?)sender);
        e.Handled = true;
    }

    private void OnHistoryDetailGripMoved(object? sender, PointerEventArgs e)
    {
        if (!_historyDetailGripDragging) return;

        var dx = e.GetPosition(this).X - _historyDetailGripStartX;
        _historyDetailWidth = Math.Clamp(_historyDetailGripStartWidth - dx,
                                         HistoryDetailWidthMin, HistoryDetailWidthMax);
        HistoryDetailPanel.Width = _historyDetailWidth;
    }

    private void OnHistoryDetailGripReleased(object? sender, PointerReleasedEventArgs e)
    {
        _historyDetailGripDragging = false;
        e.Pointer.Capture(null);
        e.Handled = true;
    }

    // ── Ручка ⋮: общая ширина колонки названий у всех карточек ───────────────────
    private bool   _historyColGripDragging;
    private double _historyColGripStartX;
    private double _historyColGripStartWidth;

    private void OnHistoryColGripPressed(object? sender, PointerPressedEventArgs e)
    {
        _historyColGripDragging   = true;
        _historyColGripStartX     = e.GetPosition(this).X;
        _historyColGripStartWidth = _historyNameColWidth;
        e.Pointer.Capture((IInputElement?)sender);
        e.Handled = true;
    }

    private void OnHistoryColGripMoved(object? sender, PointerEventArgs e)
    {
        if (!_historyColGripDragging) return;

        var dx  = e.GetPosition(this).X - _historyColGripStartX;
        // Названиям не даем съесть все значение: значению оставляем минимум места.
        var max = Math.Max(HistoryNameColMin, HistoryDetailCardsHost.Bounds.Width - 160);
        _historyNameColWidth = Math.Clamp(_historyColGripStartWidth + dx, HistoryNameColMin, max);
        foreach (var col in _historyNameCols)
            col.Width = new GridLength(_historyNameColWidth, GridUnitType.Pixel);
    }

    private void OnHistoryColGripReleased(object? sender, PointerReleasedEventArgs e)
    {
        _historyColGripDragging = false;
        e.Pointer.Capture(null);
        e.Handled = true;
    }
}
