using System.Text;

namespace SmartBase.Core.Notes;

/// <summary>Что вставить в строку целиком — отдельными строками, а не внутри абзаца.</summary>
public enum NoteBlock
{
    Table,
    CodeFence,
    Rule,
}

/// <summary>Признак абзаца, который ставится в начало строки.</summary>
public enum NotePrefix
{
    Bullet,
    Ordered,
    Quote,
}

/// <summary>Текст после команды и что в нем должно остаться выделенным.</summary>
public readonly record struct NoteEdit(string Text, int Start, int Length);

/// <summary>
/// Команды форматирования заметки: жирный, список, заголовок, таблица. Чистые функции «текст и
/// выделение → новый текст и новое выделение», без единой ссылки на Avalonia, — потому что вся
/// сложность здесь не в кнопках меню, а в краевых случаях: пустое выделение посреди слова,
/// выделение через несколько строк, повторное применение, выделение внутри уже обернутого куска.
/// Каждый из них закреплен тестом.
/// </summary>
public static class NoteFormat
{
    // Знаки, которые считаются оформлением внутри строки. Порядок важен: длинные пары проверяются
    // раньше коротких, иначе «**» разберется как две «*».
    private static readonly string[] InlineMarkers = ["***", "**", "~~", "*", "_", "`"];

    // ── Обертка ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Обернуть выделенное парой знаков или снять уже стоящую обертку. Повторное применение
    /// возвращает текст ровно к прежнему виду.
    /// </summary>
    public static NoteEdit ToggleWrap(string text, int start, int length, string marker)
    {
        text ??= "";
        Normalize(text, ref start, ref length);
        var end = start + length;

        // Выделение через несколько строк: каждую строку оборачиваем отдельно. Через перевод
        // строки пара знаков не работает — «**раз\nдва**» разметкой не является.
        if (length > 0 && text.AsSpan(start, length).Contains('\n')) return WrapEachLine(text, start, end, marker);

        // Пустое выделение внутри слова — оборачиваем слово целиком: иначе выйдет «жир****ный».
        if (length == 0)
        {
            var (ws, we) = WordAt(text, start);
            if (we > ws) { start = ws; end = we; length = we - ws; }
        }

        var m = marker.Length;

        // Уже обернуто ровно этим — снимаем. Два случая: знаки попали в выделение и знаки стоят
        // сразу за его краями (человек выделил только слово внутри готового «**слово**»).
        if (length >= 2 * m && Has(text, start, marker) && Has(text, end - m, marker))
            return Cut(text, start, end, m, m);

        if (start - m >= 0 && end + m <= text.Length && Has(text, start - m, marker) && Has(text, end, marker))
            return Cut(text, start - m, end + m, m, m);

        // Пробелы по краям выносим наружу: «** х **» разметкой по CommonMark не считается.
        while (start < end && char.IsWhiteSpace(text[start])) start++;
        while (end > start && char.IsWhiteSpace(text[end - 1])) end--;

        var body = text[start..end];
        var next = text[..start] + marker + body + marker + text[end..];
        // Выделенным оставляем сам текст, без знаков: человек продолжает печатать по делу.
        return new NoteEdit(next, start + m, body.Length);
    }

    /// <summary>Снять с выделенного все оформление внутри строки, оставив голый текст.</summary>
    public static NoteEdit ClearFormatting(string text, int start, int length)
    {
        text ??= "";
        Normalize(text, ref start, ref length);
        if (length == 0) return new NoteEdit(text, start, 0);

        var body = text.Substring(start, length);
        var sb   = new StringBuilder(body.Length);

        for (var i = 0; i < body.Length;)
        {
            var marker = InlineMarkers.FirstOrDefault(mk => i + mk.Length <= body.Length
                                                            && body.AsSpan(i, mk.Length).SequenceEqual(mk));
            if (marker is not null) { i += marker.Length; continue; }
            sb.Append(body[i++]);
        }

        var cleaned = sb.ToString();
        return new NoteEdit(text[..start] + cleaned + text[(start + length)..], start, cleaned.Length);
    }

    /// <summary>Ссылка: «[выделенное](url)». Без выделения — пустые скобки с кареткой внутри.</summary>
    public static NoteEdit Link(string text, int start, int length)
    {
        text ??= "";
        Normalize(text, ref start, ref length);

        if (length == 0)
        {
            var (ws, we) = WordAt(text, start);
            if (we > ws) { start = ws; length = we - ws; }
        }

        var label = text.Substring(start, length);
        var next  = text[..start] + "[" + label + "](url)" + text[(start + length)..];
        // Выделяем «url»: это единственное, что человеку осталось заменить.
        return new NoteEdit(next, start + 1 + label.Length + 2, 3);
    }

    // ── Признак строки ──────────────────────────────────────────────────────────

    /// <summary>
    /// Поставить или снять признак на каждой задетой строке. Если он уже есть у всех — снимаем,
    /// иначе ставим всем: так повторное нажатие всегда возвращает текст к прежнему виду.
    /// </summary>
    public static NoteEdit TogglePrefix(string text, int start, int length, NotePrefix prefix)
    {
        text ??= "";
        Normalize(text, ref start, ref length);

        var (from, to) = LineRange(text, start, length);
        var lines      = text[from..to].Split('\n');

        var meaningful = lines.Where(static l => l.Trim().Length > 0).ToArray();
        var already    = meaningful.Length > 0 && meaningful.All(l => HasPrefix(l, prefix));

        var counter = 1;
        var result  = lines.Select(line =>
        {
            if (line.Trim().Length == 0) return line;

            var indent = Indent(line);
            var body   = StripAnyPrefix(line[indent.Length..]);
            if (already) return indent + body;

            var mark = prefix switch
            {
                NotePrefix.Bullet  => "- ",
                NotePrefix.Ordered => $"{counter++}. ",
                _                  => "> ",
            };
            return indent + mark + body;
        });

        return Replace(text, from, to, string.Join('\n', result));
    }

    /// <summary>
    /// Заголовок уровня 1..6 на каждой задетой строке; <c>level == 0</c> — снять. Повторное
    /// применение того же уровня тоже снимает.
    /// </summary>
    public static NoteEdit SetHeading(string text, int start, int length, int level)
    {
        text ??= "";
        Normalize(text, ref start, ref length);

        var (from, to) = LineRange(text, start, length);
        var lines      = text[from..to].Split('\n');

        var meaningful = lines.Where(static l => l.Trim().Length > 0).ToArray();
        var already    = level > 0 && meaningful.Length > 0 && meaningful.All(l => HeadingLevel(l) == level);
        var target     = already ? 0 : Math.Clamp(level, 0, 6);

        var result = lines.Select(line =>
        {
            if (line.Trim().Length == 0) return line;
            // Заголовок несовместим ни со списком, ни с цитатой — прежний признак снимаем.
            var body = StripAnyPrefix(StripHeading(line.TrimStart()));
            return target == 0 ? body : new string('#', target) + " " + body;
        });

        return Replace(text, from, to, string.Join('\n', result));
    }

    // ── Блоки ───────────────────────────────────────────────────────────────────

    /// <summary>Вставить блок отдельными строками, отбив его пустыми строками от соседей.</summary>
    public static NoteEdit InsertBlock(string text, int start, int length, NoteBlock block)
    {
        text ??= "";
        Normalize(text, ref start, ref length);

        var body = block switch
        {
            NoteBlock.Table => "| Колонка | Колонка |\n|---|---|\n|  |  |",
            NoteBlock.Rule  => "---",
            _               => "```\n\n```",
        };

        // Каретка посреди строки — блок начинается с новой: иначе он прилипнет к чужому абзацу
        // и перестанет быть блоком.
        var at     = start + length;
        var before = at > 0 && text[at - 1] != '\n' ? "\n\n" : NeedsBlank(text, at, back: true) ? "\n" : "";
        var after  = at < text.Length && text[at] != '\n' ? "\n\n" : NeedsBlank(text, at, back: false) ? "\n" : "";

        var insert = before + body + after;
        var next   = text[..at] + insert + text[at..];

        // Выделяем первую значимую строку блока — ее человек и будет править.
        var caret = at + before.Length;
        var first = body.IndexOf('\n') is var nl && nl > 0 ? nl : body.Length;
        return new NoteEdit(next, caret, block == NoteBlock.CodeFence ? 0 : first);
    }

    // ── Минимальная правка ──────────────────────────────────────────────────────

    /// <summary>
    /// Разница между старым и новым текстом одним куском: общий префикс и суффикс отброшены.
    /// Нужна редактору — команда обязана лечь в историю ОДНИМ шагом отмены, а замена всего текста
    /// откатывала бы Ctrl+Z всю заметку целиком.
    /// </summary>
    public static (int Offset, int RemoveLength, string Insert) Minimize(string oldText, string newText)
    {
        oldText ??= "";
        newText ??= "";

        var head = 0;
        var max  = Math.Min(oldText.Length, newText.Length);
        while (head < max && oldText[head] == newText[head]) head++;

        var tail = 0;
        while (tail < max - head && oldText[^(tail + 1)] == newText[^(tail + 1)]) tail++;

        return (head, oldText.Length - head - tail, newText[head..(newText.Length - tail)]);
    }

    // ── Мелочи ──────────────────────────────────────────────────────────────────

    private static void Normalize(string text, ref int start, ref int length)
    {
        start  = Math.Clamp(start, 0, text.Length);
        length = Math.Clamp(length, 0, text.Length - start);
    }

    private static bool Has(string text, int at, string marker) =>
        at >= 0 && at + marker.Length <= text.Length && text.AsSpan(at, marker.Length).SequenceEqual(marker);

    // Убрать по m знаков с каждого края куска [from, to).
    private static NoteEdit Cut(string text, int from, int to, int head, int tail)
    {
        var body = text[(from + head)..(to - tail)];
        return new NoteEdit(text[..from] + body + text[to..], from, body.Length);
    }

    private static NoteEdit WrapEachLine(string text, int start, int end, string marker)
    {
        var (from, to) = LineRange(text, start, end - start);
        var lines = text[from..to].Split('\n')
                                  .Select(l => l.Trim().Length == 0 ? l : WrapOne(l, marker));
        return Replace(text, from, to, string.Join('\n', lines));
    }

    private static string WrapOne(string line, string marker)
    {
        var head = line.Length - line.TrimStart().Length;
        var tail = line.Length - line.TrimEnd().Length;
        var body = line[head..(line.Length - tail)];

        var m = marker.Length;
        if (body.Length >= 2 * m && body.StartsWith(marker, StringComparison.Ordinal)
                                 && body.EndsWith(marker, StringComparison.Ordinal))
            body = body[m..^m];
        else
            body = marker + body + marker;

        return line[..head] + body + line[(line.Length - tail)..];
    }

    private static NoteEdit Replace(string text, int from, int to, string body) =>
        new(text[..from] + body + text[to..], from, body.Length);

    // Границы строк, задетых выделением: от начала первой до конца последней.
    private static (int From, int To) LineRange(string text, int start, int length)
    {
        var from = text.LastIndexOf('\n', Math.Max(start - 1, 0)) + 1;
        if (start == 0) from = 0;

        var at = start + length;
        var to = text.IndexOf('\n', Math.Min(at, Math.Max(text.Length - 1, 0)));
        if (at >= text.Length || to < 0) to = text.Length;
        return (Math.Min(from, text.Length), Math.Max(to, from));
    }

    // Слово под кареткой: буквы, цифры и знаки внутри слова.
    private static (int Start, int End) WordAt(string text, int at)
    {
        var s = at;
        while (s > 0 && IsWordChar(text[s - 1])) s--;
        var e = at;
        while (e < text.Length && IsWordChar(text[e])) e++;
        return (s, e);
    }

    private static bool IsWordChar(char c) => char.IsLetterOrDigit(c) || c is '_' or '-';

    private static string Indent(string line) => line[..(line.Length - line.TrimStart().Length)];

    private static bool HasPrefix(string line, NotePrefix prefix)
    {
        var body = line.TrimStart();
        return prefix switch
        {
            NotePrefix.Bullet  => body.Length > 1 && body[0] is '-' or '*' or '+' && body[1] == ' ',
            NotePrefix.Ordered => OrderedLength(body) > 0,
            _                  => body.StartsWith('>'),
        };
    }

    private static int OrderedLength(string body)
    {
        var i = 0;
        while (i < body.Length && char.IsDigit(body[i])) i++;
        if (i == 0 || i >= body.Length || body[i] is not ('.' or ')')) return 0;
        i++;
        if (i < body.Length && body[i] == ' ') i++;
        return i;
    }

    // Снять любой признак абзаца: пункт, номер, цитату. Признаки взаимоисключающие — «- > текст»
    // не бывает, поэтому список задач и цитата не наслаиваются друг на друга.
    private static string StripAnyPrefix(string body)
    {
        if (body.Length > 1 && body[0] is '-' or '*' or '+' && body[1] == ' ') return body[2..];

        var ordered = OrderedLength(body);
        if (ordered > 0) return body[ordered..];

        if (body.StartsWith('>'))
        {
            var i = 1;
            if (i < body.Length && body[i] == ' ') i++;
            return body[i..];
        }
        return body;
    }

    private static int HeadingLevel(string line)
    {
        var body = line.TrimStart();
        var i    = 0;
        while (i < body.Length && body[i] == '#') i++;
        return i is > 0 and <= 6 && i < body.Length && body[i] == ' ' ? i : 0;
    }

    private static string StripHeading(string body)
    {
        var i = 0;
        while (i < body.Length && body[i] == '#') i++;
        if (i is 0 or > 6) return body;
        while (i < body.Length && body[i] == ' ') i++;
        return body[i..];
    }

    // Нужна ли пустая строка между блоком и соседом.
    private static bool NeedsBlank(string text, int at, bool back)
    {
        if (back)
        {
            if (at == 0) return false;
            var prev = text.LastIndexOf('\n', at - 1);
            return prev >= 0 && text[(prev + 1)..at].Trim().Length > 0;
        }

        if (at >= text.Length) return false;
        var next = text.IndexOf('\n', at);
        var line = next < 0 ? text[at..] : text[at..next];
        return line.Trim().Length > 0;
    }
}
