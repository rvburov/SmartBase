using System.IO;
using System.IO.Compression;
using System.Text;
using System.Threading;

namespace SmartBase.Extensions.DocRegistry;

/// <summary>
/// Строка классификатора ЕСКД: код от двух до шести цифр и его наименование. Длина кода — она же
/// уровень: 2 класс, 3 подкласс, 4 группа, 5 подгруппа, 6 вид.
/// </summary>
public sealed record EskdEntry(string Code, string Name)
{
    // Точки, дополняющие код до шести знаков. Сочтены заранее: строка списка спрашивает их при
    // каждой перерисовке, а вариантов всего пять.
    private static readonly string[] DotsByLevel = ["", "", "....", "...", "..", ".", ""];

    /// <summary>Шестизначная строка — конец ветки: раскрывать нечего, зато ее можно зафиксировать
    /// в обозначение. Все, что короче, — узел дерева.</summary>
    public bool IsLeaf => Code.Length == EskdCatalog.CodeLength;

    /// <summary>Точки после кода — ими в списке показан уровень, как в исходной таблице
    /// («38 ....», «380 ...», «380008»). У шестизначной строки их нет.</summary>
    public string Dots => DotsByLevel[Math.Clamp(Code.Length, 0, EskdCatalog.CodeLength)];

    /// <summary>Наименование для показа. У достроенных узлов (см. <see cref="EskdCatalog"/>) его
    /// в справочнике нет — вместо пустоты ставится прочерк.</summary>
    public string Title => Name.Length > 0 ? Name : "—";
}

/// <summary>Чем занят список: веткой дерева или плоской выдачей поиска по наименованию.</summary>
public enum EskdViewKind
{
    /// <summary>Цепочка предков, узел и его дети.</summary>
    Tree,

    /// <summary>Плоский перечень строк, у которых введенное встретилось в наименовании.</summary>
    Names,
}

/// <summary>
/// Что показать в списке.
/// </summary>
/// <param name="Rows">Строки списка. В дереве это цепочка предков сверху вниз, затем дети.</param>
/// <param name="Kind">Дерево или поиск по наименованию.</param>
/// <param name="Node">Узел, на котором стоим (дерево). <c>null</c> — показаны классы.</param>
/// <param name="ChildCount">Сколько строк в конце <paramref name="Rows"/> — дети узла.</param>
/// <param name="Total">Сколько всего нашлось (у поиска по наименованию может быть больше показанного).</param>
/// <param name="Exact">Введенный код есть в справочнике (дерево).</param>
public sealed record EskdView(
    IReadOnlyList<EskdEntry> Rows,
    EskdViewKind             Kind,
    EskdEntry?               Node,
    int                      ChildCount,
    int                      Total,
    bool                     Exact)
{
    /// <summary>Показано не все (задан предел показа). Нужен только тестам: окну предел не нужен.</summary>
    public bool Truncated => Total > Rows.Count;
}

/// <summary>
/// Классификатор ЕСКД (ГОСТ 2.201-80) — 78 480 строк пяти уровней с наименованиями. Лежит рядом
/// с расширением в <c>eskd.tsv.gz</c> и попадает в сборку встроенным ресурсом (правило в
/// SmartBase.csproj — глоб по <c>Extensions/**/*.gz</c>, имени расширения там нет).
///
/// <b>Что в файле.</b> Исходные данные — 5,5 МБ табулированного текста. В сборку идут все пять
/// уровней (43 класса, 290 подклассов, 1 723 группы, 10 238 подгрупп, 66 186 видов): по дереву
/// ходят, значит нужны и промежуточные узлы, хотя в обозначение ставится только шестизначный код.
/// Отобранное отсортировано по коду КАК ПО СТРОКЕ и сжато — 596 КБ в сборке против 4,9 МБ текста.
///
/// <b>Почему порядок именно такой.</b> Сортировка кода как строки дает обход дерева сверху вниз:
/// 38, 380, 3800, 38000, 380000, 380001, … 38001, … 381. То есть файл САМ и есть дерево — предок
/// всегда стоит перед своими потомками, а поддерево лежит подряд. Отсюда все остальное: дети
/// узла ищутся десятью бинарными поисками (код узла плюс цифра), цепочка предков — пятью,
/// пропущенный узел виден при первом же его потомке. Ничего не строится в памяти, ничего не
/// пересчитывается на нажатие клавиши.
///
/// <b>Что чинится при загрузке.</b> Файл — честная выгрузка исходника, поэтому два его дефекта
/// разбираются здесь, в <c>Repair</c>: перенесенное наименование склеивается, пропущенный узел
/// достраивается. Оба случая описаны там же.
///
/// Пять шестизначных кодов в исходных данных встречаются дважды с разными наименованиями
/// (337641 — «Браслеты» и «Броши»). Это дефект самого справочника, но безобидный: детей у
/// шестизначных нет, обе строки видны в списке, выбор за человеком.
/// </summary>
public static class EskdCatalog
{
    /// <summary>Длина кода в обозначении — ровно шесть цифр.</summary>
    public const int CodeLength = 6;

    /// <summary>Длина кода класса — с него начинается любая ветка.</summary>
    public const int RootLength = 2;

    private const string ResourceSuffix = "eskd.tsv.gz";

    /// <summary>Готовый справочник: строки в порядке обхода дерева, их коды и свертки наименований
    /// (три массива одной длины и одного порядка, индекс общий) и отдельно классы.</summary>
    private sealed record Data(EskdEntry[] Entries, string[] Codes, string[] Folded, EskdEntry[] Roots);

    private static readonly Lazy<Data> Catalog =
        new(Load, LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>Весь справочник в порядке обхода дерева.</summary>
    public static IReadOnlyList<EskdEntry> All => Catalog.Value.Entries;

    /// <summary>Классы — то, что окно показывает до всякого ввода.</summary>
    public static IReadOnlyList<EskdEntry> Roots => Catalog.Value.Roots;

    /// <summary>Сколько строк в справочнике. Первое обращение распаковывает файл.</summary>
    public static int Count => Catalog.Value.Entries.Length;

    /// <summary>
    /// Распаковать и разобрать справочник заранее, чтобы окно не ждало распаковки. Зовется из
    /// фонового потока при открытии окна — список показывается сразу, как только готов.
    /// </summary>
    public static void Warmup() => _ = Catalog.Value;

    /// <summary>Код годится для обозначения: ровно шесть цифр.</summary>
    public static bool IsFullCode(string? code) =>
        code is { Length: CodeLength } && code.All(char.IsAsciiDigit);

    /// <summary>Как называется уровень с кодом такой длины — во множественном числе, для подписи
    /// под списком.</summary>
    public static string LevelName(int codeLength) => codeLength switch
    {
        2 => "классов",
        3 => "подклассов",
        4 => "групп",
        5 => "подгрупп",
        6 => "видов",
        _ => "строк",
    };

    // ── Дерево ────────────────────────────────────────────────────────────────

    /// <summary>Строка справочника по точному коду любого уровня; <c>null</c> — такого кода нет.
    /// У задвоенного шестизначного кода отдает первую строку.</summary>
    public static EskdEntry? Node(string? code)
    {
        if (!IsCode(code)) return null;

        var data = Catalog.Value;
        var at   = LowerBound(data.Codes, code!);

        return at < data.Codes.Length && data.Codes[at] == code ? data.Entries[at] : null;
    }

    /// <summary>Строка справочника по точному шестизначному коду; <c>null</c> — такого нет.</summary>
    public static EskdEntry? Find(string? code) => IsFullCode(code) ? Node(code) : null;

    /// <summary>Цепочка от класса до самого узла включительно. Пусто — кода нет.</summary>
    public static IReadOnlyList<EskdEntry> Chain(string? code)
    {
        if (!IsCode(code)) return [];

        var chain = new List<EskdEntry>(CodeLength - RootLength + 1);
        for (var length = RootLength; length <= code!.Length; length++)
            if (Node(code[..length]) is { } node) chain.Add(node);

        return chain;
    }

    /// <summary>
    /// Прямые потомки узла: пусто или <c>null</c> — классы, шестизначный код — пусто (это конец
    /// ветки). Ищутся не обходом поддерева (у класса их десять тысяч), а десятью бинарными
    /// поисками: код узла плюс цифра.
    /// </summary>
    public static IReadOnlyList<EskdEntry> Children(string? code)
    {
        var data = Catalog.Value;
        if (string.IsNullOrEmpty(code)) return data.Roots;
        if (!IsCode(code) || code.Length == CodeLength) return [];

        var kids = new List<EskdEntry>(10);
        for (var digit = '0'; digit <= '9'; digit++)
            AddAll(data, code + digit, kids);

        return kids;
    }

    // ── Что показать по введенному ────────────────────────────────────────────

    /// <summary>
    /// Разобрать введенное и сказать, что показать в списке:
    /// <list type="bullet">
    /// <item>пусто или только цифры — ветка дерева (см. <c>Tree</c>);</item>
    /// <item>иначе — плоский перечень строк, у которых введенное встретилось в наименовании
    /// (без учета регистра и написания буквы «е»).</item>
    /// </list>
    /// <paramref name="limit"/> ограничивает ПОКАЗ (не поиск) и нужен только тестам.
    /// </summary>
    public static EskdView View(string? query, int limit = int.MaxValue)
    {
        var text = (query ?? "").Trim();
        return text.All(char.IsAsciiDigit) ? Tree(text) : Names(text, limit);
    }

    /// <summary>
    /// Ветка по введенным цифрам: цепочка предков сверху вниз, сам узел и его дети. Больше в
    /// списке ничего — соседние ветки не отвлекают.
    ///
    /// Введено то, чего в справочнике нет (в классификаторе есть пропуски: 3803 и 3806, скажем,
    /// не существуют), — опираемся на ближайшего живого предка и показываем его детей, а не
    /// отвечаем пустотой: человек все равно попал в нужное место, ошибся только последней цифрой.
    /// Введено меньше двух цифр — узла нет, зато классы просеиваются по набранному: «3» оставит
    /// 30, 31, 33, 34, 36, 37, 38.
    /// </summary>
    private static EskdView Tree(string digits)
    {
        EskdEntry? node = null;
        for (var length = Math.Min(digits.Length, CodeLength); length >= RootLength && node is null; length--)
            node = Node(digits[..length]);

        var children = Children(node?.Code);

        if (digits.Length > 0)
        {
            var narrowed = children.Where(e => e.Code.StartsWith(digits, StringComparison.Ordinal)).ToList();
            if (narrowed.Count > 0) children = narrowed;
        }

        var chain = Chain(node?.Code);
        var rows  = new List<EskdEntry>(chain.Count + children.Count);
        rows.AddRange(chain);
        rows.AddRange(children);

        return new EskdView(rows, EskdViewKind.Tree, node, children.Count, rows.Count,
                            Exact: node is not null && node.Code == digits);
    }

    private static EskdView Names(string query, int limit)
    {
        if (limit <= 0) return new EskdView([], EskdViewKind.Names, null, 0, 0, Exact: false);

        var needle = Fold(query);
        var data   = Catalog.Value;
        var shown  = new List<EskdEntry>();
        var total  = 0;

        for (var i = 0; i < data.Folded.Length; i++)
        {
            if (!data.Folded[i].Contains(needle, StringComparison.Ordinal)) continue;

            total++;
            if (shown.Count < limit) shown.Add(data.Entries[i]);
        }

        return new EskdView(shown, EskdViewKind.Names, null, 0, total, Exact: false);
    }

    // ── Мелочи поиска ─────────────────────────────────────────────────────────

    private static bool IsCode(string? code) =>
        code is { Length: >= RootLength and <= CodeLength } && code.All(char.IsAsciiDigit);

    private static void AddAll(Data data, string code, List<EskdEntry> into)
    {
        for (var i = LowerBound(data.Codes, code); i < data.Codes.Length && data.Codes[i] == code; i++)
            into.Add(data.Entries[i]);
    }

    // Первый индекс, где код >= value (или длина массива, если таких нет). Свой поиск, а не
    // Array.BinarySearch: тот при задвоенном коде отдает любую из совпавших строк, а нужна
    // ПЕРВАЯ — иначе вторая строка задвоенного кода терялась бы из списка.
    private static int LowerBound(string[] codes, string value)
    {
        int lo = 0, hi = codes.Length;
        while (lo < hi)
        {
            var mid = lo + (hi - lo) / 2;
            if (string.CompareOrdinal(codes[mid], value) < 0) lo = mid + 1;
            else                                              hi = mid;
        }
        return lo;
    }

    // Свертка запроса и наименований к одному виду: ни регистр, ни написание буквы «е» не должны
    // мешать найти строку. В данных справочника второго написания нет — файл готовится без него,
    // — но с клавиатуры его набирают, и без свертки такой запрос не нашел бы ничего.
    private static string Fold(string text) =>
        text.ToLowerInvariant().Replace('ё', 'е');

    // ── Загрузка ──────────────────────────────────────────────────────────────

    private static Data Load()
    {
        var entries = Repair(Read());

        var codes  = new string[entries.Count];
        var folded = new string[entries.Count];
        var roots  = new List<EskdEntry>(50);

        for (var i = 0; i < entries.Count; i++)
        {
            codes[i]  = entries[i].Code;
            folded[i] = Fold(entries[i].Name);
            if (entries[i].Code.Length == RootLength) roots.Add(entries[i]);
        }

        return new Data([.. entries], codes, folded, [.. roots]);
    }

    private static List<EskdEntry> Read()
    {
        using var packed = OpenResource();
        using var stream = new GZipStream(packed, CompressionMode.Decompress);
        using var reader = new StreamReader(stream, Encoding.UTF8);

        var rows = new List<EskdEntry>(80_000);

        while (reader.ReadLine() is { } line)
        {
            // Формат строки: от двух до шести цифр, табуляция, наименование. Все прочее — не наши
            // данные (первой строкой исходника, скажем, идет заголовок таблицы).
            var tab = line.IndexOf('\t');
            if (tab < RootLength || tab > CodeLength) continue;

            var code = line[..tab];
            if (!code.All(char.IsAsciiDigit)) continue;

            var name = line[(tab + 1)..].Trim();
            if (name.Length == 0) continue;

            // Порядок — не удобство, а условие работы всего остального: и бинарный поиск, и
            // достройка пропущенных узлов держатся на том, что файл идет обходом дерева сверху
            // вниз, то есть по коду КАК ПО СТРОКЕ. Проверка стоит один проход, а сломанный
            // порядок обязан назваться сам, а не превратиться в молча неполную выдачу.
            if (rows.Count > 0 && string.CompareOrdinal(code, rows[^1].Code) < 0)
                throw new InvalidOperationException(
                    $"Справочник ЕСКД не отсортирован по коду: {code} после {rows[^1].Code}. " +
                    "Файл eskd.tsv.gz готовится сортировкой кода как строки (LC_ALL=C sort -s -k1,1).");

            rows.Add(new EskdEntry(code, name));
        }

        if (rows.Count == 0)
            throw new InvalidOperationException("Справочник ЕСКД пуст — файл eskd.tsv.gz поврежден.");

        return rows;
    }

    /// <summary>
    /// Два дефекта исходных данных, из-за которых дерево иначе было бы неполным. Оба чинятся тут,
    /// а не в файле: файл — честная выгрузка, и правку лучше держать на виду и под тестами.
    /// </summary>
    private static List<EskdEntry> Repair(List<EskdEntry> rows)
    {
        var repaired = new List<EskdEntry>(rows.Count + 8);

        // Последний выданный код на каждом уровне. По нему и видно, что предок пропущен: файл
        // идет обходом дерева, поэтому живой предок к этому моменту уже был бы записан сюда.
        var chain = new string?[CodeLength + 1];

        foreach (var row in rows)
        {
            // ── Перенесенное наименование ──
            // Узел, у которого есть дети, обязан быть один: иначе непонятно, к какому из двух
            // одинаковых кодов эти дети относятся. Два таких кода в исходнике задвоены (4814 и
            // 48414), но это не две строки, а одна, разорванная переносом при выгрузке: хвост
            // уехал в конец блока. Приписываем хвост к первой строке.
            //
            // Шестизначных это НЕ касается: детей у них нет, а задвоенные коды там —
            // действительно разные строки (337641 «Браслеты» и «Броши»), и обе нужны в списке.
            if (row.Code.Length < CodeLength && repaired.Count > 0 && repaired[^1].Code == row.Code)
            {
                var head = repaired[^1];
                repaired[^1] = head with { Name = $"{head.Name} {Lower(row.Name)}" };
                continue;
            }

            // ── Пропущенный узел ──
            // Двадцать шесть шестизначных кодов остались без пятизначного родителя (30458x,
            // 30754x, 75045x). В дереве до них было бы не добраться — ни щелчком, ни вводом
            // кода. Ставим недостающий узел: код известен из кода потомка, наименования в
            // справочнике нет (в списке на его месте прочерк).
            for (var length = RootLength; length < row.Code.Length; length++)
            {
                var prefix = row.Code[..length];
                if (chain[length] == prefix) continue;

                repaired.Add(new EskdEntry(prefix, ""));
                chain[length] = prefix;
            }

            repaired.Add(row);
            chain[row.Code.Length] = row.Code;
        }

        return repaired;
    }

    // Хвост перенесенной строки начинается с прописной буквы — в середине наименования она не
    // к месту.
    private static string Lower(string text) =>
        text.Length == 0 ? text : char.ToLowerInvariant(text[0]) + text[1..];

    // Ресурс ищем по окончанию имени, а не по полному: полное имя собирается из RootNamespace и
    // пути файла, и любая правка правила в csproj молча оставила бы расширение без справочника.
    private static Stream OpenResource()
    {
        var assembly = typeof(EskdCatalog).Assembly;
        var name = assembly.GetManifestResourceNames()
                           .FirstOrDefault(n => n.EndsWith(ResourceSuffix, StringComparison.Ordinal));

        return name is not null && assembly.GetManifestResourceStream(name) is { } stream
            ? stream
            : throw new InvalidOperationException(
                $"Встроенный справочник ЕСКД не найден (ресурс «…{ResourceSuffix}»). " +
                "Проверьте правило EmbeddedResource для Extensions/**/*.gz в SmartBase.csproj.");
    }
}
