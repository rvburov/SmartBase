using SmartBase.Controls.Dialogs;

namespace SmartBase.Tests.Unit;

/// <summary>
/// Значения колонок ответа под их именами в окне раскладки (<see cref="BindingSample"/>,
/// docs/ROADMAP-API.md, раздел 2 — «показ первых 20 строк»).
///
/// Обещано это не ради полноты. Раскладка подбирается ПО ИМЕНАМ, а у чужих программ имена похожи
/// до неразличимости — <c>name</c> и <c>title</c>, <c>sum</c> и <c>total</c>, — и без значений
/// ошибку в них видно только после укладки, то есть по испорченной коллекции.
/// </summary>
public sealed class BindingSampleTests
{
    private static IReadOnlyList<IReadOnlyList<string>> Rows(params string[][] rows) =>
        [.. rows.Select(r => (IReadOnlyList<string>)r)];

    [Fact]
    public void Values_stand_under_their_own_column()
    {
        var made = BindingSample.Of(["Название", "Номер"],
                                    Rows(["Молоток", "А-1"], ["Отвертка", "А-2"]));

        Assert.Equal("Молоток, Отвертка", made[0]);
        Assert.Equal("А-1, А-2",          made[1]);
    }

    /// <summary>
    /// Пустые значения пропускаются. Колонка, у которой первые три значения пусты, а четвертое
    /// «А-1», — самый интересный случай для сверки; показать по ней «, , ,» значило бы промолчать
    /// ровно там, где ответ нужен.
    /// </summary>
    [Fact]
    public void Empty_values_are_skipped_not_shown_as_gaps()
    {
        var made = BindingSample.Of(["Номер"], Rows([""], ["   "], [""], ["А-1"]));

        Assert.Equal("А-1", made[0]);
    }

    /// <summary>Значений нет вовсе — пустая строка: ей в разметке места не дадут.</summary>
    [Fact]
    public void A_column_without_values_shows_nothing()
    {
        Assert.Equal("", BindingSample.Of(["Примечание"], Rows([""], [""]))[0]);
        Assert.Equal("", BindingSample.Of(["Примечание"], [])[0]);
    }

    /// <summary>Берутся первые ДВАДЦАТЬ — столько и обещано разделом 2.</summary>
    [Fact]
    public void Only_the_first_twenty_values_are_taken()
    {
        var rows = Enumerable.Range(1, 50).Select(i => new[] { "А-" + i }).ToArray();

        var made = BindingSample.Of(["Номер"], Rows(rows));

        Assert.StartsWith("А-1, А-2", made[0], StringComparison.Ordinal);
        Assert.Contains("А-20",       made[0], StringComparison.Ordinal);
        Assert.DoesNotContain("А-21", made[0], StringComparison.Ordinal);
    }

    /// <summary>
    /// Длинное значение обрезается: одно описание на тысячу знаков вытеснило бы из строки все
    /// остальные, и сверять стало бы нечего.
    /// </summary>
    [Fact]
    public void A_long_value_is_cut_so_that_the_rest_stay_visible()
    {
        var made = BindingSample.Of(["Описание"], Rows([new string('щ', 200)], ["коротко"]));

        Assert.Contains("…",       made[0], StringComparison.Ordinal);
        Assert.Contains("коротко", made[0], StringComparison.Ordinal);
        Assert.True(made[0].Length < 80);
    }

    /// <summary>
    /// Короткая строка не роняет разбор: у чужих программ запись без части колонок — обычное
    /// дело, и падать на ней окно раскладки не должно.
    /// </summary>
    [Fact]
    public void A_short_row_does_not_break_the_rest()
    {
        var made = BindingSample.Of(["Название", "Номер", "Статус"],
                                    Rows(["Молоток"], ["Отвертка", "А-2", "новый"]));

        Assert.Equal("Молоток, Отвертка", made[0]);
        Assert.Equal("А-2",               made[1]);
        Assert.Equal("новый",             made[2]);
    }

    /// <summary>Строк ровно столько, сколько колонок: разметка ходит по ним по индексу.</summary>
    [Fact]
    public void There_is_one_line_per_column()
    {
        Assert.Equal(3, BindingSample.Of(["а", "б", "в"], Rows(["1", "2", "3"])).Count);
    }
}
