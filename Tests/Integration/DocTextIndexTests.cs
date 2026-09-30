using System.IO;
using System.Text;
using SmartBase.Core.Documents;
using SmartBase.Core.Models;
using SmartBase.Database.DocIndex;

namespace SmartBase.Tests.Integration;

/// <summary>
/// Кеш текста документов (<c>Database/DocIndex/DocTextIndex.cs</c>) — отдельный файл
/// <c>docs_index.db</c> с таблицей FTS5.
///
/// Первое, что здесь проверяется, — что FTS5 вообще есть в сборке SQLite: без него не работает
/// весь поиск по содержимому, а узнать об этом лучше из теста, чем из пустой выдачи у
/// пользователя. Дальше — инвалидация по времени правки и размеру (без нее очередь либо
/// перемалывала бы одно и то же, либо не замечала правок) и сниппет с маркерами.
/// </summary>
public sealed class DocTextIndexTests : IDisposable
{
    private readonly string _dir;
    private readonly DocTextIndex _index;

    public DocTextIndexTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "sb-docidx-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_dir);
        _index = new DocTextIndex(Path.Combine(_dir, "docs_index.db"));
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* занят — уберет система */ }
    }

    // Текстовый файл-образец: разбирается самым простым путем, без внешних форматов.
    private string Write(string name, string text)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllText(path, text, new UTF8Encoding(false));
        return path;
    }

    private void Put(string path, string text) =>
        _index.Put(path, new DocText(DocTextStatus.Ok, DocTextExtractor.Normalize(text)));

    // ── Схема ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Кеш создается без ошибок — значит FTS5 в сборке есть (иначе <c>CREATE VIRTUAL TABLE</c>
    /// упал бы прямо в конструкторе). Это первый шаг плана раздела: без FTS5 понадобился бы
    /// запасной путь на <c>LIKE</c>.
    /// </summary>
    [Fact]
    public void The_cache_opens_which_means_fts5_is_available()
    {
        Assert.True(File.Exists(_index.Path));
        Assert.Empty(_index.Search("что угодно"));
    }

    // ── Поиск по содержимому ─────────────────────────────────────────────────

    [Fact]
    public void A_stored_document_is_found_by_a_word_inside_it()
    {
        var path = Write("gost.txt", "Стандарт устанавливает требования к обеспечению безопасности");
        Put(path, File.ReadAllText(path));

        var hit = Assert.Single(_index.Search("требования"));
        Assert.Equal(DocTextIndex.KeyOf(path), hit.Path);
    }

    /// <summary>Последнее слово запроса ищется по префиксу: «безопасн» находит «безопасности».</summary>
    [Fact]
    public void The_last_word_matches_by_prefix()
    {
        var path = Write("gost.txt", "требования к обеспечению безопасности");
        Put(path, File.ReadAllText(path));

        Assert.Single(_index.Search("безопасн"));
    }

    [Fact]
    public void Search_ignores_case_for_cyrillic()
    {
        var path = Write("gost.txt", "Автоматизированные системы управления");
        Put(path, File.ReadAllText(path));

        Assert.Single(_index.Search("автоматизированные"));
        Assert.Single(_index.Search("АВТОМАТИЗИРОВАННЫЕ"));
    }

    [Fact]
    public void All_words_of_the_query_must_be_present()
    {
        var path = Write("gost.txt", "системы управления качеством");
        Put(path, File.ReadAllText(path));

        Assert.Single(_index.Search("системы управления"));
        Assert.Empty(_index.Search("системы отопления"));
    }

    /// <summary>Сниппет обрамляет совпадение маркерами — по ним карточка рисует подсветку.</summary>
    [Fact]
    public void The_snippet_marks_the_matched_word()
    {
        var path = Write("gost.txt",
            "Пункт 3.1.2. Стандарт устанавливает требования к обеспечению безопасности " +
            "автоматизированных систем управления в области защиты информации");
        Put(path, File.ReadAllText(path));

        var hit   = Assert.Single(_index.Search("автоматизированных"));
        var parts = DocTextIndex.SplitSnippet(hit.Snippet);

        Assert.Contains(parts, p => p.Hit && p.Text.Contains("автоматизированных"));
        Assert.Contains(parts, p => !p.Hit);   // вокруг совпадения есть обычный текст
    }

    [Fact]
    public void Search_respects_the_limit()
    {
        for (var i = 0; i < 5; i++)
        {
            var path = Write($"doc{i}.txt", "общее слово документа");
            Put(path, File.ReadAllText(path));
        }

        Assert.Equal(2, _index.Search("документа", limit: 2).Count);
        Assert.Equal(5, _index.Search("документа", limit: 50).Count);
    }

    /// <summary>Битое выражение поиска — пустая выдача, а не падение раздела.</summary>
    [Fact]
    public void A_broken_query_returns_nothing_instead_of_throwing()
    {
        var path = Write("gost.txt", "текст");
        Put(path, File.ReadAllText(path));

        Assert.Empty(_index.Search("\"\"\"\""));
        Assert.Empty(_index.Search("   "));
    }

    // ── Инвалидация ──────────────────────────────────────────────────────────

    [Fact]
    public void An_unknown_file_is_pending()
    {
        var path = Write("gost.txt", "текст");

        Assert.Equal([path], _index.Pending([path]));
    }

    [Fact]
    public void An_indexed_file_is_not_pending_again()
    {
        var path = Write("gost.txt", "текст");
        Put(path, "текст");

        Assert.Empty(_index.Pending([path]));
    }

    /// <summary>
    /// Изменился размер — файл разбирается заново. Без этого правка документа не доезжала бы до
    /// поиска, и он находил бы по старому тексту.
    /// </summary>
    [Fact]
    public void A_changed_file_becomes_pending_again()
    {
        var path = Write("gost.txt", "первая редакция");
        Put(path, "первая редакция");
        Assert.Empty(_index.Pending([path]));

        File.WriteAllText(path, "вторая редакция, заметно длиннее первой", new UTF8Encoding(false));

        Assert.Equal([path], _index.Pending([path]));
    }

    /// <summary>Переиндексация ЗАМЕНЯЕТ прежний текст, а не добавляется к нему.</summary>
    [Fact]
    public void Reindexing_replaces_the_previous_text()
    {
        var path = Write("gost.txt", "первая редакция");
        Put(path, "первая редакция");
        Assert.Single(_index.Search("первая"));

        Put(path, "вторая редакция");

        Assert.Empty(_index.Search("первая"));
        Assert.Single(_index.Search("вторая"));
    }

    /// <summary>Файла нет на диске — разбирать нечего, в очередь он не попадает.</summary>
    [Fact]
    public void A_missing_file_is_never_pending()
    {
        Assert.Empty(_index.Pending([Path.Combine(_dir, "нет-такого.pdf")]));
    }

    /// <summary>
    /// Неподдержанный формат и скан без текста запоминаются как разобранные: иначе очередь
    /// перемалывала бы их при каждом заходе в раздел.
    /// </summary>
    [Fact]
    public void Unsupported_and_empty_documents_are_remembered_as_done()
    {
        var unsupported = Write("chertezh.dwg", "двоичное содержимое");
        var scan        = Write("scan.txt", "");

        _index.Put(unsupported, DocText.Of(DocTextStatus.Unsupported));
        _index.Put(scan,        DocText.Of(DocTextStatus.NoText));

        Assert.Empty(_index.Pending([unsupported, scan]));

        var known = _index.Known([DocTextIndex.KeyOf(unsupported), DocTextIndex.KeyOf(scan)]);
        Assert.Equal(DocTextStatus.Unsupported, known[DocTextIndex.KeyOf(unsupported)].Status);
        Assert.Equal(DocTextStatus.NoText,      known[DocTextIndex.KeyOf(scan)].Status);
    }

    // ── Уборка ───────────────────────────────────────────────────────────────

    [Fact]
    public void Forget_keeps_only_the_listed_paths()
    {
        var keep = Write("keep.txt", "остается в кеше");
        var drop = Write("drop.txt", "удаляется из кеша");
        Put(keep, "остается в кеше");
        Put(drop, "удаляется из кеша");

        _index.Forget([keep]);

        Assert.Single(_index.Search("кеше"));
        Assert.Empty(_index.Search("удаляется"));
    }

    [Fact]
    public void Clear_empties_the_cache_but_keeps_it_usable()
    {
        var path = Write("gost.txt", "текст документа");
        Put(path, "текст документа");

        _index.Clear();

        Assert.Empty(_index.Search("документа"));
        Assert.Equal(0, _index.Stats().Indexed);
        // Кеш остался рабочим: следующий разбор ложится в него без пересоздания файла.
        Put(path, "текст документа");
        Assert.Single(_index.Search("документа"));
    }

    [Fact]
    public void Stats_count_documents_by_status()
    {
        Put(Write("ok.txt", "есть текст"), "есть текст");
        _index.Put(Write("scan.txt", ""), DocText.Of(DocTextStatus.NoText));
        _index.Put(Write("broken.txt", "x"), DocText.Of(DocTextStatus.Failed));

        var (indexed, noText, failed, bytes) = _index.Stats();

        Assert.Equal(1, indexed);
        Assert.Equal(1, noText);
        Assert.Equal(1, failed);
        Assert.True(bytes > 0);
    }

    // ── Перепись состояний ───────────────────────────────────────────────────
    //
    // Survey — один проход вместо двух. Раздельные «что разобрать» и «сколько чего» означали бы
    // ДВА полных обхода кеша на каждый заход в раздел: на двадцати тысячах документов это
    // полсотни запросов пачками, сделанных дважды подряд ради одних и тех же строк.

    [Fact]
    public void Survey_counts_every_state_and_lists_what_to_read()
    {
        var ok      = Write("ok.txt",      "текст прочитан");
        var scan     = Write("scan.txt",    "скан");
        var foreign  = Write("draw.txt",    "чертеж");
        var broken   = Write("broken.txt",  "битый");
        var fresh    = Write("fresh.txt",   "еще не трогали");

        Put(ok, "текст прочитан");
        _index.Put(scan,    DocText.Of(DocTextStatus.NoText));
        _index.Put(foreign, DocText.Of(DocTextStatus.Unsupported));
        _index.Put(broken,  DocText.Of(DocTextStatus.Failed));

        var (pending, census) = _index.Survey([ok, scan, foreign, broken, fresh]);

        Assert.Equal([fresh], pending);
        Assert.Equal(5, census.Total);
        Assert.Equal(1, census.Read);
        Assert.Equal(1, census.NotRead);
        Assert.Equal(1, census.NoText);
        Assert.Equal(1, census.Unsupported);
        Assert.Equal(1, census.Failed);
        // Сканы, чужие форматы и битые файлы не найдутся по содержимому, сколько их ни читай.
        Assert.Equal(3, census.NeverSearchable);
    }

    // Файла нет на диске — он и не документ: считать его непрочитанным значило бы звать читать
    // то, чего нет.
    [Fact]
    public void A_missing_file_is_not_counted_at_all()
    {
        var gone = Path.Combine(_dir, "gone.txt");

        var (pending, census) = _index.Survey([gone]);

        Assert.Empty(pending);
        Assert.Equal(0, census.Total);
    }

    // Изменившийся файл считается НЕПРОЧИТАННЫМ, даже если прошлый разбор удался: в кеше лежит
    // текст прежней редакции, и поиск по нему нашел бы то, чего в документе уже нет.
    [Fact]
    public void An_edited_file_counts_as_unread_again()
    {
        var path = Write("doc.txt", "первая редакция");
        Put(path, "первая редакция");

        Assert.Equal(1, _index.Survey([path]).Census.Read);

        File.WriteAllText(path, "вторая редакция, заметно длиннее первой");
        var (pending, census) = _index.Survey([path]);

        Assert.Equal([path], pending);
        Assert.Equal(1, census.NotRead);
        Assert.Equal(0, census.Read);
    }

    [Fact]
    public void Pending_is_a_thin_wrapper_over_survey()
    {
        var read  = Write("read.txt",  "прочитан");
        var fresh = Write("fresh.txt", "не прочитан");
        Put(read, "прочитан");

        Assert.Equal(_index.Survey([read, fresh]).Pending, _index.Pending([read, fresh]));
    }

    // ── Состояния для карточек ───────────────────────────────────────────────

    // Перепись отвечает и за чипы карточек. Раньше контроллер после нее отдельно перечитывал
    // кеш, а каждая карточка еще раз спрашивала у диска время правки своего файла — в UI-потоке,
    // по вопросу на карточку. Теперь состояние каждого файла приходит из того же прохода, и
    // чипы не могут разойтись с числами подвала.

    [Fact]
    public void Survey_names_the_state_of_every_file_for_its_card()
    {
        var ok      = Write("ok.txt",     "текст прочитан");
        var scan    = Write("scan.txt",   "скан");
        var foreign = Write("draw.txt",   "чертеж");
        var broken  = Write("broken.txt", "битый");
        var fresh   = Write("fresh.txt",  "еще не трогали");

        _index.Put(ok,      new DocText(DocTextStatus.Ok, "текст прочитан", Pages: 3));
        _index.Put(scan,    new DocText(DocTextStatus.NoText, "", Pages: 12));
        _index.Put(foreign, DocText.Of(DocTextStatus.Unsupported));
        _index.Put(broken,  DocText.Of(DocTextStatus.Failed));

        var files = _index.Survey([ok, scan, foreign, broken, fresh]).Files;

        Assert.Equal(new DocFileState(DocTextState.Read, 3),        files[DocTextIndex.KeyOf(ok)]);
        Assert.Equal(new DocFileState(DocTextState.NoText, 12),     files[DocTextIndex.KeyOf(scan)]);
        Assert.Equal(new DocFileState(DocTextState.Unsupported, 0), files[DocTextIndex.KeyOf(foreign)]);
        Assert.Equal(new DocFileState(DocTextState.Failed, 0),      files[DocTextIndex.KeyOf(broken)]);
        Assert.Equal(new DocFileState(DocTextState.NotRead, 0),     files[DocTextIndex.KeyOf(fresh)]);
    }

    // Изменившийся файл на карточке — «не прочитан», и число страниц прежней редакции ему уже не
    // обещается.
    [Fact]
    public void An_edited_file_shows_as_unread_on_its_card()
    {
        var path = Write("doc.txt", "первая редакция");
        _index.Put(path, new DocText(DocTextStatus.Ok, "первая редакция", Pages: 4));

        File.WriteAllText(path, "вторая редакция, заметно длиннее первой");

        Assert.Equal(new DocFileState(DocTextState.NotRead, 0),
                     _index.Survey([path]).Files[DocTextIndex.KeyOf(path)]);
    }

    // Файла нет на диске: в числа подвала он не идет, а карточке остается то, что помнит кеш, —
    // сверять время правки не с чем. Красная плашка «файл не найден» у нее своя.
    [Fact]
    public void A_missing_file_keeps_what_the_cache_remembers()
    {
        var path = Write("gone.txt", "был");
        _index.Put(path, new DocText(DocTextStatus.Ok, "был", Pages: 2));
        File.Delete(path);

        var survey = _index.Survey([path]);

        Assert.Equal(0, survey.Census.Total);
        Assert.Equal(new DocFileState(DocTextState.Read, 2), survey.Files[DocTextIndex.KeyOf(path)]);
    }

    // Большая папка спрашивается листингом (DocStamps), а не по файлу, — а кеш записывал отметку
    // вопросом по файлу. Правку одного документа перепись обязана заметить, а нетронутые —
    // признать прочитанными: разойдись листинг с вопросом по файлу, «Обновить» либо перестала
    // бы видеть измененные документы, либо перечитывала бы все подряд.
    [Fact]
    public void A_folder_read_by_listing_agrees_with_what_the_cache_stored()
    {
        var paths = Enumerable.Range(0, DocStamps.ListingThreshold * 2)
                              .Select(i => Write($"doc{i:D2}.txt", $"документ {i}"))
                              .ToList();
        foreach (var p in paths) Put(p, "документ");

        File.WriteAllText(paths[5], "эту редакцию поправили, и она заметно длиннее прежней");
        var (pending, census) = _index.Survey(paths);

        Assert.Equal([paths[5]], pending);
        Assert.Equal(paths.Count - 1, census.Read);
    }

    // ── Страницы ─────────────────────────────────────────────────────────────

    // Число страниц считается при разборе (документ в этот момент уже открыт) и живет в кеше.
    // Поэтому у прочитанного оно есть, а у непрочитанного не бывает — карточка обещает ровно
    // то, что знает.
    [Fact]
    public void Page_count_survives_a_round_trip()
    {
        var path = Write("book.txt", "том");
        _index.Put(path, new DocText(DocTextStatus.Ok, "том", Pages: 112));

        var entry = _index.Known([DocTextIndex.KeyOf(path)])[DocTextIndex.KeyOf(path)];

        Assert.Equal(112, entry.Pages);
    }

    // У скана текста нет, а страницы есть — и «12 страниц» в карточке это правда, которая
    // помогает узнать документ.
    [Fact]
    public void A_scan_without_text_still_keeps_its_page_count()
    {
        var path = Write("scan.txt", "скан");
        _index.Put(path, new DocText(DocTextStatus.NoText, "", Pages: 12));

        var entry = _index.Known([DocTextIndex.KeyOf(path)])[DocTextIndex.KeyOf(path)];

        Assert.Equal(DocTextStatus.NoText, entry.Status);
        Assert.Equal(12, entry.Pages);
    }

    [Fact]
    public void Formats_without_pages_keep_zero()
    {
        var path = Write("note.txt", "заметка");
        Put(path, "заметка");

        var entry = _index.Known([DocTextIndex.KeyOf(path)])[DocTextIndex.KeyOf(path)];

        Assert.Equal(0, entry.Pages);
    }

    // ── Поврежденный файл кеша ───────────────────────────────────────────────
    //
    // Кеш — пересобираемый файл, и поломка в нем не должна стоить человеку поиска по
    // содержимому до переустановки программы. Оба входа в кеш (открытие при старте и «Очистить»
    // на экране настроек) обязаны привести к одному и тому же: рабочему пустому кешу.

    // Файл, который SQLite базой не считает: заголовок чужой. Так выглядит обрыв питания на
    // записи или недокачанная копия папки данных.
    private string Garbage(string name)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllBytes(path, [.. Enumerable.Repeat((byte)0x7F, 4096)]);
        return path;
    }

    [Fact]
    public void A_broken_cache_file_is_recreated_on_open()
    {
        var path = Garbage("broken.db");

        // Раньше это исключение уходило наружу: кеш не открывался, поиск по содержимому
        // пропадал, а DocIndexService.Current оставался пустым до перезапуска программы —
        // и починить было нечем, «Очистить» в этом состоянии тоже не открывала файл.
        var index = new DocTextIndex(path);

        var doc = Write("gost.txt", "Стандарт на пересозданном кеше");
        index.Put(doc, new DocText(DocTextStatus.Ok, "Стандарт на пересозданном кеше"));
        Assert.Single(index.Search("пересозданном"));
    }

    /// <summary>
    /// «Очистить» на поврежденном файле делает то, о чем просил человек: пустой рабочий кеш.
    /// Запросом такой файл не очистить — и <c>DELETE</c>, и <c>VACUUM</c> спотыкаются о ту же
    /// поломку, — поэтому файл пересоздается.
    /// </summary>
    [Fact]
    public void Clear_recreates_a_file_that_broke_after_opening()
    {
        var path  = Path.Combine(_dir, "later-broken.db");
        var index = new DocTextIndex(path);
        var doc   = Write("note.txt", "заметка до поломки");
        index.Put(doc, new DocText(DocTextStatus.Ok, "заметка до поломки"));

        // Портим файл под работающим кешем: пул держит дескриптор, но не запрещает записывать
        // в файл со стороны — именно так и выглядит поломка в жизни.
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        File.WriteAllBytes(path, [.. Enumerable.Repeat((byte)0x7F, 4096)]);
        foreach (var suffix in new[] { "-wal", "-shm" })
            try { File.Delete(path + suffix); } catch { /* нет файла — тем лучше */ }

        index.Clear();

        Assert.Equal(0, index.Stats().Indexed);
        index.Put(doc, new DocText(DocTextStatus.Ok, "заметка после починки"));
        Assert.Single(index.Search("починки"));
    }
}
