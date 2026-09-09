using SmartBase.Integrations;

namespace SmartBase.Tests.Unit;

/// <summary>
/// Привязка «ответ → коллекция» (<see cref="CollectionBinding"/>): что считается заполненным
/// правильно и как читается итог применения.
///
/// Само применение проверяется на настоящей базе — <c>Tests/Integration/CollectionBindingTests</c>;
/// здесь только форма, зато без базы и мгновенно.
/// </summary>
public sealed class CollectionBindingTests
{
    private static CollectionBinding Valid() =>
        new(7, [new BindingColumn("Название", null), new BindingColumn("Номер", 11)]);

    // ── Заполнено правильно ───────────────────────────────────────────────────

    [Fact]
    public void Filled_binding_has_no_problems() => Assert.Empty(Valid().Problems());

    [Fact]
    public void Binding_without_a_key_creates_everything()
    {
        var binding = Valid();

        Assert.False(binding.Matches);
        Assert.False(binding.MatchColumnBound);
    }

    [Fact]
    public void Key_column_points_at_its_target()
    {
        var byField = Valid() with { MatchColumn = "Номер" };
        var byTitle = Valid() with { MatchColumn = "Название" };

        Assert.True(byField.Matches);
        Assert.True(byField.MatchColumnBound);
        Assert.Equal(11, byField.MatchFieldId);

        Assert.True(byTitle.MatchColumnBound);
        Assert.Null(byTitle.MatchFieldId);   // null здесь значит «в название»
    }

    // ── Что не годится ────────────────────────────────────────────────────────

    [Fact]
    public void Collection_is_required()
    {
        Assert.Contains((Valid() with { CollectionId = 0 }).Problems(),
            p => p.Contains("коллекция", StringComparison.Ordinal));
    }

    [Fact]
    public void At_least_one_column_is_required()
    {
        Assert.Contains((Valid() with { Columns = [] }).Problems(),
            p => p.Contains("ни одной колонки", StringComparison.Ordinal));
    }

    [Fact]
    public void Only_one_column_may_become_the_title()
    {
        var binding = Valid() with
        {
            Columns = [new BindingColumn("Название", null), new BindingColumn("Имя", null)],
        };

        Assert.Contains(binding.Problems(), p => p.Contains("Названием", StringComparison.Ordinal));
    }

    [Fact]
    public void One_field_cannot_take_two_columns()
    {
        var binding = Valid() with
        {
            Columns = [new BindingColumn("Номер", 11), new BindingColumn("Код", 11)],
        };

        Assert.Contains(binding.Problems(), p => p.Contains("несколько колонок", StringComparison.Ordinal));
    }

    [Fact]
    public void Column_cannot_be_listed_twice()
    {
        var binding = Valid() with
        {
            Columns = [new BindingColumn("Номер", 11), new BindingColumn("номер", 12)],
        };

        Assert.Contains(binding.Problems(), p => p.Contains("несколько раз", StringComparison.Ordinal));
    }

    /// <summary>
    /// Искать разрешено только по тому, что и так переносится: иначе получалась бы связь,
    /// которой не видно на экране.
    /// </summary>
    [Fact]
    public void Key_column_must_be_among_the_transferred_ones()
    {
        Assert.Contains((Valid() with { MatchColumn = "Чужая" }).Problems(),
            p => p.Contains("не переносится", StringComparison.Ordinal));
    }

    /// <summary>Ни обновлять, ни заводить — привязка, которая гарантированно ничего не сделает.</summary>
    [Fact]
    public void Doing_nothing_at_all_is_reported()
    {
        var binding = Valid() with
        {
            MatchColumn    = "Номер",
            UpdateExisting = false,
            CreateMissing  = false,
        };

        Assert.Contains(binding.Problems(), p => p.Contains("переносить нечего", StringComparison.Ordinal));
    }

    // ── Итог ──────────────────────────────────────────────────────────────────

    [Fact]
    public void Summary_names_only_what_happened()
    {
        Assert.Equal("создано 2, обновлено 1", new BindingResult(2, 1, 0, 0, 0).Summary());
        Assert.Equal("пропущено 3, неоднозначных 1", new BindingResult(0, 0, 3, 1, 0).Summary());
        Assert.Equal("изменений нет", BindingResult.Nothing.Summary());
    }

    [Fact]
    public void Total_counts_every_row()
    {
        Assert.Equal(10, new BindingResult(1, 2, 3, 3, 1).Total);
    }
}
