using SmartBase.Core.Models;
using SmartBase.Views.Library;

namespace SmartBase.Tests.Unit;

public sealed class DocSelectionTests
{
    private static DocRecord Doc(int id, int db = 1, int field = 1) =>
        new() { Id = id, DbId = db, FileFieldId = field };

    [Fact]
    public void Identity_contains_database_record_and_file_field()
    {
        var selection = new DocSelection();
        foreach (var d in new[] { Doc(1), Doc(1, 2), Doc(1, field: 2) }) selection.Toggle(d);
        Assert.Equal(3, selection.Count);
        Assert.True(selection.Contains(Doc(1, field: 2)));
        selection.Toggle(Doc(1, field: 2));
        Assert.Equal(2, selection.Count);
        Assert.False(selection.Contains(Doc(1, field: 2)));
    }

    [Fact]
    public void A_reversed_range_is_inclusive_and_keeps_previous_selection()
    {
        var docs = Enumerable.Range(1, 5).Select(i => Doc(i)).ToArray();
        var selection = new DocSelection();
        selection.Select(docs[0]);
        selection.SelectRange(docs, docs[4], docs[2]);
        Assert.Equal(new[] { 1, 3, 4, 5 }, selection.Keys.Select(k => k.RecordId).Order().ToArray());
        selection.Select(docs[1]);
        Assert.Equal(2, Assert.Single(selection.Keys).RecordId);
        selection.Clear();
        Assert.Empty(selection.Keys);
    }

    [Fact]
    public void A_missing_endpoint_falls_back_to_the_destination()
    {
        var selection = new DocSelection();
        selection.Select(Doc(9));
        selection.SelectRange([Doc(1), Doc(2)], Doc(9), Doc(2));
        Assert.Equal(2, Assert.Single(selection.Keys).RecordId);
        selection.SelectRange([Doc(1)], Doc(1), new DocRecord());
        Assert.Empty(selection.Keys);
        selection.Toggle(new DocRecord());
        Assert.Empty(selection.Keys);
    }
}
