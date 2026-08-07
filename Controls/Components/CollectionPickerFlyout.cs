using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using SmartBase.Utils;
using SmartBase.Controls.Behaviors;

namespace SmartBase.Controls.Components;

/// <summary>
/// Выпадающий выбор коллекций для экспорта: поиск + чекбоксы + «(Выделить всё)» —
/// по образцу панели фильтра таблицы (TableView.BuildFilterPanel: плашка filterFlyout,
/// зум через WindowZoom.WrapPopupContent, белые карточки на серой подложке). Коллекции
/// группируются заголовками БД (заголовки видны только при нескольких базах). Семантика
/// как у фильтра: правки копятся в рабочей копии и применяются только по «Готово»;
/// закрытие мимо кнопки — отмена. Список маленький (сотни коллекций), поэтому строится
/// императивно без виртуализации — рециклинг шаблонов со смешанными типами строк не нужен.
/// </summary>
public sealed class CollectionPickerFlyout
{
    /// <summary>Строка выбора: коллекция + имена для показа (DbName — подпись группы).</summary>
    public sealed record Item(int DbId, int CollectionId, string Name, string DbName);

    private readonly IReadOnlyList<Item> _items;
    private readonly HashSet<(int Db, int Col)> _selected;

    /// <summary>По кнопке «Готово»: рабочая копия уже перенесена в набор, переданный в конструктор.</summary>
    public event EventHandler? Done;

    public CollectionPickerFlyout(IReadOnlyList<Item> items, HashSet<(int Db, int Col)> selected)
    {
        _items    = items;
        _selected = selected;
    }

    public void ShowAt(Control anchor)
    {
        var flyout = new Flyout { Placement = PlacementMode.BottomEdgeAlignedRight };
        flyout.FlyoutPresenterClasses.Add("filterFlyout");
        // Флайаут рендерится в попап-окне вне зума приложения — контент оборачивается
        // в зум-трансформ (плашка-презентер остаётся вне зума), как у фильтра таблицы.
        flyout.Content = WindowZoom.WrapPopupContent(BuildPanel(anchor, () => flyout.Hide()));
        flyout.ShowAt(anchor);
    }

    private Control BuildPanel(Control host, Action close)
    {
        var secondary = (IBrush)host.FindResource("BrushSecondaryText")!;
        var separator = (IBrush)host.FindResource("BrushSeparator")!;
        var fontBase  = (double)host.FindResource("FontSizeBase")!;
        var radius    = (Avalonia.CornerRadius)host.FindResource("RadiusBase")!;

        // Правки копятся в рабочей копии; в _selected уходят только по «Готово».
        var working = new HashSet<(int Db, int Col)>(_selected);
        var multiDb = _items.Select(i => i.DbId).Distinct().Count() > 1;

        var panel = new StackPanel
        {
            Width   = 280,
            Margin  = new Avalonia.Thickness(12),
            Spacing = 2,
            Cursor  = new Cursor(StandardCursorType.Arrow),
        };

        // ── Поиск по имени коллекции и базы ──
        var search = new TextBox
        {
            Watermark    = "Поиск коллекций...",
            Height       = 30,
            CornerRadius = radius,
            Background   = Brushes.White,   // белая карточка на серой подложке (как у фильтра)
            Margin       = new Avalonia.Thickness(0, 0, 0, 4),
            VerticalContentAlignment = VerticalAlignment.Center,
        };
        panel.Children.Add(search);

        var syncingSelectAll = false;
        var selectAll = new CheckBox
        {
            Content  = "(Выделить всё)",
            FontSize = fontBase,
            Cursor   = new Cursor(StandardCursorType.Hand),
        };

        var list = new StackPanel { Spacing = 4 };
        var scroll = new ScrollViewer
        {
            Height  = 220,
            Content = list,
        };

        var valuesBox = new StackPanel { Spacing = 4 };
        valuesBox.Children.Add(selectAll);
        valuesBox.Children.Add(scroll);

        var valuesHost = new Border
        {
            Background   = Brushes.White,
            BorderBrush  = separator,
            CornerRadius = radius,
            // Справа 0: полоса прокрутки списка прилегает к рамке карточки без белого зазора.
            // ClipToBounds — квадратные углы полосы срезаются скруглением карточки.
            Padding      = new Avalonia.Thickness(8, 6, 0, 6),
            ClipToBounds = true,
            Child        = valuesBox,
        };
        valuesHost.Bind(Border.BorderThicknessProperty, valuesHost.GetResourceObservable("BorderLine"));
        panel.Children.Add(valuesHost);

        List<Item> Matched()
        {
            var q = search.Text?.Trim() ?? "";
            return q.Length == 0
                ? _items.ToList()
                : _items.Where(i => i.Name.Contains(q, StringComparison.OrdinalIgnoreCase)
                                    || i.DbName.Contains(q, StringComparison.OrdinalIgnoreCase)).ToList();
        }

        void SyncSelectAll()
        {
            syncingSelectAll = true;
            var matched = Matched();
            selectAll.IsChecked = matched.Count > 0 && matched.All(i => working.Contains((i.DbId, i.CollectionId)));
            syncingSelectAll = false;
        }

        void RebuildList()
        {
            list.Children.Clear();
            int? lastDb = null;
            foreach (var item in Matched())
            {
                if (multiDb && item.DbId != lastDb)
                {
                    lastDb = item.DbId;
                    list.Children.Add(new TextBlock
                    {
                        Text         = item.DbName,
                        Foreground   = secondary,
                        FontSize     = fontBase,
                        Margin       = new Avalonia.Thickness(0, list.Children.Count == 0 ? 0 : 4, 0, 0),
                        TextTrimming = TextTrimming.CharacterEllipsis,
                    });
                }

                var key  = (item.DbId, item.CollectionId);
                var name = new TextBlock
                {
                    Text              = item.Name,
                    TextTrimming      = TextTrimming.CharacterEllipsis,
                    VerticalAlignment = VerticalAlignment.Center,
                };
                var cb = new CheckBox
                {
                    Content   = name,
                    FontSize  = fontBase,
                    IsChecked = working.Contains(key),
                    Margin    = new Avalonia.Thickness(multiDb ? 8 : 0, 0, 0, 0),
                    Cursor    = new Cursor(StandardCursorType.Hand),
                    HorizontalAlignment        = HorizontalAlignment.Stretch,
                    HorizontalContentAlignment = HorizontalAlignment.Left,
                };
                cb.IsCheckedChanged += (_, _) =>
                {
                    if (cb.IsChecked == true) working.Add(key);
                    else                      working.Remove(key);
                    SyncSelectAll();
                };
                list.Children.Add(cb);
            }

            if (list.Children.Count == 0)
                list.Children.Add(new TextBlock
                {
                    Text                = "Ничего не найдено",
                    Foreground          = secondary,
                    FontSize            = fontBase,
                    Margin              = new Avalonia.Thickness(0, 8, 0, 0),
                    HorizontalAlignment = HorizontalAlignment.Center,
                });
        }

        search.TextChanged += (_, _) => { RebuildList(); SyncSelectAll(); };
        selectAll.IsCheckedChanged += (_, _) =>
        {
            if (syncingSelectAll) return;
            // «Выделить всё» действует на видимое (после поиска) подмножество.
            var on = selectAll.IsChecked == true;
            foreach (var i in Matched())
            {
                if (on) working.Add((i.DbId, i.CollectionId));
                else    working.Remove((i.DbId, i.CollectionId));
            }
            RebuildList();
        };

        RebuildList();
        SyncSelectAll();

        panel.Children.Add(MakeSeparator(separator));

        var okBtn = new Button
        {
            Content             = "Готово",
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin              = new Avalonia.Thickness(0, 4, 0, 0),
        };
        okBtn.Classes.Add("dialog");
        okBtn.Click += (_, _) =>
        {
            _selected.Clear();
            _selected.UnionWith(working);
            Done?.Invoke(this, EventArgs.Empty);
            close();
        };
        panel.Children.Add(okBtn);

        return panel;
    }

    // Разделитель в попапе: контент обёрнут в зум-трансформ → обычные токены линий;
    // Border пустой → Aliased защёлкивает линию в целый пиксель (стандарт theming.md).
    private static Border MakeSeparator(IBrush brush)
    {
        var sep = new Border
        {
            Background = brush,
            Margin     = new Avalonia.Thickness(0, 6),
        };
        sep.Bind(Avalonia.Layout.Layoutable.HeightProperty, sep.GetResourceObservable("SeparatorThickness"));
        sep.Bind(Avalonia.Layout.Layoutable.UseLayoutRoundingProperty, sep.GetResourceObservable("LineUseLayoutRounding"));
        Avalonia.Media.RenderOptions.SetEdgeMode(sep, Avalonia.Media.EdgeMode.Aliased);
        return sep;
    }
}
