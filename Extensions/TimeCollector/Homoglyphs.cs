namespace SmartBase.Extensions.TimeCollector;

/// <summary>
/// Приведение текста к ОДНОМУ алфавиту по гомоглифам — буквам, которые в кириллице и латинице
/// выглядят одинаково, а для программы остаются разными символами.
///
/// Зачем: расчеты набивают в двух раскладках вперемешку, и в русском слове оказывается
/// латинская «C», а в английском — кириллическая «О». На глаз не видно, но строки разные,
/// и в своде это разъезжается по-крупному: «Обозначение КД» — ключ сортировки и раскраски
/// (см. <c>TimeCollectorEngine.Sort</c> и <c>PlanColors</c>), так что одно обозначение,
/// набранное двумя способами, дает две группы вместо одной.
///
/// Решение принимается ПО СЛОВУ, а не по символу и не по всему значению. По символу — нельзя:
/// отдельная «С» не несет ни малейшего указания, чем ей быть. По значению — тоже: наименования
/// и проекты сплошь и рядом двуязычны («Корпус ABC-200»), и английское слово в русской фразе
/// законно. Слово здесь — связный кусок букв и цифр; точка, дефис и пробел его разрывают.
/// </summary>
internal static class Homoglyphs
{
    /// <summary>
    /// Итог нормализации одного значения.
    /// </summary>
    /// <param name="Text">Текст, приведенный к одному алфавиту; менять было нечего — исходный.</param>
    /// <param name="Changed">Была ли хоть одна замена.</param>
    /// <param name="Mixed">Сколько слов оставлено КАК ЕСТЬ, потому что решить нельзя: в слове
    /// либо однозначные буквы обоих алфавитов, либо гомоглифы поровну. Такие стоит показать
    /// человеку — угадывать за него мы не беремся.</param>
    internal readonly record struct Result(string Text, bool Changed, int Mixed);

    /// <summary>Каким алфавитом слово должно быть записано.</summary>
    private enum Script
    {
        /// <summary>Решать нечего: слово уже в одном алфавите или букв в нем нет.</summary>
        None,
        Cyrillic,
        Latin,
        /// <summary>Решить нельзя — слово не трогаем.</summary>
        Mixed,
    }

    /// <summary>Что за буква с точки зрения алфавита.</summary>
    private enum Kind
    {
        /// <summary>Цифра, знак, буква чужого письма — на решение не влияет.</summary>
        Other,
        /// <summary>Кириллическая буква с латинским двойником.</summary>
        CyrillicTwin,
        /// <summary>Латинская буква с кириллическим двойником.</summary>
        LatinTwin,
        /// <summary>Кириллица без двойника — однозначный признак русского слова.</summary>
        Cyrillic,
        /// <summary>Латиница без двойника — однозначный признак английского слова.</summary>
        Latin,
    }

    /// <summary>
    /// Привести значение к одному алфавиту.
    ///
    /// Слово разбирается по лестнице из трех ступеней:
    /// <list type="number">
    /// <item>есть буквы БЕЗ двойника, и все одного алфавита — язык известен, двойники переводятся
    /// в него («Корпуc» с латинской «c» → «Корпус»: «К», «п», «р» латинских двойников не имеют);</item>
    /// <item>такие буквы есть в обоих алфавитах — слово не трогаем и считаем в
    /// <see cref="Result.Mixed"/>: это либо законная смесь, либо порча, которую нельзя разобрать
    /// без человека;</item>
    /// <item>однозначных букв нет вовсе — слово целиком из двойников («СТО», «TOP»). Решает
    /// большинство: одна сбитая буква из пяти выравнивается по остальным четырем. Поровну —
    /// не трогаем.</item>
    /// </list>
    ///
    /// ЦИФРЫ НЕ ТРОГАЮТСЯ НИКОГДА, хотя «0»/«О» и «3»/«З» — те же гомоглифы. Обозначения состоят
    /// из букв и цифр вперемешку, и подмена нуля буквой уничтожила бы номер. Чтобы решать про
    /// цифры, надо знать формат обозначения — это другая задача.
    /// </summary>
    internal static Result Normalize(string text)
    {
        char[]? fixedText = null;
        var mixed = 0;

        for (var i = 0; i < text.Length;)
        {
            if (!char.IsLetterOrDigit(text[i])) { i++; continue; }

            var start = i;
            while (i < text.Length && char.IsLetterOrDigit(text[i])) i++;

            switch (Decide(text, start, i))
            {
                case Script.Cyrillic: Apply(text, start, i, toCyrillic: true,  ref fixedText); break;
                case Script.Latin:    Apply(text, start, i, toCyrillic: false, ref fixedText); break;
                case Script.Mixed:    mixed++; break;
            }
        }

        return fixedText is null
            ? new Result(text, Changed: false, mixed)
            : new Result(new string(fixedText), Changed: true, mixed);
    }

    private static Script Decide(string text, int start, int end)
    {
        int cyrillic = 0, latin = 0, cyrillicTwins = 0, latinTwins = 0;

        for (var i = start; i < end; i++)
            switch (Classify(text[i]))
            {
                case Kind.Cyrillic:     cyrillic++;      break;
                case Kind.Latin:        latin++;         break;
                case Kind.CyrillicTwin: cyrillicTwins++; break;
                case Kind.LatinTwin:    latinTwins++;    break;
            }

        // 1. Однозначные буквы одного алфавита — язык слова известен.
        if (cyrillic > 0 && latin == 0) return Script.Cyrillic;
        if (latin > 0 && cyrillic == 0) return Script.Latin;

        // 2. Однозначные буквы обоих — за человека не решаем.
        if (cyrillic > 0) return Script.Mixed;

        // 3. Слово целиком из двойников. Один алфавит — менять нечего, два — решает большинство.
        if (cyrillicTwins == 0 || latinTwins == 0)  return Script.None;
        if (cyrillicTwins > latinTwins)             return Script.Cyrillic;
        if (latinTwins > cyrillicTwins)             return Script.Latin;
        return Script.Mixed;
    }

    private static void Apply(string text, int start, int end, bool toCyrillic, ref char[]? fixedText)
    {
        for (var i = start; i < end; i++)
        {
            var replacement = toCyrillic ? ToCyrillic(text[i]) : ToLatin(text[i]);
            if (replacement == '\0') continue;

            // Копия делается лениво: у подавляющего большинства значений менять нечего,
            // а свод — это сотни тысяч строк по четыре текстовых поля в каждой.
            fixedText ??= text.ToCharArray();
            fixedText[i] = replacement;
        }
    }

    private static Kind Classify(char c)
    {
        if (ToLatin(c)    != '\0') return Kind.CyrillicTwin;
        if (ToCyrillic(c) != '\0') return Kind.LatinTwin;

        if (c is >= 'a' and <= 'z' or >= 'A' and <= 'Z')  return Kind.Latin;
        if (c is >= 'А' and <= 'я' or 'Ё' or 'ё')         return Kind.Cyrillic;
        return Kind.Other;
    }

    /// <summary>
    /// Кириллическая буква → латинский двойник; <c>'\0'</c> — двойника нет.
    ///
    /// Пары только НЕОТЛИЧИМЫЕ на глаз: в этом весь смысл замены — если разница видна, человек
    /// написал так намеренно. Отсюда несимметричность регистров, и это не опечатка: заглавные
    /// «М» и «M» совпадают, а строчные «м» и «m» — нет; то же с «К»/«k», «Н»/«h», «Т»/«t»,
    /// «В»/«b». Поэтому строчные «b d f g h k l m n q r s t v w z» — надежный признак
    /// английского слова, а не двойники.
    /// </summary>
    private static char ToLatin(char c) => c switch
    {
        'А' => 'A', 'В' => 'B', 'Е' => 'E', 'К' => 'K', 'М' => 'M', 'Н' => 'H',
        'О' => 'O', 'Р' => 'P', 'С' => 'C', 'Т' => 'T', 'У' => 'Y', 'Х' => 'X',
        'а' => 'a', 'е' => 'e', 'о' => 'o', 'р' => 'p', 'с' => 'c', 'у' => 'y', 'х' => 'x',
        _   => '\0',
    };

    /// <summary>Латинская буква → кириллический двойник; <c>'\0'</c> — двойника нет.
    /// Обратная сторона <see cref="ToLatin"/>, тот же список.</summary>
    private static char ToCyrillic(char c) => c switch
    {
        'A' => 'А', 'B' => 'В', 'E' => 'Е', 'K' => 'К', 'M' => 'М', 'H' => 'Н',
        'O' => 'О', 'P' => 'Р', 'C' => 'С', 'T' => 'Т', 'Y' => 'У', 'X' => 'Х',
        'a' => 'а', 'e' => 'е', 'o' => 'о', 'p' => 'р', 'c' => 'с', 'y' => 'у', 'x' => 'х',
        _   => '\0',
    };
}
