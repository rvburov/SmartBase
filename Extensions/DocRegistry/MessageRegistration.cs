using System.Text.RegularExpressions;

namespace SmartBase.Extensions.DocRegistry;

/// <summary>
/// Заявка на регистрацию, разобранная из сообщения и проверенная целиком.
/// </summary>
/// <param name="Org">Код организации — заглавными русскими буквами.</param>
/// <param name="Eskd">Шестизначный код ЕСКД.</param>
/// <param name="Number">Номер документа, исполнение которого заводится; <c>null</c> — новый
/// документ, номер подберется первым свободным. При <c>Exact</c> — полный заданный номер.</param>
/// <param name="Name">Наименование документа.</param>
/// <param name="Project">Проект.</param>
/// <param name="Fio">ФИО регистрирующего — как написано в сообщении.</param>
/// <param name="Exact">Передан полный номер документа без исполнения, а не маска.</param>
public sealed record RegistrationRequest(string Org, string Eskd, int? Number,
                                         string Name, string Project, string Fio, bool Exact = false)
{
    /// <summary>Заводится исполнение уже выданного документа, а не новый документ.</summary>
    public bool NewExecution => Number is not null && !Exact;
}

/// <summary>Один номер списка с исходным текстом, местом в заявке и собственными ошибками.</summary>
public sealed record BatchRegistrationItem(int Position, string Raw, RegistrationRequest? Request,
                                           IReadOnlyList<string> Problems);

/// <summary>
/// Результат разбора списка. Даже годные элементы нельзя записывать, пока <see cref="Ok"/>
/// ложно: ошибка одного номера или общего поля отклоняет всю заявку.
/// </summary>
public sealed record BatchRegistrationRead(IReadOnlyList<BatchRegistrationItem> Items,
                                           IReadOnlyList<string> Problems, int? Total = null)
{
    /// <summary>Число введенных элементов, включая заявку, превышающую лимит обработки.</summary>
    public int Count => Total ?? Items.Count;
    /// <summary>Все номера и общие поля проверены; пустая заявка регистрации не допускается.</summary>
    public bool Ok => Problems.Count == 0 && Items.Count > 0
                      && Items.All(item => item.Request is not null && item.Problems.Count == 0);
}

/// <summary>
/// Разбор и проверка сообщения о регистрации — того вида, в котором его набирает человек в
/// переписке:
///
/// <code>
/// Действие: Регистрация
/// Номер: АБВГ.301121.ХХХ
/// Наименование: Кронштейн
/// Проект: Машина
/// ФИО: Иванов Иван Иванович
/// </code>
///
/// <b>Все ошибки сразу.</b> Отправитель исправляет сообщение по ответу, и ответ «не указан
/// проект», за которым после исправления приходит «неправильный номер», — это три круга
/// переписки вместо одного. Поэтому проверяются все строки, а ответ — по строке на поле, в том
/// же порядке, что и в сообщении.
///
/// <b>Номер пишут по маске, а не выбирают из списка.</b> В окне номер выбирают из выданных; в
/// сообщении выбирать не из чего, поэтому то, что должна подобрать программа, пишется буквами
/// «Х»: <c>ХХХ</c> — следующий свободный номер серии, <c>001-ХХ</c> — следующее свободное
/// исполнение документа 001. Полный номер без исполнения также принимается, но его занятость
/// проверяется перед записью; первый документ серии может требовать подтверждения.
///
/// <b>Латиница, похожая на кириллицу, прощается.</b> Код организации и буквы «Х» с клавиатуры
/// набирают то в одной раскладке, то в другой, а на экране они неразличимы. Для программы же
/// <c>ABBG</c> латиницей и <c>АБВГ</c> — разные серии, и номера в них пошли бы по второму кругу.
/// Поэтому похожие латинские буквы переводятся в русские до всякой проверки.
///
/// <b>Разметка снимается.</b> Программы переписки нередко присылают набранное не строками, а
/// разметкой: перенос строки — тегом, текст — в обертке. Человек видит те же пять строк и об
/// обертке не знает, поэтому снимает ее разбор (<see cref="Plain"/>).
///
/// Здесь нет базы: только текст и списки из настроек. Занятость номеров проверяет действие —
/// у него журнал под рукой.
/// </summary>
public static class MessageRegistration
{
    /// <summary>Строки сообщения — ключи в том виде, в каком их пишет человек.</summary>
    public const string ActionKey  = "Действие";

    /// <inheritdoc cref="ActionKey"/>
    public const string NumberKey  = "Номер";

    /// <inheritdoc cref="ActionKey"/>
    public const string NameKey    = "Наименование";

    /// <inheritdoc cref="ActionKey"/>
    public const string ProjectKey = "Проект";

    /// <inheritdoc cref="ActionKey"/>
    public const string FioKey     = "ФИО";

    /// <summary>Единственное действие, которое понимает сообщение.</summary>
    public const string RegisterWord = "Регистрация";

    /// <summary>Образцы для ответа с ошибкой — отправитель исправляет по ним.</summary>
    public const string NumberExample = "АААА.000000.ХХХ, АААА.000000.001 или АААА.000000.001-ХХ";

    /// <inheritdoc cref="NumberExample"/>
    public const string FioExample = "Иванов Иван Иванович";

    /// <summary>
    /// Сколько строк сообщения разбирается — тот же предохранитель, что у разбора сообщения из
    /// папки: вставленная по ошибке простыня не должна превращаться в сто ключей.
    /// </summary>
    public const int MaxLines = 100;

    /// <summary>Предельное число номеров в одном сообщении, чтобы ответ оставался читаемым.</summary>
    public const int MaxBatch = 100;

    /// <summary>
    /// Латинские буквы, неотличимые на экране от русских, и их русские пары. Только заглавные:
    /// строчные поднимаются в заглавные раньше.
    /// </summary>
    private static readonly Dictionary<char, char> LookAlike = new()
    {
        ['A'] = 'А', ['B'] = 'В', ['C'] = 'С', ['E'] = 'Е', ['H'] = 'Н', ['K'] = 'К',
        ['M'] = 'М', ['O'] = 'О', ['P'] = 'Р', ['T'] = 'Т', ['X'] = 'Х', ['Y'] = 'У',
    };

    /// <summary>
    /// Разобрать сообщение. Возвращает заявку либо перечень ошибок — по строке на поле, словами,
    /// которые отправитель поймет без справки.
    /// </summary>
    /// <param name="text">Текст сообщения.</param>
    /// <param name="orgCodes">Коды организации из настроек расширения; пусто — код не сверяется
    /// со списком (окно в этом случае тоже не предлагает ничего, но сообщение — не окно, и
    /// отказывать в регистрации из-за незаполненной настройки нечестно по отношению к отправителю:
    /// код все равно проверяется по форме).</param>
    public static (RegistrationRequest? Request, IReadOnlyList<string> Problems) Read(
        string? text, IReadOnlyList<string> orgCodes) => ReadValues(Lines(text), orgCodes);

    /// <summary>Те же правила для колонок книги: перенос внутри ячейки не становится новой строкой сообщения.</summary>
    internal static (RegistrationRequest? Request, IReadOnlyList<string> Problems) ReadValues(
        List<(string Key, string Value)> values, IReadOnlyList<string> orgCodes)
    {
        var problems = new List<string>();

        var action = Value(values, ActionKey);
        if (action.Length == 0)
            problems.Add($"{ActionKey}: не указано действие (Пример: {RegisterWord})");
        else if (!RegistrySchema.SameName(action, RegisterWord))
            problems.Add($"{ActionKey}: неизвестное действие (Пример: {RegisterWord})");

        var number = Value(values, NumberKey);
        var parsed = number.Length == 0 ? null : Number(number);
        var exact = false;
        if (parsed is null && DocumentMessages.ExactNumber(number) is { } full && !full.Contains('-'))
        {
            var parts = full.Split('.');
            parsed = (parts[0], parts[1], int.Parse(parts[2], System.Globalization.CultureInfo.InvariantCulture));
            exact = true;
        }

        if (number.Length == 0)
            problems.Add($"{NumberKey}: не указан номер (Пример: {NumberExample})");
        else if (parsed is null)
            problems.Add($"{NumberKey}: неправильный номер (Пример: {NumberExample})");
        else if (Listed(parsed.Value.Org, orgCodes) is null)
            problems.Add($"{NumberKey}: неизвестный код организации {parsed.Value.Org} "
                       + $"(Можно: {string.Join(", ", orgCodes)})");

        var name = Value(values, NameKey);
        if (name.Length == 0)
            problems.Add($"{NameKey}: не указано наименование");

        var project = Value(values, ProjectKey);
        if (project.Length == 0)
            problems.Add($"{ProjectKey}: не указан проект");

        var fio = Collapse(Value(values, FioKey));
        if (fio.Length == 0)
            problems.Add($"{FioKey}: не указано ФИО (Пример: {FioExample})");
        else if (!RegistryImport.IsFullName(fio))
            problems.Add($"{FioKey}: не полное ФИО (Пример: {FioExample})");

        if (problems.Count > 0 || parsed is not { } done) return (null, problems);

        // Код организации — в написании из настроек, как и у загрузки из файла: журнал должен
        // выглядеть одинаково, откуда бы строка в него ни пришла.
        var org = Listed(done.Org, orgCodes) ?? done.Org;

        return (new RegistrationRequest(org, done.Eskd, done.Number, name, project, fio, exact), []);
    }

    /// <summary>
    /// Номера через запятую в одной строке «Номер», общие наименование, проект и ФИО.
    /// Порядок, повторы и пустые элементы сохраняются: одинаковые маски означают несколько
    /// документов, а случайная двойная запятая — ошибку, не сокращение заявки.
    /// </summary>
    public static BatchRegistrationRead ReadBatch(string? text, IReadOnlyList<string> orgCodes)
    {
        var values = Lines(text);
        var common = new List<string>();
        var action = Value(values, ActionKey);
        if (action.Length == 0)
            common.Add($"{ActionKey}: не указано действие (Пример: {RegisterWord})");
        else if (!RegistrySchema.SameName(action, RegisterWord))
            common.Add($"{ActionKey}: неизвестное действие (Пример: {RegisterWord})");

        var name = Value(values, NameKey);
        if (name.Length == 0) common.Add($"{NameKey}: не указано наименование");
        var project = Value(values, ProjectKey);
        if (project.Length == 0) common.Add($"{ProjectKey}: не указан проект");
        var fio = Collapse(Value(values, FioKey));
        if (fio.Length == 0)
            common.Add($"{FioKey}: не указано ФИО (Пример: {FioExample})");
        else if (!RegistryImport.IsFullName(fio))
            common.Add($"{FioKey}: не полное ФИО (Пример: {FioExample})");

        var numberText = Value(values, NumberKey);
        // Разделяем с ограничением: громадный список не создает громадный массив и ответ.
        var numbers = numberText.Split([','], MaxBatch + 1, StringSplitOptions.None);
        if (numbers.Length > MaxBatch)
        {
            common.Add($"{NumberKey}: в одной заявке допускается не больше {MaxBatch} номеров");
            return new BatchRegistrationRead([], common, numberText.Count(c => c == ',') + 1);
        }

        var items = new List<BatchRegistrationItem>(numbers.Length);
        for (var index = 0; index < numbers.Length; index++)
        {
            var raw = numbers[index].Trim();
            var problems = new List<string>();
            var parsed = raw.Length == 0 ? null : Number(raw);
            var exact = false;
            if (parsed is null && DocumentMessages.ExactNumber(raw) is { } full && !full.Contains('-'))
            {
                var parts = full.Split('.');
                parsed = (parts[0], parts[1], int.Parse(parts[2], System.Globalization.CultureInfo.InvariantCulture));
                exact = true;
            }

            if (raw.Length == 0)
                problems.Add($"{NumberKey}: не указан номер (Пример: {NumberExample})");
            else if (parsed is null)
            {
                var parts = string.Concat(raw.Where(c => !char.IsWhiteSpace(c))).Split('.');
                var detail = parts.Length == 3 && !EskdCatalog.IsFullCode(parts[1])
                    ? "; код классификации в середине должен содержать ровно шесть цифр" : "";
                problems.Add($"{NumberKey}: неправильный номер{detail} (Пример: {NumberExample})");
            }
            else if (Listed(parsed.Value.Org, orgCodes) is null)
                problems.Add($"{NumberKey}: неизвестный код организации {parsed.Value.Org} "
                             + $"(Можно: {string.Join(", ", orgCodes)})");

            RegistrationRequest? request = null;
            if (problems.Count == 0 && parsed is { } done)
                request = new RegistrationRequest(Listed(done.Org, orgCodes) ?? done.Org, done.Eskd,
                    done.Number, name, project, fio, exact);
            items.Add(new BatchRegistrationItem(index + 1, raw, request, problems));
        }

        return new BatchRegistrationRead(items, common);
    }

    /// <summary>
    /// Код организации из списка настроек в его написании; список пуст — сам код;
    /// <c>null</c> — кода в списке нет.
    /// </summary>
    private static string? Listed(string org, IReadOnlyList<string> orgCodes) =>
        orgCodes.Count == 0 ? org : orgCodes.FirstOrDefault(code => Org(code) == org);

    /// <summary>
    /// Идентификаторы тех, кому разрешено регистрировать, из строки настройки.
    ///
    /// Записи разделяются точкой с запятой, запятой или переводом строки. Идентификатор — первое
    /// слово записи; все, что после него, — пометка для человека, который ведет список:
    /// <c>1001 Иванов; 1002 Петров</c>. Без пометки список из одних чисел через месяц не
    /// прочитал бы и тот, кто его составил.
    ///
    /// Что означает идентификатор, здесь не знают: это строка, которой программа, приславшая
    /// сообщение, называет отправителя. Регистр не учитывается.
    /// </summary>
    public static IReadOnlySet<string> Senders(string? setting) =>
        SenderEntries(setting).Select(e => e.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Записи списка по порядку: идентификатор и запись целиком, с пометкой, — так ее и
    /// показывают человеку (<c>1001 Иванов</c>). Повтор идентификатора пропускается.
    /// </summary>
    public static IReadOnlyList<(string Id, string Entry)> SenderEntries(string? setting)
    {
        var entries = new List<(string Id, string Entry)>();
        var seen    = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var entry in (setting ?? "").Split([';', ',', '\n', '\r'],
                                                    StringSplitOptions.RemoveEmptyEntries))
        {
            var words = entry.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (words.Length > 0 && seen.Add(words[0])) entries.Add((words[0], string.Join(' ', words)));
        }

        return entries;
    }

    // ── Номер ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Разобрать номер: <c>АБВГ.301121.ХХХ</c> — новый документ, <c>АБВГ.301121.001-ХХ</c> —
    /// исполнение документа 001. <c>null</c> — не по маске. Маска та же, что у загрузки списком из
    /// файла (<see cref="RegistryImport"/>): код организации — четыре буквы, метки — «Х».
    /// </summary>
    public static (string Org, string Eskd, int? Number)? Number(string? text)
    {
        // Пробелы внутри номера не значат ничего, кроме спешки: «АБВГ. 301121. ХХХ».
        var compact = string.Concat((text ?? "").Where(c => !char.IsWhiteSpace(c)));
        var parts   = compact.Split('.');

        if (parts.Length != 3) return null;

        var org = Org(parts[0]);
        if (org.Length != OrgLength || !org.All(IsRussianCapital)) return null;

        if (!EskdCatalog.IsFullCode(parts[1])) return null;

        var tail = Org(parts[2]);   // та же замена похожих букв: «ХХХ» пишут и латиницей

        if (tail == RegistryImport.NumberMark) return (org, parts[1], null);

        var dash = tail.IndexOf('-');
        if (dash != 3 || tail.Length != 4 + RegistryImport.ExecutionMark.Length) return null;
        if (!tail[..3].All(char.IsAsciiDigit))                                   return null;
        if (tail[4..] != RegistryImport.ExecutionMark)                           return null;

        var number = int.Parse(tail[..3], System.Globalization.CultureInfo.InvariantCulture);

        return number is >= Designation.MinNumber and <= Designation.MaxNumber
            ? (org, parts[1], number)
            : null;
    }

    /// <summary>Код организации к одному виду: заглавные, похожая латиница — русскими буквами.</summary>
    public static string Org(string? text) =>
        string.Concat((text ?? "").Trim().ToUpperInvariant()
                                  .Select(c => LookAlike.TryGetValue(c, out var russian) ? russian : c));

    private static bool IsRussianCapital(char c) => c is >= 'А' and <= 'Я';

    /// <summary>Длина кода организации — как в маске загрузки из файла.</summary>
    private const int OrgLength = 4;

    // ── ФИО ───────────────────────────────────────────────────────────────────

    /// <summary>ФИО к одному виду: слова через один пробел — и при регистрации, и при изменении.</summary>
    internal static string Collapse(string text) =>
        string.Join(" ", text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    // ── Разметка ──────────────────────────────────────────────────────────────

    private const RegexOptions MarkupOptions =
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking;

    /// <summary>Тег: слово латиницей в угловых скобках. «&lt;левый&gt;» в наименовании — не тег.</summary>
    private static readonly Regex Tag =
        new(@"</?[a-z][a-z0-9]*(?:\s[^<>]*)?/?>", MarkupOptions);

    /// <summary>Теги, за которыми человек видит новую строку: перенос и границы абзаца.</summary>
    private static readonly Regex BreakTag =
        new(@"<br(?:\s[^<>]*)?/?>|</?(?:p|div|li|tr|h[1-6]|blockquote|pre)(?:\s[^<>]*)?>", MarkupOptions);

    /// <summary>
    /// Текст без разметки — таким, каким его видит тот, кто его набирал.
    ///
    /// Разметкой текст считается, только если в нем есть хоть один тег. Иначе он остается как
    /// есть: «&amp;amp;» в обычном тексте — то, что человек и написал.
    ///
    /// Порядок важен: сначала теги, потом сущности. Наоборот «&amp;lt;br&amp;gt;», набранное
    /// буквами, стало бы переносом строки, которого человек не ставил.
    /// </summary>
    public static string Plain(string? text)
    {
        if (string.IsNullOrEmpty(text) || !Tag.IsMatch(text)) return text ?? "";

        return Unmarked(text);
    }

    /// <summary>
    /// Текст, который заведомо размечен, — без тегов и с сущностями, возвращенными знаками.
    /// В отличие от <see cref="Plain"/> не спрашивает, есть ли теги: так читаются собственные
    /// ответы расширения (<see cref="ReplyMarkup"/>), где «&amp;lt;» — всегда экранированный знак.
    /// </summary>
    internal static string Unmarked(string html)
    {
        var lines = BreakTag.Replace(html, "\n");
        var bare  = Tag.Replace(lines, "");

        // Неразрывный пробел — частый гость разметки; в журнале он ни к чему.
        return System.Net.WebUtility.HtmlDecode(bare).Replace('\u00A0', ' ');
    }

    /// <summary>
    /// Ответ на подтверждение: только целое «да» или «нет», без учета регистра.
    /// Невидимые управляющие знаки форматирования из программы-мессенджера не меняют ответ;
    /// пробелы внутри слова, другие слова и знаки препинания подтверждением не считаются.
    /// </summary>
    internal static bool? Confirmation(string? text)
    {
        var answer = string.Concat(Plain(text).Where(c =>
            char.GetUnicodeCategory(c) != System.Globalization.UnicodeCategory.Format)).Trim();
        if (answer.Equals("да", StringComparison.OrdinalIgnoreCase)) return true;
        if (answer.Equals("нет", StringComparison.OrdinalIgnoreCase)) return false;
        return null;
    }

    // ── Строки сообщения ──────────────────────────────────────────────────────

    /// <summary>
    /// Чем программы, из которых приходит текст, отделяют строку от строки. Перевод строки у
    /// каждой свой, а человек на экране видит одно и то же — пять строк.
    /// </summary>
    private static readonly char[] LineBreaks = ['\n', '\r', '\u2028', '\u2029', '\u0085', '\v', '\f'];

    /// <summary>
    /// Сколько строк сообщения узнано — из пяти, которые оно должно содержать. Ноль на непустом
    /// тексте значит, что действию дали не сообщение: это видно тому, кто настраивал шаг, а не
    /// отправителю.
    /// </summary>
    public static int KnownLines(string? text)
    {
        var values = Lines(text);

        return new[] { ActionKey, NumberKey, NameKey, ProjectKey, FioKey }
            .Count(key => values.Any(v => RegistrySchema.SameName(v.Key, key)));
    }

    /// <summary>
    /// Строки «Ключ: значение». Первое упоминание побеждает — по тому же правилу, что у разбора
    /// сообщения из папки: во втором обычно цитата, а не поправка.
    /// </summary>
    internal static List<(string Key, string Value)> Lines(string? text)
    {
        var values = new List<(string Key, string Value)>();

        foreach (var line in Plain(text).Split(LineBreaks, StringSplitOptions.RemoveEmptyEntries).Take(MaxLines))
        {
            var colon = line.IndexOf(':');
            if (colon <= 0) continue;

            var key = Key(line[..colon]);
            if (key.Length == 0 || values.Any(v => RegistrySchema.SameName(v.Key, key))) continue;

            values.Add((key, line[(colon + 1)..].Trim()));
        }

        return values;
    }

    /// <summary>
    /// Знаки, которые оказываются перед строкой, скопированной из оформленного ответа: метка
    /// цитаты и маркеры списка. Образцы сообщений в инструкции стоят цитатой, и при копировании
    /// программа может поставить их перед каждой строкой.
    /// </summary>
    private static readonly char[] LineMarks = ['>', '|', '│', '•', '·', '◦', '▪', '-', '–', '—', '*'];

    /// <summary>
    /// Ключ строки без меток цитаты и списка и без невидимых знаков форматирования: человек
    /// видит «Номер», что бы ни пришло перед ним вместе с копией.
    /// </summary>
    private static string Key(string text)
    {
        var visible = string.Concat(text.Where(c =>
            char.GetUnicodeCategory(c) != System.Globalization.UnicodeCategory.Format));

        return visible.Trim().TrimStart(LineMarks).Trim();
    }

    internal static string Value(List<(string Key, string Value)> values, string key) =>
        values.FirstOrDefault(v => RegistrySchema.SameName(v.Key, key)).Value ?? "";
}
