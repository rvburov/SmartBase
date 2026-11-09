using System.Text.RegularExpressions;

namespace SmartBase.Extensions.PdfRename.Recognition;

/// <summary>Найденное обозначение: каноническая запись, правило и место в исходном тексте.</summary>
internal sealed record DesignationMatch(string Value, string Rule, int Index, int Length);

/// <summary>
/// Правила десятичного обозначения. Формат не ограничен одним примером — поддержаны:
/// <list type="bullet">
/// <item>ЕСКД (ГОСТ 2.201): <c>АБВГ.461411.123</c>, с исполнением <c>-01</c>, групповым
/// <c>-01.02</c>, кодом документа <c>СБ</c>, <c>Э3</c>, <c>ПЭ3</c>… и <c>-ЛУ/-УЛ</c>;</item>
/// <item>ЕСПД (ГОСТ 19.103): <c>АБВГ.12345-01 34 01</c>, <c>АБВГ.12345-01</c>;</item>
/// <item>технологические: <c>АБВГ.12345.12345</c>;</item>
/// <item>отраслевые и заводские «через точки»: 1–8 букв и цифр (хотя бы одна буква), затем
/// две-четыре группы цифр через точку (<c>ВЛ2.700.004-01</c>, <c>8А1.234.567</c>), либо
/// только цифры вида <c>8.112.006</c> (средняя группа — не короче трех цифр, чтобы дата
/// «12.05.2020» не принималась за обозначение).</item>
/// </list>
/// Каноническая запись: прописные буквы, без пробелов у точек и дефисов, тире → дефис. Код
/// организации ЕСКД/ЕСПД/ТД — четыре кириллические буквы (ГОСТ 2.201), поэтому латинские
/// двойники в нем переводятся в кириллицу; буква без кириллического двойника (D, F, L…) —
/// обозначение не по ГОСТ. Цифры и буквы между собой не заменяются.
/// </summary>
internal static class Designations
{
    private const string L = "A-Za-zА-Яа-я";

    /// <summary>Коды документов ЕСКД, которые OCR может отделить пробелом: «…123 СБ» → «…123СБ».</summary>
    public static readonly HashSet<string> DocumentCodes = new(StringComparer.Ordinal)
    {
        "СБ", "ВО", "ТЧ", "ГЧ", "МЭ", "МЧ", "УЧ", "ВС", "ВД", "ВП", "ВИ", "ДП", "ПТ", "ЭП", "ТП", "ПЗ",
        "ТУ", "ПМ", "ТБ", "РР", "ИМ", "ЭД", "РЭ", "ИМ", "ФО", "ПС", "ЭТ", "ЗИ", "ВЭ", "ТО", "ИС", "КТ", "Д",
        "Э0", "Э1", "Э2", "Э3", "Э4", "Э5", "Э6", "Э7", "ПЭ3", "ПЭ4", "ПЭ5", "Г3", "К3", "П3", "С3", "Л3",
    };

    private static readonly Regex NumberedCode = new(@"^(?:ПЭ|П[ГКЛСЭ]|[ЭГКПСЛВЕРХТШ])\d$|^Д\d{1,2}$", RegexOptions.Compiled);

    /// <summary>Код документа после номера (СБ, Э3, ПЭ3, Д1…). Неизвестный код — не часть
    /// обозначения: «…123З» (буква З вместо цифры 3) обозначением не считается.</summary>
    public static bool IsKnownCode(string code) => DocumentCodes.Contains(code) || NumberedCode.IsMatch(code);

    // Каждое правило — по тексту, где пробелы у точек и дефисов уже убраны.
    private static readonly (string Name, Regex Rx, bool GostOrg)[] Rules =
    [
        ("ЕСКД", new Regex(
            $@"(?<![{L}0-9.])(?<org>[{L}]{{4}})\.(?<num>\d{{6}})\.(?<ver>\d{{3,4}})(?:-\d{{2}}\.\d{{2}})?(?:-\d{{1,3}})?" +
            $@"(?<code>[{L}]{{1,3}}\d{{0,2}})?(?:-(?:ЛУ|УЛ|лу|ул))?(?![{L}0-9]|\.\d)", RegexOptions.Compiled), true),
        ("ЕСПД", new Regex(
            $@"(?<![{L}0-9.])(?<org>[{L}]{{4}})\.\d{{5}}-\d{{2}}(?: \d{{2}} \d{{2}})?(?:-\d)?(?:-(?:ЛУ|УЛ|лу|ул))?(?![{L}0-9]|\.\d)",
            RegexOptions.Compiled), true),
        ("ТД", new Regex(
            $@"(?<![{L}0-9.])(?<org>[{L}]{{4}})\.\d{{5}}\.\d{{5}}(?:-\d{{1,3}})?(?![{L}0-9]|\.\d)", RegexOptions.Compiled), true),
        ("отраслевое", new Regex(
            $@"(?<![{L}0-9.])(?<org>[{L}0-9]{{1,8}})(?:\.\d{{2,8}}){{2,4}}(?:-\d{{1,3}})?(?<code>[{L}]{{1,3}}\d{{0,2}})?(?![{L}0-9]|\.\d)",
            RegexOptions.Compiled), false),
    ];

    /// <summary>Подготовка текста к поиску: нормализация строки наименования
    /// (<see cref="TextRules.NormalizeName"/>) и убранные пробелы у точек.</summary>
    public static string Prepare(string text) => Squeeze(TextRules.NormalizeName(text.Replace('\n', ' ')), out _);

    /// <summary>
    /// Убрать пробелы рядом с точками («АБВГ. 461411 .123» → «АБВГ.461411.123»).
    /// <paramref name="map"/>[i] — индекс символа результата в исходной строке; последний
    /// элемент — длина исходной строки.
    /// </summary>
    public static string Squeeze(string normalized, out int[] map)
    {
        var sb = new System.Text.StringBuilder(normalized.Length);
        var m = new List<int>(normalized.Length + 1);
        for (var i = 0; i < normalized.Length; i++)
        {
            var c = normalized[i];
            if (c == ' ')
            {
                var prev = sb.Length > 0 ? sb[^1] : '\0';
                var next = i + 1 < normalized.Length ? normalized[i + 1] : '\0';
                if (prev == '.' || next == '.') continue;
                // Пробел внутри группы цифр между двумя точками («.967 747.»): в группе
                // обозначения пробелов не бывает, Tesseract ставит их по ширине цифр.
                if (char.IsDigit(prev) && char.IsDigit(next) && InsideDotGroup(sb, normalized, i)) continue;
            }
            sb.Append(c);
            m.Add(i);
        }
        m.Add(normalized.Length);
        map = [.. m];
        return sb.ToString();
    }

    private static bool InsideDotGroup(System.Text.StringBuilder emitted, string text, int space)
    {
        var j = emitted.Length - 1;
        while (j >= 0 && char.IsDigit(emitted[j])) j--;
        if (j < 0 || emitted[j] != '.') return false;
        var k = space + 1;
        while (k < text.Length && (char.IsDigit(text[k]) || text[k] == ' ' && k + 1 < text.Length && char.IsDigit(text[k + 1]))) k++;
        return k < text.Length && text[k] == '.';
    }

    /// <summary>Все обозначения в тексте (одна строка, см. <see cref="Prepare"/>), по порядку.</summary>
    public static List<DesignationMatch> FindAll(string prepared)
    {
        var found = new List<DesignationMatch>();
        // Строка вида ГОСТ (АААА.000000.000), не прошедшая проверку кода организации, не
        // становится «отраслевым» обозначением: латиница в таком месте — ошибка чтения.
        var rejected = new List<(int Index, int Length)>();
        foreach (var (name, rx, gost) in Rules)
        {
            foreach (Match m in rx.Matches(prepared))
            {
                bool Overlaps(int index, int length) => m.Index < index + length && index < m.Index + m.Length;
                if (found.Any(f => Overlaps(f.Index, f.Length)) || rejected.Any(r => Overlaps(r.Index, r.Length))) continue;
                var value = Canonical(m, gost, prepared, out var length);
                if (value is null)
                {
                    if (gost) rejected.Add((m.Index, m.Length));
                    continue;
                }
                found.Add(new DesignationMatch(value, name, m.Index, length));
            }
        }
        found.Sort((a, b) => a.Index.CompareTo(b.Index));
        return found;
    }

    /// <summary>Каноническая запись совпадения; null — не годится (например, латиница в коде
    /// организации ГОСТ). <paramref name="length"/> — сколько символов текста занято, с
    /// присоединенным кодом документа.</summary>
    private static string? Canonical(Match m, bool gost, string text, out int length)
    {
        length = m.Length;
        var value = m.Value.ToUpperInvariant();
        var org = m.Groups["org"].Value.ToUpperInvariant();

        if (gost)
        {
            // Код организации — кириллица; латинская «B» остается как есть («В» или «Б» —
            // решает сверка со вторым движком), прочая латиница — не ГОСТ.
            var folded = TextRules.FoldToCyrillic(org);
            if (folded.Any(c => !TextRules.IsCyrillic(c) && c != TextRules.AmbiguousB)) return null;
            value = folded + value[org.Length..];
        }
        else
        {
            if (org.All(char.IsDigit))
            {
                // Только цифры: средняя группа не короче трех цифр (отсекает даты).
                var groups = value.Split('.', '-');
                if (groups.Length < 3 || groups[1].Length < 3) return null;
            }
            else if (!org.Any(char.IsLetter)) return null;
            value = TextRules.ResolveMixedWords(value);
        }

        // Код документа ЕСКД, отделенный пробелом: «…123 СБ».
        if (!m.Groups["code"].Success || m.Groups["code"].Length == 0)
        {
            var rest = text[(m.Index + m.Length)..];
            var code = Regex.Match(rest, $@"^ ([{L}]{{1,3}}\d{{0,2}})(?![{L}0-9])");
            if (code.Success)
            {
                var c = TextRules.FoldToCyrillic(code.Groups[1].Value.ToUpperInvariant());
                if (DocumentCodes.Contains(c))
                {
                    value += c;
                    length += code.Length;
                }
            }
        }
        else
        {
            // Код документа — только известный; латинские двойники в нем — кириллица (коды ЕСКД).
            var codeGroup = m.Groups["code"];
            var c = TextRules.FoldToCyrillic(codeGroup.Value.ToUpperInvariant());
            if (!IsKnownCode(c)) return null;
            value = value[..(codeGroup.Index - m.Index)] + c + value[(codeGroup.Index - m.Index + codeGroup.Length)..];
        }

        return value;
    }

    /// <summary>Похож ли текст на обозначение (для проверки, что наименование не им является).</summary>
    public static bool ContainsDesignation(string text) => FindAll(Prepare(text)).Count > 0;
}
