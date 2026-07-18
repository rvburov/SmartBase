using ExcelDataReader;

namespace SmartBase.Core.Importers.TableSources;

/// <summary>
/// «Все листы в одну коллекцию»: столбцы сопоставляются по итоговому имени заголовка (после
/// дедупликации в пределах листа — «Без названия» разных листов схлопывается в общий столбец),
/// объединение — в порядке первого появления по порядку листов; у листов без какого-то столбца
/// значения пустые. Подпись «Название» — с первого непустого листа. Столбец названия, строки
/// заголовков/данных и исключения (листы/строки/столбцы) одни на все листы (параметры ручного
/// режима). Полностью пустые и исключённые листы пропускаются; если не осталось ни одного —
/// ошибка. Скрытые листы импортируются наравне с видимыми.
/// </summary>
public sealed class ExcelMergedSource(string path, int headerRow, int dataRow, int titleCol,
    RangeSet excludeRows, RangeSet excludeCols, HashSet<string> excludeSheets) : ITableSource
{
    // План листа после прохода 1: индекс листа у ридера, граница строк и перекладка
    // «локальная колонка → индекс union-столбца».
    private sealed record SheetPlan(int Index, long LastRow, List<int> FieldCols, int[] UnionIdx);

    private readonly List<SheetPlan> _sheets = [];
    private int _unionCount;

    public ImportSchema Analyze(IProgress<double>? progress, CancellationToken ct)
    {
        using var fs = ImportErrors.OpenRead(path);
        using var reader = ExcelTableSource.CreateReader(fs);

        var fwd        = SourceHelpers.ForwardOnly(progress);
        var unionNames = new List<string>();
        var unionIndex = new Dictionary<string, int>(StringComparer.Ordinal);
        string? titleHeader = null;
        long rowCount = 0, emptyTitles = 0;
        var sheetIdx = 0;

        _sheets.Clear();
        do
        {
            ct.ThrowIfCancellationRequested();
            if (excludeSheets.Contains(reader.Name?.Trim() ?? "")) { sheetIdx++; continue; }

            var stat = ExcelTableSource.ScanSheet(reader, headerRow, dataRow, titleCol,
                                                  excludeRows, excludeCols, fs, fwd, ct);
            if (stat.LastRow == 0) { sheetIdx++; continue; }   // пустой лист пропускаем

            var fieldCols = ExcelTableSource.FieldCols(stat.FirstCol, stat.LastCol, titleCol, excludeCols);
            var nameByCol = ExcelTableSource.NamesFor(stat.Headers, titleCol, fieldCols);
            titleHeader ??= nameByCol[titleCol];

            var map = new int[fieldCols.Count];
            for (var i = 0; i < fieldCols.Count; i++)
            {
                var name = nameByCol[fieldCols[i]];
                if (!unionIndex.TryGetValue(name, out var idx))
                {
                    idx = unionNames.Count;
                    unionIndex[name] = idx;
                    unionNames.Add(name);
                }
                map[i] = idx;
            }

            _sheets.Add(new SheetPlan(sheetIdx, stat.LastRow, fieldCols, map));
            rowCount    += stat.DataRows;
            emptyTitles += stat.DataRows == 0 ? 0 : stat.EmptyTitles;
            sheetIdx++;
        } while (reader.NextResult());

        if (_sheets.Count == 0)
            throw new ImportException("Все листы пустые или исключены — нет данных для импорта.");

        _unionCount = unionNames.Count;
        return new ImportSchema(titleHeader, unionNames, rowCount, rowCount == 0 ? 0 : emptyTitles);
    }

    public IEnumerable<ImportRow> ReadRows(CancellationToken ct)
    {
        using var fs = ImportErrors.OpenRead(path);
        using var reader = ExcelTableSource.CreateReader(fs);

        var idx = 0;   // индекс листа, на котором стоит ридер
        foreach (var sheet in _sheets)
        {
            while (idx < sheet.Index)
            {
                if (!reader.NextResult()) yield break;   // книга изменилась между проходами
                idx++;
            }

            long row = 0;
            while (reader.Read())
            {
                ct.ThrowIfCancellationRequested();
                row++;
                if (row < dataRow) continue;
                if (row > sheet.LastRow) break;
                if (excludeRows.Contains(row)) continue;

                var fc = reader.FieldCount;
                string Val(int col) => col <= fc ? SourceHelpers.FormatExcelValue(reader.GetValue(col - 1)) : "";

                var vals = new string[_unionCount];
                Array.Fill(vals, "");
                for (var i = 0; i < sheet.FieldCols.Count; i++)
                    vals[sheet.UnionIdx[i]] = Val(sheet.FieldCols[i]);

                yield return new ImportRow(Val(titleCol), vals);
            }
        }
    }
}
