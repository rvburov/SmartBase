using System.Collections.Concurrent;
using System.Globalization;
using System.Text;

namespace SmartBase.Core.Documents;

/// <summary>Значение ячейки, показанное по записи формата: текст и «показывать красным».</summary>
public readonly record struct DocNumber(string Text, bool Red = false);

/// <summary>
/// Запись формата ячейки Excel — то, что превращает 0,15 в «15 %», а 45356 в «05.03.2024».
///
/// Зачем это вообще. В книге лежит число, а как его читать — записано отдельно, в стиле ячейки.
/// Показать «45356» вместо даты или «0,15» вместо процентов значит показать неправду, причем
/// неправду убедительную: число выглядит настоящим значением ячейки, и ошибку в нем не видно.
///
/// <b>Чего здесь нет.</b> Это не полный движок форматов Excel — их правила огромны (склейка по
/// разрядам, дробные записи, заполнение до края, подстановка текста). Разобрано то, что
/// встречается в рабочих книгах: разряды, знаки после запятой, проценты, валюта, свой текст в
/// записи и отдельная запись для отрицательных — вместе с «красным». Остальное печатается
/// числом как есть: панель читают, а не считают в ней.
/// </summary>
public static class DocNumberFormat
{
    /// <summary>
    /// Запись встроенного формата по его номеру. В файле встроенных записей нет — у них только
    /// номера, одинаковые во всех книгах, и подставить их обязаны мы. Пропусти этот список — и
    /// денежная колонка «#,##0.00» покажется голым числом без разрядов, причем молча.
    ///
    /// Денежные номера (5–8, 41–44) в Excel зависят от языка книги; знак валюты по ним не
    /// подставляется — врать о валюте хуже, чем не показать ее.
    /// </summary>
    public static string Builtin(uint id) => id switch
    {
        1  => "0",
        2  => "0.00",
        3  => "#,##0",
        4  => "#,##0.00",
        5 or 37 or 41 => "#,##0_);(#,##0)",
        6 or 38 or 42 => "#,##0_);[Red](#,##0)",
        7 or 39 or 43 => "#,##0.00_);(#,##0.00)",
        8 or 40 or 44 => "#,##0.00_);[Red](#,##0.00)",
        9  => "0%",
        10 => "0.00%",
        11 => "0.00E+00",
        12 => "# ?/?",
        13 => "# ??/??",
        14 => "dd.mm.yyyy",
        15 => "d-mmm-yy",
        16 => "d-mmm",
        17 => "mmm-yy",
        18 or 19 or 20 or 21 => "h:mm",
        22 => "dd.mm.yyyy h:mm",
        45 or 46 or 47 => "h:mm",
        48 => "##0.0E+0",
        49 => "@",
        _  => "",
    };

    /// <summary>
    /// Запись формата про дату или время. Смотрим только на буквы ВНЕ кавычек и ВНЕ скобок: в
    /// записи <c>0.00 "дней"</c> буква «д» относится к подписи, а в <c>[Red]-#,##0</c> буква «d»
    /// вообще из слова «Red» — по ней формат денег однажды стал датой.
    ///
    /// Исключение — скобки из одних часов, минут и секунд: <c>[h]:mm</c> это «прошло времени»,
    /// и это как раз время.
    /// </summary>
    public static bool IsDate(string? format)
    {
        if (string.IsNullOrEmpty(format)) return false;

        var quoted = false;
        for (var i = 0; i < format.Length; i++)
        {
            var ch = format[i];

            if (ch == '"') { quoted = !quoted; continue; }
            if (quoted) continue;
            if (ch == '\\') { i++; continue; }

            if (ch == '[')
            {
                var close = format.IndexOf(']', i + 1);
                if (close < 0) return false;

                var inner = format[(i + 1)..close];
                if (inner.Length > 0 && inner.All(c => c is 'h' or 'H' or 'm' or 'M' or 's' or 'S'))
                    return true;

                i = close;
                continue;
            }

            if (ch is 'y' or 'Y' or 'd' or 'D' or 'm' or 'M' or 'h' or 'H' or 's' or 'S') return true;
        }
        return false;
    }

    /// <summary>
    /// Разобранная запись формата. Разбирается она ОДИН раз на запись, а не на ячейку: записей
    /// в книге десятки, а ячеек десятки тысяч, и разбор строки на каждую из них — треть времени
    /// открытия книги на ровном месте.
    /// </summary>
    private readonly record struct Recipe(bool Date, bool Plain, Spec[] Parts);

    private static readonly ConcurrentDictionary<string, Recipe> Recipes = new();

    /// <summary>
    /// Потолок разобранных записей. Не расчет, а страховка: книга со сгенерированными форматами
    /// не должна растить этот словарь без конца.
    /// </summary>
    private const int RecipeCap = 512;

    /// <summary>Показать число ячейки по записи ее формата.</summary>
    public static DocNumber Apply(double value, string? format)
    {
        var code = (format ?? "").Trim();
        if (code.Length == 0 || code.Equals("General", StringComparison.OrdinalIgnoreCase))
            return new DocNumber(General(value));

        var recipe = Recipes.Count < RecipeCap ? Recipes.GetOrAdd(code, Compile) : Compile(code);

        if (recipe.Date)  return new DocNumber(AsDate(value, code));
        if (recipe.Plain) return new DocNumber(General(value));

        // Запись бывает из нескольких частей: для положительных, для отрицательных, для нуля.
        // Минус в этом случае ставит сама запись — поэтому число берется по модулю.
        var parts = recipe.Parts;
        var index = value < 0 && parts.Length > 1 ? 1
                  : value == 0 && parts.Length > 2 ? 2
                                                   : 0;

        var spec = parts[index];

        // Записей без знаков самого числа две породы, и путать их нельзя. «@» — это «показать
        // как есть», а запись из одних подписей (вроде «;;») намеренно ПРЯЧЕТ значение, и
        // подставлять в нее число значит показать то, что человек спрятал.
        if (!spec.Digits)
            return new DocNumber(spec.Passthrough ? spec.Prefix + General(value) + spec.Suffix
                                                  : spec.Prefix + spec.Suffix, spec.Red);

        var shown   = Math.Abs(value) * Math.Pow(100, spec.Percent);
        var pattern = (spec.Thousands ? "#,##0" : "0")
                    + (spec.Decimals + spec.Optional > 0
                        ? "." + new string('0', spec.Decimals) + new string('#', spec.Optional)
                        : "");

        var text = shown.ToString(pattern, CultureInfo.CurrentCulture);
        if (index == 0 && value < 0) text = "-" + text;

        return new DocNumber(spec.Prefix + text + spec.Suffix, spec.Red);
    }

    private static Recipe Compile(string code)
    {
        if (IsDate(code)) return new Recipe(true, false, []);

        // Показательная запись («0.00E+00») — отдельный разговор про порядок числа, и разбирать
        // ее вполсилы хуже, чем не разбирать: получится число, похожее на правду.
        if (code.Contains("E+", StringComparison.OrdinalIgnoreCase) ||
            code.Contains("E-", StringComparison.OrdinalIgnoreCase))
            return new Recipe(false, true, []);

        return new Recipe(false, false, [.. Sections(code).Select(Parse)]);
    }

    /// <summary>Число без записи формата: без хвоста нулей, как его показывает и сам Excel.</summary>
    public static string General(double value) =>
        value.ToString("0.##########", CultureInfo.CurrentCulture);

    /// <summary>Дата из числа Excel. Число вне диапазона дат показывается числом, а не ошибкой.</summary>
    public static string AsDate(double value, string format)
    {
        try
        {
            var date = DateTime.FromOADate(value);
            var time = Has(format, 'h');
            var day  = Has(format, 'd') || Has(format, 'y');

            if (day && time) return date.ToString("dd.MM.yyyy HH:mm", CultureInfo.CurrentCulture);
            if (time)        return date.ToString("HH:mm", CultureInfo.CurrentCulture);
            return date.ToString("dd.MM.yyyy", CultureInfo.CurrentCulture);
        }
        catch
        {
            return General(value);
        }
    }

    /// <summary>Буква в записи формата — вне кавычек и вне скобок, по тем же причинам.</summary>
    private static bool Has(string format, char letter)
    {
        var quoted = false;
        for (var i = 0; i < format.Length; i++)
        {
            var ch = format[i];
            if (ch == '"') { quoted = !quoted; continue; }
            if (quoted) continue;
            if (ch == '\\') { i++; continue; }
            if (ch == '[')
            {
                var close = format.IndexOf(']', i + 1);
                if (close < 0) return false;

                // Скобка из одних часов и минут — это тоже время, и букву в ней считаем.
                var inner = format[(i + 1)..close];
                if (inner.Length > 0 && inner.All(c => c is 'h' or 'H' or 'm' or 'M' or 's' or 'S')
                                     && inner.Contains(letter, StringComparison.OrdinalIgnoreCase))
                    return true;

                i = close;
                continue;
            }
            if (char.ToLowerInvariant(ch) == letter) return true;
        }
        return false;
    }

    // ── Разбор записи ─────────────────────────────────────────────────────────

    /// <summary>Части записи, разделенные «;». Точка с запятой внутри кавычек и скобок не в счет.</summary>
    private static List<string> Sections(string code)
    {
        var parts  = new List<string>();
        var part   = new StringBuilder();
        var quoted = false;
        var deep   = 0;

        foreach (var ch in code)
        {
            if (ch == '"') quoted = !quoted;
            else if (!quoted && ch == '[') deep++;
            else if (!quoted && ch == ']') deep = Math.Max(0, deep - 1);
            else if (!quoted && deep == 0 && ch == ';') { parts.Add(part.ToString()); part.Clear(); continue; }

            part.Append(ch);
        }
        parts.Add(part.ToString());
        return parts;
    }

    /// <summary>Что часть записи говорит о показе числа.</summary>
    private readonly record struct Spec(string Prefix, string Suffix, int Decimals, int Optional,
                                        bool Thousands, int Percent, bool Red, bool Digits,
                                        bool Passthrough);

    private static Spec Parse(string section)
    {
        var prefix = new StringBuilder();
        var suffix = new StringBuilder();

        var digits    = false;   // встретился хоть один знак самого числа
        var afterDot  = false;
        var lastDigit = false;   // предыдущий знак был знаком числа — для разделителя разрядов
        var decimals  = 0;
        var optional  = 0;
        var percent   = 0;
        var thousands = false;
        var red       = false;
        var text      = false;   // в записи есть «@» — «показать значение как есть»

        // Все, что не относится к числу, — подпись. До числа она идет слева, после — справа.
        void Put(char c)
        {
            if (digits) suffix.Append(c);
            else        prefix.Append(c);
        }

        for (var i = 0; i < section.Length; i++)
        {
            var ch = section[i];
            switch (ch)
            {
                case '"':
                    var quote = section.IndexOf('"', i + 1);
                    if (quote < 0) quote = section.Length;
                    foreach (var c in section[(i + 1)..Math.Min(quote, section.Length)]) Put(c);
                    i = quote;
                    lastDigit = false;
                    break;

                case '[':
                    var close = section.IndexOf(']', i + 1);
                    if (close < 0) { i = section.Length; break; }

                    var inner = section[(i + 1)..close];
                    if (inner.StartsWith('$'))
                    {
                        // «[$₽-419]» — знак валюты и код языка; язык нам не нужен.
                        var dash = inner.IndexOf('-');
                        foreach (var c in dash > 0 ? inner[1..dash] : inner[1..]) Put(c);
                    }
                    else if (inner.Equals("Red", StringComparison.OrdinalIgnoreCase)) red = true;

                    i = close;
                    lastDigit = false;
                    break;

                case '\\':
                    if (i + 1 < section.Length) Put(section[++i]);
                    lastDigit = false;
                    break;

                // «_x» — пробел шириной знака: им в Excel равняют столбик по краю.
                case '_':
                    if (i + 1 < section.Length) i++;
                    Put(' ');
                    lastDigit = false;
                    break;

                // «*x» — заполнение знаком до края ячейки; у нас его нет.
                case '*':
                    if (i + 1 < section.Length) i++;
                    lastDigit = false;
                    break;

                case '0':
                    digits = lastDigit = true;
                    if (afterDot) decimals++;
                    break;

                case '#':
                case '?':
                    digits = lastDigit = true;
                    if (afterDot) optional++;
                    break;

                case '.':
                    digits   = true;
                    afterDot = true;
                    break;

                case ',':
                    if (lastDigit && !afterDot) thousands = true;
                    break;

                case '%':
                    percent++;
                    Put('%');
                    lastDigit = false;
                    break;

                // «@» — подстановка значения ячейки как есть.
                case '@':
                    text      = true;
                    lastDigit = false;
                    break;

                default:
                    Put(ch);
                    lastDigit = false;
                    break;
            }
        }

        return new Spec(prefix.ToString(), suffix.ToString(), decimals, optional,
                        thousands, percent, red, digits, text);
    }
}
