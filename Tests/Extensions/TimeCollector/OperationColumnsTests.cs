using SmartBase.Extensions.TimeCollector;

namespace SmartBase.Tests.Extensions.TimeCollector;

/// <summary>
/// Колонки по операциям для выгрузки в коллекцию — чистая логика на готовых строках свода:
/// какие колонки и в каком порядке, куда уходит название, как складывается время и какие ячейки
/// краснеют. Чтение операций из настоящих расчетов и списка из «Операции.xlsx» — в
/// <c>TimeCollectorRunTests</c>.
///
/// Главное правило, которое здесь закреплено: колонки дает ТОЛЬКО список операций, а сверка
/// названий строгая. Послабления в сравнении делали ровно обратное тому, ради чего список
/// заводился: на живом своде вышло 517 колонок при 167 названиях списка.
/// </summary>
public sealed class OperationColumnsTests
{
    private static readonly OperationCatalog Catalog =
        new(["Сумма операций", "Комплектование", "Сборка", "Пайка", "Контроль"]);

    private static TimeRow Row(string deno, double? norm, params (string Name, double Value)[] ops) =>
        Row(deno, norm, "2025-03-14", ops);

    private static TimeRow Row(string deno, double? norm, string date, params (string Name, double Value)[] ops) =>
        new(RNum: 1, Name: "изделие", Deno: deno, Norm: norm, NormText: "",
            Date: DateTime.Parse(date), Efio: "Иванов", Proj: "П", SourcePath: @"C:\р.xlsm",
            Ops: ops.Select(o => new OpEntry(o.Name, o.Value)).ToArray());

    private static OperationLayout Build(params TimeRow[] rows) =>
        OperationColumns.Build(rows, Catalog, []);

    private static Dictionary<string, double> Cells(OperationLayout layout, int row) =>
        layout.Cells[row].ToDictionary(c => layout.Columns[c.Column], c => c.Value);

    // ── Состав и порядок колонок ──────────────────────────────────────────────

    // Колонки — только встретившиеся операции списка, в порядке списка; неиспользованных
    // («Комплектование») нет — макрос такие колонки прятал.
    [Fact]
    public void Columns_Are_The_Met_Operations_In_List_Order()
    {
        var layout = Build(
            Row("А1", 5, ("Контроль", 1), ("Сборка", 4)),
            Row("А2", 2, ("Пайка", 2)));

        Assert.Equal(["Сборка", "Пайка", "Контроль"], layout.Columns);
        Assert.Empty(layout.Unknown);
    }

    // Названия не из списка колонок НЕ заводят: значение не выгружается, а журнал называет
    // его и считает строки — видно, что дописать в список или поправить в расчете.
    [Fact]
    public void Unknown_Operations_Get_No_Column_And_Are_Named_With_Counts()
    {
        var layout = Build(
            Row("А1", 9, ("Шлифовка", 1), ("Сборка", 2), ("Бандаж", 3)),
            Row("А2", 1, ("Шлифовка", 4)));

        Assert.Equal(["Сборка"], layout.Columns);
        Assert.Equal(["Бандаж — строк: 1", "Шлифовка — строк: 2"], layout.Unknown);
        Assert.Equal(new Dictionary<string, double> { ["Сборка"] = 2 }, Cells(layout, 0));
        Assert.Empty(layout.Cells[1]);
    }

    // Сверка строгая: регистр, пробелы внутри и «е»/«ё» — это РАЗНЫЕ названия. Иначе название,
    // набранное иначе, считалось бы «почти тем же» и тащило в таблицу лишнее.
    [Theory]
    [InlineData("сборка")]                 // другой регистр
    [InlineData("Сбор  ка")]               // лишний пробел внутри
    [InlineData("Cборка")]                 // латинская «C» — буква из чужой раскладки
    [InlineData("Контроль функциональный")] // в списке только «Контроль»
    public void Comparison_With_The_List_Is_Strict(string written)
    {
        var layout = Build(Row("А1", 1, (written, 1)));

        Assert.Empty(layout.Columns);
        Assert.Equal([$"{written} — строк: 1"], layout.Unknown);
    }

    // Пробелы по краям прощаются с обеих сторон: их не видно ни в расчете, ни в списке —
    // в самом списке есть «Сборочно-монтажная   » с хвостом пробелов.
    [Fact]
    public void Edge_Spaces_Are_Forgiven_On_Both_Sides()
    {
        var catalog = new OperationCatalog(["  Сборочно-монтажная   "]);

        var layout = OperationColumns.Build([Row("А1", 3, ("Сборочно-монтажная ", 3))], catalog, []);

        Assert.Equal(["Сборочно-монтажная"], layout.Columns);
        Assert.Equal(3, Cells(layout, 0)["Сборочно-монтажная"]);
    }

    // Лист «Исправление»: старое название уходит в колонку правильного, а журнал получает пару
    // «было → стало». Правильное название, которого нет в списке, не спасает: значение не
    // выгружается, а журнал называет исходное название.
    [Fact]
    public void Fixes_Send_Old_Names_To_The_Right_Column()
    {
        var catalog = new OperationCatalog(
            ["Сборка", "Упаковывание"],
            [("Упаковка", "Упаковывание"), ("Сварочная", "Сварка")]);

        var layout = OperationColumns.Build(
            [Row("А1", 9, ("Упаковка", 1), ("Упаковывание", 2), ("Сварочная", 4))],
            catalog, []);

        Assert.Equal(["Упаковывание"], layout.Columns);
        Assert.Equal(3, Cells(layout, 0)["Упаковывание"]);
        Assert.Equal(["Упаковка → Упаковывание"], layout.Fixed);
        Assert.Equal(["Сварочная — строк: 1"], layout.Unknown);   // «Сварки» в списке нет
    }

    // Название из списка исправлением не подменяется: как и в макросе (GetJob), исправление —
    // запасной путь для того, чего в списке нет.
    [Fact]
    public void Name_From_The_List_Is_Not_Fixed()
    {
        var catalog = new OperationCatalog(["Сборка", "Сборочная"], [("Сборочная", "Сборка")]);

        var layout = OperationColumns.Build([Row("А1", 1, ("Сборочная", 1))], catalog, []);

        Assert.Equal(["Сборочная"], layout.Columns);
        Assert.Empty(layout.Fixed);
    }

    // «Сумма операций» — операция списка, но колонки у нее нет намеренно: строка задает время
    // одной суммой, без разбивки. Журнал считает такие строки, чтобы пропажа не была молчаливой.
    [Fact]
    public void Sum_Of_Operations_Gets_No_Column()
    {
        var layout = Build(
            Row("А1", 5, ("Сумма операций", 5)),
            Row("А2", 4, ("Сборка", 4), ("Сумма операций", 9)));

        Assert.Equal(["Сборка"], layout.Columns);
        Assert.Equal(2, layout.SumRows);
        Assert.Empty(layout.Cells[0]);
        Assert.Equal(new Dictionary<string, double> { ["Сборка"] = 4 }, Cells(layout, 1));
    }

    // ── Значения ──────────────────────────────────────────────────────────────

    // Одна операция дважды у одного изделия — время складывается (так делал GetProducts).
    // Сумма снова без шума двоичного представления: 0,1 + 0,2 — это 0,3, а не
    // 0,30000000000000004, иначе соседу с 0,3 досталась бы ложная красная ячейка.
    [Fact]
    public void Repeated_Operation_Is_Summed_Without_Binary_Noise()
    {
        var layout = Build(
            Row("А1", 1, "2025-03-14", ("Сборка", 0.1), ("Сборка", 0.2)),
            Row("А1", 1, "2025-01-09", ("Сборка", 0.3)));

        Assert.Equal(0.3, Cells(layout, 0)["Сборка"]);
        Assert.Empty(layout.RedCells);
    }

    [Fact]
    public void Row_Without_Operations_Has_No_Cells()
    {
        var layout = Build(Row("А1", 1, ("Сборка", 1)), Row("А2", 2));

        Assert.Empty(layout.Cells[1]);
    }

    // ── Красные ячейки ────────────────────────────────────────────────────────

    // У соседей с одним обозначением краснеют ячейки тех операций, что разошлись: разное время
    // или операция только у одной строки. Совпавшая операция не краснеет.
    [Fact]
    public void Only_Diverging_Cells_Turn_Red()
    {
        var layout = Build(
            Row("А1", 12, "2025-03-14", ("Сборка", 10), ("Контроль", 2)),
            Row("А1", 12, "2025-01-09", ("Сборка", 8), ("Пайка", 2), ("Контроль", 2)));

        var red = layout.RedCells.Select(c => (c.Row, layout.Columns[c.Column])).ToHashSet();
        Assert.Equal(
            new HashSet<(int, string)> { (0, "Сборка"), (1, "Сборка"), (0, "Пайка"), (1, "Пайка") },
            red);
    }

    // Раскраска операций от трудоемкости не зависит: разная трудоемкость видна своей красной
    // ячейкой, а разошедшиеся операции — своими.
    [Fact]
    public void Different_Norm_Still_Paints_Diverging_Cells()
    {
        var layout = Build(
            Row("А1", 12, "2025-03-14", ("Сборка", 12)),
            Row("А1", 10, "2025-01-09", ("Сборка", 10)));

        Assert.Equal(2, layout.RedCells.Count);
    }

    // Сравниваются только соседи с ОДНИМ обозначением — как в раскраске «Тр-ть».
    [Fact]
    public void Different_Designations_Are_Not_Compared()
    {
        var layout = Build(Row("А1", 1, ("Сборка", 1)), Row("А2", 1, ("Сборка", 5)));

        Assert.Empty(layout.RedCells);
    }

    // Средняя строка расходится с обеими соседками по одной и той же операции — ее ячейка
    // в списке красных одна, а не две.
    [Fact]
    public void Cell_Diverging_With_Both_Neighbours_Is_Listed_Once()
    {
        var layout = Build(
            Row("А1", 1, "2025-03-14", ("Сборка", 1)),
            Row("А1", 1, "2025-02-14", ("Сборка", 2)),
            Row("А1", 1, "2025-01-14", ("Сборка", 3)));

        Assert.Equal(3, layout.RedCells.Count);
        Assert.Equal(layout.RedCells.Count, layout.RedCells.Distinct().Count());
    }

    // ── Список операций ───────────────────────────────────────────────────────

    // В настоящем списке «Сборочная» встречается трижды: колонка из этого одна, и значения
    // всех таких строк идут в нее. Сколько названий в списке — дело файла, их дописывают.
    [Fact]
    public void Catalog_Drops_Repeats_And_Edge_Spaces()
    {
        var catalog = new OperationCatalog(["Сборочная", "  Сборочно-монтажная   ", "Сборочная", "", "Сборочная"]);

        Assert.Equal(["Сборочная", "Сборочно-монтажная"], catalog.Names);
        Assert.Equal(0, catalog.IndexOf("Сборочная"));
        Assert.Equal(-1, catalog.IndexOf("сборочная"));      // строго: регистр значим
        Assert.Equal(-1, catalog.IndexOf("Сборка"));

        var layout = OperationColumns.Build(
            [Row("А1", 3, ("Сборочная", 1), ("Сборочная", 2))], catalog, []);

        Assert.Equal(["Сборочная"], layout.Columns);
        Assert.Equal(3, layout.Cells[0].Single().Value);
    }
}
