using SmartBase.Database.DocIndex;
using SmartBase.Views.Library;

namespace SmartBase.Tests.Unit;

/// <summary>
/// Строка подвала блока поиска (<see cref="DocStatus"/>).
///
/// Подвал — единственное место, где раздел объясняет, почему поиск не нашел того, что человек
/// искал. Поэтому проверяется не «текст такой-то», а правила: что важнее чего, когда появляется
/// действие и когда подвал обязан молчать.
/// </summary>
public sealed class DocStatusTests
{
    private static DocStatusInput In(
        DocCensus census = default,
        bool      reading = false,
        int       readDone = 0,
        int       readTotal = 0,
        TimeSpan? left = null,
        string    query = "",
        int       inText = 0,
        int       byTitle = 0,
        bool      capped = false,
        bool      cacheOpen = true,
        bool      typesNarrowed = false) =>
        new(census, reading, readDone, readTotal, left, query, inText, byTitle, capped,
            cacheOpen, typesNarrowed);

    private static DocCensus Census(int total = 0, int read = 0, int notRead = 0,
                                    int noText = 0, int unsupported = 0, int failed = 0) =>
        new(total, read, notRead, noText, unsupported, failed);

    // ── Покой ────────────────────────────────────────────────────────────────

    [Fact]
    public void Nothing_read_offers_to_read_and_warns_that_search_goes_by_names_only()
    {
        var line = DocStatus.Compose(In(Census(total: 10, notRead: 10)));

        Assert.Contains("не прочитаны", line.Text);
        Assert.Contains("по названиям", line.Text);
        Assert.Equal(DocStatusAction.Read, line.Action);
    }

    [Fact]
    public void Partly_read_shows_live_numbers_and_offers_to_finish()
    {
        var line = DocStatus.Compose(In(Census(total: 100, read: 90, notRead: 10)));

        Assert.Contains("100 документов", line.Text);
        Assert.Contains("прочитан у 90", line.Text);
        Assert.Equal(DocStatusAction.Read, line.Action);
        Assert.Equal(DocStatus.ActionReadRest, line.ActionText);
    }

    // Все прочитано — но кнопка остается «Обновить»: документ правят в родной программе, кладут
    // поверх новую версию, подменяют скан распознанным, и кеш узнает об этом только когда его
    // попросят. Пока кнопка пропадала вместе с последним непрочитанным документом, перечитать
    // измененный файл было нечем — оставалось выключить и включить коллекцию.
    [Fact]
    public void All_read_still_offers_a_refresh()
    {
        var line = DocStatus.Compose(In(Census(total: 50, read: 50)));

        Assert.Contains(DocStatus.IdleAllRead, line.Text);
        Assert.Equal(DocStatusAction.Read, line.Action);
        Assert.Equal(DocStatus.ActionRefresh, line.ActionText);
    }

    /// <summary>
    /// Имя кнопки говорит, что именно она сделает. Одно слово на все три случая было бы либо
    /// ложью при первом заходе («Обновить» перед многочасовым разбором), либо шумом в покое
    /// («Прочитать документы», когда читать нечего).
    /// </summary>
    [Fact]
    public void The_button_is_named_by_what_it_will_do()
    {
        Assert.Equal(DocStatus.ActionRead,
                     DocStatus.Compose(In(Census(total: 50, notRead: 50))).ActionText);
        Assert.Equal(DocStatus.ActionReadRest,
                     DocStatus.Compose(In(Census(total: 50, read: 20, notRead: 30))).ActionText);
        Assert.Equal(DocStatus.ActionRefresh,
                     DocStatus.Compose(In(Census(total: 50, read: 50))).ActionText);
    }

    /// <summary>
    /// Пустой раздел кнопки не получает: обновлять там нечего, а строка подвала на пустом месте
    /// съедала бы высоту над лентой каждый день.
    /// </summary>
    [Fact]
    public void An_empty_section_offers_nothing()
    {
        var line = DocStatus.Compose(In(Census(total: 0)));

        Assert.Equal("", line.Text);
        Assert.Equal(DocStatusAction.None, line.Action);
    }

    // Сканы и чужие форматы прочитать нельзя — предлагать ЧТЕНИЕ из-за них неправда. Кнопка
    // тут все равно стоит, но зовется «Обновить»: перечитывать нечего, а проверить, не менялись
    // ли файлы, — есть зачем.
    [Fact]
    public void Scans_and_foreign_formats_are_not_a_reason_to_offer_reading()
    {
        var line = DocStatus.Compose(In(Census(total: 50, read: 20, noText: 25, unsupported: 5)));

        Assert.Equal(DocStatus.ActionRefresh, line.ActionText);
        Assert.NotEqual(DocStatus.ActionReadRest, line.ActionText);
        Assert.NotEqual(DocStatus.ActionRead,     line.ActionText);
    }

    [Fact]
    public void An_empty_section_says_nothing_at_all()
    {
        var line = DocStatus.Compose(In(Census()));

        Assert.Equal(DocStatusLine.Empty, line);
    }

    // ── Чтение ───────────────────────────────────────────────────────────────

    // Идущее чтение важнее всего: человек только что его запустил и смотрит именно на него.
    [Fact]
    public void Reading_wins_over_search_results()
    {
        var line = DocStatus.Compose(In(
            census: Census(total: 10, read: 10),
            reading: true, readDone: 4, readTotal: 10,
            query: "бетон", inText: 3, byTitle: 2));

        Assert.Contains("Читаю документы: 4 из 10", line.Text);
        Assert.Equal(DocStatusAction.Stop, line.Action);
    }

    [Fact]
    public void Reading_without_a_measurement_does_not_promise_a_time()
    {
        var line = DocStatus.Compose(In(reading: true, readDone: 2, readTotal: 100, left: null));

        Assert.DoesNotContain("осталось", line.Text);
    }

    [Fact]
    public void Reading_with_a_measurement_says_how_long_is_left()
    {
        var line = DocStatus.Compose(In(reading: true, readDone: 10, readTotal: 100,
                                        left: TimeSpan.FromMinutes(12)));

        Assert.Contains("осталось около 12 минут", line.Text);
    }

    // Точность тут вредна: секунды на тысячах документов все равно врут, а дергающееся число
    // раздражает сильнее, чем помогает. Заодно закреплен родительный падеж после «около» —
    // именительный дал бы «около 1 час».
    [Theory]
    [InlineData(20,     "меньше минуты")]
    [InlineData(90,     "около 2 минут")]
    [InlineData(660,    "около 11 минут")]
    [InlineData(1260,   "около 21 минуты")]
    [InlineData(3600,   "около 1 часа")]
    [InlineData(10800,  "около 3 часов")]
    public void Time_left_is_rounded_to_something_readable(int seconds, string expected) =>
        Assert.Equal(expected, DocStatus.Humanize(TimeSpan.FromSeconds(seconds)));

    // ── Поиск ────────────────────────────────────────────────────────────────

    // Разбивка «внутри текста против по названию» — главное число раздела: по ней видно,
    // залез поиск в документы или перебрал имена файлов.
    [Fact]
    public void Found_splits_hits_by_source()
    {
        var line = DocStatus.Compose(In(census: Census(total: 40, read: 40),
                                        query: "контроль", inText: 22, byTitle: 15));

        Assert.Contains("Найдено 37", line.Text);
        Assert.Contains("22 внутри текста", line.Text);
        Assert.Contains("15 по названию", line.Text);
    }

    // Ради этого случая подвал и заведен: без него человек решит, что поиск внутри документов
    // не работает, — и будет прав ровно наполовину.
    [Fact]
    public void Hits_only_by_name_while_nothing_is_read_says_so_and_offers_to_read()
    {
        var line = DocStatus.Compose(In(census: Census(total: 40, notRead: 40),
                                        query: "контроль", inText: 0, byTitle: 15));

        Assert.Contains("по названию", line.Text);
        Assert.Contains("не прочитаны", line.Text);
        Assert.Equal(DocStatusAction.Read, line.Action);
    }

    // Все прочитано, а внутри все равно пусто — это законный исход, и звать читать заново незачем.
    [Fact]
    public void Hits_only_by_name_with_everything_read_is_a_normal_outcome()
    {
        var line = DocStatus.Compose(In(census: Census(total: 40, read: 40),
                                        query: "контроль", inText: 0, byTitle: 15));

        Assert.Equal(DocStatusAction.None, line.Action);
    }

    [Fact]
    public void A_capped_result_says_the_list_is_cut_not_finished()
    {
        var line = DocStatus.Compose(In(census: Census(total: 9000, read: 9000),
                                        query: "бетон", inText: 400, byTitle: 100, capped: true));

        Assert.Contains("500+", line.Text);
        Assert.Contains("показаны первые 500", line.Text);
    }

    // ── Пустая выдача ────────────────────────────────────────────────────────

    // Причины перечисляются по одной и по срочности: сперва то, что человек исправит одним
    // действием, и только потом то, с чем ничего не сделать.
    [Fact]
    public void Nothing_found_blames_unread_documents_first()
    {
        var line = DocStatus.Compose(In(
            census: Census(total: 100, read: 40, notRead: 30, noText: 30),
            query: "шов", typesNarrowed: true));

        Assert.Contains("не прочитаны", line.Text);
        Assert.Equal(DocStatusAction.Read, line.Action);
    }

    [Fact]
    public void Nothing_found_blames_the_type_filter_when_everything_is_read()
    {
        var line = DocStatus.Compose(In(
            census: Census(total: 100, read: 70, noText: 30),
            query: "шов", typesNarrowed: true));

        Assert.Contains("Фильтр по типу", line.Text);
    }

    [Fact]
    public void Nothing_found_blames_scans_when_there_is_nothing_else_to_blame()
    {
        var line = DocStatus.Compose(In(
            census: Census(total: 100, read: 70, noText: 30),
            query: "шов"));

        Assert.Contains("сканы без текстового слоя", line.Text);
    }

    [Fact]
    public void Nothing_found_with_no_reason_at_all_just_says_so()
    {
        var line = DocStatus.Compose(In(census: Census(total: 100, read: 100), query: "шов"));

        Assert.Equal("Ничего не найдено", line.Text);
        Assert.Equal(DocStatusAction.None, line.Action);
    }

    // ── Кеш не открылся ──────────────────────────────────────────────────────

    // Без кеша поиска внутри документов нет вовсе, и чтение не поможет — звать к нему было бы
    // издевательством.
    [Fact]
    public void A_broken_cache_is_reported_and_offers_nothing()
    {
        var line = DocStatus.Compose(In(census: Census(total: 10, notRead: 10), cacheOpen: false));

        Assert.Contains("кеш документов не открылся", line.Text);
        Assert.Equal(DocStatusAction.None, line.Action);
    }

    // Но идущее чтение важнее даже этого: если очередь работает, показываем ее.
    [Fact]
    public void Reading_is_shown_even_when_the_cache_flag_is_off()
    {
        var line = DocStatus.Compose(In(reading: true, readDone: 1, readTotal: 5, cacheOpen: false));

        Assert.Contains("Читаю документы", line.Text);
    }
}
