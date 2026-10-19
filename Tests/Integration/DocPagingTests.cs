using SmartBase.Core.Models;
using SmartBase.Core.Documents;
using SmartBase.Database.Repositories;

namespace SmartBase.Tests.Integration;

/// <summary>
/// Постраничное чтение ленты «Библиотеки документов»
/// (<c>ConstructorRepository.DocsPaging.cs</c>): порядок страниц, фильтр по типу документа,
/// счетчики и количества для выпадашки навбара.
///
/// Главное, что здесь закрепляется: страницы обязаны воспроизводить тот же порядок, который дает
/// сплошное чтение. Разъехавшись, два режима раздела (в памяти и страницами) показывали бы
/// РАЗНОЕ на одних и тех же данных, и заметить это можно было бы только на большой базе.
/// </summary>
public sealed class DocPagingTests
{
    private sealed record Shell(int ColId, int FileId, int TypeId);

    // Коллекция с включенным файловым столбцом и «Типом документа».
    private static Shell Library(ConstructorRepository repo, string name = "Документы")
    {
        var colId = SampleData.AddCollection(repo, name).Id!.Value;
        var file  = SampleData.AddField(repo, colId, "Файл", "file");
        repo.SetFileFieldInLibrary(file.Id!.Value, true);
        return new Shell(colId, file.Id!.Value, repo.DocTypeFieldId(colId)!.Value);
    }

    // Документ: запись + путь + (необязательно) тип.
    private static int AddDoc(ConstructorRepository repo, Shell shell, string title,
                              string path, string type = "", int position = 1000)
    {
        var id = SampleData.AddRecord(repo, shell.ColId, title, position).Id!.Value;
        repo.SetFieldValue(id, shell.FileId, path);
        if (type.Length > 0) repo.SetFieldValue(id, shell.TypeId, type);
        return id;
    }

    private static ConstructorRepository.DocView Types(params string[] types) =>
        new(new HashSet<string>(types));

    [Fact]
    public async Task Cancelled_order_query_reports_cancellation_instead_of_an_empty_result()
    {
        using var db = new TempDatabase();
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var shell = Library(db.Constructor);
        Assert.ThrowsAny<OperationCanceledException>(() => db.Constructor.GetDocOrderKeys(
            shell.ColId, shell.TypeId, ConstructorRepository.DocView.All, DocSort.Modified, cts.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => db.Constructor.GetDocOrderKeysAsync(
            shell.ColId, shell.TypeId, ConstructorRepository.DocView.All, DocSort.Title, cts.Token));
    }

    [Fact]
    public void Modified_order_reads_real_file_stamps_after_the_database_query()
    {
        using var db = new TempDatabase();
        var repo = db.Constructor;
        var shell = Library(repo);
        var folder = Path.Combine(Path.GetTempPath(), "sb-sort-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var a = Path.Combine(folder, "a.txt");
            var b = Path.Combine(folder, "b.txt");
            File.WriteAllText(a, "старый");
            File.WriteAllText(b, "новый");
            File.SetLastWriteTimeUtc(a, DateTime.UtcNow.AddDays(-2));
            var old = AddDoc(repo, shell, "А", a);
            var fresh = AddDoc(repo, shell, "Я", b);
            var keys = repo.GetDocOrderKeys(shell.ColId, shell.TypeId, ConstructorRepository.DocView.All, DocSort.Modified);
            Assert.All(keys, k => Assert.True(k.Modified > 0));
            var order = DocOrderPlan.Merge([new(1, shell.ColId, keys)], DocSort.Modified);
            Assert.Equal(new[] { fresh, old }, order.Select(k => k.RecordId).ToArray());
        }
        finally { Directory.Delete(folder, true); }
    }

    [Fact]
    public void Global_order_fetches_exact_file_keys_and_applies_type_filter()
    {
        using var db = new TempDatabase();
        var repo = db.Constructor;
        var shell = Library(repo);
        var second = SampleData.AddField(repo, shell.ColId, "Скан", "file");
        repo.SetFileFieldInLibrary(second.Id!.Value, true);
        var a = AddDoc(repo, shell, "Я", "/a.pdf", "СП");
        var b = AddDoc(repo, shell, "А", "/b.pdf", "ГОСТ");
        repo.SetFieldValue(a, second.Id.Value, "/scan.pdf");
        var keys = repo.GetDocOrderKeys(shell.ColId, shell.TypeId, Types("СП"), DocSort.Title);
        Assert.Equal(2, keys.Count);
        var order = DocOrderPlan.Merge([new(1, shell.ColId, keys)], DocSort.Title);
        var docs = repo.GetDocsByOrderKeys(shell.ColId, shell.TypeId, order);
        Assert.Equal(new[] { "/a.pdf", "/scan.pdf" }, docs.Select(d => d.Path).Order().ToArray());
        Assert.DoesNotContain(docs, d => d.Id == b);
        Assert.All(docs, d => Assert.Equal(shell.ColId, d.CollectionId));
        Assert.Empty(repo.GetDocsByOrderKeys(shell.ColId, shell.TypeId, []));
        repo.SetFieldValue(a, shell.TypeId, "ГОСТ");
        Assert.Empty(repo.GetDocsByOrderKeys(shell.ColId, shell.TypeId, order, Types("СП")));
        Assert.Throws<ArgumentOutOfRangeException>(() => repo.GetDocsByOrderKeys(shell.ColId, shell.TypeId,
            Enumerable.Repeat(new DocOrderKey(0, a, shell.FileId), 201).ToArray()));
    }

    // ── Размеры ──────────────────────────────────────────────────────────────

    [Fact]
    public void Stats_report_the_document_count_and_the_type_column()
    {
        using var db = new TempDatabase();
        var repo = db.Constructor;

        var shell = Library(repo);
        AddDoc(repo, shell, "A", "/a.pdf", position: 1000);
        AddDoc(repo, shell, "B", "/b.pdf", position: 2000);

        var stats = repo.GetDocCollectionStats();
        var one   = Assert.Single(stats);

        Assert.Equal(shell.ColId, one.Key);
        Assert.Equal(2, one.Value.Total);
        Assert.Equal(shell.TypeId, one.Value.TypeFieldId);
    }

    /// <summary>Запись с двумя включенными файловыми столбцами дает ДВА документа.</summary>
    [Fact]
    public void Two_file_columns_double_the_stream()
    {
        using var db = new TempDatabase();
        var repo = db.Constructor;

        var shell = Library(repo);
        var scan  = SampleData.AddField(repo, shell.ColId, "Скан", "file", 3000);
        repo.SetFileFieldInLibrary(scan.Id!.Value, true);

        var id = AddDoc(repo, shell, "ГОСТ", "/gost.pdf");
        repo.SetFieldValue(id, scan.Id!.Value, "/gost-scan.pdf");

        Assert.Equal(2, repo.GetDocCollectionStats()[shell.ColId].Total);
        Assert.Equal(2, repo.CountDocs(shell.ColId, shell.TypeId, ConstructorRepository.DocView.All));
    }

    // ── Порядок и страницы ───────────────────────────────────────────────────

    /// <summary>
    /// Страницы воспроизводят порядок сплошного чтения ровно. Это тот самый инвариант, ради
    /// которого счет и страница собираются одними и теми же кусками SQL. Сплошное чтение — то,
    /// что делает раздел «в памяти»: одна страница во весь поток длиной в счет. Своего сплошного
    /// читателя у раздела больше нет — тест сверялся с ним, а лента его не звала, и разойтись они
    /// могли незаметно. Порядок к тому же известен заранее — по позициям записей.
    /// </summary>
    [Fact]
    public void Pages_reproduce_the_in_memory_order_exactly()
    {
        using var db = new TempDatabase();
        var repo = db.Constructor;
        var all  = ConstructorRepository.DocView.All;

        var shell = Library(repo);
        for (var i = 25; i >= 1; i--)
            AddDoc(repo, shell, $"D{i:D2}", $"/d{i:D2}.pdf", position: i * 10);

        var total = repo.CountDocs(shell.ColId, shell.TypeId, all);
        var whole = repo.GetDocPage(shell.ColId, shell.TypeId, all, 0, total).Select(d => d.Path).ToList();

        var paged = new List<string>();
        for (var skip = 0; skip < total; skip += 7)
            paged.AddRange(repo.GetDocPage(shell.ColId, shell.TypeId, all, skip, 7).Select(d => d.Path));

        Assert.Equal(Enumerable.Range(1, 25).Select(i => $"/d{i:D2}.pdf"), whole);
        Assert.Equal(whole, paged);
    }

    /// <summary>Документы одной записи идут подряд, в порядке файловых столбцов.</summary>
    [Fact]
    public void Documents_of_one_record_follow_each_other_in_column_order()
    {
        using var db = new TempDatabase();
        var repo = db.Constructor;

        var shell = Library(repo);
        var scan  = SampleData.AddField(repo, shell.ColId, "Скан", "file", 3000);
        repo.SetFileFieldInLibrary(scan.Id!.Value, true);

        var first = AddDoc(repo, shell, "Первый", "/1.pdf", position: 1000);
        repo.SetFieldValue(first, scan.Id!.Value, "/1-scan.pdf");
        AddDoc(repo, shell, "Второй", "/2.pdf", position: 2000);

        Assert.Equal(["/1.pdf", "/1-scan.pdf", "/2.pdf"],
                     repo.GetDocPage(shell.ColId, shell.TypeId,
                                     ConstructorRepository.DocView.All, 0, 10)
                         .Select(d => d.Path).ToArray());
    }

    [Fact]
    public void A_page_past_the_end_is_empty()
    {
        using var db = new TempDatabase();
        var repo = db.Constructor;

        var shell = Library(repo);
        AddDoc(repo, shell, "A", "/a.pdf");

        Assert.Empty(repo.GetDocPage(shell.ColId, shell.TypeId,
                                     ConstructorRepository.DocView.All, 10, 5));
    }

    // ── Страница от соседа: keyset-курсор ────────────────────────────────────
    //
    // Все четыре пути к странице обязаны давать ровно тот же порядок, что и чтение подряд.
    // Разъедься они, лента показывала бы дубли и дыры на стыках страниц — и только при
    // прокрутке, то есть там, где тест смещением ничего не заметит.

    private static ConstructorRepository.DocSeek After(DocRecord d, int skip = 0) =>
        new(ConstructorRepository.DocSeekOrigin.After, skip, d.Cursor);

    private static ConstructorRepository.DocSeek Before(DocRecord d, int skip = 0) =>
        new(ConstructorRepository.DocSeekOrigin.Before, skip, d.Cursor);

    private static ConstructorRepository.DocSeek FromEnd(int skip) =>
        new(ConstructorRepository.DocSeekOrigin.End, skip);

    // Поток с подвохами порядка: у каждой четвертой записи второй файловый столбец (документы
    // одной записи идут подряд), а позиции повторяются по три — порядок между такими записями
    // держит только id, и курсор обязан его учитывать.
    private static Shell KeysetStream(ConstructorRepository repo)
    {
        var shell = Library(repo);
        var scan  = SampleData.AddField(repo, shell.ColId, "Скан", "file", 3000);
        repo.SetFileFieldInLibrary(scan.Id!.Value, true);

        for (var i = 1; i <= 17; i++)
        {
            var id = AddDoc(repo, shell, $"D{i:D2}", $"/d{i:D2}.pdf",
                            type: i % 2 == 0 ? "ГОСТ" : "СТО", position: (i + 2) / 3 * 10);
            if (i % 4 == 0) repo.SetFieldValue(id, scan.Id!.Value, $"/d{i:D2}-scan.pdf");
        }
        return shell;
    }

    private static List<DocRecord> Whole(ConstructorRepository repo, Shell shell,
                                         ConstructorRepository.DocView view) =>
        repo.GetDocPage(shell.ColId, shell.TypeId, view, 0, 1000);

    private static string[] Paths(IEnumerable<DocRecord> docs) => [.. docs.Select(d => d.Path)];

    [Fact]
    public void Walking_forward_by_cursor_reproduces_the_order()
    {
        using var db = new TempDatabase();
        var repo  = db.Constructor;
        var shell = KeysetStream(repo);
        var all   = ConstructorRepository.DocView.All;

        var walked = new List<DocRecord>();
        var page   = repo.GetDocPage(shell.ColId, shell.TypeId, all, 0, 4);
        while (page.Count > 0)
        {
            walked.AddRange(page);
            page = repo.GetDocPage(shell.ColId, shell.TypeId, all, After(page[^1]), 4);
        }

        Assert.Equal(21, walked.Count);   // 17 записей + 4 скана
        Assert.Equal(Paths(Whole(repo, shell, all)), Paths(walked));
    }

    /// <summary>Прокрутка вверх после прыжка вглубь: страницы идут назад от известного документа.</summary>
    [Fact]
    public void Walking_backward_from_the_end_reproduces_the_order()
    {
        using var db = new TempDatabase();
        var repo  = db.Constructor;
        var shell = KeysetStream(repo);
        var all   = ConstructorRepository.DocView.All;

        var walked = new List<DocRecord>();
        var page   = repo.GetDocPage(shell.ColId, shell.TypeId, all, FromEnd(0), 4);
        while (page.Count > 0)
        {
            walked.InsertRange(0, page);
            page = repo.GetDocPage(shell.ColId, shell.TypeId, all, Before(page[0]), 4);
        }

        Assert.Equal(Paths(Whole(repo, shell, all)), Paths(walked));
    }

    /// <summary>
    /// Один и тот же кусок потока, взятый каждым из путей и с пропуском, совпадает с куском
    /// чтения подряд. Кусок берется и из середины, и у самых краев.
    /// </summary>
    [Fact]
    public void Every_way_to_a_chunk_lands_on_the_same_documents()
    {
        using var db = new TempDatabase();
        var repo  = db.Constructor;
        var shell = KeysetStream(repo);
        var all   = ConstructorRepository.DocView.All;
        var whole = Whole(repo, shell, all);

        foreach (var local in new[] { 3, 8, 12, 17 })
        {
            const int n = 3;
            var expected = Paths(whole.Skip(local).Take(n));
            List<DocRecord> Read(ConstructorRepository.DocSeek s) =>
                repo.GetDocPage(shell.ColId, shell.TypeId, all, s, n);

            Assert.Equal(expected, Paths(Read(ConstructorRepository.DocSeek.FromStart(local))));
            Assert.Equal(expected, Paths(Read(FromEnd(whole.Count - local - n))));
            Assert.Equal(expected, Paths(Read(After(whole[local - 1]))));
            Assert.Equal(expected, Paths(Read(After(whole[local - 3], skip: 2))));
            if (local + n < whole.Count)
                Assert.Equal(expected, Paths(Read(Before(whole[local + n]))));
            if (local + n + 1 < whole.Count)
                Assert.Equal(expected, Paths(Read(Before(whole[local + n + 1], skip: 1))));
        }
    }

    /// <summary>Курсор идет по отфильтрованному потоку: чужие типы между страницами не вклиниваются.</summary>
    [Fact]
    public void The_cursor_walks_a_narrowed_stream_too()
    {
        using var db = new TempDatabase();
        var repo  = db.Constructor;
        var shell = KeysetStream(repo);
        var gost  = Types("ГОСТ");

        var walked = new List<DocRecord>();
        var page   = repo.GetDocPage(shell.ColId, shell.TypeId, gost, 0, 3);
        while (page.Count > 0)
        {
            walked.AddRange(page);
            page = repo.GetDocPage(shell.ColId, shell.TypeId, gost, After(page[^1]), 3);
        }

        Assert.Equal(Paths(Whole(repo, shell, gost)), Paths(walked));
        Assert.All(walked, d => Assert.Equal("ГОСТ", d.DocType));
    }

    // ── Гистограмма типов ────────────────────────────────────────────────────

    /// <summary>
    /// Размер потока в любом виде, взятый из гистограммы, совпадает со счетом того же вида.
    /// Разойдись они, виртуальный список обещал бы полосой прокрутки строки, которых нет, или
    /// терял бы хвост.
    /// </summary>
    [Fact]
    public void The_histogram_sizes_every_view_like_the_count_does()
    {
        using var db = new TempDatabase();
        var repo  = db.Constructor;
        var shell = KeysetStream(repo);
        AddDoc(repo, shell, "Без типа 1", "/n1.pdf", position: 5000);
        AddDoc(repo, shell, "Без типа 2", "/n2.pdf", position: 5010);

        var hist = repo.GetDocTypeHistograms()[shell.ColId];

        foreach (var view in new[]
                 {
                     ConstructorRepository.DocView.All, Types("ГОСТ"), Types("СТО", ""), Types(""),
                     Types(), Types("нет такого"),
                 })
            Assert.Equal(repo.CountDocs(shell.ColId, shell.TypeId, view), hist.CountFor(view));
        Assert.Equal(shell.TypeId, hist.TypeFieldId);
    }

    /// <summary>
    /// Посчитанное зовущим не пересчитывается: гистограмма из <c>known</c> возвращается как
    /// есть, а считаются только недостающие коллекции.
    /// </summary>
    [Fact]
    public void Known_histograms_are_returned_without_a_recount()
    {
        using var db = new TempDatabase();
        var repo   = db.Constructor;
        var first  = Library(repo, "Стандарты");
        var second = Library(repo, "Регламенты");
        AddDoc(repo, first,  "Г1", "/g1.pdf", "ГОСТ");
        AddDoc(repo, second, "Р1", "/r1.pdf", "СТО");

        var held = new ConstructorRepository.DocTypeHistogram(first.TypeId,
                       new Dictionary<string, int> { ["ГОСТ"] = 999 });
        var result = repo.GetDocTypeHistograms(
            known: new Dictionary<int, ConstructorRepository.DocTypeHistogram> { [first.ColId] = held });

        Assert.Same(held, result[first.ColId]);
        Assert.Equal(1, result[second.ColId].Counts["СТО"]);
    }

    // ── Фильтр по типу ───────────────────────────────────────────────────────

    [Fact]
    public void The_type_filter_narrows_both_the_count_and_the_page()
    {
        using var db = new TempDatabase();
        var repo = db.Constructor;

        var shell = Library(repo);
        AddDoc(repo, shell, "Г1", "/g1.pdf", "ГОСТ", 1000);
        AddDoc(repo, shell, "С1", "/s1.pdf", "СТО",  2000);
        AddDoc(repo, shell, "Г2", "/g2.pdf", "ГОСТ", 3000);

        var view = Types("ГОСТ");
        Assert.Equal(2, repo.CountDocs(shell.ColId, shell.TypeId, view));
        Assert.Equal(["/g1.pdf", "/g2.pdf"],
                     repo.GetDocPage(shell.ColId, shell.TypeId, view, 0, 10)
                         .Select(d => d.Path).ToArray());
    }

    /// <summary>
    /// Документ без проставленного типа отмечается галочкой «(Пусто)» — пустой строкой.
    /// Значения в db_field_values у него нет вовсе, поэтому без COALESCE он не попал бы в
    /// выдачу ни при каком наборе.
    /// </summary>
    [Fact]
    public void Documents_without_a_type_are_matched_by_the_empty_value()
    {
        using var db = new TempDatabase();
        var repo = db.Constructor;

        var shell = Library(repo);
        AddDoc(repo, shell, "Г1", "/g1.pdf", "ГОСТ", 1000);
        AddDoc(repo, shell, "Без типа", "/none.pdf", position: 2000);

        var view = Types("");
        Assert.Equal(1, repo.CountDocs(shell.ColId, shell.TypeId, view));
        Assert.Equal(["/none.pdf"],
                     repo.GetDocPage(shell.ColId, shell.TypeId, view, 0, 10)
                         .Select(d => d.Path).ToArray());
    }

    /// <summary>
    /// Пустой набор типов — «не подходит ничего», а снятый фильтр (null) — «подходит все».
    /// Спутать их значило бы показать всю ленту ровно тогда, когда человек снял все галочки.
    /// </summary>
    [Fact]
    public void An_empty_type_set_matches_nothing_while_no_filter_matches_everything()
    {
        using var db = new TempDatabase();
        var repo = db.Constructor;

        var shell = Library(repo);
        AddDoc(repo, shell, "Г1", "/g1.pdf", "ГОСТ");

        var nothing = new ConstructorRepository.DocView(new HashSet<string>());
        Assert.Equal(0, repo.CountDocs(shell.ColId, shell.TypeId, nothing));
        Assert.Empty(repo.GetDocPage(shell.ColId, shell.TypeId, nothing, 0, 10));

        Assert.Equal(1, repo.CountDocs(shell.ColId, shell.TypeId, ConstructorRepository.DocView.All));
    }

    // ── Счет с потолком ──────────────────────────────────────────────────────

    /// <summary>Потолок не дает считать дальше: цена счета перестает зависеть от размера
    /// коллекции, а для пилюли с количеством точное число не нужно.</summary>
    [Fact]
    public void A_capped_count_stops_at_the_cap()
    {
        using var db = new TempDatabase();
        var repo = db.Constructor;

        var shell = Library(repo);
        for (var i = 1; i <= 12; i++) AddDoc(repo, shell, $"D{i}", $"/d{i}.pdf", position: i * 10);

        Assert.Equal(12, repo.CountDocs(shell.ColId, shell.TypeId, ConstructorRepository.DocView.All));
        Assert.Equal(5,  repo.CountDocs(shell.ColId, shell.TypeId, ConstructorRepository.DocView.All, cap: 5));
    }

    // ── Количества для выпадашки ─────────────────────────────────────────────

    [Fact]
    public void Type_counts_cover_every_value_including_the_empty_one()
    {
        using var db = new TempDatabase();
        var repo = db.Constructor;

        var shell = Library(repo);
        AddDoc(repo, shell, "Г1", "/g1.pdf", "ГОСТ", 1000);
        AddDoc(repo, shell, "Г2", "/g2.pdf", "ГОСТ", 2000);
        AddDoc(repo, shell, "С1", "/s1.pdf", "СТО",  3000);
        AddDoc(repo, shell, "Без", "/n.pdf", position: 4000);

        var counts = repo.GetDocTypeCounts();

        // Порядок: по убыванию количества, «(Пусто)» последним — как в фильтре колонки таблицы.
        Assert.Equal([("ГОСТ", 2), ("СТО", 1), ("", 1)], counts);
    }

    [Fact]
    public void Type_counts_sum_across_collections()
    {
        using var db = new TempDatabase();
        var repo = db.Constructor;

        var first  = Library(repo, "Стандарты");
        var second = Library(repo, "Регламенты");
        AddDoc(repo, first,  "Г1", "/g1.pdf", "ГОСТ");
        AddDoc(repo, second, "Г2", "/g2.pdf", "ГОСТ");

        Assert.Equal([("ГОСТ", 2)], repo.GetDocTypeCounts());
    }

    /// <summary>Коллекции вне активного набора в количества не попадают.</summary>
    [Fact]
    public void Inactive_collections_are_excluded_everywhere()
    {
        using var db = new TempDatabase();
        var repo = db.Constructor;

        var shown  = Library(repo, "Показана");
        var hidden = Library(repo, "Скрыта");
        AddDoc(repo, shown,  "Г1", "/g1.pdf", "ГОСТ");
        AddDoc(repo, hidden, "С1", "/s1.pdf", "СТО");

        var active = new HashSet<int> { shown.ColId };

        Assert.Equal([("ГОСТ", 1)], repo.GetDocTypeCounts(active));
        Assert.Single(repo.GetDocCollectionStats(active));
        Assert.Equal(["/g1.pdf"], repo.GetDocPaths(active));
        Assert.Equal(["/g1.pdf"], repo.SearchDocs(active, "Г", []).Select(d => d.Path));
    }

    /// <summary>ПУСТОЙ набор активных коллекций — «ни одной», а не «все».</summary>
    [Fact]
    public void An_empty_active_set_reads_nothing()
    {
        using var db = new TempDatabase();
        var repo = db.Constructor;

        var shell = Library(repo);
        AddDoc(repo, shell, "Г1", "/g1.pdf", "ГОСТ");

        var none = new HashSet<int>();
        Assert.Empty(repo.GetDocCollectionStats(none));
        Assert.Empty(repo.GetDocTypeCounts(none));
        Assert.Empty(repo.GetDocPaths(none));
        Assert.Empty(repo.SearchDocs(none, "Г1", []));
    }

    // ── Пути для индексации ──────────────────────────────────────────────────

    [Fact]
    public void Doc_paths_are_distinct_and_skip_empty_values()
    {
        using var db = new TempDatabase();
        var repo = db.Constructor;

        var shell = Library(repo);
        AddDoc(repo, shell, "A", "/same.pdf", position: 1000);
        AddDoc(repo, shell, "B", "/same.pdf", position: 2000);   // тот же файл в двух записях
        AddDoc(repo, shell, "C", "/other.pdf", position: 3000);
        SampleData.AddRecord(repo, shell.ColId, "Без файла", 4000);

        Assert.Equal(["/other.pdf", "/same.pdf"], repo.GetDocPaths().Order().ToArray());
    }

    /// <summary>
    /// У путей нет потолка. Раньше их бралось не больше 100 000, и библиотека крупнее читалась
    /// частично и молча: перепись считала числа подвала без хвоста, «Прочитать документы» его не
    /// разбирало, а «все уже прочитано» звучало при непрочитанном хвосте. Документы заводятся
    /// одним запросом: сотня тысяч записей через репозиторий шла бы минуты.
    /// </summary>
    [Fact]
    public void Doc_paths_have_no_cap_however_big_the_library()
    {
        const int Count = 100_001;
        using var db = new TempDatabase();
        var repo  = db.Constructor;
        var shell = Library(repo);

        using (var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={db.Path};Pooling=False"))
        {
            conn.Open();
            using var tx  = conn.BeginTransaction();
            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = $"""
                WITH RECURSIVE n(i) AS (SELECT 1 UNION ALL SELECT i + 1 FROM n WHERE i < {Count})
                INSERT INTO db_records (CollectionId, CreatedAt, UpdatedAt, CreatedBy, UpdatedBy, Position, AllDay, Title)
                SELECT {shell.ColId}, '2026-01-01T00:00:00', '2026-01-01T00:00:00', '', '', i, 0, 'Д' || i FROM n;
                INSERT INTO db_field_values (RecordId, FieldId, Value)
                SELECT Id, {shell.FileId}, '/docs/' || Id || '.pdf' FROM db_records WHERE CollectionId = {shell.ColId};
                """;
            cmd.ExecuteNonQuery();
            tx.Commit();
        }

        Assert.Equal(Count, repo.GetDocPaths().Count);
        Assert.Equal(Count, repo.LoadDocPathsAsync(new HashSet<int> { shell.ColId }).GetAwaiter().GetResult().Count);
    }

    // ── Поиск ────────────────────────────────────────────────────────────────

    [Fact]
    public void Search_finds_documents_by_title_and_by_file_name()
    {
        using var db = new TempDatabase();
        var repo = db.Constructor;

        var shell = Library(repo);
        AddDoc(repo, shell, "Регламент разработки", "/reglament.pdf", position: 1000);
        AddDoc(repo, shell, "Инструкция",           "/reglament-2.pdf", position: 2000);
        AddDoc(repo, shell, "Прочее",               "/other.pdf", position: 3000);

        // По названию.
        Assert.Equal(["/reglament.pdf"],
                     repo.SearchDocs(null, "Регламент разр", []).Select(d => d.Path).ToArray());
        // По имени файла — обе записи.
        Assert.Equal(2, repo.SearchDocs(null, "reglament", []).Count);
    }

    /// <summary>Кириллица ищется без учета регистра — SQLite сам так не умеет, помогают
    /// регистровые варианты подстроки.</summary>
    [Fact]
    public void Search_by_title_ignores_case_for_cyrillic()
    {
        using var db = new TempDatabase();
        var repo = db.Constructor;

        var shell = Library(repo);
        AddDoc(repo, shell, "Регламент разработки", "/r.pdf");

        Assert.Single(repo.SearchDocs(null, "регламент", []));
        Assert.Single(repo.SearchDocs(null, "РЕГЛАМЕНТ", []));
    }

    /// <summary>
    /// «Ё» и «е» в названии и пути не различаются — как в поиске по тексту: «Учёт материалов»
    /// находится по «учет», «Учет затрат» — по «учёт», файл «Отчёт.pdf» — по «отчет». А «й» — своя
    /// буква: «иода» не находит «йода».
    /// </summary>
    [Fact]
    public void Search_by_title_treats_yo_as_ye()
    {
        using var db = new TempDatabase();
        var repo = db.Constructor;

        var shell = Library(repo);
        AddDoc(repo, shell, "Учёт материалов", "/u.pdf");
        AddDoc(repo, shell, "Учет затрат",     "/z.pdf");
        AddDoc(repo, shell, "Сверка",          "/Отчёт.pdf");
        AddDoc(repo, shell, "Раствор йода",    "/i.pdf");

        string[] Found(string query) =>
            [.. Paths(repo.SearchDocs(null, query, [])).Order(StringComparer.Ordinal)];

        Assert.Equal(["/u.pdf", "/z.pdf"], Found("учет"));
        Assert.Equal(["/u.pdf", "/z.pdf"], Found("учёт"));
        Assert.Equal(["/u.pdf", "/z.pdf"], Found("УЧЁТ"));
        Assert.Equal(["/Отчёт.pdf"],       Found("отчет"));
        Assert.Equal(["/Отчёт.pdf"],       Found("Отчёт"));
        Assert.Empty(Found("иода"));
    }

    /// <summary>Пути, найденные в кеше текста, сшиваются с записями — это и есть поиск
    /// по содержимому со стороны базы.</summary>
    [Fact]
    public void Search_matches_documents_by_paths_found_in_the_text_cache()
    {
        using var db = new TempDatabase();
        var repo = db.Constructor;

        var shell = Library(repo);
        AddDoc(repo, shell, "Совсем другое название", "/inside.pdf", position: 1000);
        AddDoc(repo, shell, "Еще одно",               "/other.pdf",  position: 2000);

        var found = repo.SearchDocs(null, "слова-которого-нет-в-названии", ["/inside.pdf"]);

        Assert.Equal(["/inside.pdf"], found.Select(d => d.Path).ToArray());
    }

    [Fact]
    public void Search_without_query_and_without_paths_returns_nothing()
    {
        using var db = new TempDatabase();
        var repo = db.Constructor;

        var shell = Library(repo);
        AddDoc(repo, shell, "A", "/a.pdf");

        Assert.Empty(repo.SearchDocs(null, "", []));
    }

    /// <summary>Спецсимволы LIKE экранируются: «%» ищется как символ, а не как «что угодно».</summary>
    [Fact]
    public void Like_wildcards_in_the_query_are_escaped()
    {
        using var db = new TempDatabase();
        var repo = db.Constructor;

        var shell = Library(repo);
        AddDoc(repo, shell, "Скидка 50%", "/a.pdf", position: 1000);
        AddDoc(repo, shell, "Обычное",    "/b.pdf", position: 2000);

        Assert.Single(repo.SearchDocs(null, "50%", []));
        Assert.Empty(repo.SearchDocs(null, "%%%", []));
    }

    /// <summary>
    /// Путь в поле записи и ключ кеша написаны по-разному — и все равно сходятся. Кеш хранит
    /// путь нормализованным (<c>KeyOf</c>), а человек записал его с прямыми слешами: пока в
    /// запрос уходило одно написание, найденный по тексту документ молча пропадал из выдачи.
    /// </summary>
    [Fact]
    public void Search_matches_a_path_written_with_the_other_separator()
    {
        // Разделители расходятся только на Windows: на Unix обратный слеш — знак имени файла,
        // и GetFullPath его не трогает, то есть расходиться нечему.
        if (!OperatingSystem.IsWindows()) return;

        using var db = new TempDatabase();
        var repo = db.Constructor;

        var shell = Library(repo);
        const string written = "C:/Договоры/смета.xlsx";      // как записал человек
        AddDoc(repo, shell, "Совсем другое название", written, position: 1000);

        var key = SmartBase.Database.DocIndex.DocTextIndex.KeyOf(written);
        Assert.NotEqual(written, key);                        // ключ кеша нормализован

        var found = repo.SearchDocs(null, "слова-которого-нет-в-названии", [key]);

        Assert.Equal([written], found.Select(d => d.Path).ToArray());
    }

    /// <summary>
    /// Фильтр типов действует внутри запроса поиска, а не поверх выдачи. Иначе база возвращала
    /// бы ровно потолок строк, фильтр отсеивал половину, и признак обрезки молчал бы там, где
    /// выдача обрезана, — а находки нужного типа за потолком не доезжали бы вовсе.
    /// </summary>
    [Fact]
    public void Search_applies_the_type_filter_inside_the_query()
    {
        using var db = new TempDatabase();
        var repo = db.Constructor;

        var shell = Library(repo);
        AddDoc(repo, shell, "Регламент А", "/a.pdf", type: "ГОСТ", position: 1000);
        AddDoc(repo, shell, "Регламент Б", "/b.pdf", type: "СТО",  position: 2000);
        AddDoc(repo, shell, "Регламент В", "/c.pdf", position: 3000);   // без типа

        Assert.Equal(3, repo.SearchDocs(null, "Регламент", []).Count);
        Assert.Equal(["/a.pdf"],           Paths(repo.SearchDocs(null, "Регламент", [], Types("ГОСТ"))));
        Assert.Equal(["/b.pdf", "/c.pdf"], Paths(repo.SearchDocs(null, "Регламент", [], Types("СТО", ""))));
        Assert.Empty(repo.SearchDocs(null, "Регламент", [], Types()));
        // Совпадение по содержимому фильтруется так же, как по названию.
        Assert.Empty(repo.SearchDocs(null, "нет-в-названии", ["/a.pdf"], Types("СТО")));
        Assert.Single(repo.SearchDocs(null, "нет-в-названии", ["/a.pdf"], Types("ГОСТ")));
    }

    /// <summary>
    /// Потолок режет выдачу в порядке ленты, и ровно на столько, сколько просили: контроллер
    /// просит на одну строку больше остатка, и по этой лишней строке узнает про хвост.
    /// </summary>
    [Fact]
    public void Search_returns_no_more_than_the_limit_in_feed_order()
    {
        using var db = new TempDatabase();
        var repo = db.Constructor;

        var shell = Library(repo);
        AddDoc(repo, shell, "Регламент А", "/a.pdf", position: 1000);
        AddDoc(repo, shell, "Регламент Б", "/b.pdf", position: 2000);
        AddDoc(repo, shell, "Регламент В", "/c.pdf", position: 3000);

        Assert.Equal(["/a.pdf", "/b.pdf"], Paths(repo.SearchDocs(null, "Регламент", [], limit: 2)));
        Assert.Equal(3, repo.SearchDocs(null, "Регламент", [], limit: 3).Count);
        Assert.Equal(3, repo.SearchDocs(null, "Регламент", [], limit: 4).Count);
    }
}
