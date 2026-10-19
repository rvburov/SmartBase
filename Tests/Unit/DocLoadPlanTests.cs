using SmartBase.Core.Documents;
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
        Assert.True(DocLoadPlan.NeedsDocRead(0, DocLibraryLimits.InMemoryDocuments));
        Assert.True(DocLoadPlan.NeedsDocRead(DocLibraryLimits.InMemoryDocuments - 1, DocLibraryLimits.InMemoryDocuments));
        Assert.True(DocLoadPlan.NeedsDocRead(DocLibraryLimits.InMemoryDocuments, DocLibraryLimits.InMemoryDocuments));
    }

    [Fact]
    public void Sections_over_the_cap_are_paged()
    {
        Assert.False(DocLoadPlan.NeedsDocRead(DocLibraryLimits.InMemoryDocuments + 1, DocLibraryLimits.InMemoryDocuments));
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
    // Регрессия: раньше показ отметки не был закрыт проверкой поколения, а гашение было.
    // Раздел при открытии грузится несколько раз подряд, обогнанная загрузка просыпалась из
    // ожидания порога уже после того, как сменщица все показала и отметку погасила, — зажигала
    // его снова, а погасить отказывалась. На пустом разделе «Загрузка…» висела навсегда.

    [Fact]
    public void The_overlay_lights_up_when_a_current_load_is_slow()
    {
        Assert.True(DocLoadPlan.ShouldShowLoading(quiet: false, timedOut: true, gen: 3, currentGen: 3));
    }

    [Fact]
    public void A_fast_load_never_lights_the_overlay()
    {
        Assert.False(DocLoadPlan.ShouldShowLoading(quiet: false, timedOut: false, gen: 3, currentGen: 3));
    }

    /// <summary>Тихое обновление не мигает отметкой: на экране уже есть верная лента.</summary>
    [Fact]
    public void A_quiet_refresh_never_lights_the_overlay()
    {
        Assert.False(DocLoadPlan.ShouldShowLoading(quiet: true, timedOut: true, gen: 3, currentGen: 3));
    }

    /// <summary>Главное: обогнанная загрузка отметку НЕ зажигает, даже если она медленная.</summary>
    [Fact]
    public void An_outdated_load_never_lights_the_overlay()
    {
        Assert.False(DocLoadPlan.ShouldShowLoading(quiet: false, timedOut: true, gen: 2, currentGen: 5));
    }

    [Fact]
    public void Only_a_current_load_hides_the_overlay()
    {
        Assert.True(DocLoadPlan.ShouldHideLoading(gen: 5, currentGen: 5));
        Assert.False(DocLoadPlan.ShouldHideLoading(gen: 2, currentGen: 5));
    }

    /// <summary>
    /// Инвариант, ради которого правило и вынесено: показ и гашение закрыты ОДНИМ условием.
    /// Кто не вправе зажечь — не вправе и погасить, и наоборот: иначе отметка либо остается
    /// висеть, либо гаснет под уже начавшейся следующей загрузкой.
    /// </summary>
    [Theory]
    [InlineData(1, 1)]
    [InlineData(2, 5)]
    [InlineData(7, 3)]
    public void Showing_and_hiding_are_guarded_by_the_same_condition(int gen, int currentGen)
    {
        var mayShow = DocLoadPlan.ShouldShowLoading(quiet: false, timedOut: true, gen, currentGen);
        var mayHide = DocLoadPlan.ShouldHideLoading(gen, currentGen);

        Assert.Equal(mayShow, mayHide);
    }

    // ── Переключатель панели чтения ──────────────────────────────────────────

    /// <summary>
    /// Просьба открыть уже открытую панель — это эхо своего же <c>SetReaderOpen</c>, которым
    /// программа догоняет кнопку после открытия с карточки. Пока правила не было, каждое
    /// первое открытие разбирало документ ДВАЖДЫ: два Reset, две загрузки, два открытия
    /// PDFium — а в режиме «как в документе» два прохода писали в общий список совпадений, и
    /// счетчик со стрелками разъезжался на дублях.
    /// </summary>
    [Fact]
    public void Opening_an_already_open_reader_is_an_echo()
    {
        Assert.Equal(DocReaderAction.Ignore,
                     DocLoadPlan.ReaderToggle(wantOpen: true, isOpen: true, hasLastRead: true));
    }

    /// <summary>Закрытие крестиком возвращается тем же эхом — закрывать второй раз нечего.</summary>
    [Fact]
    public void Closing_an_already_closed_reader_is_an_echo()
    {
        Assert.Equal(DocReaderAction.Ignore,
                     DocLoadPlan.ReaderToggle(wantOpen: false, isOpen: false, hasLastRead: true));
    }

    /// <summary>Живое нажатие по закрытой панели возвращает последний читанный документ.</summary>
    [Fact]
    public void A_live_press_opens_the_last_read_document()
    {
        Assert.Equal(DocReaderAction.Open,
                     DocLoadPlan.ReaderToggle(wantOpen: true, isOpen: false, hasLastRead: true));
    }

    /// <summary>Живое нажатие по открытой панели ее закрывает.</summary>
    [Fact]
    public void A_live_press_closes_an_open_reader()
    {
        Assert.Equal(DocReaderAction.Close,
                     DocLoadPlan.ReaderToggle(wantOpen: false, isOpen: true, hasLastRead: true));
    }

    /// <summary>
    /// Открывать пустую панель нечем, поэтому кнопка откатывается — и откат сам эха не
    /// порождает: он ставит кнопку в то же положение, в каком стоит панель, и следующий заход
    /// уже ничего не делает. Без этого кнопка с обработчиком гоняли бы друг друга по кругу.
    /// </summary>
    [Fact]
    public void Without_a_last_read_document_the_button_rolls_back_and_settles()
    {
        Assert.Equal(DocReaderAction.Rollback,
                     DocLoadPlan.ReaderToggle(wantOpen: true, isOpen: false, hasLastRead: false));

        // Откат шлет wantOpen: false, а панель по-прежнему закрыта.
        Assert.Equal(DocReaderAction.Ignore,
                     DocLoadPlan.ReaderToggle(wantOpen: false, isOpen: false, hasLastRead: false));
    }

    /// <summary>
    /// Полный круг «нажали — открыли — догнали кнопку»: второй заход обязан быть пустым.
    /// Это и есть та проверка, которой не было, когда документ разбирался дважды.
    /// </summary>
    [Fact]
    public void The_open_round_trip_does_the_work_once()
    {
        var isOpen = false;

        var first = DocLoadPlan.ReaderToggle(wantOpen: true, isOpen, hasLastRead: true);
        Assert.Equal(DocReaderAction.Open, first);
        isOpen = true;                                   // панель открылась, документ разобран

        // SetReaderOpen(true) поднимает то же событие — это и есть эхо.
        Assert.Equal(DocReaderAction.Ignore,
                     DocLoadPlan.ReaderToggle(wantOpen: true, isOpen, hasLastRead: true));
    }

    // ── Чужие правки ─────────────────────────────────────────────────────────

    /// <summary>
    /// Лента по чужой правке перечитывается не чаще раза в промежуток: первая правка — сразу,
    /// следующая внутри промежутка ждет его конца, после конца — снова сразу.
    /// </summary>
    [Fact]
    public void Remote_refresh_waits_out_the_gap_since_the_last_one()
    {
        var gap  = TimeSpan.FromSeconds(5);
        var last = new DateTime(2026, 10, 4, 12, 0, 0, DateTimeKind.Utc);

        Assert.Equal(TimeSpan.Zero, DocLoadPlan.RemoteWait(DateTime.MinValue, last, gap));   // первая — сразу
        Assert.Equal(TimeSpan.FromSeconds(3), DocLoadPlan.RemoteWait(last, last.AddSeconds(2), gap));
        Assert.Equal(TimeSpan.Zero, DocLoadPlan.RemoteWait(last, last.AddSeconds(5), gap));
        Assert.Equal(TimeSpan.Zero, DocLoadPlan.RemoteWait(last, last.AddMinutes(1), gap));
    }
}
