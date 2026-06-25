using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using DataBase.Extensions;

namespace DataBase.Controls.Components;

/// <summary>
/// Сайдбар раздела «Расширения» (по стилю сайдбара настроек): заголовок + список
/// расширений. Строки строятся из <see cref="ExtensionRegistry"/>, выбор подсвечивается
/// классом <c>selected</c> и поднимается событием <see cref="CategorySelected"/> (Id расширения).
/// </summary>
public partial class ExtensionsSidebar : UserControl
{
    // Поднимается при выборе расширения. Аргумент — его Id.
    public event EventHandler<string>? CategorySelected;

    // lucide blocks (тот же значок, что и на кнопке ActivityBar). Stroke задаётся стилями.
    private const string BlocksIcon =
        "M10 22V7a1 1 0 0 0-1-1H4a2 2 0 0 0-2 2v12a2 2 0 0 0 2 2h12a2 2 0 0 0 2-2v-5a1 1 0 0 0-1-1H2 " +
        "M15 2 H21 A1 1 0 0 1 22 3 V9 A1 1 0 0 1 21 10 H15 A1 1 0 0 1 14 9 V3 A1 1 0 0 1 15 2 Z";

    private readonly List<Border> _rows = [];

    public ExtensionsSidebar()
    {
        InitializeComponent();
        BuildRows();
    }

    // Текущее выбранное расширение (по умолчанию — первое из реестра).
    public string SelectedKey { get; private set; } =
        ExtensionRegistry.All.FirstOrDefault()?.Id ?? "";

    /// <summary>Программно выбрать расширение: подсветить строку и уведомить подписчиков.</summary>
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

    private void BuildRows()
    {
        var rowHeight = TryDouble("RowStepStandard", 37);
        var fontBase  = TryDouble("FontSizeBase", 14);

        foreach (var ext in ExtensionRegistry.All)
        {
            // Background/Cursor НЕ задаём в коде — иначе локальное значение перебьёт
            // стили hover/selected; цвет иконки/текста тоже отдаём стилям.
            var row = new Border
            {
                Tag     = ext.Id,
                Height  = rowHeight,
                Padding = new Thickness(10, 0, 0, 0),
            };
            row.Classes.Add("navrow");
            if (ext.Id == SelectedKey) row.Classes.Add("selected");
            row.Tapped += OnRowTapped;

            var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*") };

            var box = new Viewbox
            {
                Width             = 18,
                Height            = 18,
                Margin            = new Thickness(2, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
            };
            var canvas = new Canvas { Width = 24, Height = 24 };
            canvas.Children.Add(new Avalonia.Controls.Shapes.Path
            {
                Data            = Geometry.Parse(BlocksIcon),
                StrokeThickness = 1.6,
                StrokeLineCap   = PenLineCap.Round,
                StrokeJoin      = PenLineJoin.Round,
            });
            box.Child = canvas;
            Grid.SetColumn(box, 0);

            var label = new TextBlock
            {
                Text              = ext.Name,
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

    private void OnRowTapped(object? sender, TappedEventArgs e)
    {
        if (sender is Border { Tag: string key })
            Select(key);
    }

    private static double TryDouble(string key, double fallback) =>
        Application.Current?.TryFindResource(key, out var v) == true && v is double d ? d : fallback;
}
