namespace SmartBase.Extensions.ProductCompare;

/// <summary>Общий список входимостей: строки результата по порядку. В строке — по ячейке на
/// файл (в порядке дат); <c>null</c> — на эту дату такой строки не было.</summary>
internal sealed record MergedList(IReadOnlyList<SourceRow?[]> Rows, int Products, int Operations);

/// <summary>
/// Сведение деревьев входимости всех дат в одно. Изделие одной даты совпадает с изделием
/// другой, если совпадает путь от верхнего изделия: ключ (обозначение КД, без него —
/// наименование) и порядковый номер повтора на каждом уровне. Операции сводятся внутри своего
/// изделия по названию и номеру повтора: две «Подготовки» — первая к первой, вторая ко второй.
/// Вид работ в ключ не входит — если он сменился, обе даты встанут в одну строку, и разница
/// видна рядом.
///
/// Порядок — по последней дате; строка, которой в ней уже нет, остается после той соседки,
/// за которой стояла раньше. Изделие идет строкой, за ним его операции, за ними входящие —
/// так же, как в самой расшифровке.
/// </summary>
internal static class BreakdownMerge
{
    private sealed class Node(int files)
    {
        public SourceRow?[] Rows { get; } = new SourceRow?[files];
        public List<string> OperationOrder { get; set; } = [];
        public Dictionary<string, SourceRow?[]> Operations { get; } = new(StringComparer.Ordinal);
        public List<string> ChildOrder { get; set; } = [];
        public Dictionary<string, Node> Children { get; } = new(StringComparer.Ordinal);
    }

    public static MergedList Merge(IReadOnlyList<BreakdownNode> roots)
    {
        var files = roots.Count;
        var root  = new Node(files);
        for (var i = 0; i < files; i++) Absorb(root, roots[i], i, files);

        var rows = new List<SourceRow?[]>();
        int products = 0, operations = 0;
        Emit(root, isRoot: true);
        return new MergedList(rows, products, operations);

        void Emit(Node node, bool isRoot)
        {
            if (!isRoot) { rows.Add(node.Rows); products++; }
            foreach (var id in node.OperationOrder) { rows.Add(node.Operations[id]); operations++; }
            foreach (var id in node.ChildOrder) Emit(node.Children[id], isRoot: false);
        }
    }

    private static void Absorb(Node target, BreakdownNode source, int file, int files)
    {
        target.Rows[file] = source.Row;

        var operationIds = new List<string>(source.Operations.Count);
        foreach (var (key, occurrence, row) in source.Operations)
        {
            var id = Id(key, occurrence);
            operationIds.Add(id);
            if (!target.Operations.TryGetValue(id, out var cells))
                target.Operations[id] = cells = new SourceRow?[files];
            cells[file] = row;
        }
        target.OperationOrder = MergeOrder(target.OperationOrder, operationIds);

        var childIds = new List<string>(source.Children.Count);
        foreach (var child in source.Children)
        {
            var id = Id(child.Key, child.Occurrence);
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
