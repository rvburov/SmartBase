using SmartBase.Core.Models;
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
    /// которого счет и страница собираются одними и теми же кусками SQL.
    /// </summary>
    [Fact]
    public void Pages_reproduce_the_in_memory_order_exactly()
    {
        using var db = new TempDatabase();
        var repo = db.Constructor;

        var shell = Library(repo);
        for (var i = 1; i <= 25; i++)
            AddDoc(repo, shell, $"D{i:D2}", $"/d{i:D2}.pdf", position: i * 10);

        var whole = repo.GetDocRecords().Select(d => d.Path).ToList();

        var paged = new List<string>();
        for (var skip = 0; skip < whole.Count; skip += 7)
            paged.AddRange(repo.GetDocPage(shell.ColId, shell.TypeId,
                                           ConstructorRepository.DocView.All, skip, 7)
                               .Select(d => d.Path));

        Assert.Equal(whole, paged);
        Assert.Equal(25, whole.Count);
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
        Assert.Single(repo.GetDocRecords(active));
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
        Assert.Empty(repo.GetDocRecords(none));
        Assert.Empty(repo.GetDocCollectionStats(none));
        Assert.Empty(repo.GetDocTypeCounts(none));
        Assert.Empty(repo.GetDocPaths(none));
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
}
