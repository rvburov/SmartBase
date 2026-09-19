using SmartBase.Views.Library;

namespace SmartBase.Tests.Unit;

/// <summary>
/// Правила «читать или не читать» раздела «Библиотека документов» (Views/Library/DocLoadPlan.cs).
///
/// Сайдбар присылает набор активных коллекций и когда ничего не менял (возврат в раздел,
/// перемонтирование базы), а на большой базе каждый лишний заход — это счет по всем коллекциям
/// и волна «Загрузка…» на ровном месте. Поэтому сравнение идет по СОДЕРЖИМОМУ, а не по ссылке.
/// </summary>
public sealed class DocLoadPlanTests
{
    private static Dictionary<int, HashSet<int>> Ids(params (int Db, int[] Cols)[] items) =>
        items.ToDictionary(i => i.Db, i => new HashSet<int>(i.Cols));

    // ── Крышка чтения ────────────────────────────────────────────────────────

    [Fact]
    public void Small_sections_are_read_into_memory()
    {
        Assert.True(DocLoadPlan.NeedsDocRead(0, 10_000));
        Assert.True(DocLoadPlan.NeedsDocRead(9_999, 10_000));
        Assert.True(DocLoadPlan.NeedsDocRead(10_000, 10_000));
    }

    [Fact]
    public void Sections_over_the_cap_are_paged()
    {
        Assert.False(DocLoadPlan.NeedsDocRead(10_001, 10_000));
    }

    // ── Набор активных коллекций ─────────────────────────────────────────────

    [Fact]
    public void Same_content_in_different_objects_counts_as_the_same_set()
    {
        Assert.True(DocLoadPlan.SameActiveIds(Ids((1, [10, 20])), Ids((1, [20, 10]))));
    }

    [Fact]
    public void A_different_collection_is_a_different_set()
    {
        Assert.False(DocLoadPlan.SameActiveIds(Ids((1, [10])), Ids((1, [11]))));
    }

    [Fact]
    public void A_different_database_is_a_different_set()
    {
        Assert.False(DocLoadPlan.SameActiveIds(Ids((1, [10])), Ids((2, [10]))));
    }

    [Fact]
    public void An_extra_database_is_a_different_set()
    {
        Assert.False(DocLoadPlan.SameActiveIds(Ids((1, [10])), Ids((1, [10]), (2, [20]))));
    }

    /// <summary>null = «все коллекции»: два null совпадают, null и конкретный набор — нет.</summary>
    [Fact]
    public void Null_means_all_collections()
    {
        Assert.True(DocLoadPlan.SameActiveIds(null, null));
        Assert.False(DocLoadPlan.SameActiveIds(null, Ids((1, [10]))));
        Assert.False(DocLoadPlan.SameActiveIds(Ids((1, [10])), null));
    }

    /// <summary>База без единой отмеченной коллекции — это НЕ то же, что базы нет вовсе.</summary>
    [Fact]
    public void An_empty_set_is_not_the_same_as_a_missing_database()
    {
        Assert.False(DocLoadPlan.SameActiveIds(Ids((1, [])), Ids()));
    }

    // ── Набор типов документа ────────────────────────────────────────────────

    [Fact]
    public void Same_types_in_any_order_count_as_the_same_filter()
    {
        IReadOnlySet<string> a = new HashSet<string> { "ГОСТ", "СТО" };
        IReadOnlySet<string> b = new HashSet<string> { "СТО", "ГОСТ" };

        Assert.True(DocLoadPlan.SameTypes(a, b));
    }

    /// <summary>
    /// null (фильтр снят) и ПУСТОЙ набор («ничего не подходит») — разные состояния: первое
    /// показывает всю ленту, второе — ни одной карточки. Спутать их значило бы показать
    /// человеку все документы ровно тогда, когда он снял все галочки.
    /// </summary>
    [Fact]
    public void No_filter_is_not_the_same_as_an_empty_filter()
    {
        IReadOnlySet<string> empty = new HashSet<string>();

        Assert.True(DocLoadPlan.SameTypes(null, null));
        Assert.False(DocLoadPlan.SameTypes(null, empty));
        Assert.False(DocLoadPlan.SameTypes(empty, null));
    }

    [Fact]
    public void A_different_type_is_a_different_filter()
    {
        IReadOnlySet<string> a = new HashSet<string> { "ГОСТ" };
        IReadOnlySet<string> b = new HashSet<string> { "ГОСТ", "СТО" };

        Assert.False(DocLoadPlan.SameTypes(a, b));
    }

    // ── Оверлей «Загрузка…» ──────────────────────────────────────────────────
    //
    // Регрессия: раньше показ оверлея не был закрыт проверкой поколения, а гашение было.
    // Раздел при открытии грузится несколько раз подряд, обогнанная загрузка просыпалась из
    // ожидания порога уже после того, как сменщица все показала и оверлей погасила, — зажигала
    // его снова, а погасить отказывалась. На пустом разделе «Загрузка…» висела навсегда.

    [Fact]
    public void The_overlay_lights_up_when_a_current_load_is_slow()
    {
        Assert.True(DocLoadPlan.ShouldShowOverlay(quiet: false, timedOut: true, gen: 3, currentGen: 3));
    }

    [Fact]
    public void A_fast_load_never_lights_the_overlay()
    {
        Assert.False(DocLoadPlan.ShouldShowOverlay(quiet: false, timedOut: false, gen: 3, currentGen: 3));
    }

    /// <summary>Тихое обновление не мигает оверлеем: на экране уже есть верная лента.</summary>
    [Fact]
    public void A_quiet_refresh_never_lights_the_overlay()
    {
        Assert.False(DocLoadPlan.ShouldShowOverlay(quiet: true, timedOut: true, gen: 3, currentGen: 3));
    }

    /// <summary>Главное: обогнанная загрузка оверлей НЕ зажигает, даже если она медленная.</summary>
    [Fact]
    public void An_outdated_load_never_lights_the_overlay()
    {
        Assert.False(DocLoadPlan.ShouldShowOverlay(quiet: false, timedOut: true, gen: 2, currentGen: 5));
    }

    [Fact]
    public void Only_a_current_load_hides_the_overlay()
    {
        Assert.True(DocLoadPlan.ShouldHideOverlay(gen: 5, currentGen: 5));
        Assert.False(DocLoadPlan.ShouldHideOverlay(gen: 2, currentGen: 5));
    }

    /// <summary>
    /// Инвариант, ради которого правило и вынесено: показ и гашение закрыты ОДНИМ условием.
    /// Кто не вправе зажечь — не вправе и погасить, и наоборот: иначе оверлей либо остается
    /// висеть, либо гаснет под уже начавшейся следующей загрузкой.
    /// </summary>
    [Theory]
    [InlineData(1, 1)]
    [InlineData(2, 5)]
    [InlineData(7, 3)]
    public void Showing_and_hiding_are_guarded_by_the_same_condition(int gen, int currentGen)
    {
        var mayShow = DocLoadPlan.ShouldShowOverlay(quiet: false, timedOut: true, gen, currentGen);
        var mayHide = DocLoadPlan.ShouldHideOverlay(gen, currentGen);

        Assert.Equal(mayShow, mayHide);
    }
}
