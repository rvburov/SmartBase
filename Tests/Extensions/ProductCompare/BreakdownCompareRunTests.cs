using ClosedXML.Excel;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using DocumentFormat.OpenXml.Validation;
using SmartBase.Extensions.ProductCompare;

namespace SmartBase.Tests.Extensions.ProductCompare;

/// <summary>
/// Сквозной прогон режима «расшифровка»: в папке лежат настоящие книги расшифровок, расширение
/// пишет общий список входимостей в копию шаблона, результат читается обратно. Здесь видно то,
/// чего не видно на отдельных функциях: порядок блоков по датам, сопоставление изделий по
/// входимости КД внутри родителя, операции своего изделия, непрерывная структура последней
/// даты и исторический хвост, повтор шапки и сохранность обвязки шаблона — кнопки и макросов.
/// </summary>
public sealed class BreakdownCompareRunTests : IDisposable
{
    private const int First = 6;           // первая строка данных результата
    private const int Step  = 10;          // ширина блока даты вместе с пустым столбцом

    // Столбцы блока (1-based внутри блока).
    private const int ColIndex = 2, ColName = 3, ColDeno = 4, ColQty = 6, ColNorm = 7, ColComment = 9;

    private readonly string _root = Path.Combine(Path.GetTempPath(), $"sbext_pcb_{Guid.NewGuid():N}");
    private readonly string _template;
    private readonly string _folder;
    private readonly string _out;

    public BreakdownCompareRunTests()
    {
        _template = Path.Combine(_root, "шаблон.xlsm");
        _folder   = Path.Combine(_root, "Расшифровки");
        _out      = Path.Combine(_root, "Результат");
        Directory.CreateDirectory(_folder);
        Directory.CreateDirectory(_out);
        BreakdownStand.WriteTemplate(_template);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
        catch { /* best-effort */ }
    }

    private void Breakdown(string date, params BRow[] rows) =>
        BreakdownStand.WriteBreakdown(Path.Combine(_folder, $"Изделие АБВГ.000001.001_{date}_Иванова.xlsm"), rows);

    private BreakdownResult Run(CancellationToken ct = default) =>
        BreakdownCompareEngine.Run(_template, _folder, _out, null, null, ct);

    private BreakdownResult RunAndCheck(Action<IXLWorksheet> check)
    {
        var result = Run();
        using var wb = new XLWorkbook(result.OutputPath);
        check(wb.Worksheets.First());
        return result;
    }

    private static int Col(int block, int column) => block * Step + column;

    // ── Порядок дат и заголовки ───────────────────────────────────────────────

    [Fact]
    public void Blocks_follow_the_dates_and_carry_file_names()
    {
        Breakdown("2026.06.16", new BRow("Изделие", "Комплекс", "АБВГ.000"));
        Breakdown("2026.01.05", new BRow("Изделие", "Комплекс", "АБВГ.000"));
        Breakdown("2026.03.20", new BRow("Изделие", "Комплекс", "АБВГ.000"));

        var result = RunAndCheck(ws =>
        {
            // Заголовок шаблона — A1:I1: имя файла в левой клетке объединения каждого блока.
            Assert.Equal("Изделие АБВГ.000001.001_2026.01.05_Иванова", ws.Text(1, Col(0, 1)));
            Assert.Equal("Изделие АБВГ.000001.001_2026.03.20_Иванова", ws.Text(1, Col(1, 1)));
            Assert.Equal("Изделие АБВГ.000001.001_2026.06.16_Иванова", ws.Text(1, Col(2, 1)));
        });

        Assert.Equal([new DateTime(2026, 1, 5), new DateTime(2026, 3, 20), new DateTime(2026, 6, 16)],
                     result.Files.Select(f => f.Date));
        Assert.StartsWith("Расшифровка сравнения ", Path.GetFileName(result.OutputPath));
        Assert.EndsWith(".xlsm", result.OutputPath);
    }

    // ── Сопоставление — по КД внутри родительского узла ───────────────────────

    [Fact]
    public void Drawing_numbers_keep_products_aligned_when_an_insert_changes_the_indices()
    {
        Breakdown("2026.01.01",
            new BRow("Изделие", "Комплекс", "АБВГ.000", 1),
            new BRow("1", "Плата", "АБВГ.001", 2),
            new BRow("2", "Блок",  "АБВГ.002", 1));
        Breakdown("2026.02.01",
            new BRow("Изделие", "Комплекс", "АБВГ.000", 1),
            new BRow("1", "Новый модуль", "АБВГ.009", 1),
            new BRow("2", "Плата", "АБВГ.001", 3),
            new BRow("3", "Блок",  "АБВГ.002", 1));

        var result = RunAndCheck(ws =>
        {
            Assert.Equal("Изделие", ws.Text(First, Col(0, ColIndex)));
            Assert.Equal("Комплекс", ws.Text(First, Col(1, ColName)));
            Assert.Equal("", ws.Text(First + 1, Col(0, ColName)));
            Assert.Equal("Новый модуль", ws.Text(First + 1, Col(1, ColName)));

            Assert.Equal("Плата", ws.Text(First + 2, Col(0, ColName)));
            Assert.Equal("Плата", ws.Text(First + 2, Col(1, ColName)));
            Assert.Equal("1", ws.Text(First + 2, Col(0, ColIndex)));
            Assert.Equal("2", ws.Text(First + 2, Col(1, ColIndex)));
            Assert.Equal(2.0, ws.Cell(First + 2, Col(0, ColQty)).GetDouble());
            Assert.Equal(3.0, ws.Cell(First + 2, Col(1, ColQty)).GetDouble());

            Assert.Equal("Блок", ws.Text(First + 3, Col(0, ColName)));
            Assert.Equal("Блок", ws.Text(First + 3, Col(1, ColName)));
            Assert.Equal("2", ws.Text(First + 3, Col(0, ColIndex)));
            Assert.Equal("3", ws.Text(First + 3, Col(1, ColIndex)));
        });

        Assert.Equal(4, result.TotalRows);
        Assert.Equal(4, result.Products);
    }

    [Fact]
    public void Removed_product_follows_the_current_tree_without_breaking_its_rows()
    {
        Breakdown("2026.01.01",
            new BRow("Изделие", "Комплекс", "АБВГ.000"),
            new BRow("1", "Плата",  "АБВГ.001"),
            new BRow("2", "Кабель", "АБВГ.002"),
            new BRow("3", "Блок",   "АБВГ.003"));
        Breakdown("2026.02.01",
            new BRow("Изделие", "Комплекс", "АБВГ.000"),
            new BRow("1", "Плата",  "АБВГ.001"),
            new BRow("2", "Блок",   "АБВГ.003"));

        var result = RunAndCheck(ws =>
        {
            Assert.Equal("Плата", ws.Text(First + 1, Col(1, ColName)));
            Assert.Equal("Блок", ws.Text(First + 2, Col(0, ColName)));
            Assert.Equal("Блок", ws.Text(First + 2, Col(1, ColName)));
            Assert.Equal("3", ws.Text(First + 2, Col(0, ColIndex)));
            Assert.Equal("2", ws.Text(First + 2, Col(1, ColIndex)));
            Assert.Equal("Кабель", ws.Text(First + 3, Col(0, ColName)));
            Assert.All(Enumerable.Range(1, 9), c => Assert.Equal("", ws.Text(First + 3, Col(1, c))));
        });
        Assert.Equal(4, result.TotalRows);
        Assert.Equal(4, result.Products);
    }

    // Порядок последнего файла сохраняется даже при перестановке корневых узлов.
    // Разные записи индекса влияют только на чтение дерева, а не на ключ изделия.
    [Fact]
    public void Latest_tree_order_and_comma_indices_preserve_the_drawing_number_matches()
    {
        Breakdown("2026.01.01",
            new BRow("Изделие", "Комплекс", "АБВГ.000"),
            new BRow("1.", "Шкаф", "АБВГ.100"),
            new BRow("1.2", "Плата 1.2", "АБВГ.012"),
            new BRow("1.10", "Плата 1.10", "АБВГ.110"),
            new BRow("2", "Блок", "АБВГ.200"));
        Breakdown("2026.02.01",
            new BRow("Изделие", "Комплекс", "АБВГ.000"),
            new BRow("02", "Блок", "АБВГ.200"),
            new BRow("1", "Шкаф", "АБВГ.100"),
            new BRow("1,2", "Плата 1.2", "АБВГ.012"),
            new BRow("1.9", "Плата 1.9", "АБВГ.109"),
            new BRow("1.10", "Плата 1.10", "АБВГ.110"));

        var result = RunAndCheck(ws =>
        {
            string[] expected = ["Комплекс", "Блок", "Шкаф", "Плата 1.2", "Плата 1.9", "Плата 1.10"];
            for (var r = 0; r < expected.Length; r++)
                Assert.Equal(expected[r], ws.Text(First + r, Col(1, ColName)));

            Assert.Equal("Блок", ws.Text(First + 1, Col(0, ColName)));
            Assert.Equal("Шкаф", ws.Text(First + 2, Col(0, ColName)));
            Assert.Equal("Плата 1.2", ws.Text(First + 3, Col(0, ColName)));
            Assert.Equal("", ws.Text(First + 4, Col(0, ColName)));
            Assert.Equal("Плата 1.10", ws.Text(First + 5, Col(0, ColName)));
            Assert.Equal("02", ws.Text(First + 1, Col(1, ColIndex)));
            Assert.Equal("1.", ws.Text(First + 2, Col(0, ColIndex)));
            Assert.Equal("1,2", ws.Text(First + 3, Col(1, ColIndex)));
        });
        Assert.Equal(6, result.TotalRows);
    }

    [Fact]
    public void Repeated_index_does_not_merge_distinct_drawing_numbers()
    {
        Breakdown("2026.01.01",
            new BRow("Изделие", "Комплекс", "АБВГ.000"),
            new BRow("1", "Первая", "АБВГ.001"),
            new BRow("1", "Вторая", "АБВГ.002"));
        Breakdown("2026.02.01",
            new BRow("Изделие", "Комплекс", "АБВГ.000"),
            new BRow("1", "Первая", "АБВГ.001"));

        var result = RunAndCheck(ws =>
        {
            Assert.Equal("Первая", ws.Text(First + 1, Col(0, ColName)));
            Assert.Equal("Первая", ws.Text(First + 1, Col(1, ColName)));
            Assert.Equal("Вторая", ws.Text(First + 2, Col(0, ColName)));
            Assert.Equal("", ws.Text(First + 2, Col(1, ColName)));
        });
        Assert.Equal(3, result.TotalRows);
    }

    [Fact]
    public void Repeated_drawing_number_in_one_parent_matches_each_occurrence_separately()
    {
        Breakdown("2026.01.01",
            new BRow("Изделие", "Комплекс", "АБВГ.000"),
            new BRow("1", "Первая плата", "АБВГ.001", 2, Comment: "первая старая"),
            new BRow("2", "Вторая плата", "АБВГ.001", 4, Comment: "вторая старая"));
        Breakdown("2026.02.01",
            new BRow("Изделие", "Комплекс", "АБВГ.000"),
            new BRow("7", "Плата с новым именем", "АБВГ.001", 3, Comment: "первая новая"),
            new BRow("9", "Другое имя платы", "АБВГ.001", 5, Comment: "вторая новая"));

        var result = RunAndCheck(ws =>
        {
            Assert.Equal("первая старая", ws.Text(First + 1, Col(0, ColComment)));
            Assert.Equal("первая новая", ws.Text(First + 1, Col(1, ColComment)));
            Assert.Equal("вторая старая", ws.Text(First + 2, Col(0, ColComment)));
            Assert.Equal("вторая новая", ws.Text(First + 2, Col(1, ColComment)));
            Assert.Equal("1", ws.Text(First + 1, Col(0, ColIndex)));
            Assert.Equal("7", ws.Text(First + 1, Col(1, ColIndex)));
            Assert.Equal(4.0, ws.Cell(First + 2, Col(0, ColQty)).GetDouble());
            Assert.Equal(5.0, ws.Cell(First + 2, Col(1, ColQty)).GetDouble());
        });
        Assert.Equal(3, result.Products);
    }

    [Fact]
    public void Equal_drawing_numbers_in_different_parents_do_not_cross_match_after_reordering()
    {
        Breakdown("2026.01.01",
            new BRow("Изделие", "Комплекс", "АБВГ.000"),
            new BRow("1", "Узел А", "АБВГ.100"),
            new BRow("1.1", "Деталь А", "АБВГ.001", Comment: "из узла А"),
            new BRow("2", "Узел Б", "АБВГ.200"),
            new BRow("2.1", "Деталь Б", "АБВГ.001", Comment: "из узла Б"));
        Breakdown("2026.02.01",
            new BRow("Изделие", "Комплекс", "АБВГ.000"),
            new BRow("1", "Узел Б", "АБВГ.200"),
            new BRow("1.1", "Деталь Б новая", "АБВГ.001"),
            new BRow("2", "Узел А", "АБВГ.100"),
            new BRow("2.1", "Деталь А новая", "АБВГ.001"));

        var result = RunAndCheck(ws =>
        {
            Assert.Equal("Узел Б", ws.Text(First + 1, Col(0, ColName)));
            Assert.Equal("Узел Б", ws.Text(First + 1, Col(1, ColName)));
            Assert.Equal("из узла Б", ws.Text(First + 2, Col(0, ColComment)));
            Assert.Equal("Деталь Б новая", ws.Text(First + 2, Col(1, ColName)));
            Assert.Equal("из узла А", ws.Text(First + 4, Col(0, ColComment)));
            Assert.Equal("Деталь А новая", ws.Text(First + 4, Col(1, ColName)));
        });
        Assert.Equal(5, result.Products);
    }

    [Fact]
    public void Moving_a_drawing_number_to_another_parent_keeps_old_and_new_occurrences_separate()
    {
        Breakdown("2026.01.01",
            new BRow("Изделие", "Комплекс", "АБВГ.000"),
            new BRow("1", "Узел А", "АБВГ.100"),
            new BRow("1.1", "Деталь", "АБВГ.001"),
            new BRow("2", "Узел Б", "АБВГ.200"));
        Breakdown("2026.02.01",
            new BRow("Изделие", "Комплекс", "АБВГ.000"),
            new BRow("1", "Узел А", "АБВГ.100"),
            new BRow("2", "Узел Б", "АБВГ.200"),
            new BRow("2.1", "Деталь", "АБВГ.001"));

        var result = RunAndCheck(ws =>
        {
            Assert.Equal("Узел А", ws.Text(First + 1, Col(1, ColName)));
            Assert.Equal("Узел Б", ws.Text(First + 2, Col(1, ColName)));
            Assert.Equal("", ws.Text(First + 3, Col(0, ColIndex)));
            Assert.Equal("2.1", ws.Text(First + 3, Col(1, ColIndex)));
            Assert.Equal("1.1", ws.Text(First + 4, Col(0, ColIndex)));
            Assert.All(Enumerable.Range(1, 9), c => Assert.Equal("", ws.Text(First + 4, Col(1, c))));
        });
        Assert.Equal(5, result.Products);
        Assert.Equal(5, result.TotalRows);
    }

    [Fact]
    public void Missing_drawing_number_uses_the_normalized_name_without_rewriting_source_cells()
    {
        Breakdown("2026.01.01",
            new BRow("Изделие", "Комплекс", "АБВГ.000"),
            new BRow("1", "  Деталь без КД  "));
        Breakdown("2026.02.01",
            new BRow("Изделие", "Комплекс", "АБВГ.000"),
            new BRow("9", "Деталь  без КД"));

        var result = RunAndCheck(ws =>
        {
            Assert.Equal("  Деталь без КД  ", ws.Text(First + 1, Col(0, ColName)));
            Assert.Equal("Деталь  без КД", ws.Text(First + 1, Col(1, ColName)));
            Assert.Equal("1", ws.Text(First + 1, Col(0, ColIndex)));
            Assert.Equal("9", ws.Text(First + 1, Col(1, ColIndex)));
            Assert.Equal("", ws.Text(First + 1, Col(0, ColDeno)));
            Assert.Equal("", ws.Text(First + 1, Col(1, ColDeno)));
        });
        Assert.Equal(2, result.Products);
    }

    [Fact]
    public void Rows_without_drawing_number_and_name_are_not_guessed_to_be_the_same_product()
    {
        Breakdown("2026.01.01",
            new BRow("Изделие", "Комплекс", "АБВГ.000"),
            new BRow("1", ""));
        Breakdown("2026.02.01",
            new BRow("Изделие", "Комплекс", "АБВГ.000"),
            new BRow("1", ""));

        var result = RunAndCheck(ws =>
        {
            Assert.Equal("", ws.Text(First + 1, Col(0, ColIndex)));
            Assert.Equal("1", ws.Text(First + 1, Col(1, ColIndex)));
            Assert.Equal("1", ws.Text(First + 2, Col(0, ColIndex)));
            Assert.Equal("", ws.Text(First + 2, Col(1, ColIndex)));
        });
        Assert.Equal(3, result.Products);
        Assert.Equal(3, result.TotalRows);
    }

    [Fact]
    public void Three_dates_keep_the_latest_tree_contiguous_and_preserve_historical_values_in_the_tail()
    {
        Breakdown("2026.01.01",
            new BRow("Изделие", "Комплекс", "АБВГ.000"),
            new BRow("1", "Узел", "АБВГ.100"),
            new BRow("1.1", "Деталь", "АБВГ.001"),
            new BRow("2", "Удаленный блок", "АБВГ.200", 2));
        Breakdown("2026.02.01",
            new BRow("Изделие", "Комплекс", "АБВГ.000"),
            new BRow("1", "Удаленный блок", "АБВГ.200", 3),
            new BRow("2", "Узел", "АБВГ.100"),
            new BRow("2.1", "Деталь", "АБВГ.001"));
        Breakdown("2026.03.01",
            new BRow("Изделие", "Комплекс", "АБВГ.000"),
            new BRow("1", "Новый блок", "АБВГ.900"),
            new BRow("2", "Узел", "АБВГ.100"),
            new BRow("2.1", "Деталь", "АБВГ.001"));

        var result = RunAndCheck(ws =>
        {
            string[] current = ["Комплекс", "Новый блок", "Узел", "Деталь"];
            for (var r = 0; r < current.Length; r++)
                Assert.Equal(current[r], ws.Text(First + r, Col(2, ColName)));
            Assert.Equal("Деталь", ws.Text(First + 3, Col(0, ColName)));
            Assert.Equal("Деталь", ws.Text(First + 3, Col(1, ColName)));
            Assert.Equal("Удаленный блок", ws.Text(First + 4, Col(0, ColName)));
            Assert.Equal("Удаленный блок", ws.Text(First + 4, Col(1, ColName)));
            Assert.Equal(2.0, ws.Cell(First + 4, Col(0, ColQty)).GetDouble());
            Assert.Equal(3.0, ws.Cell(First + 4, Col(1, ColQty)).GetDouble());
            Assert.All(Enumerable.Range(1, 9), c => Assert.Equal("", ws.Text(First + 4, Col(2, c))));
        });
        Assert.Equal(5, result.Products);
        Assert.Equal(5, result.TotalRows);
    }

    // ── Операции ──────────────────────────────────────────────────────────────

    [Fact]
    public void Operations_travel_with_their_product_and_repeats_match_in_order()
    {
        Breakdown("2026.01.01",
            new BRow("Изделие", "Комплекс", "АБВГ.000"),
            new BRow("", "Подготовка", "Подготовка составных частей", Norm: 1.5),
            new BRow("", "Сборка",     "Сборка и монтаж", Norm: 2.75),
            new BRow("", "Подготовка", "Подготовка составных частей", Norm: 0.65, Comment: "вторая"),
            new BRow("1", "Плата", "АБВГ.001"),
            new BRow("", "Контроль", "Мониторинг", Norm: 0.5));
        Breakdown("2026.02.01",
            new BRow("Изделие", "Комплекс", "АБВГ.000"),
            new BRow("", "Подготовка", "Подготовка составных частей", Norm: 1.0),
            new BRow("", "Подготовка", "Подготовка составных частей", Norm: 0.7),
            new BRow("1", "Плата", "АБВГ.001"),
            new BRow("", "Контроль", "Мониторинг", Norm: 0.5),
            new BRow("", "Упаковывание", "Подготовка составных частей", Norm: 0.3));

        var result = RunAndCheck(ws =>
        {
            Assert.Equal("Подготовка", ws.Text(First + 1, Col(0, ColName)));
            Assert.Equal(1.5, ws.Cell(First + 1, Col(0, ColNorm)).GetDouble());
            Assert.Equal(1.0, ws.Cell(First + 1, Col(1, ColNorm)).GetDouble());

            // Вторая подготовка — ко второй.
            Assert.Equal("вторая", ws.Text(First + 2, Col(0, ColComment)));
            Assert.Equal(0.7, ws.Cell(First + 2, Col(1, ColNorm)).GetDouble());
            Assert.Equal("Подготовка составных частей", ws.Text(First + 2, Col(1, ColDeno)));

            // Операции платы — под платой, а не под комплексом.
            Assert.Equal("Плата",        ws.Text(First + 3, Col(1, ColName)));
            Assert.Equal("Контроль",     ws.Text(First + 4, Col(0, ColName)));
            Assert.Equal("Контроль",     ws.Text(First + 4, Col(1, ColName)));
            Assert.Equal("Упаковывание", ws.Text(First + 5, Col(1, ColName)));
            Assert.Equal("",             ws.Text(First + 5, Col(0, ColName)));

            // Удаленная операция идет после всей актуальной структуры, не разрывая ее группы.
            Assert.Equal("Сборка", ws.Text(First + 6, Col(0, ColName)));
            Assert.All(Enumerable.Range(1, 9), c => Assert.Equal("", ws.Text(First + 6, Col(1, c))));
        });

        Assert.Equal(7, result.TotalRows);
        Assert.Equal(2, result.Products);
        Assert.Equal(5, result.Operations);
    }

    [Fact]
    public void Operation_matches_follow_the_drawing_number_when_an_insert_changes_the_product_index()
    {
        Breakdown("2026.01.01",
            new BRow("Изделие", "Комплекс", "АБВГ.000"),
            new BRow("1", "Плата", "АБВГ.001"),
            new BRow("", "Контроль", "Мониторинг", Norm: 0.5));
        Breakdown("2026.02.01",
            new BRow("Изделие", "Комплекс", "АБВГ.000"),
            new BRow("1", "Новый модуль", "АБВГ.009"),
            new BRow("", "Контроль", "Мониторинг", Norm: 9),
            new BRow("2", "Плата", "АБВГ.001"),
            new BRow("", "Контроль", "Мониторинг", Norm: 0.7));

        var result = RunAndCheck(ws =>
        {
            Assert.Equal("Новый модуль", ws.Text(First + 1, Col(1, ColName)));
            Assert.Equal("", ws.Text(First + 2, Col(0, ColName)));
            Assert.Equal(9.0, ws.Cell(First + 2, Col(1, ColNorm)).GetDouble());
            Assert.Equal("Плата", ws.Text(First + 3, Col(0, ColName)));
            Assert.Equal("Плата", ws.Text(First + 3, Col(1, ColName)));
            Assert.Equal(0.5, ws.Cell(First + 4, Col(0, ColNorm)).GetDouble());
            Assert.Equal(0.7, ws.Cell(First + 4, Col(1, ColNorm)).GetDouble());
        });
        Assert.Equal(3, result.Products);
        Assert.Equal(2, result.Operations);
        Assert.Equal(5, result.TotalRows);
    }

    [Fact]
    public void Removed_product_and_its_operations_remain_together_in_the_historical_tail()
    {
        Breakdown("2026.01.01",
            new BRow("Изделие", "Комплекс", "АБВГ.000"),
            new BRow("1", "Удаленная плата", "АБВГ.001"),
            new BRow("", "Старая операция", "Сборка", Norm: 2.75));
        Breakdown("2026.02.01",
            new BRow("Изделие", "Комплекс", "АБВГ.000"),
            new BRow("1", "Новый модуль", "АБВГ.009"));

        var result = RunAndCheck(ws =>
        {
            Assert.Equal("Новый модуль", ws.Text(First + 1, Col(1, ColName)));
            Assert.Equal("Удаленная плата", ws.Text(First + 2, Col(0, ColName)));
            Assert.Equal("Старая операция", ws.Text(First + 3, Col(0, ColName)));
            Assert.Equal(2.75, ws.Cell(First + 3, Col(0, ColNorm)).GetDouble());
            for (var r = First + 2; r <= First + 3; r++)
                Assert.All(Enumerable.Range(1, 9), c => Assert.Equal("", ws.Text(r, Col(1, c))));
        });
        Assert.Equal(3, result.Products);
        Assert.Equal(1, result.Operations);
        Assert.Equal(4, result.TotalRows);
    }

    // ── Оформление ────────────────────────────────────────────────────────────

    [Fact]
    public void Source_formatting_and_numbers_are_carried_over()
    {
        Breakdown("2026.01.01",
            new BRow("Изделие", "Комплекс", "АБВГ.000", 1, 92.97),
            new BRow("", "Сборка", "Сборка и монтаж", Norm: 6.97));
        Breakdown("2026.02.01",
            new BRow("Изделие", "Комплекс", "АБВГ.000", 1, 101.27));

        RunAndCheck(ws =>
        {
            Assert.Equal(BreakdownStand.ProductFill, ws.Fill(First, Col(0, ColName)));
            Assert.Equal(BreakdownStand.ProductFill, ws.Fill(First, Col(1, ColName)));
            Assert.True(ws.Cell(First, Col(1, ColName)).Style.Font.Bold);
            Assert.Null(ws.Fill(First + 1, Col(0, ColName)));

            Assert.Equal(XLDataType.Number, ws.Cell(First, Col(0, ColNorm)).DataType);
            Assert.Equal(92.97,  ws.Cell(First, Col(0, ColNorm)).GetDouble());
            Assert.Equal(101.27, ws.Cell(First, Col(1, ColNorm)).GetDouble());
            Assert.Equal(30, ws.Row(First).Height);

            // База (столбец I источника) не переносится, комментарий — в девятый столбец блока.
            Assert.DoesNotContain(Enumerable.Range(1, 9), c => ws.Text(First, c) == "база не переносится");

            // Клетка даты, где строки не было, оформлена как пустая строка данных шаблона.
            Assert.Equal(XLBorderStyleValues.Thin, ws.Cell(First + 1, Col(1, ColName)).Style.Border.TopBorder);
            Assert.Equal("", ws.Text(First + 1, Col(1, ColName)));
        });
    }

    // ── Шаблон ────────────────────────────────────────────────────────────────

    [Fact]
    public void Header_with_the_merged_title_is_repeated_over_every_block()
    {
        Breakdown("2026.01.01", new BRow("Изделие", "Комплекс", "АБВГ.000"));
        Breakdown("2026.02.01", new BRow("Изделие", "Комплекс", "АБВГ.000"));
        Breakdown("2026.03.01", new BRow("Изделие", "Комплекс", "АБВГ.000"));

        RunAndCheck(ws =>
        {
            var merged = ws.MergedRanges.Select(r => r.RangeAddress.ToString()).ToHashSet();
            Assert.Contains("A1:I1", merged);
            Assert.Contains("K1:S1", merged);
            Assert.Contains("U1:AC1", merged);
            Assert.Contains("K2:K4", merged);
            Assert.Contains("AC2:AC4", merged);

            // Текст заголовка, оставшийся в шаблоне, не уцелел ни над одним блоком.
            for (var c = 1; c <= Col(2, 9); c++)
                Assert.NotEqual(BreakdownStand.TitleText, ws.Text(1, c));

            for (var block = 1; block < 3; block++)
            {
                Assert.Equal("Уровень",               ws.Text(2, Col(block, 1)));
                Assert.Equal("Уровень идентификатор", ws.Text(2, Col(block, 2)));
                Assert.Equal("Комментарий",           ws.Text(2, Col(block, 9)));
                Assert.True(ws.Cell(2, Col(block, 3)).Style.Font.Bold);
                Assert.Equal(ws.Column(1).Width,  ws.Column(Col(block, 1)).Width);
                Assert.Equal(ws.Column(3).Width,  ws.Column(Col(block, 3)).Width);
                Assert.Equal(ws.Column(10).Width, ws.Column(Col(block, 10)).Width);
            }

            Assert.Equal("A5:AC6", ws.AutoFilter.Range.RangeAddress.ToString());
        });
    }

    // Прежняя раскладка шаблона (заголовок C1:I1 рядом с кнопкой) тоже работает: имя файла —
    // в левую клетку того объединения, которое накрывает C1.
    [Fact]
    public void Title_goes_into_C1_when_the_template_merges_C1_to_I1()
    {
        BreakdownStand.WriteTemplate(_template, titleFromC: true);
        Breakdown("2026.01.01", new BRow("Изделие", "Комплекс", "АБВГ.000"));
        Breakdown("2026.02.01", new BRow("Изделие", "Комплекс", "АБВГ.000"));

        RunAndCheck(ws =>
        {
            Assert.Equal("Изделие АБВГ.000001.001_2026.01.01_Иванова", ws.Text(1, Col(0, 3)));
            Assert.Equal("Изделие АБВГ.000001.001_2026.02.01_Иванова", ws.Text(1, Col(1, 3)));
            Assert.Contains("M1:S1", ws.MergedRanges.Select(r => r.RangeAddress.ToString()));
        });
    }

    [Fact]
    public void Shapes_macros_and_other_sheets_survive()
    {
        Breakdown("2026.01.01", new BRow("Изделие", "Комплекс", "АБВГ.000"));
        Breakdown("2026.02.01", new BRow("Изделие", "Комплекс", "АБВГ.000"));

        var result = Run();

        using var doc  = SpreadsheetDocument.Open(result.OutputPath, false);
        var book = doc.WorkbookPart!;
        Assert.Equal(SpreadsheetDocumentType.MacroEnabledWorkbook, doc.DocumentType);

        using (var vba = book.VbaProjectPart!.GetStream())
        using (var copy = new MemoryStream())
        {
            vba.CopyTo(copy);
            Assert.Equal(BreakdownStand.VbaBytes, copy.ToArray());
        }

        var sheets = book.Workbook.Sheets!.Elements<Sheet>().ToList();
        Assert.Equal(["Лист1", "Операции"], sheets.Select(s => s.Name!.Value));

        var part   = (WorksheetPart)book.GetPartById(sheets[0].Id!);
        var legacy = part.Worksheet.GetFirstChild<LegacyDrawing>();
        Assert.NotNull(legacy);
        var vml = (VmlDrawingPart)part.GetPartById(legacy!.Id!);
        using (var reader = new StreamReader(vml.GetStream()))
            Assert.Equal(BreakdownStand.ButtonVml, reader.ReadToEnd());

        // Книга обязана открываться в Excel без «восстановления»: ошибок схемы быть не должно
        // (шаблон стенда сам по себе схеме соответствует).
        var errors = new OpenXmlValidator().Validate(doc).Select(e => $"{e.Path?.XPath}: {e.Description}");
        Assert.Empty(errors);
    }

    [Fact]
    public void Template_itself_is_not_changed()
    {
        Breakdown("2026.01.01", new BRow("Изделие", "Комплекс", "АБВГ.000"));
        Breakdown("2026.02.01", new BRow("Изделие", "Комплекс", "АБВГ.000"));
        var before = File.ReadAllBytes(_template);

        Run();

        Assert.Equal(before, File.ReadAllBytes(_template));
    }

    [Fact]
    public void Result_saved_into_the_source_folder_is_skipped_next_time()
    {
        Breakdown("2026.01.01", new BRow("Изделие", "Комплекс", "АБВГ.000"));
        Breakdown("2026.02.01", new BRow("Изделие", "Комплекс", "АБВГ.000"));

        var first  = BreakdownCompareEngine.Run(_template, _folder, _folder, null, null, default);
        Thread.Sleep(1100);                               // имя результата — с точностью до секунды
        var second = BreakdownCompareEngine.Run(_template, _folder, _folder, null, null, default);

        Assert.Equal(2, second.Files.Count);
        Assert.NotEqual(first.OutputPath, second.OutputPath);
    }

    [Fact]
    public void Cancellation_stops_the_run()
    {
        Breakdown("2026.01.01", new BRow("Изделие", "Комплекс", "АБВГ.000"));
        Breakdown("2026.02.01", new BRow("Изделие", "Комплекс", "АБВГ.000"));
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.ThrowsAny<OperationCanceledException>(() => Run(cts.Token));
        Assert.Empty(Directory.GetFiles(_out));
    }
}
