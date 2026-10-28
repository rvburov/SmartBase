namespace SmartBase.Extensions.ProductCompare;

/// <summary>Общий список строк: строки результата по порядку. В строке — по ячейке на файл (в
/// порядке дат); <c>null</c> — на эту дату такой строки не было.</summary>
internal sealed record MergedList(IReadOnlyList<SourceRow?[]> Rows, int Products, int Operations);

/// <summary>
/// Сведение расшифровок всех дат в один список строк.
///
/// Изделия сопоставляются по децимальному номеру КД внутри соответствующего родительского
/// узла; без КД — по наименованию. Повторы одного ключа среди входящих изделий одного узла
/// сопоставляются по порядку: первый к первому, второй ко второму. Ключи и номер повтора на
/// каждом уровне задают путь входимости, поэтому одинаковые КД разных узлов не смешиваются.
/// Если нет ни КД, ни наименования, строка остается отдельной для каждого файла.
///
/// Сначала идут все строки последней даты в ее исходном порядке: изделие, его операции и
/// входящие изделия. Затем — исторические строки, которых на последнюю дату нет.
/// Актуальная структура последней даты остается непрерывной.
/// Исходные индексы, уровни и остальные ячейки каждого файла переносятся без исправлений.
///
/// Операции — внутри своего изделия по названию и номеру повтора: две
/// «Подготовки» — первая к первой, вторая ко второй. Вид работ в ключ не входит — если он
/// сменился, обе даты встанут в одну строку, и разница видна рядом. Операции до первого изделия
/// остаются перед изделиями своей части списка: актуальной или исторической.
/// </summary>
internal static class BreakdownMerge
{
    private sealed class Node(int files)
    {
        public SourceRow?[] Rows       { get; } = new SourceRow?[files];
        public List<string> OperationOrder { get; set; } = [];
        public Dictionary<string, SourceRow?[]> Operations { get; } = new(StringComparer.Ordinal);
        public List<string> ChildOrder { get; set; } = [];
        public Dictionary<string, Node> Children { get; } = new(StringComparer.Ordinal);
    }

    public static MergedList Merge(IReadOnlyList<BreakdownNode> roots)
    {
        var files = roots.Count;
        if (files == 0) return new MergedList([], 0, 0);

        var root = new Node(files);
        for (var file = 0; file < files; file++) Absorb(root, roots[file], file, files);

        var current = new List<SourceRow?[]>();
        var historical = new List<SourceRow?[]>();
        int products = 0, operations = 0;
        Emit(root, withRow: false);
        current.AddRange(historical);
        return new MergedList(current, products, operations);

        void Add(SourceRow?[] cells) =>
            (cells[^1] is not null ? current : historical).Add(cells);

        void Emit(Node node, bool withRow)
        {
            if (withRow) { Add(node.Rows); products++; }
            foreach (var id in node.OperationOrder) { Add(node.Operations[id]); operations++; }
            foreach (var id in node.ChildOrder) Emit(node.Children[id], withRow: true);
        }
    }

    private static void Absorb(Node target, BreakdownNode source, int file, int files)
    {
        target.Rows[file] = source.Row;

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

        var childIds = new List<string>(source.Children.Count);
        var seen = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var child in source.Children)
        {
            var occurrence = seen.GetValueOrDefault(child.Key);
            seen[child.Key] = occurrence + 1;
            var id = Id(child.Key, occurrence);
            // Без обоих идентификаторов нельзя утверждать, что это одна и та же позиция.
            if (child.Key is "" or "Имя:") id += $"\u0001{file}";
            childIds.Add(id);
            if (!target.Children.TryGetValue(id, out var node))
                target.Children[id] = node = new Node(files);
            Absorb(node, child, file, files);
        }
        target.ChildOrder = MergeOrder(target.ChildOrder, childIds);
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
