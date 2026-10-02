using System.Globalization;
using System.Text;

namespace SmartBase.Core.Documents;

/// <summary>
/// Где в тексте документа стоят совпадения с поисковым запросом — для подсветки в панели
/// чтения. Чистая логика без UI и без базы.
///
/// <b>Правила совпадения здесь НЕ свои, а те же, по которым ищет FTS5.</b> Это главное в этом
/// файле. Поиск в разделе идет запросом <c>MATCH</c> (<c>DocTextIndex.BuildMatch</c>), и если
/// подсветка станет считать совпадения как-нибудь иначе — «поиском нашлось, а в документе
/// ничего не подсвечено» — человек решит, что панель сломана. Поэтому:
///
/// <list type="bullet">
/// <item>запрос режется на слова ОДНОЙ функцией <see cref="Tokens"/> — ее же зовет
///       <c>BuildMatch</c>, второго разбора запроса в программе нет;</item>
/// <item>слово запроса с пунктуацией внутри («21.501», «C++», «кв.м») — для индекса ФРАЗА:
///       <c>BuildMatch</c> берет его в кавычки, а токенизатор режет по всему, что не буква и не
///       цифра, и ищет получившиеся слова ПОДРЯД. Подсветка режет так же (<see cref="Parts"/>)
///       и ищет те же слова подряд; слово из одной пунктуации индекс не видит вовсе, и оно
///       пропускается;</item>
/// <item>последнее слово ищется по началу слова (в MATCH ему приписывается <c>*</c>), все
///       остальные — целым словом; у фразы «*» достается ее последней части;</item>
/// <item>регистр и «е/ё» не различаются — так настроен токенизатор индекса
///       (<c>unicode61 remove_diacritics 2</c>);</item>
/// <item>число из четырех и больше цифр ищется и с разрядами (<see cref="Variants"/>):
///       «30772» находит «30 772,48» — так сумму показывает книга.</item>
/// </list>
///
/// Подсвечивается всегда ЦЕЛОЕ слово, даже когда совпало только его начало: набравший
/// «автоматизир» ищет «автоматизированных», и желтым обязано быть слово, а не его обрубок.
/// Фраза подсвечивается одним куском — от первого своего слова до последнего.
/// </summary>
public static class DocHighlight
{
    /// <summary>Совпадение в тексте: с какого символа и сколько их.</summary>
    public readonly record struct Hit(int Start, int Length)
    {
        public int End => Start + Length;
    }

    /// <summary>
    /// Слова запроса в порядке набора. Разделители те же, что человек ставит между словами;
    /// кавычки и прочая пунктуация остаются частью слова — в MATCH они экранируются, а в
    /// подсветке слово режется на части по тем же правилам, что у индекса (<see cref="Parts"/>).
    /// </summary>
    public static List<string> Tokens(string? query) =>
        [.. (query ?? "")
            .Split([' ', '\t', '\n', '\r', ',', ';'], StringSplitOptions.RemoveEmptyEntries)
            .Select(t => t.Trim())
            .Where(t => t.Length > 0)];

    /// <summary>
    /// Части слова запроса — так, как режет его токенизатор <c>unicode61</c>: буквы и цифры
    /// подряд образуют часть, все остальное — разделитель. «21.501» — две части, «C++» — одна
    /// («c»), «++» — ни одной. Части уже сложены (<see cref="Fold"/>).
    /// </summary>
    public static string[] Parts(string? token)
    {
        if (string.IsNullOrEmpty(token)) return [];
        var parts = new List<string>();
        foreach (var (start, end) in Words(token)) parts.Add(Fold(token.AsSpan(start, end - start)));
        return [.. parts];
    }

    /// <summary>
    /// Варианты слова запроса — части, которые ищутся подряд. Обычно вариант один. У числа из
    /// четырех и больше цифр их два: как набрано и с разрядами, как его показывает книга по
    /// записи «#,##0», — «30772» и «30 772»; дробная часть остается при числе: «30772.48» —
    /// это и «30 772 48».
    ///
    /// Зачем. Поиск хранит книгу так, как ее показывает панель, — с разрядами, а число человек
    /// набирает подряд. Без второго варианта сумма «30 772,48» по запросу «30772» не находилась
    /// бы, хотя прежде, пока индекс хранил числа как они лежат в файле, находилась. Разряды
    /// ставятся по языку системы — тем же, по которому их ставит панель.
    ///
    /// Вариантом обзаводится только слово, которое ЦЕЛИКОМ число: «12345-2019» или «№12345» —
    /// коды, их книга разрядами не делит, как и число с ведущим нулем («01234»).
    /// </summary>
    public static IReadOnlyList<string[]> Variants(string? token)
    {
        var parts = Parts(token);
        if (parts.Length == 0) return [];

        var grouped = Grouped(token!);
        return grouped is null ? [parts] : [parts, grouped];
    }

    // Число с разрядами, разрезанное на части, как его режет индекс; null — слово не число или
    // разряды его не меняют.
    private static string[]? Grouped(string token)
    {
        var number = token.AsSpan();
        if (number.Length > 0 && number[0] is '-' or '+') number = number[1..];

        var point = number.IndexOfAny('.', ',');
        var whole = point < 0 ? number : number[..point];
        var tail  = point < 0 ? ReadOnlySpan<char>.Empty : number[(point + 1)..];

        if (whole.Length is < 4 or > 18 || whole[0] == '0' || !AllDigits(whole)) return null;
        if (point >= 0 && (tail.Length == 0 || !AllDigits(tail))) return null;

        var parts = Parts(long.Parse(whole, CultureInfo.InvariantCulture)
                              .ToString("#,##0", CultureInfo.CurrentCulture));
        if (parts.Length < 2) return null;

        return tail.Length > 0 ? [.. parts, tail.ToString()] : parts;
    }

    private static bool AllDigits(ReadOnlySpan<char> text)
    {
        foreach (var c in text)
            if (!char.IsAsciiDigit(c)) return false;
        return true;
    }

    /// <summary>
    /// Все совпадения в тексте, по возрастанию позиции. Пересечений не бывает: слово,
    /// совпавшее сразу с двумя словами запроса, дает одно совпадение.
    /// </summary>
    /// <param name="lastIsPrefix">
    /// Последнее слово запроса ищется по началу слова. False — когда запрос заведомо
    /// завершен (например, подсветка по готовому списку слов).
    /// </param>
    public static List<Hit> Find(string? text, IReadOnlyList<string>? tokens, bool lastIsPrefix = true)
    {
        var hits = new List<Hit>();
        if (string.IsNullOrEmpty(text) || tokens is null || tokens.Count == 0) return hits;

        var phrases = Phrases(tokens, lastIsPrefix);
        if (phrases.Count == 0) return hits;

        // Слова текста с границами. Складываются лениво и по одному разу: сравнение доходит
        // не до каждого слова целиком, а текст бывает на миллионы знаков.
        var words  = Words(text);
        var folded = new string?[words.Count];
        string At(int i) => folded[i] ??= Fold(text.AsSpan(words[i].Start, words[i].End - words[i].Start));

        bool MatchesAt(Phrase phrase, int at)
        {
            if (at + phrase.Parts.Length > words.Count) return false;
            for (var k = 0; k < phrase.Parts.Length; k++)
            {
                var word   = At(at + k);
                var part   = phrase.Parts[k];
                var prefix = phrase.LastIsPrefix && k == phrase.Parts.Length - 1;
                if (prefix ? !word.StartsWith(part, StringComparison.Ordinal)
                           : !string.Equals(word, part, StringComparison.Ordinal))
                    return false;
            }
            return true;
        }

        var i = 0;
        while (i < words.Count)
        {
            // Из фраз, стоящих на этом месте, берется самая длинная: «21» и «21.501» в одном
            // запросе подсвечивают «21.501» целиком, а не его первую половину.
            var best = 0;
            foreach (var phrase in phrases)
                if (phrase.Parts.Length > best && MatchesAt(phrase, i)) best = phrase.Parts.Length;

            if (best == 0) { i++; continue; }
            hits.Add(new Hit(words[i].Start, words[i + best - 1].End - words[i].Start));
            i += best;
        }
        return hits;
    }

    /// <summary>
    /// Совпадения на странице PDF, где текст — это слова с местами, а не строка: какие слова
    /// подсветить. Слова сшиваются через пробел и ищутся тем же <see cref="Find"/>: фраза
    /// запроса переходит через границу слов так же, как ее находит индекс, у которого текст
    /// страницы — сплошная строка. Сумма «30 772,48» на странице — два слова, и запрос «30772»
    /// обязан подсветить оба; так же и «21.501», разбитое версткой надвое.
    ///
    /// Совпадение — диапазон слов от <c>First</c> до <c>Last</c>. Два совпадения в одном слове
    /// сливаются в одно: подсвечивается слово, и считать его дважды незачем.
    /// </summary>
    public static List<(int First, int Last)> FindInWords(IReadOnlyList<string> words,
                                                          IReadOnlyList<string>? tokens,
                                                          bool lastIsPrefix = true)
    {
        var found = new List<(int First, int Last)>();
        if (words.Count == 0 || tokens is null || tokens.Count == 0) return found;

        var text   = new StringBuilder();
        var starts = new int[words.Count];
        for (var w = 0; w < words.Count; w++)
        {
            if (w > 0) text.Append(' ');
            starts[w] = text.Length;
            text.Append(words[w]);
        }

        foreach (var hit in Find(text.ToString(), tokens, lastIsPrefix))
        {
            var first = WordAt(starts, hit.Start);
            var last  = WordAt(starts, hit.End - 1);

            if (found.Count > 0 && found[^1].Last >= first)
                found[^1] = (found[^1].First, Math.Max(found[^1].Last, last));
            else
                found.Add((first, last));
        }
        return found;
    }

    // Слово, которому принадлежит знак: последнее из начавшихся не позже него.
    private static int WordAt(int[] starts, int at)
    {
        var i = Array.BinarySearch(starts, at);
        return i >= 0 ? i : ~i - 1;
    }

    // Фраза запроса: части подряд; у последней части последнего слова запроса — поиск по началу.
    private readonly record struct Phrase(string[] Parts, bool LastIsPrefix);

    private static List<Phrase> Phrases(IReadOnlyList<string> tokens, bool lastIsPrefix)
    {
        var phrases = new List<Phrase>(tokens.Count);
        for (var t = 0; t < tokens.Count; t++)
        {
            // Слово из одной пунктуации: индекс из него не делает ни одной части и в поиске
            // его не учитывает («гост ++» находит все, где есть «гост»). Подсветке — то же:
            // вариантов у такого слова нет.
            foreach (var parts in Variants(tokens[t]))
                phrases.Add(new Phrase(parts, lastIsPrefix && t == tokens.Count - 1));
        }
        return phrases;
    }

    // Границы слов текста: [Start, End) каждой непрерывной цепочки букв и цифр.
    private static List<(int Start, int End)> Words(string text)
    {
        var words = new List<(int Start, int End)>();
        var pos = 0;
        while (pos < text.Length)
        {
            if (!IsWordChar(text[pos])) { pos++; continue; }
            var start = pos;
            while (pos < text.Length && IsWordChar(text[pos])) pos++;
            words.Add((start, pos));
        }
        return words;
    }

    /// <summary>
    /// Буква слова. Цифры считаются буквами (артикул «ГОСТ 21.501» — три слова, и «21» из
    /// них ищется наравне с прочими), знак подчеркивания — нет: так режет на слова токенизатор
    /// <c>unicode61</c>.
    /// </summary>
    private static bool IsWordChar(char c) => char.IsLetterOrDigit(c);

    /// <summary>
    /// Слово в виде, в котором его сравнивают: нижний регистр, «ё» как «е», «й» как «и».
    ///
    /// Последнее выглядит странно, но это ровно то, что делает индекс: у токенизатора
    /// <c>unicode61</c> с <c>remove_diacritics 2</c> обе буквы раскладываются на основу плюс
    /// надстрочный знак, а знак снимается. Расходиться с ним нельзя — иначе найденное в поиске
    /// слово в панели чтения осталось бы неподсвеченным.
    /// </summary>
    public static string Fold(string? word) => string.IsNullOrEmpty(word) ? "" : Fold(word.AsSpan());

    private static string Fold(ReadOnlySpan<char> word)
    {
        var chars = new char[word.Length];
        for (var i = 0; i < word.Length; i++) chars[i] = Fold(word[i]);
        return new string(chars);
    }

    private static char Fold(char c) => char.ToLowerInvariant(c) switch
    {
        'ё' => 'е',
        'й' => 'и',
        var lower => lower,
    };
}
