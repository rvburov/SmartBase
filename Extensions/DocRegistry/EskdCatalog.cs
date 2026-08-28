using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Threading;

namespace SmartBase.Extensions.DocRegistry;

/// <summary>Строка классификатора ЕСКД: шестизначный код и его наименование.</summary>
public sealed record EskdEntry(string Code, string Name);

/// <summary>Результат подбора: строки и их число (оно же идет в подсказку под списком).</summary>
public sealed record EskdSearch(IReadOnlyList<EskdEntry> Shown, int Total)
{
    /// <summary>Показано не все (задан предел показа). При обычном подборе всегда <c>false</c>.</summary>
    public bool Truncated => Total > Shown.Count;
}

/// <summary>
/// Классификатор ЕСКД (ГОСТ 2.201-80) — 66 186 шестизначных кодов с наименованиями. Лежит рядом
/// с расширением в <c>eskd.tsv.gz</c> и попадает в сборку встроенным ресурсом (правило в
/// SmartBase.csproj — глоб по <c>Extensions/**/*.gz</c>, имени расширения там нет).
///
/// <b>Почему такой формат.</b> Исходные данные — 5,5 МБ табулированного текста со всеми уровнями
/// классификатора. В сборку идет только то, что можно выбрать: полные шестизначные коды
/// (классы «04» и подклассы «040» в обозначение не ставятся). Отобранное отсортировано по коду
/// и сжато — 447 КБ в сборке против 4,2 МБ текста.
///
/// <b>Как оно живет в памяти.</b> <see cref="All"/> — готовый список строк, который окно отдает
/// списку целиком: справочник виден сразу, без ввода. Рядом лежат два служебных массива той же
/// длины и того же порядка: коды числами (<see cref="int"/>) для поиска по цифрам и свертка
/// наименований к единому виду (см. <c>Fold</c>) для поиска по слову. Свертка считается один раз
/// при загрузке — сворачивать 66 тыс. названий на каждое нажатие клавиши стоило бы дороже поиска.
///
/// <b>Почему это быстро.</b> Коды отсортированы, поэтому подбор по цифрам — не обход справочника,
/// а бинарный поиск начала диапазона и чтение подряд: «301» — это ровно коды 301000…301999.
/// Поиск по наименованию остается обходом (подстрока может быть где угодно), но идет по готовым
/// сверткам сравнением без учета культуры. Результат ссылается на ТЕ ЖЕ объекты, что и
/// <see cref="All"/>, — список не пересоздает строки на каждый фильтр.
///
/// Пять кодов в исходных данных встречаются дважды с разными наименованиями (337641 — «Браслеты»
/// и «Броши»). Это дефект самого справочника: обе строки видны в списке, выбор за человеком;
/// <see cref="Find"/> при вводе кода руками отдает первую.
/// </summary>
public static class EskdCatalog
{
    /// <summary>Длина кода в обозначении — ровно шесть цифр.</summary>
    public const int CodeLength = 6;

    private const string ResourceSuffix = "eskd.tsv.gz";

    /// <summary>Три массива одной длины и одного порядка: строка справочника, ее код числом
    /// и свернутое наименование. Индекс — общий для всех трех.</summary>
    private sealed record Data(EskdEntry[] Entries, int[] Codes, string[] Folded);

    private static readonly Lazy<Data> Catalog =
        new(Load, LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>Весь справочник по возрастанию кода — то, что окно показывает до всякого ввода.</summary>
    public static IReadOnlyList<EskdEntry> All => Catalog.Value.Entries;

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

    /// <summary>Строка справочника по точному коду; <c>null</c> — такого кода нет.</summary>
    public static EskdEntry? Find(string? code)
    {
        if (!IsFullCode(code)) return null;

        var data  = Catalog.Value;
        var value = int.Parse(code!, NumberStyles.None, CultureInfo.InvariantCulture);
        var at    = LowerBound(data.Codes, value);

        return at < data.Codes.Length && data.Codes[at] == value ? data.Entries[at] : null;
    }

    /// <summary>
    /// Подбор по строке ввода:
    /// <list type="bullet">
    /// <item>пусто — ВЕСЬ справочник (список показывается сразу, до всякого ввода);</item>
    /// <item>только цифры — коды, НАЧИНАЮЩИЕСЯ с введенного (код набирается слева направо);</item>
    /// <item>иначе — наименования, СОДЕРЖАЩИЕ введенное (без учета регистра и написания «е»).</item>
    /// </list>
    /// Порядок — по возрастанию кода. <paramref name="limit"/> ограничивает ПОКАЗ (не поиск) и
    /// нужен только тестам: окну предел не нужен, список виртуализирован и держит все 66 тысяч.
    /// </summary>
    public static EskdSearch Search(string? query, int limit = int.MaxValue)
    {
        var text = (query ?? "").Trim();
        if (limit <= 0) return new EskdSearch([], 0);

        if (text.Length == 0)
        {
            var all = All;
            return limit >= all.Count
                ? new EskdSearch(all, all.Count)
                : new EskdSearch([.. all.Take(limit)], all.Count);
        }

        return text.All(char.IsAsciiDigit) ? ByCode(text, limit) : ByName(text, limit);
    }

    // ── Подбор по коду: диапазон вместо обхода ────────────────────────────────

    // Цифровая приставка задает непрерывный диапазон кодов: «301» — это 301000…301999,
    // «040000» — сам себя. Границы считаются арифметикой, начало ищется бинарным поиском,
    // дальше чтение подряд — ровно столько шагов, сколько строк попало в диапазон.
    private static EskdSearch ByCode(string prefix, int limit)
    {
        if (prefix.Length > CodeLength) return new EskdSearch([], 0);   // длиннее кода — совпасть не с чем

        var scale = (int)Math.Pow(10, CodeLength - prefix.Length);
        var from  = int.Parse(prefix, NumberStyles.None, CultureInfo.InvariantCulture) * scale;
        var to    = from + scale - 1;

        var data  = Catalog.Value;
        var shown = new List<EskdEntry>();
        var total = 0;

        for (var i = LowerBound(data.Codes, from); i < data.Codes.Length && data.Codes[i] <= to; i++)
        {
            total++;
            if (shown.Count < limit) shown.Add(data.Entries[i]);
        }

        return new EskdSearch(shown, total);
    }

    // ── Подбор по наименованию: обход по готовым сверткам ─────────────────────

    private static EskdSearch ByName(string query, int limit)
    {
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

        return new EskdSearch(shown, total);
    }

    // Первый индекс, где код >= value (или длина массива, если таких нет). Свой поиск, а не
    // Array.BinarySearch: тот при дублях кода отдает любой из совпавших, а нужен ПЕРВЫЙ —
    // иначе вторая строка дублирующегося кода терялась бы из списка.
    private static int LowerBound(int[] codes, int value)
    {
        int lo = 0, hi = codes.Length;
        while (lo < hi)
        {
            var mid = lo + (hi - lo) / 2;
            if (codes[mid] < value) lo = mid + 1;
            else                    hi = mid;
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
        using var packed = OpenResource();
        using var stream = new GZipStream(packed, CompressionMode.Decompress);
        using var reader = new StreamReader(stream, Encoding.UTF8);

        var entries = new List<EskdEntry>(70_000);
        var codes   = new List<int>(70_000);
        var folded  = new List<string>(70_000);

        while (reader.ReadLine() is { } line)
        {
            // Формат строки: шесть цифр, табуляция, наименование. Все прочее — не наши данные.
            if (line.Length <= CodeLength || line[CodeLength] != '\t') continue;
            if (!int.TryParse(line.AsSpan(0, CodeLength), NumberStyles.None,
                              CultureInfo.InvariantCulture, out var code)) continue;

            var name = line[(CodeLength + 1)..].Trim();
            if (name.Length == 0) continue;

            // Порядок — не удобство, а условие работы бинарного поиска. Файл готовится
            // отсортированным, и проверка тут стоит один проход: сломанный порядок должен
            // назваться сам, а не превратиться в молча неполную выдачу.
            if (codes.Count > 0 && code < codes[^1])
                throw new InvalidOperationException(
                    $"Справочник ЕСКД не отсортирован по коду: {code:D6} после {codes[^1]:D6}. " +
                    "Файл eskd.tsv.gz должен быть отсортирован при подготовке.");

            entries.Add(new EskdEntry(line[..CodeLength], name));
            codes.Add(code);
            folded.Add(Fold(name));
        }

        if (entries.Count == 0)
            throw new InvalidOperationException("Справочник ЕСКД пуст — файл eskd.tsv.gz поврежден.");

        return new Data([.. entries], [.. codes], [.. folded]);
    }

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
