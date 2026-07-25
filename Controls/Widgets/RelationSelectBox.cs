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
using SmartBase.Database.Repositories;
using SmartBase.Utils;

namespace SmartBase.Controls.Widgets;

/// <summary>
/// Редактор поля-связи (тип «relation») в стиле «Мультисписка»: показывает названия связанных
/// записей ТЕКСТОМ через выбранный разделитель (пусто → серое «связи нет»), фон прозрачный (как у
/// ячейки таблицы), без рамки. В таблице этот контрол — только в РЕЖИМЕ РЕДАКТИРОВАНИЯ ячейки
/// (<paramref name="editing"/> = true, как чеклист «Мультисписка»): справа появляется иконка-связь
/// (та же, что в шапке колонки), по клику по НЕЙ открывается выпадающий список выбора (поиск +
/// виртуализированный чеклист записей целевой коллекции; single — выбор одной сбрасывает прочие).
/// У каждого пункта — стрелка «→» перехода к записи (<see cref="Navigate"/>). В режиме показа
/// (editing = false) — только текст, без иконки. На панели записи всегда editing = true.
///
/// Данные тянет сам по <see cref="RecordId"/> (GetLinks) — при пере-DataContext таблицы RecordId
/// переустанавливается; перечитывает связи и при повторном показе (AttachedToVisualTree — после
/// выхода из режима правки). Правки пишет сразу (SetLinks) и поднимает <see cref="Changed"/>.
/// </summary>
public sealed class RelationSelectBox : Panel
{
    private readonly ConstructorRepository _repo;
    private readonly int  _fieldId;
    private readonly RelationConfig _cfg;
    private readonly string _separator;
    private readonly bool _editing;

    private readonly List<int>                     _selected = [];
    private readonly Dictionary<int, string>       _titles   = new();
    private readonly AvaloniaList<RelationOptionVm> _shown    = [];
    private readonly TextBlock _valueText;
    private readonly TextBlock _placeholder;
    private readonly Popup?    _popup;
    private TextBox? _search;
    private int _recordId;

    /// <summary>Поднимается после изменения набора связей.</summary>
    public event EventHandler? Changed;
    /// <summary>Переход к связанной записи (её Id) — стрелка «→» у пункта списка.</summary>
    public event EventHandler<int>? Navigate;

    public RelationSelectBox(ConstructorRepository repo, int fieldId, RelationConfig cfg,
                             bool editing = false, double contentLeftPadding = 0)
    {
        _repo      = repo;
        _fieldId   = fieldId;
        _cfg       = cfg;
        _separator = cfg.SeparatorString;
        _editing   = editing;

        _valueText = new TextBlock
        {
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming      = TextTrimming.CharacterEllipsis,
            Foreground        = Brush("BrushPrimaryText", Colors.Black),
        };
        _placeholder = new TextBlock
        {
            Text              = "связи нет",
            VerticalAlignment = VerticalAlignment.Center,
            Foreground        = Brush("BrushSecondaryText", Colors.Gray),
        };

        var left = new Panel();
        left.Children.Add(_placeholder);
        left.Children.Add(_valueText);

        var content = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), VerticalAlignment = VerticalAlignment.Stretch };
        Grid.SetColumn(left, 0);
        content.Children.Add(left);

        Control? iconHit = null;
        if (editing)
        {
            // Иконка-связь справа (как в шапке колонки) — единственный триггер открытия списка.
            // Отступы — как у иконки файла (timeArrow: небольшой слева, шире справа).
            iconHit = new Border
            {
                Background        = Brushes.Transparent,
                Padding           = new Thickness(4, 0, 8, 0),
                VerticalAlignment = VerticalAlignment.Stretch,
                Child             = BuildIcon("IconFieldRelation"),
                Cursor            = new Cursor(StandardCursorType.Hand),
            };
            iconHit.PointerPressed += (_, e) => { e.Handled = true; OpenPopup(); };
            Grid.SetColumn(iconHit, 1);
            content.Children.Add(iconHit);
        }

        // Фон прозрачный (как у ячейки таблицы), без рамки — «как у текста».
        var box = new Border
        {
            Background        = Brushes.Transparent,
            Padding           = new Thickness(contentLeftPadding, 0, editing ? 0 : contentLeftPadding, 0),
            MinHeight         = 30,
            VerticalAlignment = VerticalAlignment.Stretch,
            Child             = content,
        };
        Children.Add(box);

        if (editing)
        {
            _popup = new Popup
            {
                PlacementTarget       = iconHit,
                Placement             = PlacementMode.BottomEdgeAlignedRight,
                IsLightDismissEnabled = true,
                Child                 = WindowZoom.WrapPopupContent(BuildChecklist()),
            };
            Children.Add(_popup);
        }

        // Повторный показ (после выхода из правки ячейки) — перечитать связи, чтобы текст был свежим.
        AttachedToVisualTree += (_, _) => { if (_recordId > 0) ReloadSelected(); };

        RefreshDisplay();
    }

    /// <summary>Запись-источник. Смена → перечитать связи и перерисовать показ.</summary>
    public int RecordId
    {
        get => _recordId;
        set { if (_recordId == value) return; _recordId = value; ReloadSelected(); }
    }

    /// <summary>Перечитать связи из БД (после внешних изменений).</summary>
    public void Reload() => ReloadSelected();

    private void ReloadSelected()
    {
        _selected.Clear();
        if (_recordId > 0)
            foreach (var (id, title) in _repo.GetLinks(_fieldId, _recordId))
            {
                _selected.Add(id);
                _titles[id] = title;
            }
        RefreshDisplay();
        if (_popup is { IsOpen: true }) RefreshList(_search?.Text);
    }

    private void OpenPopup()
    {
        if (_recordId <= 0 || _popup is null) return;
        RefreshList("");
        _popup.IsOpen = true;
        _search?.Focus();
    }

    // Показ закрытого вида — названия через разделитель поля (как read-ячейка мультисписка).
    private void RefreshDisplay()
    {
        _valueText.Text        = string.Join(_separator, _selected.Select(id => _titles.GetValueOrDefault(id, "")));
        _valueText.IsVisible   = _selected.Count > 0;
        _placeholder.IsVisible = _selected.Count == 0;
    }

    // Иконка из реестра (Viewbox 24×24 + Path обводкой) — как «календарь» у поля даты / «скрепка»
    // у файла. Используется для иконки-связи в ячейке и «карандаша» перехода в списке.
    private static Control BuildIcon(string key)
    {
        var geo    = Application.Current!.TryFindResource(key, out var g) && g is Geometry geom ? geom : null;
        var size   = Res("IconSizeSmall", 16.0);
        var thick  = Res("IconStrokeSmall", 1.8);
        var stroke = Brush("BrushSecondaryText", Colors.Gray);

        var canvas = new Canvas { Width = 24, Height = 24 };
        canvas.Children.Add(new Avalonia.Controls.Shapes.Path
        {
            Data            = geo,
            Stroke          = stroke,
            StrokeThickness = thick,
            StrokeLineCap   = PenLineCap.Round,
            StrokeJoin      = PenLineJoin.Round,
        });
        return new Viewbox { Width = size, Height = size, Child = canvas, VerticalAlignment = VerticalAlignment.Center };
    }

    // ── Выпадающий чеклист (как у MultiSelectBox, источник — записи целевой коллекции из БД) ──

    private Control BuildChecklist()
    {
        var fontBase = Res("FontSizeBase", 12.0);

        var valuesList = new ListBox
        {
            Background      = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Padding         = new Thickness(0),
            ItemsSource     = _shown,
            ItemTemplate    = BuildItemTemplate(fontBase),
        };
        valuesList.Styles.Add(new Style(x => x.OfType<ListBoxItem>())
        {
            Setters = { new Setter(ListBoxItem.PaddingProperty, new Thickness(0, 2)),
                        new Setter(ListBoxItem.MinHeightProperty, 0.0) },
        });
        void FlatRow(params string[] pseudo) =>
            valuesList.Styles.Add(new Style(x =>
            {
                Avalonia.Styling.Selector s = x.OfType<ListBoxItem>();
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
            Watermark                = "Поиск по названию…",
            Height                   = 30,
            FontSize                 = fontBase,
            Background               = Brush("BrushBg", Colors.White),
            CornerRadius             = Radius("RadiusBase", 6),
            VerticalContentAlignment = VerticalAlignment.Center,
            Margin                   = new Thickness(0, 0, 0, 4),
        };
        _search.TextChanged += (_, _) => RefreshList(_search.Text);

        var valuesCard = new Border
        {
            Background      = Brush("BrushBg", Colors.White),
            BorderBrush     = Brush("BrushSeparator", Colors.LightGray),
            BorderThickness = new Thickness(1),
            CornerRadius    = Radius("RadiusBase", 6),
            Padding         = new Thickness(8, 6),
            Height          = 232,
            Child           = valuesList,
        };

        var stack = new StackPanel { Spacing = 2, MinWidth = 260 };
        stack.Children.Add(_search);
        stack.Children.Add(valuesCard);

        return new Border
        {
            Background      = Brush("BrushSecondaryBg", Colors.WhiteSmoke),
            BorderBrush     = Brush("BrushSeparator", Colors.LightGray),
            BorderThickness = new Thickness(1),
            CornerRadius    = Radius("RadiusMedium", 8),
            Padding         = new Thickness(12),
            BoxShadow       = BoxShadows.Parse("0 4 16 0 #22000000"),
            Child           = stack,
        };
    }

    // Строка: галочка (выбор) + название + стрелка «→» (переход к записи).
    private IDataTemplate BuildItemTemplate(double fontBase) =>
        new FuncDataTemplate<RelationOptionVm>((_, _) =>
        {
            var name = new TextBlock { TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
            name.Bind(TextBlock.TextProperty, new Binding(nameof(RelationOptionVm.Title)));

            var cb = new CheckBox
            {
                Content                    = name,
                FontSize                   = fontBase,
                HorizontalAlignment        = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Left,
                Margin                     = new Thickness(0),
                Cursor                     = new Cursor(StandardCursorType.Hand),
            };
            cb.Bind(ToggleButton.IsCheckedProperty, new Binding(nameof(RelationOptionVm.Checked)) { Mode = BindingMode.TwoWay });

            // «Карандаш» — перейти к связанной записи (иконка из реестра, вместо стрелки).
            var editHit = new Border
            {
                Background = Brushes.Transparent,
                Padding    = new Thickness(6, 2),
                Child      = BuildIcon("IconPencil"),
                Cursor     = new Cursor(StandardCursorType.Hand),
            };
            editHit.PointerPressed += (s, e) =>
            {
                e.Handled = true;
                if ((s as Control)?.DataContext is RelationOptionVm vm)
                {
                    if (_popup is not null) _popup.IsOpen = false;
                    Navigate?.Invoke(this, vm.Id);
                }
            };

            var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
            Grid.SetColumn(cb, 0);
            Grid.SetColumn(editHit, 1);
            grid.Children.Add(cb);
            grid.Children.Add(editHit);
            return grid;
        });

    private void RefreshList(string? query)
    {
        var q       = query?.Trim() ?? "";
        var results = _repo.SearchRecordTitles(_cfg.TargetCollectionId, q, 200, _recordId);
        foreach (var (id, title) in results) _titles[id] = title;

        var list = new List<RelationOptionVm>();
        if (q.Length == 0)
        {
            foreach (var id in _selected)
                list.Add(new RelationOptionVm(id, _titles.GetValueOrDefault(id, ""), this));
            foreach (var (id, title) in results)
                if (!_selected.Contains(id)) list.Add(new RelationOptionVm(id, title, this));
        }
        else
        {
            foreach (var (id, title) in results) list.Add(new RelationOptionVm(id, title, this));
        }
        _shown.Clear();
        _shown.AddRange(list);
    }

    private void Toggle(int id, bool on)
    {
        if (_cfg.Single)
        {
            _selected.Clear();
            if (on) _selected.Add(id);
            foreach (var it in _shown) it.Refresh();
        }
        else if (on)
        {
            if (!_selected.Contains(id)) _selected.Add(id);
        }
        else
        {
            _selected.Remove(id);
        }

        if (_recordId > 0) _repo.SetLinks(_fieldId, _recordId, _selected);
        RefreshDisplay();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private static IBrush Brush(string key, Color fallback) =>
        Application.Current!.TryFindResource(key, out var r) && r is IBrush b ? b : new SolidColorBrush(fallback);

    private static CornerRadius Radius(string key, double fallback) =>
        Application.Current!.TryFindResource(key, out var r) && r is CornerRadius c ? c : new CornerRadius(fallback);

    private static double Res(string key, double fallback) =>
        Application.Current!.TryFindResource(key, out var r) && r is double d ? d : fallback;

    private sealed class RelationOptionVm(int id, string title, RelationSelectBox owner) : INotifyPropertyChanged
    {
        public int    Id    => id;
        public string Title => title;

        public bool Checked
        {
            get => owner._selected.Contains(id);
            set
            {
                owner.Toggle(id, value);
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Checked)));
            }
        }

        public void Refresh() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Checked)));

        public event PropertyChangedEventHandler? PropertyChanged;
    }
}
