using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;

namespace DataBase.Controls.Components;

/// <summary>Пункт списка-сайдбара: ключ + подпись + иконка (ключ x:Key из Icons.axaml).</summary>
public sealed record SidebarListItem(string Id, string Label, string Icon);

/// <summary>
/// Универсальный сайдбар-список (настройки, расширения и т.п.): заголовок сверху + строки.
/// Пункты задаёт родитель через <see cref="SetItems"/> (их источник — SettingsRegistry/
/// ExtensionRegistry — знает он). Выбор подсвечивается классом <c>selected</c> и поднимается
/// событием <see cref="CategorySelected"/> (Id пункта).
/// </summary>
public partial class SidebarList : UserControl
{
    /// <summary>Поднимается при выборе пункта. Аргумент — Id пункта.</summary>
    public event EventHandler<string>? CategorySelected;

    private readonly List<Border> _rows = [];

    public SidebarList() => InitializeComponent();

    /// <summary>Заголовок раздела (шапка списка).</summary>
    public string Title { set => TitleText.Text = value; }

    /// <summary>Текущий выбранный пункт (Id). До задания пунктов — пусто.</summary>
    public string SelectedKey { get; private set; } = "";

    /// <summary>Задать пункты списка. Первый становится выбранным по умолчанию.</summary>
    public void SetItems(IEnumerable<SidebarListItem> items)
    {
        RowsHost.Children.Clear();
        _rows.Clear();

        var rowHeight = TryDouble("RowStepStandard", 37);
        var fontBase  = TryDouble("FontSizeBase", 14);

        var first = true;
        foreach (var item in items)
        {
            if (first) { SelectedKey = item.Id; first = false; }

            // Background/Cursor/цвета НЕ задаём в коде — иначе локальное значение перебьёт
            // стили hover/selected; их отдаём стилям navrow.
            var row = new Border
            {
                Tag     = item.Id,
                Height  = rowHeight,
                Padding = new Thickness(10, 0, 0, 0),
            };
            row.Classes.Add("navrow");
            if (item.Id == SelectedKey) row.Classes.Add("selected");
            row.Tapped += OnRowTapped;

            var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*") };

            var iconSz = this.TryFindResource("IconSizeMedium", out var szR) && szR is double szD ? szD : 18d;
            var box = new Viewbox
            {
                Width             = iconSz,
                Height            = iconSz,
                Margin            = new Thickness(2, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
            };
            var canvas = new Canvas { Width = 24, Height = 24 };
            canvas.Children.Add(new Avalonia.Controls.Shapes.Path
            {
                Data            = this.TryFindResource(item.Icon, out var ico) && ico is Geometry geo ? geo : null,
                StrokeThickness = 1.6,
                StrokeLineCap   = PenLineCap.Round,
                StrokeJoin      = PenLineJoin.Round,
            });
            box.Child = canvas;
            Grid.SetColumn(box, 0);

            var label = new TextBlock
            {
                Text              = item.Label,
                VerticalAlignment = VerticalAlignment.Center,
                Margin            = new Thickness(6, 0, 8, 0),
                FontSize          = fontBase,
                TextTrimming      = TextTrimming.CharacterEllipsis,
            };
            Grid.SetColumn(label, 1);

            grid.Children.Add(box);
            grid.Children.Add(label);
            row.Child = grid;

            RowsHost.Children.Add(row);
            _rows.Add(row);
        }
    }

    /// <summary>Программно выбрать пункт: подсветить строку и уведомить подписчиков.</summary>
    public void Select(string key)
    {
        SelectedKey = key;
        foreach (var row in _rows)
        {
            var isSelected = (row.Tag as string) == key;
            if (isSelected) row.Classes.Add("selected");
            else            row.Classes.Remove("selected");
        }
        CategorySelected?.Invoke(this, key);
    }

    private void OnRowTapped(object? sender, TappedEventArgs e)
    {
        if (sender is Border { Tag: string key })
            Select(key);
    }

    private static double TryDouble(string key, double fallback) =>
        Application.Current?.TryFindResource(key, out var v) == true && v is double d ? d : fallback;
}
