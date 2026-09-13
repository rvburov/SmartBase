using System.Globalization;
using System.Text;
using SmartBase.Core.Models;

namespace SmartBase.Integrations;

/// <summary>
/// Выражение над строкой — язык шага «преобразовать строки» (docs/ROADMAP-API.md, шаг 12).
///
/// <b>Это нарочно не язык программирования.</b> Вход — значения одной строки, выход — одно
/// значение; ни файлов, ни сети, ни базы, ни циклов, ни переменных. Безопасность здесь по
/// построению, а не по проверке: нечему запрещать открывать сокет, если открывать нечем.
/// Ровно на этой границе выражение остается ДАННЫМИ и не становится программой — граница
/// записана в разделе 13 роадмапа как обещание, и переносить ее нельзя.
///
/// Что умеет: арифметика, сравнение, ЕСЛИ, склейка и обрезка строк, дата ± дни, разбор числа из
/// текста. Колонка пишется <c>{{Имя}}</c> — той же записью, что в шагах «действие» и «создать
/// запись»: двух способов сослаться на колонку в одном сценарии быть не должно.
///
/// Разбор отделен от вычисления намеренно. Выражение разбирается ОДИН раз на шаг, а считается на
/// каждой строке: пятьсот строк и десять выражений — это пять тысяч вычислений, и разбирать
/// текст заново на каждом было бы расточительством без всякой пользы. Заодно ошибка записи
/// находится до первой строки и говорится человеку целиком.
/// </summary>
public sealed class Formula
{
    /// <summary>
    /// Сколько знаков умещается в одном выражении. Предохранитель того же рода, что потолок шагов
    /// и условий: выражение в полтысячи знаков еще читается глазами, а в полторы тысячи уже
    /// живет своей жизнью — и разбираться с ним приходится отладчиком, которого здесь нет.
    /// </summary>
    public const int MaxLength = 500;

    /// <summary>
    /// Предел вложенности скобок и вызовов. Защита разбора от самого себя: рекурсивный спуск на
    /// строке из тысячи открывающих скобок уронил бы не выражение, а программу целиком.
    /// </summary>
    public const int MaxDepth = 16;

    private readonly Node _root;

    private Formula(Node root, IReadOnlyList<string> columns)
    {
        _root   = root;
        Columns = columns;
    }

    /// <summary>
    /// Колонки, которые выражение называет, — в порядке появления и без повторов. Шаг проверяет
    /// их ДО первой строки: опечатка в имени иначе всплыла бы на середине выгрузки, оставив
    /// половину строк посчитанной.
    /// </summary>
    public IReadOnlyList<string> Columns { get; }

    /// <summary>
    /// Разобрать выражение; <c>null</c> — не разобралось, и причина в <paramref name="trouble"/>
    /// человеческими словами («нет закрывающей скобки», «функция ОКРУГЛИТЬ не знакома»).
    /// </summary>
    public static Formula? TryParse(string? text, out string trouble)
    {
        trouble = "";

        var source = (text ?? "").Trim();

        if (source.Length == 0)
        {
            trouble = "выражение не заполнено";
            return null;
        }

        if (source.Length > MaxLength)
        {
            trouble = $"выражение длиннее {MaxLength} знаков";
            return null;
        }

        try
        {
            var reader  = new Reader(Scan(source));
            var root    = reader.Expression(0);

            reader.End();

            return new Formula(root, reader.Columns);
        }
        catch (FormulaTrouble failure)
        {
            trouble = failure.Message;
            return null;
        }
    }

    /// <summary>
    /// Посчитать выражение на одной строке. Пустой <paramref name="trouble"/> — значение готово;
    /// непустой — считать не вышло, и это останавливает шаг: молча подставленная пустота дала бы
    /// колонку, неотличимую от честно пустой.
    /// </summary>
    /// <param name="row">Значения строки; колонки ищутся без учета регистра.</param>
    /// <param name="now">Момент запуска — его видит <c>СЕГОДНЯ()</c>. Приходит параметром, как
    /// и везде в обмене: иначе выражение с датой нельзя проверить, не подводя системное время.</param>
    public string Run(IReadOnlyDictionary<string, string> row, DateTimeOffset now, out string trouble)
    {
        trouble = "";

        try
        {
            return Eval(_root, new Cell(row, now)).ToText();
        }
        catch (FormulaTrouble failure)
        {
            trouble = failure.Message;
            return "";
        }
    }

    /// <summary>Справка по функциям — то, что видно в окне настройки шага.</summary>
    public static IReadOnlyList<(string Signature, string What)> Help { get; } =
    [
        ("ЕСЛИ(условие; тогда; иначе)", "выбор по условию"),
        ("И(…) · ИЛИ(…) · НЕ(…)",       "несколько условий разом"),
        ("ПУСТО(значение)",             "значения нет или одни пробелы"),
        ("ЧИСЛО(текст)",                "число из текста: «1 250 руб.» → 1250"),
        ("ОКРУГЛ(число; знаков)",       "округление"),
        ("СЦЕПИТЬ(…)",                  "склейка значений в одну строку"),
        ("ОБРЕЗАТЬ(текст)",             "пробелы по краям долой"),
        ("ЛЕВО(текст; N) · ПРАВО(текст; N)", "первые или последние N знаков"),
        ("ДЛИНА(текст)",                "сколько знаков"),
        ("ЗАМЕНИТЬ(текст; что; чем)",   "замена всех вхождений"),
        ("ВЕРХНИЙ(текст) · НИЖНИЙ(текст)", "регистр"),
        ("ДАТА(текст)",                 "дата из текста; к ней можно прибавлять дни"),
        ("СЕГОДНЯ()",                   "день запуска сценария"),
    ];

    // ── Разбор на знаки ───────────────────────────────────────────────────────

    private enum TokenKind { Number, Text, Column, Name, Sign, Open, Close, Comma, End }

    private readonly record struct Token(TokenKind Kind, string Text, double Number = 0);

    /// <summary>
    /// Текст выражения на знаки. Ошибки здесь называют место словами: человек правит выражение
    /// в однострочном поле, и «непонятный знак» без указания, какой именно, ему не помогает.
    /// </summary>
    private static List<Token> Scan(string text)
    {
        var tokens = new List<Token>();
        var at     = 0;

        while (at < text.Length)
        {
            var symbol = text[at];

            if (char.IsWhiteSpace(symbol)) { at++; continue; }

            // Колонка: {{Имя}}. Запись та же, что в остальных шагах сценария.
            if (symbol == '{' && at + 1 < text.Length && text[at + 1] == '{')
            {
                var close = text.IndexOf("}}", at + 2, StringComparison.Ordinal);

                if (close < 0)
                    throw new FormulaTrouble("после {{ нет закрывающих }}");

                var name = text[(at + 2)..close].Trim();

                if (name.Length == 0)
                    throw new FormulaTrouble("в {{ }} не названа колонка");

                tokens.Add(new Token(TokenKind.Column, name));
                at = close + 2;
                continue;
            }

            // Текст в кавычках; две кавычки подряд внутри — сама кавычка.
            if (symbol == '"')
            {
                var value = new StringBuilder();
                var index = at + 1;
                var ended = false;

                while (index < text.Length)
                {
                    if (text[index] == '"')
                    {
                        if (index + 1 < text.Length && text[index + 1] == '"')
                        {
                            value.Append('"');
                            index += 2;
                            continue;
                        }

                        ended = true;
                        index++;
                        break;
                    }

                    value.Append(text[index]);
                    index++;
                }

                if (!ended) throw new FormulaTrouble("текст в кавычках не закрыт");

                tokens.Add(new Token(TokenKind.Text, value.ToString()));
                at = index;
                continue;
            }

            if (char.IsDigit(symbol))
            {
                var start = at;
                var dot   = false;

                while (at < text.Length && (char.IsDigit(text[at]) || (text[at] == '.' && !dot)))
                {
                    if (text[at] == '.') dot = true;
                    at++;
                }

                var number = text[start..at];

                // Разделитель дробной части в ЛИТЕРАЛЕ — точка, и только она: запятая здесь
                // разделяет аргументы функции, и «ОКРУГЛ(1,5; 2)» иначе означало бы две разные
                // вещи в зависимости от того, как посмотреть.
                if (!double.TryParse(number, NumberStyles.Float, CultureInfo.InvariantCulture,
                                     out var value))
                    throw new FormulaTrouble($"«{number}» — это не число");

                tokens.Add(new Token(TokenKind.Number, number, value));
                continue;
            }

            if (char.IsLetter(symbol))
            {
                var start = at;
                while (at < text.Length && (char.IsLetter(text[at]) || char.IsDigit(text[at]))) at++;

                tokens.Add(new Token(TokenKind.Name, text[start..at]));
                continue;
            }

            switch (symbol)
            {
                case '(': tokens.Add(new Token(TokenKind.Open,  "(")); at++; continue;
                case ')': tokens.Add(new Token(TokenKind.Close, ")")); at++; continue;

                // Аргументы разделяются точкой с запятой — как в русском Excel, откуда человек и
                // приходит. Запятая принимается тоже: набравшему ее незачем объяснять разницу.
                case ';':
                case ',': tokens.Add(new Token(TokenKind.Comma, ";")); at++; continue;

                case '+':
                case '-':
                case '*':
                case '/':
                case '=': tokens.Add(new Token(TokenKind.Sign, symbol.ToString())); at++; continue;

                case '<':
                    if (at + 1 < text.Length && text[at + 1] == '>')
                    {
                        tokens.Add(new Token(TokenKind.Sign, "<>"));
                        at += 2;
                        continue;
                    }

                    if (at + 1 < text.Length && text[at + 1] == '=')
                    {
                        tokens.Add(new Token(TokenKind.Sign, "<="));
                        at += 2;
                        continue;
                    }

                    tokens.Add(new Token(TokenKind.Sign, "<"));
                    at++;
                    continue;

                case '>':
                    if (at + 1 < text.Length && text[at + 1] == '=')
                    {
                        tokens.Add(new Token(TokenKind.Sign, ">="));
                        at += 2;
                        continue;
                    }

                    tokens.Add(new Token(TokenKind.Sign, ">"));
                    at++;
                    continue;

                default:
                    throw new FormulaTrouble($"знак «{symbol}» в выражении не понятен");
            }
        }

        tokens.Add(new Token(TokenKind.End, ""));
        return tokens;
    }

    // ── Дерево ────────────────────────────────────────────────────────────────

    private abstract record Node;

    private sealed record Fixed(FormulaValue Value)                  : Node;
    private sealed record Column(string Name)                        : Node;
    private sealed record Minus(Node Inner)                          : Node;
    private sealed record Pair(string Sign, Node Left, Node Right)   : Node;
    private sealed record Call(string Name, IReadOnlyList<Node> Args) : Node;

    /// <summary>
    /// Разбор по правилам приоритета: сравнение → сложение → умножение → знак → скобки. Спуск
    /// рекурсивный и без таблиц: правил шесть, и таблица разбора была бы длиннее их самих.
    /// </summary>
    private sealed class Reader(List<Token> tokens)
    {
        private int _at;

        private readonly List<string> _columns = [];

        public IReadOnlyList<string> Columns => _columns;

        private Token Current => tokens[_at];

        private Token Take()
        {
            var token = tokens[_at];
            if (token.Kind != TokenKind.End) _at++;
            return token;
        }

        /// <summary>Разобрано ли все до конца; хвост — это почти всегда лишняя скобка.</summary>
        public void End()
        {
            if (Current.Kind == TokenKind.End) return;

            throw new FormulaTrouble(Current.Kind == TokenKind.Close
                ? "закрывающих скобок больше, чем открывающих"
                : $"выражение кончилось, а «{Current.Text}» осталось");
        }

        public Node Expression(int depth)
        {
            if (depth > MaxDepth)
                throw new FormulaTrouble($"вложенность глубже {MaxDepth} — разложите выражение "
                                       + "на несколько колонок");

            var left = Sum(depth);

            if (Current.Kind == TokenKind.Sign
                && Current.Text is "=" or "<>" or "<" or ">" or "<=" or ">=")
            {
                var sign = Take().Text;
                return new Pair(sign, left, Sum(depth));
            }

            return left;
        }

        private Node Sum(int depth)
        {
            var left = Product(depth);

            while (Current.Kind == TokenKind.Sign && Current.Text is "+" or "-")
            {
                var sign = Take().Text;
                left = new Pair(sign, left, Product(depth));
            }

            return left;
        }

        private Node Product(int depth)
        {
            var left = Signed(depth);

            while (Current.Kind == TokenKind.Sign && Current.Text is "*" or "/")
            {
                var sign = Take().Text;
                left = new Pair(sign, left, Signed(depth));
            }

            return left;
        }

        private Node Signed(int depth)
        {
            if (Current.Kind == TokenKind.Sign && Current.Text == "-")
            {
                Take();
                return new Minus(Signed(depth));
            }

            // Плюс перед числом ничего не меняет, но написать его человеку никто не запрещал.
            if (Current.Kind == TokenKind.Sign && Current.Text == "+")
            {
                Take();
                return Signed(depth);
            }

            return Primary(depth);
        }

        private Node Primary(int depth)
        {
            var token = Take();

            switch (token.Kind)
            {
                case TokenKind.Number:
                    return new Fixed(FormulaValue.Of(token.Number));

                case TokenKind.Text:
                    return new Fixed(FormulaValue.Of(token.Text));

                case TokenKind.Column:
                    if (!_columns.Contains(token.Text, StringComparer.OrdinalIgnoreCase))
                        _columns.Add(token.Text);

                    return new Column(token.Text);

                case TokenKind.Open:
                    var inner = Expression(depth + 1);
                    Close();
                    return inner;

                case TokenKind.Name:
                    return Function(token.Text, depth);

                case TokenKind.End:
                    throw new FormulaTrouble("выражение обрывается на середине");

                default:
                    throw new FormulaTrouble($"«{token.Text}» стоит не на своем месте");
            }
        }

        private Node Function(string name, int depth)
        {
            var known = Formula.Known(name);

            if (known is null)
                throw new FormulaTrouble($"функция {name.ToUpperInvariant()} не знакома; "
                                       + "какие есть — написано в окне под таблицей");

            if (Current.Kind != TokenKind.Open)
                throw new FormulaTrouble($"после {known.Value.Name} нужна скобка со значениями");

            Take();

            var args = new List<Node>();

            if (Current.Kind == TokenKind.Close)
            {
                Take();
            }
            else
            {
                while (true)
                {
                    args.Add(Expression(depth + 1));

                    if (Current.Kind == TokenKind.Comma) { Take(); continue; }

                    Close();
                    break;
                }
            }

            var (canonical, least, most) = known.Value;

            if (args.Count < least || args.Count > most)
                throw new FormulaTrouble(least == most
                    ? $"{canonical} ждет значений: {least}, а их {args.Count}"
                    : $"{canonical} ждет от {least} до {most} значений, а их {args.Count}");

            return new Call(canonical, args);
        }

        private void Close()
        {
            if (Current.Kind != TokenKind.Close)
                throw new FormulaTrouble("не хватает закрывающей скобки");

            Take();
        }
    }

    // ── Функции ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Что знает язык. Список короткий и таким задуман: каждая функция — это то, что иначе
    /// пришлось бы писать скриптом, и ни одна из них не открывает доступ никуда наружу.
    /// </summary>
    private static readonly (string Name, int Least, int Most)[] Names =
    [
        ("ЕСЛИ",      3, 3),
        ("И",         2, 10),
        ("ИЛИ",       2, 10),
        ("НЕ",        1, 1),
        ("ПУСТО",     1, 1),
        ("ЧИСЛО",     1, 1),
        ("ОКРУГЛ",    2, 2),
        ("СЦЕПИТЬ",   1, 10),
        ("ОБРЕЗАТЬ",  1, 1),
        ("ЛЕВО",      2, 2),
        ("ПРАВО",     2, 2),
        ("ДЛИНА",     1, 1),
        ("ЗАМЕНИТЬ",  3, 3),
        ("ВЕРХНИЙ",   1, 1),
        ("НИЖНИЙ",    1, 1),
        ("ДАТА",      1, 1),
        ("СЕГОДНЯ",   0, 0),
    ];

    /// <summary>Известна ли такая функция; регистр не важен — их пишут и строчными.</summary>
    private static (string Name, int Least, int Most)? Known(string name)
    {
        foreach (var known in Names)
            if (string.Equals(known.Name, name, StringComparison.OrdinalIgnoreCase))
                return known;

        return null;
    }

    // ── Вычисление ────────────────────────────────────────────────────────────

    /// <summary>Строка и момент запуска — все, что выражение вообще может увидеть.</summary>
    private readonly record struct Cell(IReadOnlyDictionary<string, string> Row, DateTimeOffset Now);

    private static FormulaValue Eval(Node node, Cell cell) => node switch
    {
        Fixed  fixedValue => fixedValue.Value,
        Column column     => FormulaValue.Of(Value(column.Name, cell.Row)),
        Minus  minus      => FormulaValue.Of(-Eval(minus.Inner, cell).AsNumber("после знака минус")),
        Pair   pair       => Apply(pair, cell),
        Call   call       => Invoke(call, cell),
        _                 => throw new FormulaTrouble("выражение не разобралось"),
    };

    /// <summary>
    /// Значение колонки. Колонки в строке нет — это отказ, а не пустота: шаг проверяет имена до
    /// первой строки, и сюда такое доходит только когда строки в одной выгрузке разного состава.
    /// </summary>
    private static string Value(string name, IReadOnlyDictionary<string, string> row)
    {
        if (row.TryGetValue(name, out var exact)) return exact ?? "";

        foreach (var (column, value) in row)
            if (string.Equals(column, name, StringComparison.OrdinalIgnoreCase))
                return value ?? "";

        throw new FormulaTrouble($"колонки «{name}» в строке нет");
    }

    // ── Действия над двумя значениями ─────────────────────────────────────────

    private static FormulaValue Apply(Pair pair, Cell cell)
    {
        var left  = Eval(pair.Left,  cell);
        var right = Eval(pair.Right, cell);

        return pair.Sign switch
        {
            "+" => Add(left, right),
            "-" => Subtract(left, right),
            "*" => FormulaValue.Of(left.AsNumber("слева от «*»") * right.AsNumber("справа от «*»")),
            "/" => Divide(left, right),
            _   => FormulaValue.Of(Compare(pair.Sign, left, right)),
        };
    }

    /// <summary>Сложение; дата плюс число — это дни, и другого прочтения у него нет.</summary>
    private static FormulaValue Add(FormulaValue left, FormulaValue right)
    {
        if (left.Kind == FormulaKind.Date)
        {
            var (moment, timed) = left.AsDate("слева от «+»");
            return FormulaValue.OfDate(moment.AddDays(right.AsNumber("справа от «+»")), timed);
        }

        if (right.Kind == FormulaKind.Date)
        {
            var (moment, timed) = right.AsDate("справа от «+»");
            return FormulaValue.OfDate(moment.AddDays(left.AsNumber("слева от «+»")), timed);
        }

        // Склейки знаком «плюс» нет намеренно: «1» + «2» тогда означало бы то 3, то «12» в
        // зависимости от того, что пришло в колонке. Склеивает СЦЕПИТЬ — и всегда одинаково.
        return FormulaValue.Of(left.AsNumber("слева от «+»") + right.AsNumber("справа от «+»"));
    }

    /// <summary>Вычитание; дата минус дата — это дни между ними.</summary>
    private static FormulaValue Subtract(FormulaValue left, FormulaValue right)
    {
        if (left.Kind == FormulaKind.Date && right.Kind == FormulaKind.Date)
        {
            var (from, _) = right.AsDate("справа от «-»");
            var (to,   _) = left.AsDate("слева от «-»");

            return FormulaValue.Of((to - from).TotalDays);
        }

        if (left.Kind == FormulaKind.Date)
        {
            var (moment, timed) = left.AsDate("слева от «-»");
            return FormulaValue.OfDate(moment.AddDays(-right.AsNumber("справа от «-»")), timed);
        }

        return FormulaValue.Of(left.AsNumber("слева от «-»") - right.AsNumber("справа от «-»"));
    }

    private static FormulaValue Divide(FormulaValue left, FormulaValue right)
    {
        var divisor = right.AsNumber("справа от «/»");

        // Ноль в знаменателе — отказ, а не «бесконечность»: колонка с надписью «∞» хуже
        // остановленного шага, потому что уедет дальше и ляжет в чужую запись.
        if (divisor == 0) throw new FormulaTrouble("деление на ноль");

        return FormulaValue.Of(left.AsNumber("слева от «/»") / divisor);
    }

    /// <summary>
    /// Сравнение. Равенство работает над чем угодно — над числами как числа, над датами как
    /// даты, над остальным как текст без учета регистра (так же, как в отборе: «Оплачен» и
    /// «оплачен» в чужой выгрузке — одно слово).
    ///
    /// А вот «больше» и «меньше» требуют чисел или дат. Сравнивать по величине ТЕКСТ — значит
    /// угадывать за человека, что «10» меньше «9»; ровно от этого отказались условия отбора, и
    /// здесь правило то же.
    /// </summary>
    private static bool Compare(string sign, FormulaValue left, FormulaValue right)
    {
        if (left.Kind == FormulaKind.Date || right.Kind == FormulaKind.Date)
        {
            var (first,  _) = left.AsDate($"слева от «{sign}»");
            var (second, _) = right.AsDate($"справа от «{sign}»");

            return Decide(sign, first.CompareTo(second));
        }

        if (Numeric(left, out var a) && Numeric(right, out var b))
            return Decide(sign, a.CompareTo(b));

        if (sign is not ("=" or "<>"))
            throw new FormulaTrouble(
                $"сравнить по величине «{left.ToText()}» и «{right.ToText()}» нельзя: "
                + "«больше» и «меньше» работают с числами и датами");

        var same = string.Equals(left.ToText().Trim(), right.ToText().Trim(),
                                 StringComparison.OrdinalIgnoreCase);

        return sign == "=" ? same : !same;
    }

    /// <summary>Годится ли значение числом — без отказа: это вопрос, а не приведение.</summary>
    private static bool Numeric(FormulaValue value, out double number)
    {
        number = 0;

        if (value.Kind == FormulaKind.Number) { number = value.AsNumber(""); return true; }
        if (value.Kind != FormulaKind.Text)   return false;

        return FormulaValue.TryNumber(value.ToText(), out number);
    }

    private static bool Decide(string sign, int order) => sign switch
    {
        "="  => order == 0,
        "<>" => order != 0,
        "<"  => order <  0,
        ">"  => order >  0,
        "<=" => order <= 0,
        _    => order >= 0,
    };

    // ── Вызовы функций ────────────────────────────────────────────────────────

    private static FormulaValue Invoke(Call call, Cell cell)
    {
        // Три функции считают свои значения ЛЕНИВО, и это не оптимизация, а смысл: ЕСЛИ(ПУСТО(…);
        // 0; ЧИСЛО(…)) обязано пережить пустую колонку, а посчитанная заранее вторая ветка
        // отказала бы на ней раньше, чем условие успело ее отвести.
        switch (call.Name)
        {
            case "ЕСЛИ":
                return Eval(call.Args[Eval(call.Args[0], cell).AsLogic("условие ЕСЛИ") ? 1 : 2], cell);

            case "И":
                foreach (var argument in call.Args)
                    if (!Eval(argument, cell).AsLogic("значение И")) return FormulaValue.Of(false);

                return FormulaValue.Of(true);

            case "ИЛИ":
                foreach (var argument in call.Args)
                    if (Eval(argument, cell).AsLogic("значение ИЛИ")) return FormulaValue.Of(true);

                return FormulaValue.Of(false);
        }

        var args = new FormulaValue[call.Args.Count];
        for (var i = 0; i < args.Length; i++) args[i] = Eval(call.Args[i], cell);

        return call.Name switch
        {
            "НЕ"       => FormulaValue.Of(!args[0].AsLogic("значение НЕ")),
            "ПУСТО"    => FormulaValue.Of(args[0].IsBlank),
            "ЧИСЛО"    => Number(args[0]),
            "ОКРУГЛ"   => Round(args[0], args[1]),
            "СЦЕПИТЬ"  => FormulaValue.Of(string.Concat(args.Select(a => a.ToText()))),
            "ОБРЕЗАТЬ" => FormulaValue.Of(args[0].ToText().Trim()),
            "ЛЕВО"     => Side(args[0], args[1], left: true),
            "ПРАВО"    => Side(args[0], args[1], left: false),
            "ДЛИНА"    => FormulaValue.Of((double)args[0].ToText().Length),
            "ЗАМЕНИТЬ" => Replace(args[0], args[1], args[2]),
            "ВЕРХНИЙ"  => FormulaValue.Of(args[0].ToText().ToUpperInvariant()),
            "НИЖНИЙ"   => FormulaValue.Of(args[0].ToText().ToLowerInvariant()),
            "ДАТА"     => Date(args[0]),
            "СЕГОДНЯ"  => FormulaValue.OfDate(cell.Now.ToLocalTime().Date, timed: false),
            _          => throw new FormulaTrouble($"функция {call.Name} не знакома"),
        };
    }

    /// <summary>Число из текста — терпимо к разрядам и хвостам: «1 250 руб.» это 1250.</summary>
    private static FormulaValue Number(FormulaValue value)
    {
        if (value.Kind == FormulaKind.Number) return value;

        if (FormulaValue.TryLooseNumber(value.ToText(), out var number))
            return FormulaValue.Of(number);

        throw new FormulaTrouble($"ЧИСЛО: в «{value.ToText().Trim()}» числа нет");
    }

    private static FormulaValue Round(FormulaValue value, FormulaValue places)
    {
        var digits = (int)Math.Round(places.AsNumber("знаки ОКРУГЛ"));

        if (digits is < 0 or > 10)
            throw new FormulaTrouble("ОКРУГЛ: знаков после запятой — от 0 до 10");

        // Половина уходит ВВЕРХ, как учили в школе: банковское округление в накладной на
        // 2,5 рубля объяснять получателю пришлось бы отдельно.
        return FormulaValue.Of(Math.Round(value.AsNumber("значение ОКРУГЛ"), digits,
                                          MidpointRounding.AwayFromZero));
    }

    private static FormulaValue Side(FormulaValue value, FormulaValue count, bool left)
    {
        var what = left ? "ЛЕВО" : "ПРАВО";
        var take = (int)Math.Round(count.AsNumber($"число знаков {what}"));

        if (take < 0) throw new FormulaTrouble($"{what}: число знаков не бывает отрицательным");

        var text = value.ToText();

        // Просят больше, чем есть, — отдаем что есть: обрезка по длине не должна ломать шаг из-за
        // того, что одна строка короче остальных.
        if (take >= text.Length) return FormulaValue.Of(text);

        return FormulaValue.Of(left ? text[..take] : text[^take..]);
    }

    private static FormulaValue Replace(FormulaValue value, FormulaValue what, FormulaValue with)
    {
        var sample = what.ToText();

        if (sample.Length == 0)
            throw new FormulaTrouble("ЗАМЕНИТЬ: не сказано, что менять");

        return FormulaValue.Of(value.ToText().Replace(sample, with.ToText(), StringComparison.Ordinal));
    }

    /// <summary>
    /// Дата из текста. Разбирает <see cref="DateValue"/> — тот же разбор, которым живут поля-даты
    /// и календарь: «01.02.2026», «2026-02-01», «01.02.2026 14:00». Берется НАЧАЛО: у диапазона
    /// «01.02.2026 - 03.02.2026» датой считается первый день.
    /// </summary>
    private static FormulaValue Date(FormulaValue value)
    {
        var text = value.ToText().Trim();

        if (text.Length == 0) throw new FormulaTrouble("ДАТА: пусто");

        if (!DateValue.TryParse(text, out var parsed))
            throw new FormulaTrouble($"ДАТА: «{text}» не похоже на дату — ждем «01.02.2026» "
                                   + "или «01.02.2026 14:00»");

        return FormulaValue.OfDate(parsed.Start, timed: !parsed.AllDay);
    }
}
