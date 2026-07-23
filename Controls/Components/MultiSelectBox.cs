using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using SmartBase.Core.Models;
using SmartBase.Utils;

namespace SmartBase.Controls.Components;

/// <summary>
/// Редактор значения «Мультисписок» (`multiselect`): выбранные опции показываются
/// текстом через запятую (как в ячейке таблицы), клик открывает выпадающий чеклист
/// (CheckBox на каждую опцию — можно отметить несколько). Значение — JSON-массив
/// (<see cref="MultiSelectValue"/>). Один и тот же контрол используется в ячейке таблицы
/// (edit) и в панели записи; <paramref name="flat"/> убирает собственную рамку (её даёт
/// карточка панели записи).
/// </summary>
public sealed class MultiSelectBox : Panel
{
    private readonly List<string> _options;
    private readonly HashSet<string> _selected;
    private readonly TextBlock _valueText;
    private readonly TextBlock _placeholder;
    private readonly Popup _popup;

    /// <summary>Поднимается при изменении набора выбранных опций (после чтения <see cref="Storage"/>).</summary>
    public event EventHandler? Changed;

    public MultiSelectBox(IReadOnlyList<string> options, string? storage, bool flat = false)
    {
        _options  = options.ToList();
        _selected = [.. MultiSelectValue.Parse(storage)];

        _valueText = new TextBlock
        {
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming      = TextTrimming.CharacterEllipsis,
            Foreground        = Brush("BrushPrimaryText", Colors.Black),
        };
        _placeholder = new TextBlock
        {
            Text              = "Выберите…",
            VerticalAlignment = VerticalAlignment.Center,
            Foreground        = Brush("BrushSecondaryText", Colors.Gray),
        };

        var left = new Panel();
        left.Children.Add(_placeholder);
        left.Children.Add(_valueText);

        var caret = new TextBlock
        {
            Text              = "▾",
            Margin            = new Thickness(4, 0, 2, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Foreground        = Brush("BrushSecondaryText", Colors.Gray),
        };

        var content = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(left, 0);
        Grid.SetColumn(caret, 1);
        content.Children.Add(left);
        content.Children.Add(caret);

        var box = new Border
        {
            Background      = Brush(flat ? "BrushBg" : "BrushSecondaryBg", Colors.White),
            BorderBrush     = Brush("BrushSeparator", Colors.LightGray),
            BorderThickness = new Thickness(flat ? 0 : 1),
            CornerRadius    = Radius("RadiusBase", 6),
            Padding         = new Thickness(8, 4),
            MinHeight       = 30,
            Cursor          = new Cursor(StandardCursorType.Hand),
            Child           = content,
        };

        _popup = new Popup
        {
            PlacementTarget       = box,
            Placement             = PlacementMode.BottomEdgeAlignedLeft,
            IsLightDismissEnabled = true,
            // Попап рендерится в отдельном попап-руте вне зума приложения — контент оборачиваем
            // в зум-трансформ (как фильтр-флайаут), иначе текст/поле мельче остального UI.
            Child                 = WindowZoom.WrapPopupContent(BuildChecklist()),
        };
        box.PointerPressed += (_, _) => _popup.IsOpen = true;

        Children.Add(box);
        Children.Add(_popup);

        RefreshDisplay();
    }

    /// <summary>Текущее значение для хранения — JSON-массив выбранных опций (в порядке опций).</summary>
    public string Storage => MultiSelectValue.Serialize(_options.Where(_selected.Contains));

    /// <summary>Задать выбранные значения после создания (RecordDialog строит контрол до загрузки
    /// значений). Обновляет и показ, и чеклист. Событие Changed НЕ поднимается.</summary>
    public void SetStorage(string? storage)
    {
        _selected.Clear();
        foreach (var v in MultiSelectValue.Parse(storage)) _selected.Add(v);
        _popup.Child = WindowZoom.WrapPopupContent(BuildChecklist());
        RefreshDisplay();
    }

    // Попап один-в-один с дропдауном фильтра/сортировки таблицы (FlyoutPresenter.filterFlyout):
    // серая подложка BrushSecondaryBg, белые карточки поиска и списка, watermark «Поиск значений...»,
    // шрифт FontSizeBase. Список — фиксированной высоты (не «схлопывается» при пустом поиске).
    // Ничего больше (сортировка/«Готово» не нужны — закрытие по клику вне попапа, light dismiss).
    private Control BuildChecklist()
    {
        var fontBase = Font("FontSizeBase", 12);

        var rows = new List<(CheckBox Cb, string Opt)>();
        var list = new StackPanel { Spacing = 4 };
        foreach (var opt in _options)
        {
            var cb = new CheckBox
            {
                Content   = opt,
                FontSize  = fontBase,
                IsChecked = _selected.Contains(opt),
                Cursor    = new Cursor(StandardCursorType.Hand),
            };
            cb.IsCheckedChanged += (_, _) =>
            {
                if (cb.IsChecked == true) _selected.Add(opt); else _selected.Remove(opt);
                RefreshDisplay();
                Changed?.Invoke(this, EventArgs.Empty);
            };
            rows.Add((cb, opt));
            list.Children.Add(cb);
        }

        var search = new TextBox
        {
            Watermark                = "Поиск значений...",
            Height                   = 30,
            FontSize                 = fontBase,
            Background               = Brush("BrushBg", Colors.White),
            CornerRadius             = Radius("RadiusBase", 6),
            VerticalContentAlignment = VerticalAlignment.Center,
            Margin                   = new Thickness(0, 0, 0, 4),
        };
        search.TextChanged += (_, _) =>
        {
            var q = search.Text?.Trim() ?? "";
            foreach (var (cb, opt) in rows)
                cb.IsVisible = q.Length == 0 || opt.Contains(q, StringComparison.OrdinalIgnoreCase);
        };

        // Белая карточка списка — фиксированной высоты (как valuesHost фильтра, Height=232):
        // при пустом поиске окно не сужается до тонкой полоски.
        var valuesCard = new Border
        {
            Background      = Brush("BrushBg", Colors.White),
            BorderBrush     = Brush("BrushSeparator", Colors.LightGray),
            BorderThickness = new Thickness(1),
            CornerRadius    = Radius("RadiusBase", 6),
            Padding         = new Thickness(8, 6),
            Height          = 232,
            Child           = new ScrollViewer { Content = list },
        };

        var content = new StackPanel { Spacing = 2, MinWidth = 240 };
        content.Children.Add(search);
        content.Children.Add(valuesCard);

        // Внешняя карточка — серая подложка с рамкой и скруглением (как FlyoutPresenter.filterFlyout).
        return new Border
        {
            Background      = Brush("BrushSecondaryBg", Colors.WhiteSmoke),
            BorderBrush     = Brush("BrushSeparator", Colors.LightGray),
            BorderThickness = new Thickness(1),
            CornerRadius    = Radius("RadiusMedium", 8),
            Padding         = new Thickness(12),
            BoxShadow       = BoxShadows.Parse("0 4 16 0 #22000000"),
            Child           = content,
        };
    }

    // Показ выбранного в закрытом виде — текстом через запятую (как read-ячейка таблицы).
    private void RefreshDisplay()
    {
        var selected = _options.Where(_selected.Contains).ToList();
        _valueText.Text        = string.Join(", ", selected);
        _placeholder.IsVisible = selected.Count == 0;
    }

    private static IBrush Brush(string key, Color fallback) =>
        Application.Current!.TryFindResource(key, out var r) && r is IBrush b ? b : new SolidColorBrush(fallback);

    private static CornerRadius Radius(string key, double fallback) =>
        Application.Current!.TryFindResource(key, out var r) && r is CornerRadius c ? c : new CornerRadius(fallback);

    private static double Font(string key, double fallback) =>
        Application.Current!.TryFindResource(key, out var r) && r is double d ? d : fallback;
}
