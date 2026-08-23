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
using SmartBase.Core.Models;
using SmartBase.Utils;
using SmartBase.Controls.Behaviors;

namespace SmartBase.Controls.Components;

/// <summary>
/// Редактор значения «Мультисписок» (`multiselect`): выбранные опции показываются
/// текстом через запятую (как в ячейке таблицы), клик открывает выпадающий чеклист
/// (CheckBox на каждую опцию — можно отметить несколько). Значение — JSON-массив
/// (<see cref="MultiSelectValue"/>). Один и тот же контрол используется в ячейке таблицы
/// (edit) и в панели записи; <paramref name="flat"/> убирает собственную рамку (ее дает
/// карточка панели записи).
///
/// Чеклист — виртуализирующий ListBox (один-в-один с дропдауном фильтра таблицы): рисуются
/// только видимые строки, поэтому список держит хоть тысячи опций без задержки открытия и
/// поиска. Состояние «отмечено» живет в модели (<see cref="OptionVm.Checked"/> → общий
/// <c>_selected</c>), а не на самом чекбоксе — при виртуализации контейнеры строк
/// переиспользуются, и state на контроле бы терялся.
/// </summary>
public sealed class MultiSelectBox : Panel
{
    private readonly List<string> _options;
    private readonly HashSet<string> _selected;
    private readonly string _separator;
    private readonly List<OptionVm> _items;
    private readonly AvaloniaList<OptionVm> _shown = [];   // отфильтрованный источник для ListBox
    private readonly TextBlock _valueText;
    private readonly TextBlock _placeholder;
    private readonly Popup _popup;
    private TextBox? _search;

    /// <summary>Поднимается при изменении набора выбранных опций (после чтения <see cref="Storage"/>).</summary>
    public event EventHandler? Changed;

    public MultiSelectBox(IReadOnlyList<string> options, string? storage, bool flat = false, string separator = ", ")
    {
        _options   = options.ToList();
        _selected  = [.. MultiSelectValue.Parse(storage)];
        _separator = separator;
        _items     = _options.Select(o => new OptionVm(o, _selected, OnItemToggled)).ToList();
        ResetShown();

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
        foreach (var it in _items) it.Refresh();   // перечитать Checked у реализованных строк
        if (_search is not null) _search.Text = "";
        ResetShown();
        RefreshDisplay();
    }

    // Попап один-в-один с дропдауном фильтра/сортировки таблицы (FlyoutPresenter.filterFlyout):
    // серая подложка BrushSecondaryBg, белые карточки поиска и списка, watermark «Поиск значений...»,
    // шрифт FontSizeBase. Список — виртуализирующий ListBox фиксированной высоты (не «схлопывается»
    // при пустом поиске). Ничего больше (сортировка/«Готово» не нужны — light dismiss).
    private Control BuildChecklist()
    {
        var fontBase = Res("FontSizeBase", 12);

        // Виртуализирующий ListBox (панель по умолчанию — VirtualizingStackPanel): реализуются
        // только видимые строки, поэтому тысячи опций открываются мгновенно.
        var valuesList = new ListBox
        {
            Background      = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            // Воздух вокруг строк — здесь, а не у карточки: Padding списка это Margin
            // презентера ВНУТРИ области прокрутки, поэтому полоса остается у края карточки.
            Padding         = new Thickness(8, 6),
            ItemsSource     = _shown,
            ItemTemplate    = BuildItemTemplate(fontBase),
        };
        valuesList.Styles.Add(new Style(x => x.OfType<ListBoxItem>())
        {
            // Вертикальный отступ строк — как у списка значений фильтра (2px сверху/снизу).
            Setters = { new Setter(ListBoxItem.PaddingProperty, new Thickness(0, 2)),
                        new Setter(ListBoxItem.MinHeightProperty, 0.0) },
        });
        // Строки без подсветки наведения/выбора — взаимодействуем только с чекбоксом,
        // поэтому фон контейнера строки прозрачен во всех состояниях.
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

        _search = new TextBox
        {
            Watermark                = "Поиск значений...",
            Height                   = 30,
            FontSize                 = fontBase,
            Background               = Brush("BrushBg", Colors.White),
            CornerRadius             = Radius("RadiusBase", 6),
            VerticalContentAlignment = VerticalAlignment.Center,
            Margin                   = new Thickness(0, 0, 0, 4),
        };
        _search.TextChanged += (_, _) => Filter(_search.Text);

        // Белая карточка списка — фиксированной высоты (общий стандарт PopupListHeight,
        // как у фильтра колонки): при пустом поиске окно не сужается до тонкой полоски.
        // ListBox сам скроллит контент.
        var valuesCard = new Border
        {
            Background      = Brush("BrushBg", Colors.White),
            BorderBrush     = Brush("BrushSeparator", Colors.LightGray),
            BorderThickness = new Thickness(1),
            CornerRadius    = Radius("RadiusBase", 6),
            // Padding НУЛЕВОЙ: полоса прокрутки прилегает ко всем краям карточки без белого
            // зазора, воздух вокруг строк задан Padding'ом самого списка.
            // ClipToBounds — квадратные углы полосы срезаются скруглением карточки.
            Padding         = new Thickness(0),
            ClipToBounds    = true,
            Height          = Res("PopupListHeight", 232),
            // Пустой список — надпись «Список пуст» / «Ничего не найдено» вместо голой карточки.
            Child           = EmptyListHint.Wrap(valuesList, _search,
                                                 Brush("BrushSecondaryText", Colors.Gray), fontBase),
        };

        // Ширина фиксированная (общий стандарт): блок одинаков независимо от длины опций.
        var content = new StackPanel { Spacing = 2, Width = Res("PopupContentWidth", 240) };
        content.Children.Add(_search);
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

    // Шаблон строки — чекбокс с меткой; привязки следуют за DataContext при переиспользовании
    // контейнеров виртуализированного ListBox.
    private static IDataTemplate BuildItemTemplate(double fontBase) =>
        new FuncDataTemplate<OptionVm>((_, _) =>
        {
            var name = new TextBlock { TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
            name.Bind(TextBlock.TextProperty, new Binding(nameof(OptionVm.Name)));

            var cb = new CheckBox
            {
                Content                    = name,
                FontSize                   = fontBase,
                HorizontalAlignment        = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Left,
                Margin                     = new Thickness(0),
                Cursor                     = new Cursor(StandardCursorType.Hand),
            };
            cb.Bind(ToggleButton.IsCheckedProperty, new Binding(nameof(OptionVm.Checked)) { Mode = BindingMode.TwoWay });
            return cb;
        });

    private void Filter(string? query)
    {
        var q = query?.Trim() ?? "";
        _shown.Clear();
        _shown.AddRange(q.Length == 0
            ? _items
            : _items.Where(i => i.Name.Contains(q, StringComparison.OrdinalIgnoreCase)));
    }

    private void ResetShown()
    {
        _shown.Clear();
        _shown.AddRange(_items);
    }

    // Вызывается моделью строки при клике по чекбоксу (_selected уже обновлен в сеттере Checked).
    private void OnItemToggled()
    {
        RefreshDisplay();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    // Показ выбранного в закрытом виде — текстом через разделитель поля (как read-ячейка таблицы).
    private void RefreshDisplay()
    {
        var selected = _options.Where(_selected.Contains).ToList();
        _valueText.Text        = string.Join(_separator, selected);
        _placeholder.IsVisible = selected.Count == 0;
    }

    private static IBrush Brush(string key, Color fallback) =>
        Application.Current!.TryFindResource(key, out var r) && r is IBrush b ? b : new SolidColorBrush(fallback);

    private static CornerRadius Radius(string key, double fallback) =>
        Application.Current!.TryFindResource(key, out var r) && r is CornerRadius c ? c : new CornerRadius(fallback);

    // Числовой токен из ресурсов (размер шрифта, ширина/высота блока-списка).
    private static double Res(string key, double fallback) =>
        Application.Current!.TryFindResource(key, out var r) && r is double d ? d : fallback;

    // Строка чеклиста: Checked читает/пишет общий _selected; при изменении из UI дергает onChanged
    // (обновление показа + событие Changed). Refresh() лишь уведомляет привязку перечитать getter
    // при внешней смене выбора (SetStorage) — _selected и onChanged не трогает.
    private sealed class OptionVm(string name, HashSet<string> selected, Action onChanged) : INotifyPropertyChanged
    {
        public string Name { get; } = name;

        public bool Checked
        {
            get => selected.Contains(Name);
            set
            {
                if (value) selected.Add(Name); else selected.Remove(Name);
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Checked)));
                onChanged();
            }
        }

        public void Refresh() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Checked)));

        public event PropertyChangedEventHandler? PropertyChanged;
    }
}
