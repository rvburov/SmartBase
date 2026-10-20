using System.Globalization;
using SmartBase.Core.Documents;
using SmartBase.Core.Models;

namespace SmartBase.Tests.Unit;

public sealed class DocOrderPlanTests
{
    private static readonly DocOrderStream[] Streams =
    [
        new(2, 8, [new(9, 1, "Я", 10), new(2, 1, "А", 30)]),
        new(1, 5, [new(1, 2, "А", 20), new(1, 1, "А", 20), new(7, 1, "Б", 40)])
    ];

    [Theory]
    [InlineData(DocSort.Collections, "0:9:1,0:2:1,1:1:2,1:1:1,1:7:1")]
    [InlineData(DocSort.Title, "1:1:1,1:1:2,0:2:1,1:7:1,0:9:1")]
    [InlineData(DocSort.TitleDescending, "0:9:1,1:7:1,1:1:1,1:1:2,0:2:1")]
    [InlineData(DocSort.Modified, "1:7:1,0:2:1,1:1:1,1:1:2,0:9:1")]
    public void Global_order_has_stable_identity_ties(DocSort sort, string expected)
    {
        var result = DocOrderPlan.Merge(Streams, sort);
        Assert.Equal(expected, string.Join(",", result.Select(k => $"{k.Entry}:{k.RecordId}:{k.FieldId}")));
        Assert.Equal("Я", Streams[0].Items[0].Title);
        Assert.Empty(DocOrderPlan.Merge([], sort));
    }

    [Theory]
    [InlineData(DocSort.Collections, "2,0,1")]
    [InlineData(DocSort.Title, "0,1,2")]
    [InlineData(DocSort.TitleDescending, "0,1,2")]
    [InlineData(DocSort.Modified, "0,1,2")]
    public void Files_of_one_property_have_distinct_stable_order_keys(DocSort sort, string expected)
    {
        DocOrderStream[] streams = [new(1, 1, [
            new(7, 3, "Комплект", 10, FileIndex: 2),
            new(7, 3, "Комплект", 10, FileIndex: 0),
            new(7, 3, "Комплект", 10, FileIndex: 1)])];
        var result = DocOrderPlan.Merge(streams, sort);
        Assert.Equal(expected, string.Join(",", result.Select(k => k.FileIndex)));
        Assert.Equal(3, result.Distinct().Count());
        var first = new DocRecord { DbId = 1, CollectionId = 1, Id = 7, FileFieldId = 3,
                                    FileIndex = 0, Title = "Комплект", Mtime = 10 };
        var second = new DocRecord { DbId = 1, CollectionId = 1, Id = 7, FileFieldId = 3,
                                     FileIndex = 1, Title = "Комплект", Mtime = 10 };
        Assert.True(DocOrderPlan.Compare(first, second, sort) < 0);
    }

    [Theory]
    [InlineData(DocSort.Title, "2,3,1")]
    [InlineData(DocSort.TitleDescending, "1,3,2")]
    public void Title_order_respects_the_users_alphabet(DocSort sort, string expected)
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("ru-RU");
            var result = DocOrderPlan.Merge([new(1, 1, [new(1, 1, "Жук"), new(2, 1, "Еж"), new(3, 1, "\u0401лка")])], sort);
            Assert.Equal(expected, string.Join(",", result.Select(k => k.RecordId)));
        }
        finally { CultureInfo.CurrentCulture = previous; }
    }

    [Theory]
    [InlineData(DocSort.Collections)]
    [InlineData(DocSort.Title)]
    [InlineData(DocSort.TitleDescending)]
    [InlineData(DocSort.Modified)]
    public void Cancelled_order_does_not_read_the_sources(DocSort sort)
    {
        using var cts = new CancellationTokenSource();
        var reads = 0;
        var items = new ReadProbe(() => reads++);
        cts.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => DocOrderPlan.Merge([new(1, 1, items)], sort, cts.Token));
        Assert.Equal(0, reads);
    }

    [Theory]
    [InlineData(DocSort.Collections)]
    [InlineData(DocSort.Title)]
    [InlineData(DocSort.TitleDescending)]
    [InlineData(DocSort.Modified)]
    public void Cancellation_while_reading_does_not_continue_to_the_next_source(DocSort sort)
    {
        using var cts = new CancellationTokenSource();
        var laterReads = 0;
        DocOrderStream[] streams = [new(1, 1, new ReadProbe(cts.Cancel)), new(2, 1, new ReadProbe(() => laterReads++))];
        Assert.ThrowsAny<OperationCanceledException>(() => DocOrderPlan.Merge(streams, sort, cts.Token));
        Assert.Equal(0, laterReads);
    }

    private sealed class ReadProbe(Action read) : IReadOnlyList<DocOrderItem>
    {
        public int Count => 1;
        public DocOrderItem this[int index] => new(1, 1, "Документ");
        public IEnumerator<DocOrderItem> GetEnumerator()
        {
            read();
            yield return this[0];
        }
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }

    [Fact]
    public void Large_directory_keeps_all_document_keys()
    {
        var streams = Enumerable.Range(0, 3).Select(e => new DocOrderStream(e, e,
            Enumerable.Range(0, 4200).Select(i => new DocOrderItem(i, 1, $"{12599 - (i * 3 + e):D5}")).ToArray())).ToArray();
        var order = DocOrderPlan.Merge(streams, DocSort.Title);
        Assert.Equal(12600, order.Length);
        Assert.Equal(new DocOrderKey(2, 4199, 1), order[0]);
        Assert.Equal(new DocOrderKey(0, 0, 1), order[^1]);
        Assert.Equal(order.Length, order.Distinct().Count());
    }
}
