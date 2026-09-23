namespace SmartBase.Extensions.TimeCollector;

/// <summary>Операция изделия в том виде, в каком ее прочитали из расчета: название из колонки C
/// строки операции и время из G — на одну штуку изделия.</summary>
public readonly record struct OpEntry(string Name, double Value);

/// <summary>Время операции в строке свода: номер колонки операции (в
/// <see cref="OperationLayout.Columns"/>) и значение.</summary>
public readonly record struct OpCell(int Column, double Value);

/// <summary>
/// Колонки по операциям — все, что нужно приемнику, чтобы их записать.
/// </summary>
/// <param name="Columns">Названия колонок в порядке листа «Операции»: только те операции списка,
/// которые встретились в расчетах.</param>
/// <param name="Cells">Время операций у каждой строки свода (индекс — строка свода), по
/// возрастанию номера колонки. Пустой массив — у изделия нет собственных операций.</param>
/// <param name="RedCells">Ячейки операций с расхождением: строка свода и номер колонки.</param>
/// <param name="Unknown">Названия, которых нет в списке, с числом строк: «название — строк: N».
/// В коллекцию такие значения не попадают.</param>
/// <param name="Fixed">Исправления по листу «Исправление», по одному на название: «было → стало».</param>
/// <param name="SumRows">Строки операции «Сумма операций»: колонки у нее нет намеренно.</param>
/// <param name="TextValues">Расчеты, где время операции записано не числом: «файл — строк: N».</param>
public sealed record OperationLayout(
    IReadOnlyList<string> Columns,
    IReadOnlyList<OpCell[]> Cells,
    IReadOnlyList<(int Row, int Column)> RedCells,
    IReadOnlyList<string> Unknown,
    IReadOnlyList<string> Fixed,
    int SumRows,
    IReadOnlyList<string> TextValues);

/// <summary>
/// Колонки по операциям для выгрузки в коллекцию — порт закомментированной части макроса
/// <c>TimeCollector</c>: <c>GetProducts</c> складывал операции под изделием в колонки после
/// «Ссылки», а <c>SetHeaderData</c> подписывал их названиями из листа «Операции».
///
/// Правила — как в расчете и в макросе:
/// <list type="bullet">
/// <item>у изделия только СОБСТВЕННЫЕ операции (строки под ним до следующего изделия) —
/// операции составных частей в его колонки не входят;</item>
/// <item>значение — время из колонки G, на одну штуку изделия; одна операция дважды у одного
/// изделия — значения складываются;</item>
/// <item>колонки дает ТОЛЬКО список операций: название сверяется с ним строго (см.
/// <see cref="OperationCatalog.Clean"/>), не нашлось — ищется в листе «Исправление», и если
/// правильное название есть в списке, значение уходит в его колонку. Не подошло ни то, ни
/// другое — значение в коллекцию не идет, а журнал называет операцию и число строк.</item>
/// </list>
///
/// Раскраска — по образцу ячейки «Тр-ть», но без заливки строк: у соседних строк одного
/// обозначения краснеют ячейки тех операций, время которых разное или которые есть только
/// у одной строки. Красный цвет строки по-прежнему значит одно — разную трудоемкость при тех
/// же проекте и дате.
///
/// Решения пользователя от 23.09.2026: строгое сравнение и отказ вместо колонки на каждое
/// незнакомое название (на живом своде их набралось столько, что колонок вышло 517 вместо
/// 167 названий списка), колонки «Операции» с пометкой «отличается» нет вовсе, колонка
/// «Сумма операций» не заводится.
/// </summary>
public static class OperationColumns
{
    /// <summary>Операция, которой колонки нет намеренно: ее строка задает время одной суммой,
    /// без разбивки, и в таблице разбивки ей не место. В макросе она же была особым случаем
    /// (<c>SumOperations</c> считала ее отдельно от вида работ).</summary>
    public const string SumOperation = "Сумма операций";

    // Решение по названию: место в списке либо отказ.
    private const int NotInList = -1;
    private const int SumSkip   = -2;

    /// <summary>Собрать колонки по строкам свода в их КОНЕЧНОМ порядке: раскраска сравнивает
    /// соседей, поэтому звать после сортировки.</summary>
    /// <param name="textValues">Расчеты с нечисловым временем операции — собраны при чтении,
    /// здесь только передаются в итог.</param>
    public static OperationLayout Build(IReadOnlyList<TimeRow> rows, OperationCatalog catalog,
                                        IReadOnlyList<string> textValues)
    {
        var fixes = new List<string>();

        // Решение принимается один раз на НАЗВАНИЕ: разных названий на весь свод — сотни,
        // а строк операций — десятки миллионов.
        var decided = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var row in rows)
            foreach (var op in row.Ops ?? [])
                if (!decided.ContainsKey(op.Name))
                    decided[op.Name] = Decide(op.Name, catalog, fixes);

        var (columns, columnOf) = Order(decided, catalog);

        var dropped = new Dictionary<string, int>(StringComparer.Ordinal);
        var sumRows = 0;
        var cells   = new OpCell[rows.Count][];
        for (var i = 0; i < rows.Count; i++)
            cells[i] = RowCells(rows[i].Ops, decided, columnOf, dropped, ref sumRows);

        return new OperationLayout(columns, cells, Compare(rows, cells),
                                   Unknown(dropped), fixes, sumRows, textValues);
    }

    // Куда ведет название: список → лист «Исправление» → отказ. Исправление спрашивается
    // только про название, которого в списке нет, — как GetJob в макросе.
    private static int Decide(string raw, OperationCatalog catalog, List<string> fixes)
    {
        var name = OperationCatalog.Clean(raw);
        if (name == SumOperation) return SumSkip;

        var index = catalog.IndexOf(name);
        if (index >= 0) return index;

        if (catalog.Fix(name) is not { } target) return NotInList;

        var fixedIndex = target == SumOperation ? SumSkip : catalog.IndexOf(target);
        if (fixedIndex == NotInList) return NotInList;   // правильного названия нет в списке

        fixes.Add($"{name} → {target}");
        return fixedIndex;
    }

    // Колонки — операции списка, которые встретились, в порядке списка. columnOf переводит
    // место в списке в номер колонки.
    private static (List<string> Columns, Dictionary<int, int> ColumnOf) Order(
        Dictionary<string, int> decided, OperationCatalog catalog)
    {
        var used = decided.Values.Where(i => i >= 0).Distinct().OrderBy(i => i).ToList();

        var columns  = new List<string>(used.Count);
        var columnOf = new Dictionary<int, int>(used.Count);
        foreach (var index in used)
        {
            columnOf[index] = columns.Count;
            columns.Add(catalog.Names[index]);
        }
        return (columns, columnOf);
    }

    // Операции строки → ячейки по колонкам: сумма по колонке (одна операция дважды либо старое
    // и новое название, сведенные исправлением), по возрастанию номера колонки. Что в колонку
    // не легло — считается поименно для журнала.
    private static OpCell[] RowCells(OpEntry[]? ops, Dictionary<string, int> decided,
                                     Dictionary<int, int> columnOf,
                                     Dictionary<string, int> dropped, ref int sumRows)
    {
        if (ops is null || ops.Length == 0) return [];

        var sums = new SortedDictionary<int, double>();
        foreach (var op in ops)
        {
            var index = decided[op.Name];
            if (index == SumSkip)   { sumRows++; continue; }
            if (index == NotInList)
            {
                var name = OperationCatalog.Clean(op.Name);
                dropped[name] = dropped.TryGetValue(name, out var count) ? count + 1 : 1;
                continue;
            }

            var column = columnOf[index];
            sums[column] = sums.TryGetValue(column, out var sum) ? sum + op.Value : op.Value;
        }

        if (sums.Count == 0) return [];

        var result = new OpCell[sums.Count];
        var k = 0;
        // Сложение возвращает шум двоичного представления, который читатель уже снял с каждого
        // слагаемого: без повторного округления 2,1 + 4 сравнивались бы с 6,1 как разные числа.
        foreach (var (column, sum) in sums)
            result[k++] = new OpCell(column, CalcFileReader.WithoutBinaryNoise(sum));
        return result;
    }

    private static List<string> Unknown(Dictionary<string, int> dropped) =>
        dropped.OrderBy(p => p.Key, StringComparer.CurrentCultureIgnoreCase)
               .Select(p => $"{p.Key} — строк: {p.Value}")
               .ToList();

    /// <summary>
    /// Соседи с одинаковым обозначением (равенство побайтовое, как у раскраски
    /// <see cref="TimeCollectorEngine.PlanColors"/>): у операций с разным временем или
    /// с операцией только у одной строки краснеют ячейки обеих строк.
    /// </summary>
    private static List<(int, int)> Compare(IReadOnlyList<TimeRow> rows, OpCell[][] cells)
    {
        var red    = new List<(int, int)>();
        var marked = new HashSet<(int, int)>();
        var diff   = new List<int>();

        for (var i = 1; i < rows.Count; i++)
        {
            var deno = rows[i].Deno;
            if (deno.Length == 0 || !string.Equals(deno, rows[i - 1].Deno, StringComparison.Ordinal))
                continue;

            DiffColumns(cells[i - 1], cells[i], diff);
            foreach (var column in diff)
            {
                if (marked.Add((i - 1, column))) red.Add((i - 1, column));
                if (marked.Add((i, column)))     red.Add((i, column));
            }
        }

        return red;
    }

    // Колонки, где две строки расходятся: разное время или операция только у одной.
    // Обе последовательности упорядочены по колонке — хватает одного прохода слиянием.
    private static void DiffColumns(OpCell[] a, OpCell[] b, List<int> diff)
    {
        diff.Clear();
        int i = 0, j = 0;
        while (i < a.Length || j < b.Length)
        {
            if (j == b.Length || (i < a.Length && a[i].Column < b[j].Column)) { diff.Add(a[i++].Column); continue; }
            if (i == a.Length || b[j].Column < a[i].Column)                   { diff.Add(b[j++].Column); continue; }

            if (a[i].Value != b[j].Value) diff.Add(a[i].Column);
            i++; j++;
        }
    }
}
