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
        bool      typesNarrowed = false,
        bool      loading = false,
        bool      textFailed = false) =>
        new(census, reading, readDone, readTotal, left, query, inText, byTitle, capped,
            cacheOpen, typesNarrowed, loading, textFailed);

    private static DocCensus Census(int total = 0, int read = 0, int notRead = 0,
                                    int noText = 0, int unsupported = 0, int failed = 0) =>
        new(total, read, notRead, noText, unsupported, failed);

    // ── Сборка ленты ─────────────────────────────────────────────────────────

    /// <summary>
    /// Долгая сборка ленты говорит о себе СТРОКОЙ, а не оверлеем на весь блок. Оверлей закрывал
    /// ленту целиком, и на большой библиотеке в порог не укладывалась ни одна загрузка: раздел
    /// моргал пустым экраном на каждое касание галочки в сайдбаре.
    /// </summary>
    [Fact]
    public void A_slow_load_says_so_in_one_line()
    {
        var line = DocStatus.Compose(In(Census(total: 50, read: 50), loading: true));

        Assert.Equal(DocStatus.LoadingText, line.Text);
        Assert.Equal(DocStatusAction.None, line.Action);
    }

    /// <summary>
    /// Итоги ПРОШЛОГО запроса во время сборки не показываются: человек прочел бы их как ответ
    /// на новый, а лента под ними уже меняется.
    /// </summary>
    [Fact]
    public void While_loading_the_previous_search_result_is_not_shown()
    {
        var line = DocStatus.Compose(In(Census(total: 50, read: 50),
                                        query: "договор", inText: 7, byTitle: 2, loading: true));

        Assert.Equal(DocStatus.LoadingText, line.Text);
        Assert.DoesNotContain("Найдено", line.Text);
    }

    /// <summary>
    /// Идущее чтение важнее сборки: у него есть и остаток времени, и кнопка «Остановить», а у
    /// сборки сказать нечего, кроме «подождите».
    /// </summary>
    [Fact]
    public void Reading_outranks_loading()
    {
        var line = DocStatus.Compose(In(Census(total: 50, read: 10),
                                        reading: true, readDone: 3, readTotal: 40, loading: true));

        Assert.Contains("Читаю документы", line.Text);
        Assert.Equal(DocStatusAction.Stop, line.Action);
    }

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

    // ── Поиск внутри текста сорвался ─────────────────────────────────────────

    // Сломанный кеш раньше выглядел как «ничего не найдено»: подвал говорил о полном ответе, а
    // ответ был половиной. Теперь он говорит, что нашлось только по названию и почему.
    [Fact]
    public void A_failed_text_search_says_the_hits_are_by_name_only()
    {
        var line = DocStatus.Compose(In(census: Census(total: 40, read: 40),
                                        query: "контроль", byTitle: 5, textFailed: true));

        Assert.Equal($"Найдено 5 по названию. {DocStatus.TextSearchFailed}", line.Text);
        Assert.Equal(DocStatusAction.None, line.Action);
    }

    // Пустая выдача при сорвавшемся поиске — не «ничего не найдено»: внутрь документов поиск не
    // заглядывал. И причина тут одна — сбой, а не непрочитанные документы: читать их сейчас
    // некуда, кеш не отвечает.
    [Fact]
    public void A_failed_text_search_with_nothing_by_name_is_not_called_nothing_found()
    {
        var line = DocStatus.Compose(In(census: Census(total: 40, read: 10, notRead: 30),
                                        query: "контроль", textFailed: true));

        Assert.Equal($"Ничего не найдено по названию. {DocStatus.TextSearchFailed}", line.Text);
        Assert.Equal(DocStatusAction.None, line.Action);
    }

    // Сбой важнее упора в потолок: обрезанный список лечится уточнением запроса, сбой — нет.
    [Fact]
    public void A_failed_text_search_outranks_the_cap()
    {
        var line = DocStatus.Compose(In(census: Census(total: 9000, read: 9000),
                                        query: "бетон", byTitle: 500, capped: true, textFailed: true));

        Assert.StartsWith("Найдено 500+ по названию.", line.Text);
        Assert.EndsWith(DocStatus.TextSearchFailed, line.Text);
    }

    // Без запроса признак сбоя ни при чем: подвал покоя говорит о документах, а не о поиске.
    [Fact]
    public void A_failed_text_search_is_forgotten_without_a_query()
    {
        var line = DocStatus.Compose(In(census: Census(total: 40, read: 40), textFailed: true));

        Assert.DoesNotContain("не сработал", line.Text);
    }

    // ── Сорвавшееся чтение ───────────────────────────────────────────────────

    private static DocIndexRunEnd Failed(Exception error) => new(120, 2000, error);

    // Причину называем там, где с ней есть что делать: место освобождают, а прочитанное до сбоя
    // цело — SQLite откатывает только несостоявшуюся запись.
    [Fact]
    public void A_full_disk_is_named_and_the_read_part_is_said_to_be_kept()
    {
        var end = Failed(new Microsoft.Data.Sqlite.SqliteException("database or disk is full", 13));

        Assert.Equal("Сорвалось на 120 из 2000: на диске нет места", DocStatus.ReadFailedDetail(end));
        Assert.StartsWith("Чтение документов сорвалось: на диске нет места.", DocStatus.ReadFailedNotice(end));
        Assert.Contains("сохранено", DocStatus.ReadFailedNotice(end));
    }

    // Испорченный файл кеша чинится кнопкой «Очистить» — карточка говорит именно это.
    [Theory]
    [InlineData(11)]   // CORRUPT
    [InlineData(26)]   // NOTADB
    public void A_broken_cache_file_points_to_the_clear_button(int code)
    {
        var end = Failed(new Microsoft.Data.Sqlite.SqliteException("database disk image is malformed", code));

        Assert.EndsWith("файл кеша поврежден", DocStatus.ReadFailedDetail(end));
        Assert.Contains("«Очистить»", DocStatus.ReadFailedNotice(end));
    }

    // Остальное — без догадок: что не принял кеш или что сломалось в программе, и где подробности.
    [Fact]
    public void Other_failures_send_to_the_diagnostics()
    {
        var cache = Failed(new Microsoft.Data.Sqlite.SqliteException("cannot commit", 1));
        var ours  = Failed(new InvalidOperationException("дефект"));

        Assert.EndsWith("кеш документов не принимает запись", DocStatus.ReadFailedDetail(cache));
        Assert.EndsWith("сбой в программе", DocStatus.ReadFailedDetail(ours));
        Assert.Contains("«Диагностика»", DocStatus.ReadFailedNotice(cache));
        Assert.Contains("«Диагностика»", DocStatus.ReadFailedNotice(ours));
    }

    // ── Согласование с числом ────────────────────────────────────────────────
    //
    // Сказуемое — по форме числа («21 документ не прочитан»), местоимение и «скан» — по смыслу
    // («внутри них», пока документов не ровно один). Пока подвал склонял одно существительное,
    // выходило «1 документ не прочитаны — внутри них». Числа — на все ловушки: один, два, пять,
    // одиннадцать (форма «много» вопреки последней цифре) и двадцать один (форма «один», но
    // документов много).

    [Theory]
    [InlineData(1,  "1 документ не прочитан — искать можно только по названиям")]
    [InlineData(2,  "2 документа не прочитаны — искать можно только по названиям")]
    [InlineData(5,  "5 документов не прочитаны — искать можно только по названиям")]
    [InlineData(11, "11 документов не прочитаны — искать можно только по названиям")]
    [InlineData(21, "21 документ не прочитан — искать можно только по названиям")]
    public void Nothing_read_agrees_with_the_number(int n, string expected) =>
        Assert.Equal(expected, DocStatus.Compose(In(Census(total: n, notRead: n))).Text);

    [Theory]
    [InlineData(1,  "Ничего не найдено. 1 документ не прочитан — внутри него поиск не искал")]
    [InlineData(2,  "Ничего не найдено. 2 документа не прочитаны — внутри них поиск не искал")]
    [InlineData(5,  "Ничего не найдено. 5 документов не прочитаны — внутри них поиск не искал")]
    [InlineData(11, "Ничего не найдено. 11 документов не прочитаны — внутри них поиск не искал")]
    [InlineData(21, "Ничего не найдено. 21 документ не прочитан — внутри них поиск не искал")]
    public void Nothing_found_among_unread_agrees_with_the_number(int n, string expected) =>
        Assert.Equal(expected, DocStatus.Compose(In(Census(total: n + 10, read: 10, notRead: n), query: "шов")).Text);

    [Theory]
    [InlineData(1,  "Ничего не найдено. 1 документ — скан без текстового слоя, внутри него поиск не ищет")]
    [InlineData(2,  "Ничего не найдено. 2 документа — сканы без текстового слоя, внутри них поиск не ищет")]
    [InlineData(11, "Ничего не найдено. 11 документов — сканы без текстового слоя, внутри них поиск не ищет")]
    [InlineData(21, "Ничего не найдено. 21 документ — сканы без текстового слоя, внутри них поиск не ищет")]
    public void Nothing_found_among_scans_agrees_with_the_number(int n, string expected) =>
        Assert.Equal(expected, DocStatus.Compose(In(Census(total: n + 10, read: 10, noText: n), query: "шов")).Text);

    [Theory]
    [InlineData(1,  "Ничего не найдено. У 1 документа формат без чтения текста — он ищется только по названию")]
    [InlineData(2,  "Ничего не найдено. У 2 документов формат без чтения текста — они ищутся только по названию")]
    [InlineData(11, "Ничего не найдено. У 11 документов формат без чтения текста — они ищутся только по названию")]
    [InlineData(21, "Ничего не найдено. У 21 документа формат без чтения текста — они ищутся только по названию")]
    public void Nothing_found_among_foreign_formats_agrees_with_the_number(int n, string expected) =>
        Assert.Equal(expected, DocStatus.Compose(In(Census(total: n + 10, read: 10, unsupported: n), query: "шов")).Text);

    /// <summary>
    /// Нашлось только по названию, а прочитано почти все: внутри прочитанного поиск искал, и
    /// «внутри текста не искал» было бы неправдой. Подвал говорит о непрочитанных — теми же
    /// словами, что и при пустой выдаче.
    /// </summary>
    [Theory]
    [InlineData(0,  1,  "Найдено 15 по названию. 1 документ не прочитан — внутри него поиск не искал")]
    [InlineData(90, 10, "Найдено 15 по названию. 10 документов не прочитаны — внутри них поиск не искал")]
    [InlineData(0,  21, "Найдено 15 по названию. 21 документ не прочитан — внутри них поиск не искал")]
    public void Hits_by_name_speak_of_the_unread_only(int read, int notRead, string expected)
    {
        var line = DocStatus.Compose(In(census: Census(total: read + notRead, read: read, notRead: notRead),
                                        query: "контроль", inText: 0, byTitle: 15));

        Assert.Equal(expected, line.Text);
        Assert.Equal(DocStatusAction.Read, line.Action);
    }

    [Theory]
    [InlineData(false, 1,  0, "Читаю 1 документ. Можно работать дальше — ход виден в панели разделов слева.")]
    [InlineData(false, 21, 0, "Читаю 21 документ. Можно работать дальше — ход виден в панели разделов слева.")]
    [InlineData(true,  5,  1, "Добавлено к чтению: 1 документ. Читаю его тем же прогоном.")]
    [InlineData(true,  30, 21, "Добавлено к чтению: 21 документ. Читаю их тем же прогоном.")]
    [InlineData(true,  1,  0, "Этот документ уже стоит в очереди чтения.")]
    [InlineData(true,  21, 0, "Эти документы уже стоят в очереди чтения.")]
    public void The_read_start_card_agrees_with_the_number(bool running, int pending, int added, string expected) =>
        Assert.Equal(expected, DocStatus.ReadStartedNotice(running, pending, added));

    [Theory]
    [InlineData(1,  0,  "Прочитано документов: 1. Поиск теперь ищет внутри него.")]
    [InlineData(21, 0,  "Прочитано документов: 21. Поиск теперь ищет внутри них.")]
    [InlineData(40, 1,  "Прочитано документов: 40. Внутри 1 документа поиск искать не будет: это скан без текстового слоя или формат без чтения текста.")]
    [InlineData(40, 5,  "Прочитано документов: 40. Внутри 5 документов поиск искать не будет: это сканы без текстового слоя и форматы без чтения текста.")]
    [InlineData(40, 21, "Прочитано документов: 40. Внутри 21 документа поиск искать не будет: это сканы без текстового слоя и форматы без чтения текста.")]
    public void The_read_done_card_agrees_with_the_number(int done, int never, string expected) =>
        Assert.Equal(expected, DocStatus.ReadDoneNotice(done, never));
}
