namespace SmartBase.Extensions.ProductCompare;

/// <summary>Общий список строк: строки результата по порядку. В строке — по ячейке на файл (в
/// порядке дат); <c>null</c> — на эту дату такой строки не было.</summary>
internal sealed record MergedList(IReadOnlyList<SourceRow?[]> Rows, int Products, int Operations);

/// <summary>
/// Сведение расшифровок всех дат в один список строк.
///
/// **Изделия — по индексу** (столбец B): «2» встает напротив «2», «1.1» напротив «1.1». Так решил
/// пользователь: по номеру видно, что на этом месте стало другое изделие или нумерация
/// сдвинулась (макрос пересчитывает индексы при вставке изделия). Индекс сравнивается в
/// нормальном виде: «1.» и «1» — одно, «1,2» — то же, что «1.2» (как <c>RepearData</c> в
/// макросе), «01» — то же, что «1». Повтор одного индекса в файле (ошибка в расшифровке) не
/// склеивается — идет отдельной строкой, первый к первому, второй ко второму.
///
/// Порядок изделий — по индексу как по номерам: «Изделие», 1, 1.1, 1.2, 1.10, 2… Так за каждым
/// изделием идут его входящие, как в самой расшифровке.
///
/// **Операции — внутри своего изделия (того же индекса) по названию и номеру повтора**: две
/// «Подготовки» — первая к первой, вторая ко второй. Вид работ в ключ не входит — если он
/// сменился, обе даты встанут в одну строку, и разница видна рядом. Порядок операций — по
/// последней дате; операция, которой в ней уже нет, остается после той, за которой стояла
/// раньше. Операции до первого изделия (без индекса в начале листа) идут первыми.
/// </summary>
internal static class BreakdownMerge
{
    private sealed class Product(IndexKey index, int occurrence, int files)
    {
        public IndexKey     Index      { get; } = index;
        public int          Occurrence { get; } = occurrence;
        public SourceRow?[] Rows       { get; } = new SourceRow?[files];
        public List<string> OperationOrder { get; set; } = [];
        public Dictionary<string, SourceRow?[]> Operations { get; } = new(StringComparer.Ordinal);
    }

    public static MergedList Merge(IReadOnlyList<BreakdownNode> roots)
    {
        var files    = roots.Count;
        var prelude  = new Product(IndexKey.Parse(""), 0, files);   // операции до первого изделия
        var products = new Dictionary<string, Product>(StringComparer.Ordinal);

        for (var file = 0; file < files; file++)
        {
            AbsorbOperations(prelude, roots[file], file, files);

            var seen = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var node in Flatten(roots[file]))
            {
                var index = IndexKey.Parse(node.Index);
                var occurrence = seen.TryGetValue(index.Text, out var n) ? n + 1 : 0;
                seen[index.Text] = occurrence;

                var id = Id(index.Text, occurrence);
                if (!products.TryGetValue(id, out var product))
                    products[id] = product = new Product(index, occurrence, files);
                product.Rows[file] = node.Row;
                AbsorbOperations(product, node, file, files);
            }
        }

        var rows = new List<SourceRow?[]>();
        var operations = 0;
        Emit(prelude, withRow: false);
        foreach (var product in products.Values.OrderBy(p => p.Index).ThenBy(p => p.Occurrence))
            Emit(product, withRow: true);
        return new MergedList(rows, products.Count, operations);

        void Emit(Product product, bool withRow)
        {
            if (withRow) rows.Add(product.Rows);
            foreach (var id in product.OperationOrder) { rows.Add(product.Operations[id]); operations++; }
        }
    }

    // Все изделия файла в порядке листа (дерево разворачивается так же, как оно было прочитано).
    private static IEnumerable<BreakdownNode> Flatten(BreakdownNode root)
    {
        foreach (var child in root.Children)
        {
            yield return child;
            foreach (var nested in Flatten(child)) yield return nested;
        }
    }

    private static void AbsorbOperations(Product target, BreakdownNode source, int file, int files)
    {
        var ids = new List<string>(source.Operations.Count);
        foreach (var (key, occurrence, row) in source.Operations)
        {
            var id = Id(key, occurrence);
            ids.Add(id);
            if (!target.Operations.TryGetValue(id, out var cells))
                target.Operations[id] = cells = new SourceRow?[files];
            cells[file] = row;
        }
        target.OperationOrder = MergeOrder(target.OperationOrder, ids);
    }

    private static string Id(string key, int occurrence) => $"{key}\u0001{occurrence}";

    /// <summary>
    /// Порядок <paramref name="next"/> (более поздняя дата) с вклеенными строками из
    /// <paramref name="earlier"/>, которых в нем нет: каждая встает сразу за той строкой, за
    /// которой шла раньше (за последней общей перед ней); шедшие до всех общих — в начало.
    /// </summary>
    internal static List<string> MergeOrder(List<string> earlier, List<string> next)
    {
        if (earlier.Count == 0) return next;

        var present = next.ToHashSet(StringComparer.Ordinal);
        var head    = new List<string>();
        var after   = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        string? anchor = null;
        foreach (var id in earlier)
        {
            if (present.Contains(id)) { anchor = id; continue; }
            if (anchor is null) head.Add(id);
            else
            {
                if (!after.TryGetValue(anchor, out var tail)) after[anchor] = tail = [];
                tail.Add(id);
            }
        }

        var merged = new List<string>(earlier.Count + next.Count);
        merged.AddRange(head);
        foreach (var id in next)
        {
            merged.Add(id);
            if (after.TryGetValue(id, out var tail)) merged.AddRange(tail);
        }
        return merged;
    }
}

/// <summary>
/// Индекс изделия в нормальном виде и его порядок «как номеров»: «Изделие» — первым, дальше
/// части через точку сравниваются числами (1.2 раньше 1.10), короткий индекс раньше своих
/// продолжений (1 раньше 1.1). Нечисловая часть идет после числовых и сравнивается как текст.
/// </summary>
internal readonly record struct IndexKey(string Text, string[] Parts) : IComparable<IndexKey>
{
    public const string Top = "Изделие";

    public static IndexKey Parse(string? raw)
    {
        var text = (raw ?? "").Trim();
        if (text.Equals(Top, StringComparison.OrdinalIgnoreCase)) return new IndexKey(Top, []);

        var parts = text.Replace(',', '.')
                        .Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                        .Select(NormalizePart)
                        .ToArray();
        return new IndexKey(string.Join('.', parts), parts);
    }

    // «01» — то же, что «1»; нечисловая часть остается как есть.
    private static string NormalizePart(string part)
    {
        if (!IsNumber(part)) return part;
        var trimmed = part.TrimStart('0');
        return trimmed.Length > 0 ? trimmed : "0";
    }

    private static bool IsNumber(string part) => part.Length > 0 && part.All(char.IsAsciiDigit);

    private bool IsTop => Text == Top;

    public int CompareTo(IndexKey other)
    {
        if (IsTop || other.IsTop) return other.IsTop.CompareTo(IsTop);
        var n = Math.Min(Parts.Length, other.Parts.Length);
        for (var i = 0; i < n; i++)
        {
            var c = ComparePart(Parts[i], other.Parts[i]);
            if (c != 0) return c;
        }
        return Parts.Length.CompareTo(other.Parts.Length);
    }

    private static int ComparePart(string a, string b)
    {
        var an = IsNumber(a);
        var bn = IsNumber(b);
        if (an && bn) return a.Length != b.Length ? a.Length.CompareTo(b.Length) : string.CompareOrdinal(a, b);
        if (an != bn) return an ? -1 : 1;
        return string.Compare(a, b, StringComparison.CurrentCultureIgnoreCase);
    }
}
