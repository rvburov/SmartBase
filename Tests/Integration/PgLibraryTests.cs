using Npgsql;
using SmartBase.Core.Models;
using SmartBase.Database;
using SmartBase.Database.Repositories;
using DocView = SmartBase.Database.Repositories.ConstructorRepository.DocView;
using DocSeek = SmartBase.Database.Repositories.ConstructorRepository.DocSeek;
using DocSeekOrigin = SmartBase.Database.Repositories.ConstructorRepository.DocSeekOrigin;

namespace SmartBase.Tests.Integration;

/// <summary>
/// «Библиотека документов» на PostgreSQL. Читающий путь раздела — рукописный SQL
/// (<c>ConstructorRepository.Docs.cs</c>, <c>ConstructorRepository.DocsPaging.cs</c>): размеры
/// коллекций, страницы ленты от края и от соседа, гистограмма типов, поиск, пути для очереди
/// чтения. Ни один тест раньше не гонял его по PostgreSQL.
///
/// Проверка — СВЕРКОЙ: один и тот же сценарий идет на SQLite и на PostgreSQL, и каждый ответ
/// раздела обязан совпасть строка в строку. Так набор ловит не только запрос, который PostgreSQL
/// не примет, но и тихое расхождение — запрос, который проходит, а отвечает другое. Без стенда
/// (порт 5455) тесты помечаются Skipped, как и прочие тесты PostgreSQL.
/// </summary>
public sealed class PgLibraryTests
{
    private static void ResetSchema(string connString)
    {
        using var conn = new NpgsqlConnection(connString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DROP SCHEMA IF EXISTS public CASCADE; CREATE SCHEMA public;";
        cmd.ExecuteNonQuery();
    }

    /// <summary>Ответы сценария на SQLite и на PostgreSQL.</summary>
    private static (List<string> Lite, List<string> Pg) RunOnBoth()
    {
        List<string> lite;
        using (var db = new TempDatabase()) lite = Scenario(db.Constructor);

        var cs = PgTestServer.ConnectionString(PgTestServer.Database1);
        ResetSchema(cs);
        List<string> pg;
        using (var m = DatabaseManager.Open(101, "pg-library", cs, provider: "postgres"))
        {
            Assert.True(m.Context.IsPostgres);
            pg = Scenario(m.Constructor);
        }
        return (lite, pg);
    }

    [SkippableFact]
    public void Library_answers_on_PostgreSQL_the_same_as_on_SQLite()
    {
        Skip.IfNot(PgTestServer.IsAvailable, "Стенд PostgreSQL sb-pg (порт 5455) не поднят");

        var (lite, pg) = RunOnBoth();

        // Расхождение показываем строками: какой ответ есть только у SQLite и какой только у
        // PostgreSQL, — по одному номеру позиции в списке не понять, что разошлось.
        var onlyLite = lite.Except(pg).ToList();
        var onlyPg   = pg.Except(lite).ToList();
        Assert.True(onlyLite.Count == 0 && onlyPg.Count == 0,
                    "только SQLite:\n  " + string.Join("\n  ", onlyLite) +
                    "\nтолько PostgreSQL:\n  " + string.Join("\n  ", onlyPg));
        Assert.Equal(lite, pg);

        // Сверка ловит расхождение провайдеров, но не промах, общий для обоих. Поэтому главные
        // ответы раздела закреплены и сами по себе.
        string Answer(string what) =>
            pg.Single(l => l.StartsWith(what + ": ", StringComparison.Ordinal))[(what.Length + 2)..];

        Assert.Equal("1",     Answer("починено коллекций"));
        Assert.Equal("False", Answer("флаг на текстовом поле"));
        Assert.Equal("8", Answer("счет «Нормативов»"));
        Assert.Equal("3", Answer("счет с крышкой 3"));
        // Регистр латиницы поиск не различает — и в названии записи, и в имени файла.
        Assert.Contains("GoSt mixed",     Answer("поиск «gost»"));
        Assert.Contains("MiXeD-Case.pdf", Answer("поиск «mixed-case»"));
        // Знаки LIKE в запросе — обычные знаки.
        Assert.Contains("Скидка 50% на поверку", Answer("поиск «50%»"));
        Assert.DoesNotContain("Скидка 500",      Answer("поиск «50%»"));
        Assert.DoesNotContain("axb",             Answer("поиск «a_b»"));
        Assert.Contains("Scan_01.PDF",           Answer(@"поиск «Archive\Scan»"));
        // Путь из кеша сходится с записью, а на Windows — и с написанием прямыми слешами.
        Assert.Contains("ГОСТ 21.501.pdf", Answer("поиск по путям кеша"));
        if (OperatingSystem.IsWindows())
            Assert.Contains("gost-report.pdf", Answer("поиск по путям кеша"));
        // Сосед по ключу у первого договора — второй документ той же записи.
        Assert.StartsWith("[Договоры · ДОГОВОР поставки · Скан ·", Answer("после первого договора"));
    }

    // ── Сценарий ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Раздел глазами его вызовов: каждый ответ — строка «что спросили: что ответили». В строки не
    /// попадают id — у провайдеров они свои; документ описан коллекцией, названием, столбцом,
    /// путем и типом.
    /// </summary>
    private static List<string> Scenario(ConstructorRepository repo)
    {
        var lines = new List<string>();
        void Say(string what, object answer) => lines.Add($"{what}: {answer}");

        // «Нормативы» подключаются штатно — как кнопкой «+» раздела: заводятся «Файл» и «Тип
        // документа».
        var norms     = SampleData.AddCollection(repo, "Нормативы").Id!.Value;
        var normsFile = repo.EnsureDocCollection(norms);
        var normsType = repo.DocTypeFieldId(norms)!.Value;

        // «Договоры» — свои файловые столбцы, и их ДВА: у записи со сканом два документа подряд.
        var deals     = SampleData.AddCollection(repo, "Договоры").Id!.Value;
        var dealsFile = SampleData.AddField(repo, deals, "Договор", "file").Id!.Value;
        repo.SetFileFieldInLibrary(dealsFile, true);
        var dealsScan = SampleData.AddField(repo, deals, "Скан", "file", 3000).Id!.Value;
        repo.SetFileFieldInLibrary(dealsScan, true);
        var dealsType = repo.DocTypeFieldId(deals)!.Value;

        // «Архив» — с файловым полем, но еще не подключен: план подключения, потом подключение.
        var archive = SampleData.AddCollection(repo, "Архив").Id!.Value;
        SampleData.AddField(repo, archive, "Вложение", "file");

        var names = new Dictionary<int, string> { [norms] = "Нормативы", [deals] = "Договоры", [archive] = "Архив" };

        void Doc(int col, int file, int type, string title, int position, string path, string kind = "",
                 (int Field, string Path)? second = null)
        {
            var id = SampleData.AddRecord(repo, col, title, position).Id!.Value;
            if (path.Length > 0)  repo.SetFieldValue(id, file, path);
            if (kind.Length > 0)  repo.SetFieldValue(id, type, kind);
            if (second is { } s)  repo.SetFieldValue(id, s.Field, s.Path);
        }

        Doc(norms, normsFile, normsType, "ГОСТ 21.501-2018",      10, @"C:\Нормы\ГОСТ 21.501.pdf", "ГОСТ");
        Doc(norms, normsFile, normsType, "СП 48.13330",           20, @"C:\Нормы\СП 48.pdf",       "СП");
        Doc(norms, normsFile, normsType, "Gost Report",           30, "C:/Нормы/gost-report.pdf",  "ГОСТ");
        Doc(norms, normsFile, normsType, "GoSt mixed",            40, @"C:\Нормы\mixed.pdf");
        Doc(norms, normsFile, normsType, "Скидка 50% на поверку", 50, @"C:\Нормы\скидка.pdf",      "Приказ");
        Doc(norms, normsFile, normsType, "Скидка 500 рублей",     60, @"C:\Нормы\500.pdf",         "Приказ");
        Doc(norms, normsFile, normsType, "a_b",                   70, @"C:\Нормы\a_b.txt");
        Doc(norms, normsFile, normsType, "axb",                   80, @"C:\Нормы\axb.txt");
        Doc(norms, normsFile, normsType, "Без файла",             90, "",                          "ГОСТ");

        Doc(deals, dealsFile, dealsType, "ДОГОВОР поставки", 10, @"D:\Archive\Scan_01.PDF", "Договор",
            second: (dealsScan, @"D:\Archive\Scan_01-скан.pdf"));
        Doc(deals, dealsFile, dealsType, "Договор аренды",   20, @"D:\Archive\MiXeD-Case.pdf", "Договор");
        Doc(deals, dealsFile, dealsType, "договор подряда",  30, @"D:\Archive\подряд.docx");

        string Show(DocRecord d) =>
            $"{d.CollectionName} · {d.Title} · {d.FileFieldName} · {d.Path} · «{d.DocType}»";
        string List(IEnumerable<DocRecord> docs) => "[" + string.Join(" | ", docs.Select(Show)) + "]";
        static DocView Types(params string[] types) => new(new HashSet<string>(types));

        // ── Состав раздела ──
        var plan = repo.PlanDocSetup(archive);
        Say("план подключения «Архива»",
            $"взять {string.Join(",", plan.Candidates.Select(c => c.FieldName))}; " +
            $"завести при отказе {string.Join(",", plan.FieldsToCreateIfDeclined)}; " +
            $"при согласии {string.Join(",", plan.FieldsToCreateIfAdopted)}");
        Say("есть ли документы в базе", repo.HasAnyDocFileField());
        Say("подключен ли «Архив» до подключения", repo.HasDocFileField(archive));
        repo.EnsureDocCollection(archive);
        Say("подключен ли «Архив» после", repo.HasDocFileField(archive));
        Say("файловых столбцов у «Договоров»", repo.DocFileFieldIds(deals).Count);

        // Починка ролей при монтировании: флаг источника документов на текстовом поле —
        // состояние старой базы или ручной правки. Флаг снимается, повторная починка пустая.
        var broken = SampleData.AddCollection(repo, "Сломанная").Id!.Value;
        repo.AddField(new DbField
        {
            CollectionId = broken, Name = "Призрак", FieldType = "text", Position = 1000, IsDocFile = true,
        });
        Say("починено коллекций", repo.RepairDocFieldRoles());
        Say("починено повторно", repo.RepairDocFieldRoles());
        Say("флаг на текстовом поле", repo.GetFields(broken).Single(f => f.Name == "Призрак").IsDocFile);
        Say("коллекции раздела", string.Join(", ", repo.GetDocCollectionIds().Select(id => names[id]).Order()));

        // ── Размеры ──
        string Stats(Dictionary<int, ConstructorRepository.DocCollectionStats> stats) =>
            string.Join("; ", stats.OrderBy(p => names[p.Key])
                                   .Select(p => $"{names[p.Key]} {p.Value.Total} (тип {(p.Value.TypeFieldId is null ? "нет" : "есть")})"));
        Say("размеры", Stats(repo.GetDocCollectionStats()));
        Say("размеры в фоне", Stats(repo.GetDocCollectionStatsAsync().GetAwaiter().GetResult()));
        Say("размеры только ГОСТ", Stats(repo.GetDocCollectionStats(view: Types("ГОСТ"))));
        Say("счет «Нормативов»", repo.CountDocs(norms, normsType, DocView.All));
        Say("счет с крышкой 3", repo.CountDocs(norms, normsType, DocView.All, cap: 3));
        Say("счет ГОСТ и пустых", repo.CountDocs(norms, normsType, Types("ГОСТ", "")));
        Say("счет без единого типа", repo.CountDocs(norms, normsType, Types()));
        Say("счет в фоне", repo.CountDocsAsync(deals, dealsType, DocView.All).GetAwaiter().GetResult());

        // ── Страницы ──
        var first = repo.GetDocPage(norms, normsType, DocView.All, 0, 3);
        Say("страница 0+3", List(first));
        Say("страница 3+3", List(repo.GetDocPage(norms, normsType, DocView.All, 3, 3)));
        Say("страница 6+3", List(repo.GetDocPage(norms, normsType, DocView.All, 6, 3)));
        Say("с конца 0+3", List(repo.GetDocPage(norms, normsType, DocView.All, new DocSeek(DocSeekOrigin.End, 0), 3)));
        Say("с конца 2+3", List(repo.GetDocPage(norms, normsType, DocView.All, new DocSeek(DocSeekOrigin.End, 2), 3)));
        Say("после второго", List(repo.GetDocPage(norms, normsType, DocView.All,
                                                   new DocSeek(DocSeekOrigin.After, 0, first[1].Cursor), 3)));
        Say("после второго с пропуском 1", List(repo.GetDocPage(norms, normsType, DocView.All,
                                                   new DocSeek(DocSeekOrigin.After, 1, first[1].Cursor), 3)));
        var tail = repo.GetDocPage(norms, normsType, DocView.All, 5, 3);
        Say("перед шестым", List(repo.GetDocPage(norms, normsType, DocView.All,
                                                  new DocSeek(DocSeekOrigin.Before, 0, tail[0].Cursor), 3)));
        Say("ГОСТ и пустые", List(repo.GetDocPage(norms, normsType, Types("ГОСТ", ""), 0, 10)));
        Say("ни одного типа", List(repo.GetDocPage(norms, normsType, Types(), 0, 10)));

        // Две записи «Договоров» с двумя столбцами: сосед по ключу — второй документ ТОЙ ЖЕ
        // записи (позиция и id записи равны, решает позиция столбца).
        var deal = repo.GetDocPage(deals, dealsType, DocView.All, 0, 1);
        Say("договоры от начала", List(deal));
        Say("после первого договора", List(repo.GetDocPage(deals, dealsType, DocView.All,
                                                            new DocSeek(DocSeekOrigin.After, 0, deal[0].Cursor), 2)));
        Say("договоры в фоне", List(repo.GetDocPageAsync(deals, dealsType, DocView.All, 0, 10).GetAwaiter().GetResult()));

        // ── Типы ──
        string Hist(Dictionary<int, ConstructorRepository.DocTypeHistogram> hist) =>
            string.Join("; ", hist.OrderBy(p => names[p.Key]).Select(p =>
                $"{names[p.Key]}: " + string.Join(", ", p.Value.Counts.OrderBy(c => c.Key, StringComparer.Ordinal)
                                                                         .Select(c => $"«{c.Key}» {c.Value}"))));
        var histograms = repo.GetDocTypeHistograms();
        Say("типы", Hist(histograms));
        Say("типы в фоне", Hist(repo.GetDocTypeHistogramsAsync().GetAwaiter().GetResult()));
        Say("выпадашка типов", string.Join(", ", ConstructorRepository.MergeTypeCounts(histograms.Values)
                                                                     .Select(t => $"«{t.Value}» {t.Count}")));

        // ── Поиск ──
        string Find(string query, IReadOnlyCollection<string>? paths = null, DocView? view = null,
                    int limit = 500, ISet<int>? active = null) =>
            List(repo.SearchDocs(active, query, paths ?? [], view, limit));

        Say("поиск «договор»", Find("договор"));
        Say("поиск «ДОГОВОР»", Find("ДОГОВОР"));
        Say("поиск «gost»", Find("gost"));
        Say("поиск «GOST»", Find("GOST"));
        Say("поиск «mixed-case»", Find("mixed-case"));
        Say("поиск «scan_01»", Find("scan_01"));
        Say("поиск «скан»", Find("скан"));
        Say("поиск «50%»", Find("50%"));
        Say("поиск «a_b»", Find("a_b"));
        Say(@"поиск «Archive\Scan»", Find(@"Archive\Scan"));
        // Ключи кеша — такие, какими их дает KeyOf на Windows: с обратными слешами. Второй документ
        // записан в поле прямыми слешами и находится только через написания пути
        // (DocTextIndex.Spellings). Сам KeyOf не зовется: на Linux «C:\…» не корень, и ключ ушел
        // бы в рабочую папку — сверка прошла бы на пустом ответе.
        Say("поиск по путям кеша", Find("", [@"C:\Нормы\ГОСТ 21.501.pdf", @"C:\Нормы\gost-report.pdf"]));
        Say("поиск «договор» только Договор", Find("договор", view: Types("Договор")));
        Say("поиск «договор» только пустые", Find("договор", view: Types("")));
        Say("поиск «договор» с потолком 2", Find("договор", limit: 2));
        Say("поиск «договор» в «Нормативах»", Find("договор", active: new HashSet<int> { norms }));
        Say("поиск в фоне", List(repo.SearchDocsAsync(new HashSet<int> { deals }, "аренд", []).GetAwaiter().GetResult()));

        // ── Пути для очереди чтения и сплошное чтение ──
        Say("пути", string.Join(" | ", repo.GetDocPaths().Order(StringComparer.Ordinal)));
        Say("пути в фоне", string.Join(" | ", repo.LoadDocPathsAsync().GetAwaiter().GetResult().Order(StringComparer.Ordinal)));
        Say("все документы", List(repo.GetDocRecords()));
        Say("документы в фоне", List(repo.LoadDocsAsync(new HashSet<int> { deals }).GetAwaiter().GetResult()));

        return lines;
    }
}
