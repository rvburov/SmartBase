using SmartBase.Core.Models;

namespace SmartBase.Core.Documents;

public enum DocSort { Collections, Title, TitleDescending, Modified }
public readonly record struct DocOrderItem(int RecordId, int FieldId, string Title, long Modified = 0, int FileIndex = 0);
public readonly record struct DocOrderKey(int Entry, int RecordId, int FieldId, int FileIndex = 0);
public sealed record DocOrderStream(int DbId, int CollectionId, IReadOnlyList<DocOrderItem> Items);

/// <summary>Слияние легких ключей всех коллекций. В готовом порядке остаются только четыре int
/// на документ; названия и пути не удерживаются виртуальной лентой.</summary>
public static class DocOrderPlan
{
    public static string Title(string title, string path)
    {
        var line = TitleText.FirstLine(title);
        return line.Length > 0 ? line : FileValue.DisplayName(path);
    }

    public static int Compare(DocRecord a, DocRecord b, DocSort sort)
    {
        var value = CompareValue(Title(a.Title, a.Path), a.Mtime, Title(b.Title, b.Path), b.Mtime, sort);
        if (value != 0) return value;
        value = a.DbId.CompareTo(b.DbId);
        if (value != 0) return value;
        value = Nullable.Compare(a.CollectionId, b.CollectionId);
        if (value != 0) return value;
        value = Nullable.Compare(a.Id, b.Id);
        if (value != 0) return value;
        value = a.FileFieldId.CompareTo(b.FileFieldId);
        return value != 0 ? value : a.FileIndex.CompareTo(b.FileIndex);
    }

    private static int CompareValue(string a, long at, string b, long bt, DocSort sort)
    {
        if (sort == DocSort.Modified && at != bt) return bt.CompareTo(at);
        var result = string.Compare(a, b, StringComparison.CurrentCultureIgnoreCase);
        return sort == DocSort.TitleDescending ? -result : result;
    }

    private readonly record struct Head(int Entry, int Offset, DocOrderItem Item);

    public static DocOrderKey[] Merge(IReadOnlyList<DocOrderStream> streams, DocSort sort, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (sort == DocSort.Collections)
        {
            var keys = new List<DocOrderKey>();
            for (var e = 0; e < streams.Count; e++)
                foreach (var item in streams[e].Items)
                {
                    ct.ThrowIfCancellationRequested();
                    keys.Add(new DocOrderKey(e, item.RecordId, item.FieldId, item.FileIndex));
                }
            ct.ThrowIfCancellationRequested();
            return keys.ToArray();
        }
        var groups = new DocOrderItem[streams.Count][];
        for (var e = 0; e < streams.Count; e++)
        {
            ct.ThrowIfCancellationRequested();
            groups[e] = streams[e].Items.ToArray();
        }
        ct.ThrowIfCancellationRequested();
        int CompareHead(Head a, Head b)
        {
            var value = CompareValue(a.Item.Title, a.Item.Modified, b.Item.Title, b.Item.Modified, sort);
            if (value != 0) return value;
            value = streams[a.Entry].DbId.CompareTo(streams[b.Entry].DbId);
            if (value != 0) return value;
            value = streams[a.Entry].CollectionId.CompareTo(streams[b.Entry].CollectionId);
            if (value != 0) return value;
            value = a.Item.RecordId.CompareTo(b.Item.RecordId);
            if (value != 0) return value;
            value = a.Item.FieldId.CompareTo(b.Item.FieldId);
            return value != 0 ? value : a.Item.FileIndex.CompareTo(b.Item.FileIndex);
        }
        var heap = new PriorityQueue<Head, Head>(Comparer<Head>.Create(CompareHead));
        for (var e = 0; e < groups.Length; e++)
        {
            ct.ThrowIfCancellationRequested();
            var entry = e;
            Array.Sort(groups[e], (a, b) => CompareHead(new Head(entry, 0, a), new Head(entry, 0, b)));
            ct.ThrowIfCancellationRequested();
            if (groups[e].Length > 0)
            {
                var head = new Head(e, 0, groups[e][0]);
                heap.Enqueue(head, head);
            }
        }
        var result = new DocOrderKey[groups.Sum(g => g.Length)];
        var at = 0;
        while (heap.TryDequeue(out var head, out _))
        {
            if ((at & 255) == 0) ct.ThrowIfCancellationRequested();
            result[at++] = new DocOrderKey(head.Entry, head.Item.RecordId, head.Item.FieldId, head.Item.FileIndex);
            groups[head.Entry][head.Offset] = default;
            var next = head.Offset + 1;
            if (next >= groups[head.Entry].Length) continue;
            var following = new Head(head.Entry, next, groups[head.Entry][next]);
            heap.Enqueue(following, following);
        }
        ct.ThrowIfCancellationRequested();
        return result;
    }
}
