using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
// Оформление документа живет в пространстве рисования, и в нем свои Color и Font — они спорят
// с одноименными типами таблиц. Поэтому оно вводится псевдонимом.
using A = DocumentFormat.OpenXml.Drawing;

namespace SmartBase.Core.Documents;

/// <summary>
/// Цвета книги Excel. Цвет в книге почти никогда не лежит цветом: это либо номер в палитре, либо
/// номер в теме оформления, да еще и с осветлением. Развернуть их в настоящий цвет умеет только
/// тот, кто прочитал книгу целиком, — отсюда отдельный разборщик на книгу.
///
/// <b>Палитру книга вправе переопределить своей.</b> Это не редкость и не мелочь: у книги,
/// сохраненной из Numbers, <c>indexed="10"</c> — серый <c>#AAAAAA</c>, а в стандартной палитре
/// под этим номером ярко-красный. Прочитай мы только стандартную — таблица с аккуратными серыми
/// линиями вышла бы в красно-зеленую полоску, и ни одной ошибки при этом не случилось бы.
///
/// Исключений не бросает: книга без темы и без палитры — обычное дело, просто цветов в ней
/// меньше.
/// </summary>
public sealed class DocPalette
{
    /// <summary>«Цвета нет» — в ячейке останется цвет по умолчанию.</summary>
    public const uint None = 0;

    private readonly uint[] _indexed;
    private readonly uint[] _theme;

    private DocPalette(uint[] indexed, uint[] theme)
    {
        _indexed = indexed;
        _theme   = theme;
    }

    /// <summary>Пустая палитра — для книги, у которой нечего читать.</summary>
    public static DocPalette Empty { get; } = new(Standard, []);

    public static DocPalette Read(WorkbookPart book)
    {
        var indexed = Standard;

        // Своя палитра книги. Она перечисляет цвета ПОДРЯД с нулевого номера и может быть
        // короче стандартной — недостающие номера остаются стандартными.
        if (book.WorkbookStylesPart?.Stylesheet?.Colors?.IndexedColors is { } own)
        {
            var list = new uint[Standard.Length];
            Array.Copy(Standard, list, Standard.Length);

            var i = 0;
            foreach (var color in own.Elements<RgbColor>())
            {
                if (i >= list.Length) break;
                if (color.Rgb?.Value is { Length: > 0 } hex) list[i] = FromHex(hex);
                i++;
            }
            indexed = list;
        }

        return new DocPalette(indexed, ThemeColors(book));
    }

    /// <summary>Развернуть цвет ячейки в ARGB. <see cref="None"/> — цвета нет.</summary>
    public uint Resolve(ColorType? color)
    {
        if (color is null) return None;

        uint argb;
        if (color.Rgb?.Value is { Length: > 0 } hex)   argb = FromHex(hex);
        else if (color.Indexed?.Value is { } index)    argb = Indexed((int)index);
        else if (color.Theme?.Value is { } theme)      argb = Theme((int)theme);
        else                                           return None;   // в том числе auto="1"

        if (argb == None) return None;

        var tint = color.Tint?.Value ?? 0;
        return tint == 0 ? argb : Tinted(argb, tint);
    }

    private uint Indexed(int index) =>
        // 64 и 65 — «цвет системы»: текст и фон окна. Своего цвета у них нет, и подставлять
        // черный с белым нельзя — у нас свои кисти, и в темной теме это был бы белый по белому.
        index is 64 or 65 or < 0 || index >= _indexed.Length ? None : _indexed[index];

    private uint Theme(int index) => index >= 0 && index < _theme.Length ? _theme[index] : None;

    // ── Тема оформления ───────────────────────────────────────────────────────

    /// <summary>
    /// Цвета темы по их НОМЕРАМ в книге. Порядок в файле темы и порядок номеров — разные:
    /// в теме идет dk1, lt1, dk2, lt2, а номера ждут lt1, dk1, lt2, dk2. Перепутать их значит
    /// написать белым по белому.
    /// </summary>
    private static uint[] ThemeColors(WorkbookPart book)
    {
        if (book.ThemePart?.Theme?.ThemeElements?.ColorScheme is not { } scheme) return [];

        return
        [
            Scheme(scheme.Light1Color), Scheme(scheme.Dark1Color),
            Scheme(scheme.Light2Color), Scheme(scheme.Dark2Color),
            Scheme(scheme.Accent1Color), Scheme(scheme.Accent2Color),
            Scheme(scheme.Accent3Color), Scheme(scheme.Accent4Color),
            Scheme(scheme.Accent5Color), Scheme(scheme.Accent6Color),
            Scheme(scheme.Hyperlink), Scheme(scheme.FollowedHyperlinkColor),
        ];
    }

    private static uint Scheme(A.Color2Type? color)
    {
        if (color?.RgbColorModelHex?.Val?.Value is { Length: > 0 } hex) return FromHex(hex);
        if (color?.SystemColor?.LastColor?.Value is { Length: > 0 } last) return FromHex(last);
        return None;
    }

    // ── Осветление и затемнение ───────────────────────────────────────────────

    /// <summary>
    /// Оттенок цвета темы: от -1 (в черный) до +1 (в белый). Считается по яркости, как этого
    /// требует формат, — не по каждой составляющей отдельно, иначе цвет уходит в серый.
    /// </summary>
    public static uint Tinted(uint argb, double tint)
    {
        var (a, r, g, b) = (argb >> 24 & 0xFF, argb >> 16 & 0xFF, argb >> 8 & 0xFF, argb & 0xFF);
        var (h, l, s)    = ToHls(r / 255.0, g / 255.0, b / 255.0);

        l = tint < 0 ? l * (1 + tint) : l * (1 - tint) + tint;
        var (nr, ng, nb) = FromHls(h, Math.Clamp(l, 0, 1), s);

        return (a << 24) | ((uint)Math.Round(nr * 255) << 16)
                         | ((uint)Math.Round(ng * 255) << 8)
                         |  (uint)Math.Round(nb * 255);
    }

    private static (double H, double L, double S) ToHls(double r, double g, double b)
    {
        var max = Math.Max(r, Math.Max(g, b));
        var min = Math.Min(r, Math.Min(g, b));
        var l   = (max + min) / 2;

        if (Math.Abs(max - min) < 1e-9) return (0, l, 0);

        var d = max - min;
        var s = l > 0.5 ? d / (2 - max - min) : d / (max + min);
        var h = max == r ? (g - b) / d + (g < b ? 6 : 0)
              : max == g ? (b - r) / d + 2
                         : (r - g) / d + 4;
        return (h / 6, l, s);
    }

    private static (double R, double G, double B) FromHls(double h, double l, double s)
    {
        if (s <= 0) return (l, l, l);

        var q = l < 0.5 ? l * (1 + s) : l + s - l * s;
        var p = 2 * l - q;
        return (Channel(p, q, h + 1.0 / 3), Channel(p, q, h), Channel(p, q, h - 1.0 / 3));
    }

    private static double Channel(double p, double q, double t)
    {
        if (t < 0) t += 1;
        if (t > 1) t -= 1;
        if (t < 1.0 / 6) return p + (q - p) * 6 * t;
        if (t < 1.0 / 2) return q;
        if (t < 2.0 / 3) return p + (q - p) * (2.0 / 3 - t) * 6;
        return p;
    }

    // ── Разбор записи цвета ───────────────────────────────────────────────────

    /// <summary>Цвет записью «FFAABBCC» или «AABBCC»; непрозрачность подразумевается полной.</summary>
    public static uint FromHex(string hex)
    {
        if (hex.Length == 6) hex = "FF" + hex;
        return hex.Length == 8 && uint.TryParse(hex, System.Globalization.NumberStyles.HexNumber,
                                                System.Globalization.CultureInfo.InvariantCulture,
                                                out var value)
            ? value
            : None;
    }

    /// <summary>
    /// Стандартная палитра Excel. Номера 0–7 — старые дубликаты, настоящая палитра начинается
    /// с восьмого; так это и лежит в файлах.
    /// </summary>
    private static uint[] Standard =>
    [
        0xFF000000, 0xFFFFFFFF, 0xFFFF0000, 0xFF00FF00, 0xFF0000FF, 0xFFFFFF00, 0xFFFF00FF, 0xFF00FFFF,
        0xFF000000, 0xFFFFFFFF, 0xFFFF0000, 0xFF00FF00, 0xFF0000FF, 0xFFFFFF00, 0xFFFF00FF, 0xFF00FFFF,
        0xFF800000, 0xFF008000, 0xFF000080, 0xFF808000, 0xFF800080, 0xFF008080, 0xFFC0C0C0, 0xFF808080,
        0xFF9999FF, 0xFF993366, 0xFFFFFFCC, 0xFFCCFFFF, 0xFF660066, 0xFFFF8080, 0xFF0066CC, 0xFFCCCCFF,
        0xFF000080, 0xFFFF00FF, 0xFFFFFF00, 0xFF00FFFF, 0xFF800080, 0xFF800000, 0xFF008080, 0xFF0000FF,
        0xFF00CCFF, 0xFFCCFFFF, 0xFFCCFFCC, 0xFFFFFF99, 0xFF99CCFF, 0xFFFF99CC, 0xFFCC99FF, 0xFFFFCC99,
        0xFF3366FF, 0xFF33CCCC, 0xFF99CC00, 0xFFFFCC00, 0xFFFF9900, 0xFFFF6600, 0xFF666699, 0xFF969696,
        0xFF003366, 0xFF339966, 0xFF003300, 0xFF333300, 0xFF993300, 0xFF993366, 0xFF333399, 0xFF333333,
    ];
}
