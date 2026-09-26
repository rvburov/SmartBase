using Avalonia;

namespace SmartBase.Controls.DrawnGrid;

/// <summary>
/// Автопрокрутка при протягивании выделения за край области строк — чистая арифметика: указатель
/// внутри области ничего не двигает; снаружи таблица едет в его сторону тем быстрее, чем дальше он
/// увел, а выделение дотягивается до ячейки на краю. У нынешней таблицы автопрокрутки не было
/// (протягивание останавливалось у края экрана); в рисованной сетке она штатная.
/// </summary>
public static class DragScroll
{
    /// <summary>На сколько пикселей за краем скорость растет еще на один шаг.</summary>
    public const double Ramp = 40;

    /// <summary>Наибольшее число шагов за один тик — чтобы далеко уведенная мышь не пролистывала
    /// тысячи строк за секунду.</summary>
    public const int MaxSteps = 8;

    /// <summary>
    /// Шаг автопрокрутки для указателя <paramref name="pointer"/> при области строк <paramref name="body"/>
    /// (координаты поверхности). Возвращает точку, прижатую к области (по ней ищется ячейка-фокус), и
    /// сдвиг прокрутки: по вертикали кратно шагу строки <paramref name="rowStep"/>, по горизонтали —
    /// <paramref name="colStep"/>. Внутри области сдвиг нулевой.
    /// </summary>
    public static (Point Clamped, double Dx, double Dy) Step(Point pointer, Rect body, double rowStep, double colStep = 48)
    {
        if (body.Width <= 0 || body.Height <= 0) return (pointer, 0, 0);

        var right  = body.Right  - 1e-6;
        var bottom = body.Bottom - 1e-6;
        var x = Math.Clamp(pointer.X, body.X, Math.Max(body.X, right));
        var y = Math.Clamp(pointer.Y, body.Y, Math.Max(body.Y, bottom));

        var dx = 0.0;
        var dy = 0.0;
        if (pointer.Y < body.Y)       dy = -Steps(body.Y - pointer.Y) * rowStep;
        else if (pointer.Y > bottom)  dy =  Steps(pointer.Y - bottom) * rowStep;
        if (pointer.X < body.X)       dx = -Steps(body.X - pointer.X) * colStep;
        else if (pointer.X > right)   dx =  Steps(pointer.X - right) * colStep;

        return (new Point(x, y), dx, dy);
    }

    private static int Steps(double distance) => Math.Min(MaxSteps, 1 + (int)(Math.Max(0, distance) / Ramp));
}
