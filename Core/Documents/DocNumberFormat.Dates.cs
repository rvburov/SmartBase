using System.Globalization;
using System.Text;

namespace SmartBase.Core.Documents;

/// <summary>
/// Даты и время ячейки по записи формата: «d mmmm yyyy» — «5 марта 2024», «mmm-yy» — «мар-24»,
/// «[h]:mm» — «36:30».
///
/// Раньше любая дата печаталась жестким «дд.ММ.гггг», а время — «чч:мм», что бы ни стояло в
/// записи: месяц словом, год двумя цифрами и секунды пропадали, а день появлялся там, где запись
/// его прятала («mmm-yy»). Для чтения это полбеды. Хуже, что поиск по содержимому хранит книгу
/// так, как ее показывает панель, и «марта 2024» в нем не нашлось бы вовсе.
///
/// <b>Два счета дней.</b> Книга хранит дату номером дня. Обычно счет идет от 1900 года — с
/// ошибкой, которую табличные редакторы держат ради совместимости: 1900 год у них високосный,
/// 29 февраля 1900 существует (номер 60), а до 1 марта номера на день расходятся с календарем.
/// Иные книги считают от 1904 года (<c>workbookPr date1904</c>), и без учета этого все их даты
/// уехали бы на четыре года назад.
///
/// Названия месяцев и дней недели — языка системы: так их показывает и сам табличный редактор.
/// </summary>
public static partial class DocNumberFormat
{
    /// <summary>Номер последнего дня при счете от 1900 года — 31.12.9999.</summary>
    private const long LastDay1900 = 2_958_465;

    /// <summary>На столько номеров счет от 1904 года отстает от счета от 1900-го.</summary>
    private const long Shift1904 = 1_462;

    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

    private enum Piece { Text, Year, Month, Day, Hour, Minute, Second, Fraction, Noon }

    /// <summary>
    /// Кусок записи даты: что печатать и сколько знаков. <paramref name="Elapsed"/> — «прошло
    /// всего» (<c>[h]</c>, <c>[mm]</c>, <c>[ss]</c>), а не «который час».
    /// </summary>
    private readonly record struct DatePart(Piece Kind, int Width = 0, string Text = "", bool Elapsed = false);

    /// <summary>Разобранная запись даты.</summary>
    /// <param name="Twelve">Часы по двенадцать — в записи есть «AM/PM».</param>
    /// <param name="Fraction">Сколько знаков у долей секунды.</param>
    /// <param name="Genitive">Месяц словом стоит рядом с числом дня: «5 марта», но «март 2024».</param>
    /// <param name="System">Запись просит формат системы: 'D' — длинную дату, 'T' — время.</param>
    private sealed record DateRecipe(DatePart[] Parts, bool Twelve, int Fraction, bool Genitive, char System);

    private static DateRecipe CompileDate(string code)
    {
        // У даты из нескольких частей («dd.mm.yyyy;@») число показывает первая: вторая — для
        // отрицательных, а отрицательных дат не бывает.
        var section = Sections(code)[0];
        var parts   = new List<DatePart>();
        var text    = new StringBuilder();
        var system  = '\0';

        void Add(DatePart part)
        {
            if (text.Length > 0) { parts.Add(new DatePart(Piece.Text, Text: text.ToString())); text.Clear(); }
            parts.Add(part);
        }

        // Сколько раз подряд стоит та же буква: «yyyy», «mmm», «dd».
        int Run(int at)
        {
            var letter = char.ToLowerInvariant(section[at]);
            var end    = at;
            while (end < section.Length && char.ToLowerInvariant(section[end]) == letter) end++;
            return end - at;
        }

        for (var i = 0; i < section.Length; i++)
        {
            var ch = section[i];
            switch (char.ToLowerInvariant(ch))
            {
                case '"':
                    var quote = section.IndexOf('"', i + 1);
                    if (quote < 0) quote = section.Length;
                    text.Append(section, i + 1, quote - i - 1);
                    i = quote;
                    break;

                case '\\':
                    if (i + 1 < section.Length) text.Append(section[++i]);
                    break;

                // «_x» — пробел шириной знака, «*x» — заполнение до края: так же, как у чисел.
                case '_':
                    if (i + 1 < section.Length) i++;
                    text.Append(' ');
                    break;

                case '*':
                    if (i + 1 < section.Length) i++;
                    break;

                case '[':
                    var close = section.IndexOf(']', i + 1);
                    if (close < 0) { i = section.Length; break; }
                    var inner = section[(i + 1)..close];
                    i = close;

                    if (Elapsed(inner) is { } elapsed) { Add(elapsed); break; }

                    // «[$-F800]» и «[$-F400]» — длинная дата и время в формате системы. Прочие
                    // «[$…]» несут знак и язык записи; знак печатается, как у чисел. Цвет и
                    // условие («[Red]», «[<1]») к дате отношения не имеют.
                    if (inner.StartsWith('$'))
                    {
                        var dash = inner.IndexOf('-');
                        text.Append(dash > 0 ? inner[1..dash] : dash < 0 ? inner[1..] : "");
                        if (inner.EndsWith("F800", StringComparison.OrdinalIgnoreCase)) system = 'D';
                        else if (inner.EndsWith("F400", StringComparison.OrdinalIgnoreCase)) system = 'T';
                    }
                    break;

                case 'y':
                case 'e':
                    var years = Run(i);
                    i += years - 1;
                    Add(new DatePart(Piece.Year, char.ToLowerInvariant(ch) == 'e' || years > 2 ? 4 : 2));
                    break;

                case 'd':
                    var days = Run(i);
                    i += days - 1;
                    Add(new DatePart(Piece.Day, Math.Min(days, 4)));
                    break;

                case 'm':
                    var months = Run(i);
                    i += months - 1;
                    Add(new DatePart(Piece.Month, Math.Min(months, 5)));
                    break;

                case 'h':
                    var hours = Run(i);
                    i += hours - 1;
                    Add(new DatePart(Piece.Hour, Math.Min(hours, 2)));
                    break;

                case 's':
                    var seconds = Run(i);
                    i += seconds - 1;
                    Add(new DatePart(Piece.Second, Math.Min(seconds, 2)));
                    break;

                case 'a':
                    var noon = Noon(section, i);
                    if (noon.Length > 0) { Add(new DatePart(Piece.Noon, Text: noon)); i += noon.Length - 1; }
                    else text.Append(ch);
                    break;

                // «ss.00» — доли секунды. Точка без нулей или не после секунд — просто точка.
                case '.':
                    var zeros = 0;
                    while (i + 1 + zeros < section.Length && section[i + 1 + zeros] == '0') zeros++;
                    if (zeros > 0 && parts.Any(p => p.Kind == Piece.Second))
                    {
                        Add(new DatePart(Piece.Fraction, Math.Min(zeros, 3)));
                        i += zeros;
                    }
                    else text.Append(ch);
                    break;

                // «@» — подстановка текста, а у числа текста нет.
                case '@':
                    break;

                default:
                    text.Append(ch);
                    break;
            }
        }
        if (text.Length > 0) parts.Add(new DatePart(Piece.Text, Text: text.ToString()));

        // «m» — это месяц, но сразу после часов или прямо перед секундами — минуты: «h:mm»,
        // «mm:ss». Другого признака в записи нет, и табличный редактор решает так же.
        for (var p = 0; p < parts.Count; p++)
        {
            if (parts[p].Kind != Piece.Month || parts[p].Width > 2) continue;
            if (Beside(parts, p, -1) == Piece.Hour || Beside(parts, p, +1) == Piece.Second)
                parts[p] = parts[p] with { Kind = Piece.Minute };
        }

        return new DateRecipe([.. parts],
                              Twelve:   parts.Any(p => p.Kind == Piece.Noon),
                              Fraction: parts.Where(p => p.Kind == Piece.Fraction).Select(p => p.Width).FirstOrDefault(),
                              Genitive: parts.Any(p => p.Kind == Piece.Day && p.Width <= 2),
                              System:   system);
    }

    // «[h]», «[mm]», «[ss]» — сколько прошло всего; null — в скобке что-то другое.
    private static DatePart? Elapsed(string inner)
    {
        if (inner.Length == 0) return null;

        var letter = char.ToLowerInvariant(inner[0]);
        if (letter is not ('h' or 'm' or 's')) return null;
        foreach (var c in inner)
            if (char.ToLowerInvariant(c) != letter) return null;

        var kind = letter switch { 'h' => Piece.Hour, 'm' => Piece.Minute, _ => Piece.Second };
        return new DatePart(kind, Math.Min(inner.Length, 2), Elapsed: true);
    }

    // «AM/PM» или «A/P» с этого места — как написаны, с их регистром; пусто — не они.
    private static string Noon(string section, int at)
    {
        foreach (var marker in (string[])["AM/PM", "A/P"])
            if (at + marker.Length <= section.Length &&
                string.Compare(section, at, marker, 0, marker.Length, StringComparison.OrdinalIgnoreCase) == 0)
                return section.Substring(at, marker.Length);
        return "";
    }

    // Ближайший кусок-не-текст в сторону step; Text — такого нет.
    private static Piece Beside(List<DatePart> parts, int at, int step)
    {
        for (var i = at + step; i >= 0 && i < parts.Count; i += step)
            if (parts[i].Kind != Piece.Text) return parts[i].Kind;
        return Piece.Text;
    }

    /// <summary>Дата по разобранной записи; null — число не дата (отрицательное или за 9999 годом).</summary>
    private static string? FormatDate(double value, DateRecipe recipe, bool date1904)
    {
        if (double.IsNaN(value) || value < 0) return null;
        if (value >= (date1904 ? LastDay1900 - Shift1904 : LastDay1900) + 1) return null;

        // Время округляется до показанной точности — секунды или ее долей — и только потом
        // делится на часы и минуты: 10:29:59,6 по «h:mm» — это «10:30», как у табличного
        // редактора, а не «10:29».
        var scale  = recipe.Fraction switch { 1 => 10L, 2 => 100L, 3 => 1_000L, _ => 1L };
        var perDay = 86_400L * scale;
        var units  = (long)Math.Round(value * perDay, MidpointRounding.AwayFromZero);

        if (!DayOf(units / perDay, date1904, out var year, out var month, out var day, out var weekday))
            return null;

        var rest   = units % perDay;
        var hour   = (int)(rest / (3_600 * scale));
        var minute = (int)(rest / (60 * scale) % 60);
        var second = (int)(rest / scale % 60);
        var tail   = (int)(rest % scale);

        var culture = CultureInfo.CurrentCulture;
        var names   = culture.DateTimeFormat;

        // Формат системы: длинная дата («5 марта 2024 г.») и время. Дня, которого нет в
        // календаре (ноль и 29.02.1900), у системы не спросишь — он печатается по записи.
        if (recipe.System == 'D' && day > 0 && day <= DateTime.DaysInMonth(year, month))
            return new DateTime(year, month, day).ToString("D", culture);
        if (recipe.System == 'T')
            return new DateTime(1, 1, 1, hour, minute, second).ToString("T", culture);

        var sb = new StringBuilder();
        foreach (var part in recipe.Parts)
        {
            var digits = part.Width == 2 ? "00" : "0";
            switch (part.Kind)
            {
                case Piece.Text:
                    sb.Append(part.Text);
                    break;

                case Piece.Year:
                    sb.Append(part.Width == 2 ? (year % 100).ToString("00", Invariant) : year.ToString(Invariant));
                    break;

                case Piece.Month:
                    sb.Append(part.Width switch
                    {
                        1 => month.ToString(Invariant),
                        2 => month.ToString("00", Invariant),
                        3 => Short(Name(recipe.Genitive ? names.AbbreviatedMonthGenitiveNames
                                                        : names.AbbreviatedMonthNames,
                                        names.AbbreviatedMonthNames, month - 1)),
                        4 => Name(recipe.Genitive ? names.MonthGenitiveNames : names.MonthNames,
                                  names.MonthNames, month - 1),
                        _ => Initial(names.MonthNames[month - 1], culture),
                    });
                    break;

                case Piece.Day:
                    sb.Append(part.Width switch
                    {
                        1 => day.ToString(Invariant),
                        2 => day.ToString("00", Invariant),
                        3 => Short(names.AbbreviatedDayNames[(int)weekday]),
                        _ => names.DayNames[(int)weekday],
                    });
                    break;

                case Piece.Hour:
                    var h = part.Elapsed  ? units / (3_600 * scale)
                          : recipe.Twelve ? (hour % 12 == 0 ? 12 : hour % 12)
                                          : hour;
                    sb.Append(h.ToString(digits, Invariant));
                    break;

                case Piece.Minute:
                    sb.Append((part.Elapsed ? units / (60 * scale) : minute).ToString(digits, Invariant));
                    break;

                case Piece.Second:
                    sb.Append((part.Elapsed ? units / scale : second).ToString(digits, Invariant));
                    break;

                case Piece.Fraction:
                    sb.Append(culture.NumberFormat.NumberDecimalSeparator)
                      .Append(tail.ToString(new string('0', recipe.Fraction), Invariant));
                    break;

                case Piece.Noon:
                    var slash = part.Text.IndexOf('/');
                    sb.Append(hour < 12 ? part.Text[..slash] : part.Text[(slash + 1)..]);
                    break;
            }
        }
        return sb.ToString();
    }

    /// <summary>
    /// Число, месяц, год и день недели по номеру дня книги. При счете от 1900 года — с той же
    /// ошибкой, что у табличного редактора: номер 60 — это 29.02.1900, ноль — «00.01.1900», а
    /// номер 1 — это 1 января 1900-го, тогда как обычный счет дал бы по нему 31 декабря 1899-го:
    /// до 1 марта 1900 года обычный счет отстает на сутки. День недели — по счету табличного
    /// редактора; с 1 марта 1900 года он совпадает с настоящим.
    /// </summary>
    private static bool DayOf(long serial, bool date1904, out int year, out int month, out int day,
                              out DayOfWeek weekday)
    {
        (year, month, day, weekday) = (0, 0, 0, DayOfWeek.Sunday);
        if (serial < 0 || serial > (date1904 ? LastDay1900 - Shift1904 : LastDay1900)) return false;

        if (date1904)
        {
            var date = new DateTime(1904, 1, 1).AddDays(serial);
            (year, month, day, weekday) = (date.Year, date.Month, date.Day, date.DayOfWeek);
            return true;
        }

        weekday = (DayOfWeek)((serial + 6) % 7);
        if (serial == 0)  { (year, month, day) = (1900, 1, 0);  return true; }
        if (serial == 60) { (year, month, day) = (1900, 2, 29); return true; }

        var real = serial < 60 ? new DateTime(1900, 1, 1).AddDays(serial - 1)
                               : new DateTime(1899, 12, 30).AddDays(serial);
        (year, month, day) = (real.Year, real.Month, real.Day);
        return true;
    }

    // Название из списка языка; пустое (падежей у языка нет) — из основного списка.
    private static string Name(string[] names, string[] fallback, int index) =>
        index < names.Length && names[index].Length > 0 ? names[index] : fallback[index];

    // Сокращение без точки на конце: «5-мар-24», а не «5-мар.-24» — так показывает и табличный
    // редактор.
    private static string Short(string name) => name.EndsWith('.') ? name[..^1] : name;

    private static string Initial(string name, CultureInfo culture) =>
        name.Length > 0 ? name[..1].ToUpper(culture) : "";
}
