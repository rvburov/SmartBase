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
/// <param name="Columns">Названия колонок по порядку: сначала операции из списка в его порядке,
/// потом не найденные в нем — по алфавиту.</param>
/// <param name="Cells">Время операций у каждой строки свода (индекс — строка свода), по
/// возрастанию номера колонки. Пустой массив — у изделия нет собственных операций.</param>
/// <param name="Differs">Пометка «отличается» в колонке «Операции».</param>
/// <param name="RedCells">Ячейки операций с расхождением: строка свода и номер колонки.</param>
/// <param name="Unknown">Операции, которых нет в списке (у них свои колонки в конце).</param>
/// <param name="Fixed">Исправления по листу «Исправление», по одному на название: «было → стало».</param>
/// <param name="TextValues">Расчеты, где время операции записано не числом: «файл — строк: N».</param>
public sealed record OperationLayout(
    IReadOnlyList<string> Columns,
    IReadOnlyList<OpCell[]> Cells,
    bool[] Differs,
    IReadOnlyList<(int Row, int Column)> RedCells,
    IReadOnlyList<string> Unknown,
    IReadOnlyList<string> Fixed,
    IReadOnlyList<string> TextValues);

/// <summary>
/// Колонки по операциям для выгрузки в коллекцию — порт закомментированной части макроса
/// <c>TimeCollector</c>: <c>GetProducts</c> складывал операции под изделием в колонки после
/// «Ссылки», <c>SetHeaderData</c> подписывал их названиями из листа «Операции», а
/// <c>OperationsColor</c> ставил «отличается».
///
/// Правила — как в расчете и в макросе:
/// <list type="bullet">
/// <item>у изделия только СОБСТВЕННЫЕ операции (строки под ним до следующего изделия) —
/// операции составных частей в его колонки не входят;</item>
/// <item>значение — время из колонки G, на одну штуку изделия; одна операция дважды у одного
/// изделия — значения складываются;</item>
/// <item>название ищется в списке, не нашлось — в листе «Исправление»; не нашлось и там —
/// у операции своя колонка в конце, а журнал ее называет.</item>
/// </list>
///
/// Раскраска — по образцу ячейки «Тр-ть», но без заливки строк (решение пользователя от
/// 22.09.2026): у соседних строк одного обозначения краснеют ячейки тех операций, время которых
/// разное или которые есть только у одной строки. Красный цвет строки по-прежнему значит одно —
/// разную трудоемкость при тех же проекте и дате.
/// </summary>
public static class OperationColumns
{
    /// <summary>Пометка в колонке «Операции»: трудоемкость у соседей одинаковая, а разбивка по
    /// операциям — нет.</summary>
    public const string DiffersText = "отличается";

    /// <summary>Собрать колонки по строкам свода в их КОНЕЧНОМ порядке: пометки и раскраска
    /// сравнивают соседей, поэтому звать после сортировки.</summary>
    /// <param name="fixText">Приводить ли названия к одному алфавиту перед поиском в списке —
    /// та же галочка, что для наименований и обозначений.</param>
    /// <param name="textValues">Расчеты с нечисловым временем операции — собраны при чтении,
    /// здесь только передаются в итог.</param>
    public static OperationLayout Build(IReadOnlyList<TimeRow> rows, OperationCatalog catalog,
                                        bool fixText, IReadOnlyList<string> textValues)
    {
        var targets = new Targets(catalog, fixText);

        // Название из ячейки → цель. Разных названий на весь свод — сотни, а строк операций —
        // десятки миллионов, поэтому решение принимается один раз на название.
        var byName = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var row in rows)
            foreach (var op in row.Ops ?? [])
                if (!byName.ContainsKey(op.Name))
                    byName[op.Name] = targets.Resolve(op.Name);

        var (columns, columnOf) = targets.Order();

        var cells = new OpCell[rows.Count][];
        for (var i = 0; i < rows.Count; i++)
            cells[i] = RowCells(rows[i].Ops, byName, columnOf);

        var (differs, red) = Compare(rows, cells);

        return new OperationLayout(columns, cells, differs, red,
                                   targets.Unknown(), targets.Fixed, textValues);
    }

    // Операции строки → ячейки по колонкам: сумма по колонке (одна операция дважды, или старое
    // и новое название, сведенные исправлением), по возрастанию номера колонки.
    private static OpCell[] RowCells(OpEntry[]? ops, Dictionary<string, int> byName, int[] columnOf)
    {
        if (ops is null || ops.Length == 0) return [];

        var sums = new SortedDictionary<int, double>();
        foreach (var op in ops)
        {
            var column = columnOf[byName[op.Name]];
            sums[column] = sums.TryGetValue(column, out var sum) ? sum + op.Value : op.Value;
        }

        var result = new OpCell[sums.Count];
        var k = 0;
        // Сложение возвращает шум двоичного представления, который читатель уже снял с каждого
        // слагаемого: без повторного округления 2,1 + 4 сравнивались бы с 6,1 как разные числа.
        foreach (var (column, sum) in sums)
            result[k++] = new OpCell(column, CalcFileReader.WithoutBinaryNoise(sum));
        return result;
    }

    /// <summary>
    /// Соседи с одинаковым обозначением (равенство побайтовое, как у раскраски
    /// <see cref="TimeCollectorEngine.PlanColors"/>): у операций с разным временем или
    /// с операцией только у одной строки краснеют ячейки обеих строк; если при этом
    /// трудоемкость одинаковая — обе строки получают «отличается» (<c>OperationsColor</c>
    /// макроса звался ровно при равной трудоемкости).
    /// </summary>
    private static (bool[] Differs, List<(int, int)> Red) Compare(IReadOnlyList<TimeRow> rows, OpCell[][] cells)
    {
        var differs = new bool[rows.Count];
        var red     = new List<(int, int)>();
        var marked  = new HashSet<(int, int)>();
        var diff    = new List<int>();

        for (var i = 1; i < rows.Count; i++)
        {
            var deno = rows[i].Deno;
            if (deno.Length == 0 || !string.Equals(deno, rows[i - 1].Deno, StringComparison.Ordinal))
                continue;

            DiffColumns(cells[i - 1], cells[i], diff);
            if (diff.Count == 0) continue;

            if (Norm(rows[i]) == Norm(rows[i - 1]))
                differs[i] = differs[i - 1] = true;

            foreach (var column in diff)
            {
                if (marked.Add((i - 1, column))) red.Add((i - 1, column));
                if (marked.Add((i, column)))     red.Add((i, column));
            }
        }

        return (differs, red);
    }

    // Та же трактовка, что у PlanColors: нечисловая трудоемкость идет в сравнение нулем.
    private static double Norm(TimeRow row) => row.Norm ?? 0;

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

    // ── Куда ведет название ───────────────────────────────────────────────────

    /// <summary>Цели названий — будущие колонки. Цель из списка помнит место в нем, остальные
    /// сводятся по ключу сравнения (<see cref="OperationCatalog.Key"/>), чтобы «Упаковка» и
    /// «упаковка » из двух расчетов не стали двумя колонками.</summary>
    private sealed class Targets(OperationCatalog catalog, bool fixText)
    {
        private readonly List<(string Name, int ListIndex)> _all   = [];
        private readonly Dictionary<int, int>               _known = new();
        private readonly Dictionary<string, int>            _other = new(StringComparer.Ordinal);
        private readonly HashSet<string>                    _fixed = new(StringComparer.Ordinal);

        public List<string> Fixed { get; } = [];

        public int Resolve(string raw)
        {
            var name  = fixText ? Homoglyphs.Normalize(raw).Text : raw;
            var index = catalog.IndexOf(name);
            if (index >= 0) return Known(index);

            if (catalog.Fix(name) is { } target)
            {
                var note = $"{OperationCatalog.Clean(name)} → {target}";
                if (_fixed.Add(note)) Fixed.Add(note);

                index = catalog.IndexOf(target);
                return index >= 0 ? Known(index) : Other(target);
            }

            return Other(OperationCatalog.Clean(name));
        }

        private int Known(int index)
        {
            if (_known.TryGetValue(index, out var id)) return id;
            _all.Add((catalog.Names[index], index));
            return _known[index] = _all.Count - 1;
        }

        private int Other(string name)
        {
            var key = OperationCatalog.Key(name);
            if (_other.TryGetValue(key, out var id)) return id;
            _all.Add((name, -1));
            return _other[key] = _all.Count - 1;
        }

        /// <summary>Колонки по порядку и номер колонки каждой цели: из списка — в его порядке,
        /// остальные — после них, по алфавиту.</summary>
        public (List<string> Columns, int[] ColumnOf) Order()
        {
            var order = Enumerable.Range(0, _all.Count)
                .OrderBy(id => _all[id].ListIndex < 0 ? 1 : 0)
                .ThenBy(id => _all[id].ListIndex)
                .ThenBy(id => _all[id].Name, StringComparer.CurrentCultureIgnoreCase)
                .ToList();

            var columnOf = new int[_all.Count];
            for (var column = 0; column < order.Count; column++) columnOf[order[column]] = column;
            return (order.Select(id => _all[id].Name).ToList(), columnOf);
        }

        public List<string> Unknown() =>
            _all.Where(t => t.ListIndex < 0)
                .Select(t => t.Name)
                .OrderBy(n => n, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
    }
}
