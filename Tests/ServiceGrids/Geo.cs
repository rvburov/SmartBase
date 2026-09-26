using Avalonia;

namespace SmartBase.Tests.ServiceGrids;

/// <summary>Геометрия таблицы в окне: откуда она начинается и какой ширины колонки. Шапка и шаг
/// строки — токены служебных таблиц (RowHeightStandard 36 плюс линия).</summary>
sealed record Geo(Point Origin, double[] Widths)
{
    public const double Head  = 36;   // высота шапки
    public const double Pitch = 37;   // шаг строки: высота + линия

    public double Left(int c) => Widths.Take(c).Sum();
    public Point RowCenter(int row, double x) => new(Origin.X + x, Origin.Y + Head + Pitch * row + Pitch / 2);
    public Point HeaderAt(int c, double dx)   => new(Origin.X + Left(c) + dx, Origin.Y + Head / 2);
    // Кнопка фильтра 20×20 у правого края заголовка за отступом 6 перед линией.
    public Point Chevron(int c) => new(Origin.X + Left(c) + Widths[c] - 1 - 6 - 10, Origin.Y + Head / 2);
    // Над навбаром, вне таблицы: наведение там ничего не красит.
    public Point Park => new(Origin.X + 300, Origin.Y - 20);
    public int ColumnAt(double dx)
    {
        var acc = 0.0;
        for (var i = 0; i < Widths.Length; i++) { acc += Widths[i]; if (dx < acc) return i; }
        return -1;
    }
}
