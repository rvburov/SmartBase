using System.Collections.Concurrent;
using Avalonia.Media;
using SmartBase.Core.Models;

namespace SmartBase.Utils;

/// <summary>
/// Превращение hex-цвета из <see cref="AppPalette"/> в кисти для отрисовки.
///
/// Две задачи. Первая — общая арифметика «пастель/насыщенный»: насыщенный
/// <c>#FF3B30</c> нельзя лить в фон строки, под ним не читается текст, поэтому фон
/// смешивается с белым, а текст затемняется. Раньше обе формулы были скопированы в
/// пять файлов календаря — здесь их единственный экземпляр.
///
/// Вторая — <b>кеш кистей</b>. Виртуализованная таблица перебирает ячейки на каждой
/// прокрутке; <c>new SolidColorBrush(...)</c> на ячейку — мусор в куче и лишние
/// перерисовки. Кисти неизменяемы и переиспользуются между строками.
/// </summary>
public static class ColorTools
{
    // Насыщенность фоновой заливки. Значения — те же, что исторически стоят в календаре:
    // 40 — спокойный фон, 90 — заметный.
    public const byte FillAlpha       = 40;
    public const byte FillAlphaStrong = 90;

    private static readonly ConcurrentDictionary<string, IBrush> FillCache   = new();
    private static readonly ConcurrentDictionary<string, IBrush> StrongCache = new();
    private static readonly ConcurrentDictionary<string, IBrush> TextCache   = new();
    private static readonly ConcurrentDictionary<string, IBrush> SolidCache  = new();

    /// <summary>
    /// Приглушённая заливка под текст. ПОЛУПРОЗРАЧНАЯ намеренно: в таблице она ложится
    /// поверх подсветки наведения и выделения строки/колонки, и прозрачность даёт
    /// естественное смешение вместо «цвет закрыл выделение». На белом фоне выглядит так же,
    /// как смешивание с белым (BlendWithWhite с тем же alpha).
    /// </summary>
    public static IBrush? Fill(string? hex) =>
        Cached(FillCache, hex, c => new SolidColorBrush(Color.FromArgb(FillAlpha, c.R, c.G, c.B)));

    /// Заметная заливка — тот же принцип, выше насыщенность.
    public static IBrush? FillStrong(string? hex) =>
        Cached(StrongCache, hex, c => new SolidColorBrush(Color.FromArgb(FillAlphaStrong, c.R, c.G, c.B)));

    /// Цвет текста: затемнённый исходный — на светлой заливке читается, в отличие от самого цвета.
    public static IBrush? Text(string? hex) =>
        Cached(TextCache, hex, c => new SolidColorBrush(Darken(c)));

    /// Цвет как есть — для полосок, точек и чипов, где нужна полная насыщенность.
    public static IBrush? Solid(string? hex) =>
        Cached(SolidCache, hex, c => new SolidColorBrush(c));

    /// Затемнение (по умолчанию до 60 % яркости) — насыщенный цвет для текста и обводок.
    public static Color Darken(Color c, double f = 0.6)
        => Color.FromArgb(c.A, (byte)(c.R * f), (byte)(c.G * f), (byte)(c.B * f));

    /// Смешивание с белым фоном: прозрачность «запекается» в цвет, поэтому слои под
    /// плашкой (линии сетки, чередование) сквозь неё не просвечивают.
    public static Color BlendWithWhite(Color c, byte alpha)
    {
        var a = alpha / 255.0;
        return Color.FromRgb(
            (byte)(c.R * a + 255 * (1 - a)),
            (byte)(c.G * a + 255 * (1 - a)),
            (byte)(c.B * a + 255 * (1 - a)));
    }

    /// Разбор hex с защитой от мусора в БД: неразбираемое значение = «нет цвета»,
    /// а не исключение посреди отрисовки строки.
    public static Color? Parse(string? hex) =>
        AppPalette.IsHex(hex) && Color.TryParse(hex, out var c) ? c : null;

    private static IBrush? Cached(ConcurrentDictionary<string, IBrush> cache, string? hex, Func<Color, IBrush> make)
    {
        if (!AppPalette.IsHex(hex)) return null;
        return cache.GetOrAdd(hex!, key => Color.TryParse(key, out var c) ? make(c) : Brushes.Transparent);
    }
}
