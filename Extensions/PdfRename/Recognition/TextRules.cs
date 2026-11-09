using System.Text;
using System.Text.RegularExpressions;

namespace SmartBase.Extensions.PdfRename.Recognition;

/// <summary>
/// Нормализация распознанного текста: пробелы, переносы строк, тире, кавычки и буквы-двойники
/// (латинская «A» и кириллическая «А» выглядят одинаково).
///
/// <para>Латинская «B» — НЕ однозначный двойник: на скане ей могут соответствовать и «В», и «Б»
/// (оба движка читали «Б» как «B» на проверочных сканах). Поэтому «B» правилами не меняется
/// никогда; кириллической «В» она становится, только если второй движок прочитал на этом месте
/// именно «В» (<see cref="MergeTwins"/>).</para>
///
/// <para>Остальные двойники заменяются только там, где замену подтверждает правило:
/// <list type="bullet">
/// <item>в одном слове не бывает двух алфавитов: если в слове есть буква, которая есть только в
/// кириллице (Б, Г, Ц…), а латинских букв без кириллического двойника нет, — двойники в этом слове
/// кириллические (и наоборот для латиницы);</item>
/// <item>код организации в обозначении по ГОСТ 2.201 — кириллица (см. <see cref="Designations"/>).</item>
/// </list>
/// Слово только из двойников («ТКС» или «TKC») не меняется: какой это алфавит, решают оба
/// движка; прочитали по-разному — расхождение, файл пропускается. Буквы и цифры (О и 0, З и 3)
/// не заменяются никогда.</para>
/// </summary>
internal static class TextRules
{
    // Латинская буква → ее единственный кириллический двойник. Без «B»: она и «В», и «Б».
    private static readonly Dictionary<char, char> LatinToCyrillic = new()
    {
        ['A'] = 'А', ['C'] = 'С', ['E'] = 'Е', ['H'] = 'Н', ['K'] = 'К', ['M'] = 'М',
        ['O'] = 'О', ['P'] = 'Р', ['T'] = 'Т', ['X'] = 'Х', ['Y'] = 'У',
        ['a'] = 'а', ['c'] = 'с', ['e'] = 'е', ['o'] = 'о', ['p'] = 'р', ['x'] = 'х', ['y'] = 'у',
    };

    // Кириллическая буква → латинский двойник. «В» здесь есть: в латинском слове кириллическая
    // «В» может быть только «B» («Б» в латинском слове не бывает).
    private static readonly Dictionary<char, char> CyrillicToLatin =
        LatinToCyrillic.ToDictionary(p => p.Value, p => p.Key).Append(new('В', 'B')).ToDictionary();

    /// <summary>Латинская «B» на месте кириллической буквы: «В» или «Б» — решает второй движок.</summary>
    public const char AmbiguousB = 'B';

    public static bool IsCyrillic(char c) => c is >= 'А' and <= 'я' or 'Ё' or 'ё';
    public static bool IsLatin(char c) => c is >= 'A' and <= 'Z' or >= 'a' and <= 'z';

    /// <summary>Буква-двойник: латинская или кириллическая буква, у которой есть пара того же вида.</summary>
    public static bool IsHomoglyph(char c) => LatinToCyrillic.ContainsKey(c) || CyrillicToLatin.ContainsKey(c);

    /// <summary>Латинские двойники → кириллица (для кода организации по ГОСТ).</summary>
    public static string FoldToCyrillic(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (var c in s) sb.Append(LatinToCyrillic.TryGetValue(c, out var r) ? r : c);
        return sb.ToString();
    }

    /// <summary>
    /// Слова со смешанным алфавитом приводятся к алфавиту, который подтверждают однозначные
    /// буквы этого же слова. Слово — непрерывная последовательность букв.
    /// </summary>
    public static string ResolveMixedWords(string s)
    {
        var chars = s.ToCharArray();
        var i = 0;
        while (i < chars.Length)
        {
            if (!char.IsLetter(chars[i])) { i++; continue; }
            var start = i;
            while (i < chars.Length && char.IsLetter(chars[i])) i++;

            bool pureCyr = false, pureLat = false, hasCyrTwin = false, hasLatTwin = false;
            for (var j = start; j < i; j++)
            {
                var c = chars[j];
                if (c == AmbiguousB) hasLatTwin = true;   // «B» не решает алфавит слова и сама не меняется
                else if (LatinToCyrillic.ContainsKey(c)) hasLatTwin = true;
                else if (CyrillicToLatin.ContainsKey(c)) hasCyrTwin = true;
                else if (IsCyrillic(c)) pureCyr = true;
                else if (IsLatin(c)) pureLat = true;
            }
            if (pureCyr && !pureLat && hasLatTwin)
                for (var j = start; j < i; j++)
                    if (LatinToCyrillic.TryGetValue(chars[j], out var r)) chars[j] = r;
            if (pureLat && !pureCyr && hasCyrTwin)
                for (var j = start; j < i; j++)
                    if (CyrillicToLatin.TryGetValue(chars[j], out var r)) chars[j] = r;
        }
        return new string(chars);
    }

    /// <summary>Внутри слова с кириллицей (там «B» — это «В» или «Б»).</summary>
    public static bool InCyrillicWord(string s, int index)
    {
        int a = index, b = index;
        while (a > 0 && char.IsLetter(s[a - 1])) a--;
        while (b < s.Length - 1 && char.IsLetter(s[b + 1])) b++;
        for (var i = a; i <= b; i++)
            if (IsCyrillic(s[i])) return true;
        return false;
    }

    /// <summary>
    /// Свести прочтения двух движков, различающиеся только неоднозначной «B»: где один прочитал
    /// кириллическую «В», а другой — «B», остается «В» (подтверждено вторым движком). «B» у обоих
    /// внутри кириллического слова — не подтверждено: «В» или «Б». Любое другое различие —
    /// расхождение. Возвращает итог или причину.
    /// </summary>
    public static (string? Value, string? Problem) MergeTwins(string a, string b)
    {
        if (a.Length != b.Length) return (null, null);
        var sb = new StringBuilder(a.Length);
        for (var i = 0; i < a.Length; i++)
        {
            char x = a[i], y = b[i];
            if (x == y)
            {
                if (x == AmbiguousB && InCyrillicWord(a, i) && InCyrillicWord(b, i))
                    return (null, "оба движка прочитали латинскую «B» на месте кириллической буквы — «В» или «Б», не подтверждено");
                sb.Append(x);
            }
            else if (x == AmbiguousB && y == 'В' || x == 'В' && y == AmbiguousB) sb.Append('В');
            else return (null, null);
        }
        return (sb.ToString(), null);
    }

    private static readonly Regex Spaces = new(@"[ \t  -​ ]+", RegexOptions.Compiled);

    /// <summary>
    /// Строка как текст: тире всех видов → «-», типографские кавычки → прямые/елочки,
    /// пробелы схлопнуты, по краям убран мусор от линий таблицы (| _ ~ `), «е с точками» → «е».
    /// </summary>
    public static string NormalizeLine(string line)
    {
        var sb = new StringBuilder(line.Length);
        foreach (var c in line)
        {
            sb.Append(c switch
            {
                '‐' or '‑' or '‒' or '–' or '—' or '―' or '−' => '-',
                '“' or '”' or '„' or '‟' or '″' => '"',
                '‘' or '’' or '‚' or '‛' or '′' => '\'',
                'ё' => 'е',
                'Ё' => 'Е',
                _ when char.IsControl(c) => ' ',
                _ => c,
            });
        }
        var s = Spaces.Replace(sb.ToString(), " ").Trim();
        s = s.Trim('|', '_', '~', '`', ' ');
        s = s.TrimStart('.', ',', ';', ':', '-', ' ').TrimEnd(',', ';', ':', ' ');
        return s;
    }

    private static readonly Regex SpacesAroundHyphen = new(@" ?- ?", RegexOptions.Compiled);

    /// <summary>Строка наименования: <see cref="NormalizeLine"/> и без пробелов вокруг дефиса
    /// («Плата - адаптер» и «Плата-адаптер» у разных движков — одно и то же).</summary>
    public static string NormalizeName(string line) => SpacesAroundHyphen.Replace(NormalizeLine(line), "-");

    /// <summary>
    /// Строки в одну: через пробел; строка, оканчивающаяся дефисом, соединяется со следующей
    /// без пробела с сохранением дефиса («Плата-» + «адаптер» → «Плата-адаптер»).
    /// </summary>
    public static string JoinLines(IEnumerable<string> lines)
    {
        var sb = new StringBuilder();
        foreach (var raw in lines)
        {
            var line = NormalizeName(raw);
            if (line.Length == 0) continue;
            if (sb.Length > 0 && sb[^1] != '-') sb.Append(' ');
            sb.Append(line);
        }
        return Spaces.Replace(sb.ToString(), " ").Trim();
    }

    /// <summary>Есть ли в тексте буквы или цифры (не только знаки).</summary>
    public static bool HasAlnum(string s) => s.Any(char.IsLetterOrDigit);
}
