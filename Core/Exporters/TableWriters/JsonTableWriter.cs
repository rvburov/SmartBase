using System.Text.Encodings.Web;
using System.Text.Json;
using SmartBase.Core.Models;

namespace SmartBase.Core.Exporters.TableWriters;

/// <summary>
/// JSON: массив объектов, UTF-8 без отступов (на 10 млн строк отступы — +30–50 % объема).
/// Ключи — заголовки колонок (пустые → «Колонка N», дубликаты → «Имя (2)»); колонка
/// названия уходит своим именем (дефолт «Название») — импорт находит ее первой, круг
/// сходится. Значения типизированы: number → число, checkbox → true/false, date —
/// ISO-канон хранения (лосслесс круг через DateValue.TryParseStorage), остальное — строки.
/// Encoder — UnsafeRelaxedJsonEscaping, иначе кириллица превращается в \uXXXX.
/// Flush раз в 500 строк обязателен: Utf8JsonWriter копит ВСЕ записанное до Flush
/// (без него память O(n)).
/// </summary>
public sealed class JsonTableWriter : ITableWriter
{
    private const int FlushEvery = 500;

    private readonly FileStream _fs;
    private readonly Utf8JsonWriter _w;
    private IReadOnlyList<ExportColumn>? _cols;
    private RelationPortableSchema? _portable;
    private JsonEncodedText[] _keys = [];
    private int _sinceFlush;

    public JsonTableWriter(string path)
    {
        _fs = ExportErrors.CreateFile(path);
        _w = new Utf8JsonWriter(_fs, new JsonWriterOptions
        {
            Indented = false,
            Encoder  = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        });
    }

    public void BeginTable(string name, IReadOnlyList<ExportColumn> columns, bool writeHeader)
    {
        if (_cols is not null)
            throw new InvalidOperationException("JSON поддерживает одну таблицу на файл.");
        _cols = columns;
        _keys = MakeKeys(columns);
        _portable = columns.Select(c => c.PortableSchema).FirstOrDefault(s => s is not null);
        if (_portable is not null)
        {
            _w.WriteStartObject();
            _w.WriteNumber("$smartbase", 1);
            _w.WritePropertyName("schema");
            JsonSerializer.Serialize(_w, _portable);
            _w.WritePropertyName("rows");
        }
        _w.WriteStartArray();
    }

    public void WriteRow(IReadOnlyList<string> values)
    {
        var cols = _cols ?? throw new InvalidOperationException("BeginTable не вызван.");
        if (_portable is not null)
        {
            if (values.Count != cols.Count + 1)
                throw new ExportException("Не получены полные данные связей для JSON SmartBase.");
            try
            {
                var row = JsonSerializer.Deserialize<RelationPortableRow>(values[^1])
                    ?? throw new ExportException("Не получены данные записи для JSON SmartBase.");
                JsonSerializer.Serialize(_w, row);
            }
            catch (JsonException) { throw new ExportException("Некорректные данные связей для JSON SmartBase."); }
            FlushRow();
            return;
        }
        _w.WriteStartObject();
        var n = Math.Min(values.Count, cols.Count);
        for (var i = 0; i < n; i++)
        {
            var raw = values[i];
            switch (cols[i].FieldType)
            {
                case "checkbox":
                    _w.WriteBoolean(_keys[i], WriterHelpers.IsChecked(raw));
                    break;
                case "number" when raw.Length > 0 && WriterHelpers.TryNumber(raw, out var num):
                    _w.WriteNumber(_keys[i], num);
                    break;
                case "multiselect":
                    _w.WriteStartArray(_keys[i]);
                    foreach (var s in MultiSelectValue.Parse(raw)) _w.WriteStringValue(s);
                    _w.WriteEndArray();
                    break;
                default:
                    _w.WriteString(_keys[i], raw);
                    break;
            }
        }
        _w.WriteEndObject();
        FlushRow();
    }

    private void FlushRow()
    {
        if (++_sinceFlush >= FlushEvery)
        {
            _w.Flush();
            _sinceFlush = 0;
        }
    }

    public void EndTable()
    {
        _w.WriteEndArray();
        if (_portable is not null) _w.WriteEndObject();
    }

    public void Finish() => _w.Flush();

    public void Dispose()
    {
        _w.Dispose();
        _fs.Dispose();
    }

    /// <summary>Ключи объектов: пустые заголовки → «Колонка N», повторы → «Имя (2)», «Имя (3)»…</summary>
    private static JsonEncodedText[] MakeKeys(IReadOnlyList<ExportColumn> columns)
    {
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var keys = new JsonEncodedText[columns.Count];
        for (var i = 0; i < columns.Count; i++)
        {
            var key = string.IsNullOrWhiteSpace(columns[i].Header) ? $"Колонка {i + 1}" : columns[i].Header.Trim();
            if (!used.Add(key))
            {
                var n = 2;
                while (!used.Add($"{key} ({n})")) n++;
                key = $"{key} ({n})";
            }
            keys[i] = JsonEncodedText.Encode(key, JavaScriptEncoder.UnsafeRelaxedJsonEscaping);
        }
        return keys;
    }
}
