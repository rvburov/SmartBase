using SmartBase.Core.Models;

namespace SmartBase.Tests.Unit;

/// <summary>Тексты блока «Ссылаются на эту запись» и сравнение ответов, по которому страница записи
/// решает, перестраивать ли карточки.</summary>
public sealed class RelationBacklinkTextTests
{
    private static RelationBacklinkGroup Group(int sources, bool more = false, params string[] titles) =>
        new(0, 7, "Клиент", 3, "Счета", sources, more, titles.Select((t, i) => new RelationBacklinkSource(100 - i, t)).ToList(), [0]);

    [Theory]
    [InlineData(1, "запись")]
    [InlineData(2, "записи")]
    [InlineData(4, "записи")]
    [InlineData(5, "записей")]
    [InlineData(11, "записей")]
    [InlineData(14, "записей")]
    [InlineData(21, "запись")]
    [InlineData(22, "записи")]
    [InlineData(111, "записей")]
    [InlineData(1000, "записей")]
    public void Record_word_follows_the_number(int n, string word) => Assert.Equal(word, RelationBacklinkText.RecordsWord(n));

    [Fact]
    public void A_card_value_names_the_count_and_the_newest_sources()
    {
        Assert.Equal("ссылок нет", RelationBacklinkText.Value(Group(0)));
        Assert.Equal("Счет №41", RelationBacklinkText.Value(Group(1, false, "Счет №41")));
        Assert.Equal("2 записи: Счет №41, Счет №40", RelationBacklinkText.Value(Group(2, false, "Счет №41", "Счет №40")));
        Assert.Equal("3 записи: А, Б, В", RelationBacklinkText.Value(Group(3, false, "А", "Б", "В")));
        Assert.Equal("21 запись: А, Б, В…", RelationBacklinkText.Value(Group(21, false, "А", "Б", "В")));
        Assert.Equal($"более {RelationBacklinkText.Cap:N0} записей: А, Б, В…",
            RelationBacklinkText.Value(Group(RelationBacklinkText.Cap, true, "А", "Б", "В")));
        // Пустое и многострочное название — одной строкой.
        Assert.Equal("2 записи: Без названия, Первая", RelationBacklinkText.Value(Group(2, false, "  ", "\nПервая\nвторая")));
        Assert.Equal("Счета · Клиент", RelationBacklinkText.Name(Group(0)));
    }

    [Fact]
    public void Headers_and_list_hints()
    {
        Assert.Equal("Ссылаются на эту запись", RelationBacklinkText.Header(null));
        Assert.Equal("Ссылаются на эту запись из базы «Учет»", RelationBacklinkText.Header("Учет"));
        Assert.Equal($"Показаны 200 из {1234:N0}", RelationBacklinkText.PageHint(200, 1234, true));
        Assert.Equal($"Показаны 200 из более чем {RelationBacklinkText.Cap:N0}", RelationBacklinkText.PageHint(200, null, true));
        Assert.Equal("", RelationBacklinkText.PageHint(200, null, false));
        Assert.Equal("Показаны первые 200 — прокрутите или уточните поиск", RelationBacklinkText.SearchHint(200));
    }

    [Fact]
    public void Texts_have_no_letter_yo()
    {
        var texts = new[]
        {
            RelationBacklinkText.Value(Group(5, false, "А")), RelationBacklinkText.Value(Group(1000, true, "А")),
            RelationBacklinkText.Header("Учет"), RelationBacklinkText.PageHint(1, 2, true), RelationBacklinkText.PageHint(1, null, true),
            RelationBacklinkText.SearchHint(1), RelationBacklinkText.None, RelationBacklinkText.FailedName, RelationBacklinkText.FailedValue,
        };
        Assert.All(texts, t => Assert.DoesNotContain('\u0451', t));
    }

    [Fact]
    public void Answers_are_compared_by_content()
    {
        RelationBacklinkSection Section(RelationBacklinkGroup group, bool failed = false) => new(0, null, [group], failed);
        var a = Section(Group(2, false, "А", "Б"));
        Assert.True(RelationBacklinkSection.Same([a], [Section(Group(2, false, "А", "Б"))]));
        Assert.False(RelationBacklinkSection.Same([a], [Section(Group(3, false, "А", "Б"))]));
        Assert.False(RelationBacklinkSection.Same([a], [Section(Group(2, false, "А", "В"))]));
        Assert.False(RelationBacklinkSection.Same([a], [Section(Group(2, true, "А", "Б"))]));
        Assert.False(RelationBacklinkSection.Same([a], [a with { Failed = true }]));
        Assert.False(RelationBacklinkSection.Same([a], [a, a]));
        Assert.False(RelationBacklinkSection.Same([a], [a with { BaseName = "Учет" }]));
    }
}
