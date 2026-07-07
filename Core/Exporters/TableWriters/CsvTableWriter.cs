using System.Text;

namespace DataBase.Core.Exporters.TableWriters;

/// <summary>
/// CSV: разделитель «;», CRLF, кавычки RFC 4180, UTF-8 С BOM — Excel открывает кириллицу
/// без вопросов, а наш импорт сниффит BOM и «;» → круговой обмен без настроек.
/// Значения: даты — человекочитаемо (дд.MM.гггг…), checkbox — «1»/пусто (truthy-круг),
/// остальное — как хранится.
/// </summary>
public sealed class CsvTableWriter : ITableWriter
{
    private readonly StreamWriter _w;
    private IReadOnlyList<ExportColumn>? _cols;
    private readonly StringBuilder _line = new(256);

    public CsvTableWriter(string path)
    {
        _w = new StreamWriter(ExportErrors.CreateFile(path), new UTF8Encoding(true), 1 << 20)
        {
            NewLine = "\r\n",
        };
    }

    public void BeginTable(string name, IReadOnlyList<ExportColumn> columns, bool writeHeader)
    {
        if (_cols is not null)
            throw new InvalidOperationException("CSV поддерживает одну таблицу на файл.");
        _cols = columns;
        if (writeHeader)
            WriteLine(columns.Select(c => c.Header));
    }

    public void WriteRow(IReadOnlyList<string> values)
    {
        var cols = _cols ?? throw new InvalidOperationException("BeginTable не вызван.");
        _line.Clear();
        var n = Math.Min(values.Count, cols.Count);
        for (var i = 0; i < n; i++)
        {
            if (i > 0) _line.Append(';');
            AppendField(cols[i].FieldType switch
            {
                "date"     => WriterHelpers.DateDisplay(values[i]),
                "checkbox" => WriterHelpers.IsChecked(values[i]) ? "1" : "",
                _          => values[i],
            });
        }
        _w.WriteLine(_line);
    }

    public void EndTable() { }

    public void Finish() => _w.Flush();

    public void Dispose() => _w.Dispose();

    private void WriteLine(IEnumerable<string> fields)
    {
        _line.Clear();
        var first = true;
        foreach (var f in fields)
        {
            if (!first) _line.Append(';');
            first = false;
            AppendField(f);
        }
        _w.WriteLine(_line);
    }

    /// <summary>Кавычки по RFC 4180: цитируем при «;», кавычке или переносе; «"» → «""».</summary>
    private void AppendField(string value)
    {
        if (value.AsSpan().IndexOfAny(';', '"', '\n') < 0 && !value.Contains('\r'))
        {
            _line.Append(value);
            return;
        }
        _line.Append('"');
        foreach (var ch in value)
        {
            if (ch == '"') _line.Append('"');
            _line.Append(ch);
        }
        _line.Append('"');
    }
}
