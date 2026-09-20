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
/// <item>последнее слово ищется по началу слова (в MATCH ему приписывается <c>*</c>), все
///       остальные — целым словом;</item>
/// <item>регистр и «е/ё» не различаются — так настроен токенизатор индекса
///       (<c>unicode61 remove_diacritics 2</c>).</item>
/// </list>
///
/// Подсвечивается всегда ЦЕЛОЕ слово, даже когда совпало только его начало: набравший
/// «автоматизир» ищет «автоматизированных», и желтым обязано быть слово, а не его обрубок.
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
    /// кавычки и прочая пунктуация остаются частью слова — в MATCH они экранируются, а здесь
    /// просто не совпадут ни с чем, и это честно.
    /// </summary>
    public static List<string> Tokens(string? query) =>
        [.. (query ?? "")
            .Split([' ', '\t', '\n', '\r', ',', ';'], StringSplitOptions.RemoveEmptyEntries)
            .Select(t => t.Trim())
            .Where(t => t.Length > 0)];

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

        // Слова запроса складываем сложенными (регистр и «ё» уже сняты) — сравнение в цикле
        // идет посимвольно по тексту, и складывать слово запроса на каждом шаге незачем.
        var whole  = new List<string>(tokens.Count);
        var prefix = new List<string>(1);
        for (var i = 0; i < tokens.Count; i++)
        {
            var folded = Fold(tokens[i]);
            if (folded.Length == 0) continue;
            if (lastIsPrefix && i == tokens.Count - 1) prefix.Add(folded);
            else                                       whole.Add(folded);
        }
        if (whole.Count == 0 && prefix.Count == 0) return hits;

        var pos = 0;
        while (pos < text.Length)
        {
            if (!IsWordChar(text[pos])) { pos++; continue; }

            var start = pos;
            while (pos < text.Length && IsWordChar(text[pos])) pos++;
            var word = Fold(text[start..pos]);

            if (Matches(word, whole, prefix)) hits.Add(new Hit(start, pos - start));
        }
        return hits;
    }

    private static bool Matches(string word, List<string> whole, List<string> prefix)
    {
        foreach (var t in whole)
            if (string.Equals(word, t, StringComparison.Ordinal)) return true;

        foreach (var t in prefix)
            if (word.StartsWith(t, StringComparison.Ordinal)) return true;

        return false;
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
    public static string Fold(string? word)
    {
        if (string.IsNullOrEmpty(word)) return "";

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
