using ClosedXML.Excel;
using SmartBase.Extensions.PdfAnalyzer;

namespace SmartBase.Tests.Extensions.PdfAnalyzer;

/// <summary>
/// Сквозной прогон «Анализа PDF»: в папке собираются настоящие PDF (генератор — писатель самого
/// PdfPig, см. PdfStand), расширение считает форматы и пишет отчет, отчет читается обратно.
///
/// Здесь проверяется все, чего не видно на чистой логике DocumentCodes: распознавание форматов
/// с допуском, подсчет листов А4, состав колонок по типу выгрузки, отбор КД/ПД, обход папок и
/// обработка битых файлов.
/// </summary>
public sealed class PdfAnalyzerRunTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"sbext_pa_{Guid.NewGuid():N}");
    private readonly string _pdfs;
    private readonly string _report;

    public PdfAnalyzerRunTests()
    {
        _pdfs   = Path.Combine(_root, "Документы");
        _report = Path.Combine(_root, "отчет.xlsx");
        Directory.CreateDirectory(_pdfs);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
        catch { /* best-effort */ }
    }

    // ── Стенд ─────────────────────────────────────────────────────────────────

    private string Pdf(string fileName, params (double W, double H)[] pages)
    {
        var path = Path.Combine(_pdfs, fileName);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        PdfStand.Write(path, pages.Length > 0 ? pages : [PdfStand.A4]);
        return path;
    }

    private AnalyzeResult Run(
        ExportType exportType = ExportType.All, bool recursive = false, bool preserveOrder = false,
        bool splitName = false, string? savePath = null,
        IProgress<AnalyzeProgress>? progress = null, CancellationToken ct = default) =>
        PdfAnalyzerEngine.Run(_pdfs, savePath ?? _report, exportType, recursive, preserveOrder,
                              splitName, progress, ct);

    /// <summary>Прогнать анализ и проверить лист отчета (книга закрывается сама).</summary>
    private AnalyzeResult RunAndCheck(Action<IXLWorksheet> check, ExportType exportType = ExportType.All,
                                      bool recursive = false, bool preserveOrder = false, bool splitName = false)
    {
        var result = Run(exportType, recursive, preserveOrder, splitName);
        using var wb = new XLWorkbook(result.SavePath);
        check(wb.Worksheet("ОПИСЬ"));
        return result;
    }

    // Значение колонки с заданной подписью в строке отчета (строка 1 — шапка, данные с 2-й).
    private static string Cell(IXLWorksheet ws, int dataRow, string header)
    {
        var col = ws.Row(1).CellsUsed().First(c => c.GetString() == header).Address.ColumnNumber;
        return ws.Cell(dataRow + 1, col).GetString();
    }

    private static string[] Headers(IXLWorksheet ws) =>
        [.. ws.Row(1).CellsUsed().Select(c => c.GetString())];

    // ── Смоук ─────────────────────────────────────────────────────────────────

    [Fact]
    public void Generated_pdf_is_analyzed()
    {
        Pdf("АБВГ.301234.001СБ Блок питания.pdf");

        var result = Run();

        Assert.Equal(1, result.TotalFiles);
        Assert.Equal(1, result.Processed);
        Assert.Equal(0, result.Errors);
        Assert.True(File.Exists(result.SavePath));
    }

    // ── Путь отчета ───────────────────────────────────────────────────────────

    [Fact]
    public void Report_lands_next_to_the_folder_when_no_path_is_given()
    {
        Pdf("документ.pdf");

        var result = Run(savePath: "");

        Assert.Equal(Path.Combine(_pdfs, "pdf_info.xlsx"), result.SavePath);
    }

    [Fact]
    public void Existing_folder_as_save_path_gets_the_default_name()
    {
        Pdf("документ.pdf");
        var folder = Path.Combine(_root, "Отчеты");
        Directory.CreateDirectory(folder);

        var result = Run(savePath: folder);

        Assert.Equal(Path.Combine(folder, "pdf_info.xlsx"), result.SavePath);
    }

    [Fact]
    public void Save_path_without_extension_gets_xlsx()
    {
        Pdf("документ.pdf");

        var result = Run(savePath: Path.Combine(_root, "свод"));

        Assert.Equal(Path.Combine(_root, "свод.xlsx"), result.SavePath);
    }

    // ── Состав колонок по типу выгрузки ───────────────────────────────────────

    [Fact]
    public void Register_of_kd_names_the_kd_designation_column()
    {
        Pdf("АБВГ.301234.001СБ Блок питания.pdf");

        RunAndCheck(ws =>
        {
            var headers = Headers(ws);
            Assert.Equal(18, headers.Length);
            Assert.Contains("Обозначение документа КД", headers);
            Assert.Contains("Листов формата А4", headers);
        }, ExportType.Kd);
    }

    [Fact]
    public void Register_of_pd_names_the_pd_designation_column()
    {
        Pdf("АБВГ.12345-01 34 01 Руководство оператора.pdf");

        RunAndCheck(ws =>
        {
            var headers = Headers(ws);
            Assert.Equal(18, headers.Length);
            Assert.Contains("Обозначение документа ПД", headers);
        }, ExportType.PdRegister);
    }

    [Fact]
    public void Register_of_td_names_the_td_designation_column()
    {
        Pdf("АБВГ.12345.67890 Маршрутная карта.pdf");

        RunAndCheck(ws =>
        {
            var headers = Headers(ws);
            Assert.Equal(18, headers.Length);
            Assert.Contains("Обозначение документа ТД", headers);
        }, ExportType.Td);
    }

    // Описи — сокращенный формат: пять колонок вместо восемнадцати.
    [Theory]
    [InlineData(ExportType.Inventory,   "АБВГ.301234.001СБ Блок питания.pdf")]
    [InlineData(ExportType.PdInventory, "АБВГ.12345-01 34 01 Руководство оператора.pdf")]
    public void Inventory_is_a_short_five_column_form(ExportType exportType, string fileName)
    {
        Pdf(fileName);

        RunAndCheck(ws =>
        {
            var headers = Headers(ws);
            Assert.Equal(5, headers.Length);
            Assert.Equal(["№ п/п", "Обозначение", "Наименование",
                          "Количество листов в документе", "Примечание"], headers);
        }, exportType);
    }

    [Fact]
    public void All_data_export_adds_the_file_location_columns()
    {
        Pdf("АБВГ.301234.001СБ Блок питания.pdf");

        RunAndCheck(ws =>
        {
            var headers = Headers(ws);
            Assert.Contains("Путь к файлу", headers);
            Assert.Contains("Папка", headers);
            Assert.Contains("Имя файла", headers);
        });
    }

    // ── Форматы листов ────────────────────────────────────────────────────────

    [Fact]
    public void Page_formats_are_counted_per_sheet()
    {
        Pdf("документ.pdf", PdfStand.A4, PdfStand.A3, PdfStand.A1, PdfStand.A0);

        RunAndCheck(ws =>
        {
            Assert.Equal("1", Cell(ws, 1, "А4"));
            Assert.Equal("1", Cell(ws, 1, "А3"));
            Assert.Equal("",  Cell(ws, 1, "А2"));   // нулевые счетчики остаются пустыми
            Assert.Equal("1", Cell(ws, 1, "А1"));
            Assert.Equal("1", Cell(ws, 1, "А0"));
            Assert.Equal("4", Cell(ws, 1, "Физических листов"));
        });
    }

    [Fact]
    public void Non_standard_page_goes_to_additional_formats()
    {
        Pdf("документ.pdf", PdfStand.Odd);

        RunAndCheck(ws => Assert.Equal("500×700", Cell(ws, 1, "Доп. форматы")));
    }

    [Fact]
    public void Repeated_non_standard_size_is_counted()
    {
        Pdf("документ.pdf", PdfStand.Odd, PdfStand.Odd);

        RunAndCheck(ws => Assert.Equal("500×700 (2 шт.)", Cell(ws, 1, "Доп. форматы")));
    }

    /// <summary>Производный формат (A4×3 и подобные) в колонке «Доп. форматы» показывается
    /// СЧЕТЧИКОМ, а не названием — названием выводятся только нестандартные размеры вида
    /// «500×700» (см. AdditionalValue: имя, начинающееся с «A», в ячейку не идет). Само название
    /// при этом не теряется: по нему считается множитель листов А4
    /// (см. A4_sheet_count_adds_derived_formats). Фиксируем как есть.</summary>
    [Fact]
    public void Derived_format_is_counted_not_named_in_additional_formats()
    {
        Pdf("документ.pdf", PdfStand.A4x3, PdfStand.A4x3);

        RunAndCheck(ws => Assert.Equal("2", Cell(ws, 1, "Доп. форматы")));
    }

    /// <summary>A5 намеренно не считается базовым форматом: столбца под него нет, и лист «терялся»
    /// бы в разбивке. Отличие от исходной утилиты — фиксируем как есть.</summary>
    [Fact]
    public void A5_page_is_not_a_base_format()
    {
        Pdf("документ.pdf", PdfStand.A5);

        RunAndCheck(ws =>
        {
            Assert.Equal("", Cell(ws, 1, "А4"));
            Assert.Equal("148×210", Cell(ws, 1, "Доп. форматы"));
        });
    }

    /// <summary>Допуск 30 мм — основа распознавания: реальные чертежи почти никогда не совпадают
    /// с ГОСТ-размером до миллиметра.</summary>
    [Fact]
    public void Tolerance_of_thirty_millimetres_still_recognises_a_format()
    {
        Pdf("документ.pdf", (205, 300));

        RunAndCheck(ws => Assert.Equal("1", Cell(ws, 1, "А4")));
    }

    [Fact]
    public void A4_sheet_count_sums_the_formats()
    {
        Pdf("документ.pdf", PdfStand.A4, PdfStand.A3, PdfStand.A0);   // 1 + 2 + 16

        RunAndCheck(ws => Assert.Equal("19", Cell(ws, 1, "Листов формата А4")));
    }

    [Fact]
    public void A4_sheet_count_adds_derived_formats()
    {
        Pdf("документ.pdf", PdfStand.A4, PdfStand.A4x3);   // 1 + 3

        RunAndCheck(ws => Assert.Equal("4", Cell(ws, 1, "Листов формата А4")));
    }

    // ── Контрольная сумма и ошибки ────────────────────────────────────────────

    [Fact]
    public void Checksum_is_equal_for_identical_files()
    {
        Pdf("1копия.pdf", PdfStand.A4);
        File.Copy(Path.Combine(_pdfs, "1копия.pdf"), Path.Combine(_pdfs, "2копия.pdf"));
        Pdf("3другой.pdf", PdfStand.A3);

        RunAndCheck(ws =>
        {
            var first  = Cell(ws, 1, "Контрольная сумма");
            var second = Cell(ws, 2, "Контрольная сумма");
            var third  = Cell(ws, 3, "Контрольная сумма");

            Assert.Equal(first, second);
            Assert.NotEqual(first, third);
        });
    }

    /// <summary>Битый файл — рядовая ситуация на сетевой папке: он обязан стать строкой «Ошибка»,
    /// а не оборвать прогон на середине.</summary>
    [Fact]
    public void Broken_pdf_becomes_an_error_row()
    {
        Pdf("1хороший.pdf");
        PdfStand.WriteBroken(Path.Combine(_pdfs, "2битый.pdf"));

        var result = RunAndCheck(ws =>
        {
            Assert.Equal("Ошибка", Cell(ws, 2, "Контрольная сумма"));
            Assert.Equal("2битый.pdf", Cell(ws, 2, "Имя файла"));
        });

        Assert.Equal(1, result.Errors);
        Assert.Equal(2, result.Processed);   // хороший файл все равно обработан
    }

    // ── Отбор КД и ПД ─────────────────────────────────────────────────────────

    [Fact]
    public void Only_program_documents_go_into_the_pd_register()
    {
        Pdf("АБВГ.301234.001СБ Блок питания.pdf");
        Pdf("АБВГ.12345-01 34 01 Руководство оператора.pdf");

        RunAndCheck(ws =>
        {
            var row = Assert.Single(ws.RowsUsed().Skip(1));
            Assert.Equal("АБВГ.12345-01 34 01", Cell(ws, row.RowNumber() - 1, "Обозначение документа ПД"));
        }, ExportType.PdRegister);
    }

    [Fact]
    public void Program_documents_are_kept_out_of_the_kd_register()
    {
        Pdf("АБВГ.301234.001СБ Блок питания.pdf");
        Pdf("АБВГ.12345-01 34 01 Руководство оператора.pdf");

        RunAndCheck(ws =>
        {
            Assert.Single(ws.RowsUsed().Skip(1));
            Assert.Equal("АБВГ.301234.001СБ", Cell(ws, 1, "Обозначение документа КД"));
        }, ExportType.Kd);
    }

    [Fact]
    public void Nothing_to_export_is_a_friendly_error()
    {
        Pdf("АБВГ.301234.001СБ Блок питания.pdf");   // только КД

        var ex = Assert.Throws<PdfAnalyzeException>(() => Run(ExportType.PdRegister));

        Assert.Contains("не найдено программной документации (ПД)", ex.Message);
    }

    // ── Обход папок и порядок ─────────────────────────────────────────────────

    [Fact]
    public void Recursive_walk_adds_subfolder_files_and_fills_the_folder_column()
    {
        Pdf("корневой.pdf");
        Pdf(Path.Combine("Вложенная", "вложенный.pdf"));

        var result = RunAndCheck(ws =>
        {
            var folders = ws.RowsUsed().Skip(1).Select(r => Cell(ws, r.RowNumber() - 1, "Папка")).ToList();
            Assert.Contains("Вложенная", folders);
            Assert.Contains("", folders);   // файл из корня — папка пустая
        }, recursive: true);

        Assert.Equal(2, result.TotalFiles);
    }

    [Fact]
    public void Flat_walk_ignores_subfolders()
    {
        Pdf("корневой.pdf");
        Pdf(Path.Combine("Вложенная", "вложенный.pdf"));

        var result = Run();

        Assert.Equal(1, result.TotalFiles);
    }

    /// <summary>«Сохранить порядок» глушит рекурсию: обход строится как
    /// <c>recursive &amp;&amp; !preserveOrder</c>. Связка неочевидная и ломается при первой же
    /// правке — фиксируем.</summary>
    [Fact]
    public void Preserve_order_ignores_the_recursive_flag()
    {
        Pdf("корневой.pdf");
        Pdf(Path.Combine("Вложенная", "вложенный.pdf"));

        var result = Run(recursive: true, preserveOrder: true);

        Assert.Equal(1, result.TotalFiles);   // подпапка не обойдена, хотя recursive = true
    }

    [Fact]
    public void Preserve_order_sorts_by_modification_time()
    {
        var older = Pdf("яполе.pdf");
        var newer = Pdf("абвгд.pdf");
        File.SetLastWriteTimeUtc(older, new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        File.SetLastWriteTimeUtc(newer, new DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc));

        RunAndCheck(ws =>
        {
            Assert.Equal("яполе.pdf", Cell(ws, 1, "Имя файла"));   // раньше по времени правки
            Assert.Equal("абвгд.pdf", Cell(ws, 2, "Имя файла"));
        }, preserveOrder: true);
    }

    [Fact]
    public void Flat_walk_sorts_by_file_name()
    {
        Pdf("Бвторой.pdf");
        Pdf("Апервый.pdf");

        RunAndCheck(ws =>
        {
            Assert.Equal("Апервый.pdf", Cell(ws, 1, "Имя файла"));
            Assert.Equal("Бвторой.pdf", Cell(ws, 2, "Имя файла"));
        });
    }

    // ── Наименование ──────────────────────────────────────────────────────────

    /// <summary>Имя изделия подтягивается из соседней спецификации: файл без кода документа дает
    /// название всем документам того же изделия.</summary>
    [Fact]
    public void Product_names_are_taken_from_the_neighbouring_specification()
    {
        Pdf("АБВГ.301234.001 Блок питания.pdf");     // спецификация — источник имени
        Pdf("АБВГ.301234.001СБ.pdf");                // документ без имени в файле

        RunAndCheck(ws =>
        {
            var names = ws.RowsUsed().Skip(1)
                          .Select(r => Cell(ws, r.RowNumber() - 1, "Наименование документа")).ToList();
            Assert.Contains(names, n => n.Contains("Блок питания") && n.Contains("Сборочный чертеж"));
        }, ExportType.Kd);
    }

    [Fact]
    public void Split_name_puts_the_parts_on_separate_lines()
    {
        Pdf("АБВГ.301234.001 Блок питания.pdf");
        Pdf("АБВГ.301234.001СБ.pdf");

        RunAndCheck(ws =>
        {
            var names = ws.RowsUsed().Skip(1)
                          .Select(r => Cell(ws, r.RowNumber() - 1, "Наименование документа")).ToList();
            Assert.Contains(names, n => n.Contains('\n'));
        }, ExportType.Kd, splitName: true);
    }

    // ── Спецификации КД и исполнения ──────────────────────────────────────────

    // Строки ведомости КД по порядку: обозначение, наименование, есть ли заливка «Проверить».
    private static List<(string Designation, string Name, bool Marked)> KdRows(
        IXLWorksheet ws, string nameHeader = "Наименование документа", string desHeader = "Обозначение документа КД")
    {
        var nameCol = ws.Row(1).CellsUsed().First(c => c.GetString() == nameHeader).Address.ColumnNumber;
        return [.. ws.RowsUsed().Skip(1).Select(r => (
            Cell(ws, r.RowNumber() - 1, desHeader),
            Cell(ws, r.RowNumber() - 1, nameHeader),
            r.Cell(nameCol).Style.Fill.PatternType != XLFillPatternValues.None))];
    }

    /// <summary>Спецификация исполнения (…-02) — такой же источник имени, как базовая, но только
    /// для документов своего исполнения. Общий документ …СБ к исполнению −02 не относится: без
    /// базовой спецификации он «Проверить».</summary>
    [Fact]
    public void Execution_specification_names_only_its_own_documents()
    {
        Pdf("АБВГ.301234.001-02 Блок питания.pdf");
        Pdf("АБВГ.301234.001-02СБ.pdf");
        Pdf("АБВГ.301234.001-02СБ-ЛУ.pdf");
        Pdf("АБВГ.301234.001-02Э3.pdf");
        Pdf("АБВГ.301234.001СБ.pdf");

        RunAndCheck(ws => Assert.Equal(
        [
            ("АБВГ.301234.001СБ",       "Проверить", true),
            ("АБВГ.301234.001-02",      "Блок питания", false),
            ("АБВГ.301234.001-02СБ",    "Блок питания Сборочный чертеж", false),
            ("АБВГ.301234.001-02СБ-ЛУ", "Блок питания Сборочный чертеж Лист утверждения", false),
            ("АБВГ.301234.001-02Э3",    "Блок питания Схема электрическая принципиальная", false),
        ], KdRows(ws)), ExportType.Kd, recursive: true);
    }

    /// <summary>У каждого исполнения свое имя: −01 не берет имя базовой (раньше брало), а
    /// название в имени файла документа уступает имени спецификации. Строки идут группами по
    /// исполнениям: базовое, −01, −02.</summary>
    [Fact]
    public void Every_execution_keeps_its_own_name_and_group()
    {
        Pdf("АБВГ.301234.001-02 Блок питания облегченный.pdf");
        Pdf("АБВГ.301234.001-02СБ Сборка.pdf");
        Pdf("АБВГ.301234.001-01Э3.pdf");
        Pdf("АБВГ.301234.001-01СБ.pdf");
        Pdf("АБВГ.301234.001-01 Блок питания резервный.pdf");
        Pdf("АБВГ.301234.001СБ.pdf");
        Pdf("АБВГ.301234.001 Блок питания.pdf");

        RunAndCheck(ws => Assert.Equal(
        [
            ("АБВГ.301234.001",      "Блок питания", false),
            ("АБВГ.301234.001СБ",    "Блок питания Сборочный чертеж", false),
            ("АБВГ.301234.001-01",   "Блок питания резервный", false),
            ("АБВГ.301234.001-01СБ", "Блок питания резервный Сборочный чертеж", false),
            ("АБВГ.301234.001-01Э3", "Блок питания резервный Схема электрическая принципиальная", false),
            ("АБВГ.301234.001-02",   "Блок питания облегченный", false),
            ("АБВГ.301234.001-02СБ", "Блок питания облегченный Сборочный чертеж", false),
        ], KdRows(ws)), ExportType.Kd, recursive: true);
    }

    /// <summary>Нет спецификации своего исполнения — имя не берется ни у базовой, ни из имени
    /// файла документа: в ячейке «Проверить» со светло-красной заливкой.</summary>
    [Fact]
    public void Document_without_its_execution_specification_is_marked_to_check()
    {
        Pdf("АБВГ.301234.001 Блок питания.pdf");
        Pdf("АБВГ.301234.001-03Э3 Блок питания усиленный.pdf");
        Pdf("АБВГ.301234.001-03ПЭ3.pdf");

        RunAndCheck(ws =>
        {
            Assert.Equal(
            [
                ("АБВГ.301234.001",       "Блок питания", false),
                ("АБВГ.301234.001-03Э3",  "Проверить", true),
                ("АБВГ.301234.001-03ПЭ3", "Проверить", true),
            ], KdRows(ws));

            var fill = ws.Cell(3, 2).Style.Fill.BackgroundColor.Color;   // «Наименование документа»
            Assert.Equal("FFFFC7CE", fill.ToArgb().ToString("X8"));
        }, ExportType.Kd, recursive: true);
    }

    /// <summary>Спецификация без названия в имени файла имени изделию не дает: «Проверить» и у нее,
    /// и у документов ее исполнения.</summary>
    [Fact]
    public void Specification_without_a_name_marks_its_execution_to_check()
    {
        Pdf("АБВГ.301234.001-01.pdf");
        Pdf("АБВГ.301234.001-01СБ.pdf");

        RunAndCheck(ws => Assert.Equal(
        [
            ("АБВГ.301234.001-01",   "Проверить", true),
            ("АБВГ.301234.001-01СБ", "Проверить", true),
        ], KdRows(ws)), ExportType.Kd, recursive: true);
    }

    // Опись КД подписывает колонку «Наименование» — заливка ставится и туда.
    [Fact]
    public void Inventory_marks_the_name_column_too()
    {
        Pdf("АБВГ.301234.001СБ Блок питания.pdf");

        RunAndCheck(ws => Assert.Equal(
            [("АБВГ.301234.001СБ", "Проверить", true)],
            KdRows(ws, nameHeader: "Наименование", desHeader: "Обозначение")), ExportType.Inventory);
    }

    // ── Прогресс, отмена и статистика ─────────────────────────────────────────

    [Fact]
    public void Progress_reports_every_file()
    {
        Pdf("1первый.pdf");
        Pdf("2второй.pdf");
        var reported = new List<AnalyzeProgress>();

        Run(progress: new SyncProgress<AnalyzeProgress>(reported.Add));

        Assert.Equal([1, 2], reported.Select(r => r.Current));
        Assert.All(reported, r => Assert.Equal(2, r.Total));
    }

    [Fact]
    public void Cancellation_stops_the_run_and_writes_no_report()
    {
        Pdf("документ.pdf");
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.Throws<OperationCanceledException>(() => Run(ct: cts.Token));

        Assert.False(File.Exists(_report));
    }

    [Fact]
    public void Statistics_text_names_the_export_type()
    {
        Pdf("АБВГ.301234.001СБ Блок питания.pdf");

        var result = Run(ExportType.Kd);

        Assert.Contains(result.SavePath, result.StatsText);
        Assert.Contains("Всего найдено файлов: 1", result.StatsText);
        Assert.Contains("конструкторской документации (КД)", result.StatsText);
    }
}
