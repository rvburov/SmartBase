using SmartBase.Extensions.TimeCollector;

namespace SmartBase.Tests.Extensions.TimeCollector;

/// <summary>
/// Колонки по операциям для выгрузки в коллекцию — чистая логика на готовых строках свода:
/// какие колонки и в каком порядке, куда уходит название, как складывается время и что
/// помечается «отличается» и красным. Чтение операций из настоящих расчетов и списка из
/// «Операции.xlsx» — в <c>TimeCollectorRunTests</c>.
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
        OperationColumns.Build(rows, Catalog, fixText: true, []);

    private static Dictionary<string, double> Cells(OperationLayout layout, int row) =>
        layout.Cells[row].ToDictionary(c => layout.Columns[c.Column], c => c.Value);

    // ── Состав и порядок колонок ──────────────────────────────────────────────

    // Колонки — только встретившиеся операции, в порядке списка; неиспользованных в списке
    // («Сумма операций», «Комплектование») нет — макрос такие колонки прятал.
    [Fact]
    public void Columns_Are_The_Met_Operations_In_List_Order()
    {
        var layout = Build(
            Row("А1", 5, ("Контроль", 1), ("Сборка", 4)),
            Row("А2", 2, ("Пайка", 2)));

        Assert.Equal(["Сборка", "Пайка", "Контроль"], layout.Columns);
        Assert.Empty(layout.Unknown);
    }

    // Название не из списка не теряется: у него своя колонка в конце, а журнал его называет.
    // Такие колонки идут по алфавиту — порядка для них нет, а по алфавиту их легко найти.
    [Fact]
    public void Unknown_Operations_Get_Own_Columns_At_The_End_In_Alphabetical_Order()
    {
        var layout = Build(Row("А1", 9, ("Шлифовка", 1), ("Сборка", 2), ("Бандаж", 3)));

        Assert.Equal(["Сборка", "Бандаж", "Шлифовка"], layout.Columns);
        Assert.Equal(["Бандаж", "Шлифовка"], layout.Unknown);
    }

    // Регистр, лишние пробелы и «е»/«ё» одну операцию на две колонки не делят.
    [Fact]
    public void Case_Spaces_And_Yo_Do_Not_Split_An_Operation()
    {
        var catalog = new OperationCatalog(["Испытания приёмосдаточные"]);
        var layout  = OperationColumns.Build(
            [Row("А1", 3, ("испытания  приемосдаточные ", 1), ("Испытания приёмосдаточные", 2))],
            catalog, fixText: true, []);

        Assert.Equal(["Испытания приёмосдаточные"], layout.Columns);
        Assert.Equal(3, Cells(layout, 0)["Испытания приёмосдаточные"]);
    }

    // Лист «Исправление»: старое название уходит в колонку правильного, а журнал получает
    // пару «было → стало». Правильное, которого нет в списке, — своя колонка в конце.
    [Fact]
    public void Fixes_Send_Old_Names_To_The_Right_Column()
    {
        var catalog = new OperationCatalog(
            ["Сборка", "Упаковывание"],
            [("Упаковка", "Упаковывание"), ("Сварочная", "Сварка")]);

        var layout = OperationColumns.Build(
            [Row("А1", 9, ("Упаковка", 1), ("Упаковывание", 2), ("Сварочная", 4))],
            catalog, fixText: true, []);

        Assert.Equal(["Упаковывание", "Сварка"], layout.Columns);
        Assert.Equal(3, Cells(layout, 0)["Упаковывание"]);
        Assert.Equal(["Сварка"], layout.Unknown);
        Assert.Equal(["Упаковка → Упаковывание", "Сварочная → Сварка"], layout.Fixed);
    }

    // Название из списка исправлением не подменяется: как и в макросе (GetJob), исправление —
    // запасной путь для того, чего в списке нет.
    [Fact]
    public void Name_From_The_List_Is_Not_Fixed()
    {
        var catalog = new OperationCatalog(["Сборка", "Сборочная"], [("Сборочная", "Сборка")]);

        var layout = OperationColumns.Build([Row("А1", 1, ("Сборочная", 1))], catalog, fixText: true, []);

        Assert.Equal(["Сборочная"], layout.Columns);
        Assert.Empty(layout.Fixed);
    }

    // Буква из чужой раскладки («C» латинская) — та же галочка, что для наименований: с ней
    // «Cборка» находит свою колонку, без нее остается отдельной.
    [Fact]
    public void Foreign_Layout_Letter_Is_Fixed_Only_With_The_Checkbox()
    {
        TimeRow[] rows = [Row("А1", 1, ("Cборка", 1))];

        Assert.Equal(["Сборка"], OperationColumns.Build(rows, Catalog, fixText: true, []).Columns);
        Assert.Equal(["Cборка"], OperationColumns.Build(rows, Catalog, fixText: false, []).Unknown);
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
        Assert.DoesNotContain(true, layout.Differs);
    }

    [Fact]
    public void Row_Without_Operations_Has_No_Cells()
    {
        var layout = Build(Row("А1", 1, ("Сборка", 1)), Row("А2", 2));

        Assert.Empty(layout.Cells[1]);
    }

    // ── «отличается» и красные ячейки ─────────────────────────────────────────

    // Трудоемкость одна, а разбивка разная — обе строки «отличается» (OperationsColor макроса),
    // и краснеют ячейки тех операций, что разошлись: разное время или операция только у одной
    // строки. Совпавшая операция не краснеет.
    [Fact]
    public void Same_Norm_With_Different_Breakdown_Is_Marked_And_Only_Diverging_Cells_Turn_Red()
    {
        var layout = Build(
            Row("А1", 12, "2025-03-14", ("Сборка", 10), ("Контроль", 2)),
            Row("А1", 12, "2025-01-09", ("Сборка", 8), ("Пайка", 2), ("Контроль", 2)));

        Assert.Equal([true, true], layout.Differs);

        var red = layout.RedCells.Select(c => (c.Row, layout.Columns[c.Column])).ToHashSet();
        Assert.Equal(
            new HashSet<(int, string)> { (0, "Сборка"), (1, "Сборка"), (0, "Пайка"), (1, "Пайка") },
            red);
    }

    // Трудоемкость разная — «отличается» не ставится (разная трудоемкость видна и так, ее ячейка
    // красная), но ячейки разошедшихся операций краснеют: раскраска операций от трудоемкости
    // не зависит.
    [Fact]
    public void Different_Norm_Paints_Cells_But_Is_Not_Marked()
    {
        var layout = Build(
            Row("А1", 12, "2025-03-14", ("Сборка", 12)),
            Row("А1", 10, "2025-01-09", ("Сборка", 10)));

        Assert.Equal([false, false], layout.Differs);
        Assert.Equal(2, layout.RedCells.Count);
    }

    // Сравниваются только соседи с ОДНИМ обозначением — как в раскраске «Тр-ть».
    [Fact]
    public void Different_Designations_Are_Not_Compared()
    {
        var layout = Build(Row("А1", 1, ("Сборка", 1)), Row("А2", 1, ("Сборка", 5)));

        Assert.Empty(layout.RedCells);
        Assert.DoesNotContain(true, layout.Differs);
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

    // В настоящем списке «Сборочная» встречается трижды, а у «Сборочно-монтажная   » хвост
    // пробелов: колонки из этого — одна и без хвоста.
    [Fact]
    public void Catalog_Drops_Repeats_And_Extra_Spaces()
    {
        var catalog = new OperationCatalog(["Сборочная", "  Сборочно-монтажная   ", "Сборочная", "", "Сборочная"]);

        Assert.Equal(["Сборочная", "Сборочно-монтажная"], catalog.Names);
        Assert.Equal(1, catalog.IndexOf("сборочно-монтажная"));
        Assert.Equal(-1, catalog.IndexOf("Сборка"));
    }
}
