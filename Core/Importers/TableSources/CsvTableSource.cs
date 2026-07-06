using System.Text;

namespace DataBase.Core.Importers.TableSources;

/// <summary>
/// CSV-источник (потоковый, RFC 4180: кавычки экранируют разделитель и перенос строки, "" → ").
/// Кодировка (UTF-8 / Windows-1251 / UTF-16 по BOM) и разделитель («,»/«;»/таб) определяются
/// автоматически (<see cref="SourceHelpers.SniffCsv"/>). Проход 1 считает записи и реальную
/// ширину: последний столбец — самый правый непустой хоть в одной строке (Excel при экспорте
/// набивает хвост пустых полей — без обрезки это тысячи лишних «Без названия»-столбцов), хвостовые
/// полностью пустые строки отбрасываются. Проход 2 переоткрывает файл и отдаёт записи потоком.
/// </summary>
public sealed class CsvTableSource(string path, int headerRow, int dataRow, int titleCol) : ITableSource
{
    private Encoding? _enc;
    private char      _delim;
    private long      _lastRow;         // последняя непустая строка (1-based)
    private List<int> _fieldCols = [];

    public ImportSchema Analyze(IProgress<double>? progress, CancellationToken ct)
    {
        (_enc, _delim) = SourceHelpers.SniffCsv(path);

        long row = 0, lastRow = 0, emptyTitles = 0, emptySnapshot = 0;
        var lastCol = 0;
        string[]? headers = null;

        foreach (var rec in ReadRecords(progress, ct))
        {
            row++;

            var rightmost = 0;
            for (var c = rec.Length; c >= 1; c--)
                if (!string.IsNullOrWhiteSpace(rec[c - 1])) { rightmost = c; break; }

            if (row == headerRow) headers = rec;
            if (row >= dataRow && string.IsNullOrWhiteSpace(Cell(rec, titleCol))) emptyTitles++;

            if (rightmost > 0)
            {
                lastRow = row;
                if (rightmost > lastCol) lastCol = rightmost;
                emptySnapshot = emptyTitles;   // счётчик пустых названий в границах непустого хвоста
            }
        }

        if (lastRow == 0) throw new ImportException("Файл пустой — нет данных для импорта.");

        _lastRow   = lastRow;
        _fieldCols = [];
        for (var c = 1; c <= lastCol; c++)
            if (c != titleCol)
                _fieldCols.Add(c);

        string Header(int col) => headers is not null ? Cell(headers, col).Trim() : "";
        var named = new List<(int Col, string Raw)> { (titleCol, Header(titleCol)) };
        named.AddRange(_fieldCols.Select(c => (c, Header(c))));
        named.Sort((a, b) => a.Col.CompareTo(b.Col));
        var nameByCol = SourceHelpers.AssignUniqueNames(named);

        var rowCount = Math.Max(0, lastRow - dataRow + 1);
        return new ImportSchema(nameByCol[titleCol],
                                _fieldCols.Select(c => nameByCol[c]).ToList(),
                                rowCount,
                                rowCount == 0 ? 0 : emptySnapshot);
    }

    public IEnumerable<ImportRow> ReadRows(CancellationToken ct)
    {
        long row = 0;
        foreach (var rec in ReadRecords(null, ct))
        {
            row++;
            if (row < dataRow) continue;
            if (row > _lastRow) yield break;

            var vals = new string[_fieldCols.Count];
            for (var i = 0; i < _fieldCols.Count; i++) vals[i] = Cell(rec, _fieldCols[i]);
            yield return new ImportRow(Cell(rec, titleCol), vals);
        }
    }

    private static string Cell(string[] rec, int col) =>
        col >= 1 && col <= rec.Length ? rec[col - 1] ?? "" : "";

    // Потоковый RFC-4180-токенизатор: чтение чанками, состояние (кавычки, CR, «кавычка внутри
    // кавычек») переживает границы чанков — файл целиком в память не поднимается.
    private IEnumerable<string[]> ReadRecords(IProgress<double>? progress, CancellationToken ct)
    {
        using var fs = ImportErrors.OpenRead(path);
        var total = Math.Max(1L, fs.Length);
        using var reader = new StreamReader(fs, _enc!, detectEncodingFromByteOrderMarks: true, bufferSize: 1 << 16);

        var buf       = new char[1 << 16];
        var field     = new StringBuilder();
        var row       = new List<string>();
        var inQuotes  = false;
        var quoteSeen = false;   // внутри кавычек встретили " — ждём следующий символ ("" → ")
        var skipLf    = false;   // после \r следующий \n — часть того же перевода строки
        var lastPct   = -1;

        void EndField() { row.Add(field.ToString()); field.Clear(); }
        string[] FinishRow()
        {
            EndField();
            var arr = row.ToArray();
            row.Clear();
            return arr;
        }

        int n;
        while ((n = reader.Read(buf, 0, buf.Length)) > 0)
        {
            ct.ThrowIfCancellationRequested();

            for (var i = 0; i < n; i++)
            {
                var ch = buf[i];

                if (skipLf)
                {
                    skipLf = false;
                    if (ch == '\n') continue;
                }

                if (quoteSeen)   // предыдущий символ — " внутри кавычек
                {
                    quoteSeen = false;
                    if (ch == '"') { field.Append('"'); continue; }   // "" → "
                    inQuotes = false;                                  // закрывающая кавычка; ch — обычная обработка
                }

                if (inQuotes)
                {
                    if (ch == '"') quoteSeen = true;
                    else field.Append(ch);
                    continue;
                }

                string[]? done = null;
                switch (ch)
                {
                    case '"':  inQuotes = true; break;
                    case '\r': done = FinishRow(); skipLf = true; break;
                    case '\n': done = FinishRow(); break;
                    default:
                        if (ch == _delim) EndField();
                        else field.Append(ch);
                        break;
                }
                if (done is not null) yield return done;
            }

            if (progress is not null)
            {
                var pct = (int)(fs.Position * 100 / total);
                if (pct != lastPct) { progress.Report(Math.Min(1.0, fs.Position / (double)total)); lastPct = pct; }
            }
        }

        // Последнее поле/строка без завершающего перевода строки.
        if (field.Length > 0 || row.Count > 0) yield return FinishRow();
        progress?.Report(1.0);
    }
}
