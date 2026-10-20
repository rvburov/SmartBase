using System.Globalization;
using System.Text.Json;
using SmartBase.Core.Models;

namespace SmartBase.Core.Importers.TableSources;

/// <summary>Читает схему отдельно от массива: порядок полей конверта не требует держать записи
/// в памяти. Второй проход проверяет каждую запись до создания импортируемой коллекции.</summary>
internal sealed class RelationPortableTableSource
{
    private readonly string _path;
    private readonly RelationPortableSchema _schema;
    private readonly Dictionary<int, RelationPortableField> _fields;

    private RelationPortableTableSource(string path, RelationPortableSchema schema)
    {
        _path = path;
        _schema = schema;
        _fields = schema.Fields.ToDictionary(f => f.Id);
    }

    public static RelationPortableTableSource? TryOpen(string path, CancellationToken ct)
    {
        using var stream = ImportErrors.OpenRead(path);
        var cursor = new JsonCursor(stream);
        if (cursor.Peek() != JsonTokenType.StartObject) return null;
        cursor.Consume();
        var marker = false;
        var version = 0;
        var rows = false;
        var rowsArray = false;
        var repeated = false;
        var names = new HashSet<string>(StringComparer.Ordinal);
        JsonDocument? schemaDocument = null;
        try
        {
            while (cursor.Peek() != JsonTokenType.EndObject)
            {
                ct.ThrowIfCancellationRequested();
                if (cursor.Peek() != JsonTokenType.PropertyName) throw JsonCursor.Broken();
                var name = cursor.TakeName();
                if (name is "$smartbase" or "schema" or "rows") repeated |= !names.Add(name);
                switch (name)
                {
                    case "$smartbase":
                        marker = true;
                        using (var value = cursor.Take())
                            if (value.RootElement.ValueKind == JsonValueKind.Number)
                                value.RootElement.TryGetInt32(out version);
                        break;
                    case "schema":
                        schemaDocument?.Dispose();
                        schemaDocument = cursor.Take();
                        break;
                    case "rows":
                        rows = true;
                        rowsArray = cursor.Peek() == JsonTokenType.StartArray;
                        if (rowsArray) SkipRows(cursor, ct);
                        else cursor.Skip();
                        break;
                    default:
                        cursor.Skip();
                        break;
                }
            }
            cursor.Consume();
            // Обычный объект/NDJSON продолжает читаться прежним источником.
            if (!marker) return null;
            if (version != 1) throw Invalid("версия формата не поддерживается");
            if (repeated || !rows || !rowsArray || schemaDocument is null)
                throw Invalid("требуются одна схема и один массив rows");
            if (cursor.Peek() != JsonTokenType.None) throw JsonCursor.Broken();
            var schema = Deserialize<RelationPortableSchema>(schemaDocument.RootElement);
            ValidateSchema(schema);
            ValidateKeys(schemaDocument.RootElement.GetProperty("DatabaseIdentities"));
            return new RelationPortableTableSource(path, schema);
        }
        finally { schemaDocument?.Dispose(); }
    }

    private static void SkipRows(JsonCursor cursor, CancellationToken ct)
    {
        cursor.Consume();
        while (cursor.Peek() != JsonTokenType.EndArray)
        {
            ct.ThrowIfCancellationRequested();
            cursor.Skip();
        }
        cursor.Consume();
    }

    public ImportSchema Analyze(IProgress<double>? progress, CancellationToken ct)
    {
        long count = 0;
        long empty = 0;
        foreach (var row in ReadPortableRows(progress, ct))
        {
            count++;
            if (string.IsNullOrWhiteSpace(row.Title)) empty++;
        }
        if (count == 0) throw new ImportException("В файле нет записей — нет данных для импорта.");
        return new ImportSchema(_schema.TitleHeader, _schema.Fields.Select(f => f.Name).ToList(), count, empty)
        {
            PortableSchema = _schema,
        };
    }

    public IEnumerable<ImportRow> ReadRows(CancellationToken ct)
    {
        foreach (var row in ReadPortableRows(null, ct))
            yield return new ImportRow(row.Title, _schema.Fields.Select(f => row.Values.GetValueOrDefault(f.Id, "")).ToList())
            {
                PortableRow = row,
            };
    }

    private IEnumerable<RelationPortableRow> ReadPortableRows(IProgress<double>? progress, CancellationToken ct)
    {
        using var stream = ImportErrors.OpenRead(_path);
        var total = Math.Max(1L, stream.Length);
        var cursor = new JsonCursor(stream);
        cursor.Descend("rows");
        if (cursor.Peek() != JsonTokenType.StartArray) throw Invalid("rows должен быть массивом");
        cursor.Consume();
        var lastPercent = -1;
        while (cursor.Peek() != JsonTokenType.EndArray)
        {
            ct.ThrowIfCancellationRequested();
            using var document = cursor.Take();
            var row = Deserialize<RelationPortableRow>(document.RootElement);
            ValidateRow(document.RootElement, row);
            if (progress is not null)
            {
                var percent = (int)(cursor.Position * 100 / total);
                if (percent != lastPercent)
                {
                    progress.Report(Math.Min(1.0, cursor.Position / (double)total));
                    lastPercent = percent;
                }
            }
            yield return row;
        }
        cursor.Consume();
        while (cursor.Peek() == JsonTokenType.PropertyName)
        {
            ct.ThrowIfCancellationRequested();
            cursor.Consume();
            cursor.Skip();
        }
        if (cursor.Peek() != JsonTokenType.EndObject) throw JsonCursor.Broken();
        cursor.Consume();
        if (cursor.Peek() != JsonTokenType.None) throw JsonCursor.Broken();
        progress?.Report(1.0);
    }

    private static T Deserialize<T>(JsonElement element) where T : class
    {
        try
        {
            if (element.ValueKind != JsonValueKind.Object) throw Invalid("ожидался объект");
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
                if (!names.Add(property.Name)) throw Invalid("повторное свойство объекта");
            return element.Deserialize<T>() ?? throw Invalid("пустой объект");
        }
        catch (JsonException) { throw Invalid("неверные типы свойств"); }
    }

    private static void ValidateSchema(RelationPortableSchema schema)
    {
        if (schema.Version != 1 || schema.SourceCollectionId <= 0 || schema.SourceDbId < 0
            || string.IsNullOrWhiteSpace(schema.SourceIdentity) || schema.DatabaseIdentities is null
            || schema.Fields is null || string.IsNullOrWhiteSpace(schema.TitleHeader))
            throw Invalid("неполная схема коллекции");
        if (schema.DatabaseIdentities.Any(p => p.Key <= 0 || string.IsNullOrWhiteSpace(p.Value)))
            throw Invalid("неверные идентификаторы баз данных");
        var ids = new HashSet<int>();
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var field in schema.Fields)
            if (field is null || field.Id <= 0 || !ids.Add(field.Id) || string.IsNullOrWhiteSpace(field.Name)
                || !names.Add(field.Name) || string.IsNullOrWhiteSpace(field.FieldType))
                throw Invalid("неверные или повторяющиеся свойства коллекции");
    }

    private void ValidateRow(JsonElement element, RelationPortableRow row)
    {
        if (row.Id <= 0 || row.Title is null || row.Values is null || row.Links is null
            || !element.TryGetProperty("Values", out var values) || values.ValueKind != JsonValueKind.Object
            || !element.TryGetProperty("Links", out var links) || links.ValueKind != JsonValueKind.Object)
            throw Invalid("неполная запись");
        ValidateKeys(values);
        ValidateKeys(links);
        foreach (var (id, value) in row.Values)
            if (value is null || !_fields.TryGetValue(id, out var field)
                || field.FieldType is "relation" or "lookup" or "rollup")
                throw Invalid("значение относится к неизвестному или вычисляемому свойству");
        foreach (var (id, targets) in row.Links)
        {
            if (!_fields.TryGetValue(id, out var field) || field.FieldType != "relation" || targets is null)
                throw Invalid("связь относится к неизвестному свойству");
            var unique = new HashSet<RelationTarget>();
            foreach (var target in targets)
                if (target.RecordId <= 0 || target.DbId < 0 || !unique.Add(target)
                    || target.DbId > 0 && !_schema.DatabaseIdentities.ContainsKey(target.DbId))
                    throw Invalid("неверная, повторная или неизвестная цель связи");
            var config = RelationConfig.FromConfig(field.ConfigJson);
            if (targets.Count > 0 && config is null || config?.Single == true && targets.Count > 1)
                throw Invalid("цели не соответствуют настройке связи");
        }
    }

    private static void ValidateKeys(JsonElement dictionary)
    {
        var seen = new HashSet<int>();
        foreach (var property in dictionary.EnumerateObject())
            if (!int.TryParse(property.Name, NumberStyles.None, CultureInfo.InvariantCulture, out var id)
                || id <= 0 || property.Name != id.ToString(CultureInfo.InvariantCulture) || !seen.Add(id))
                throw Invalid("повторный или неверный ключ свойства");
    }

    private static ImportException Invalid(string reason) => new($"Некорректный JSON SmartBase: {reason}.");
}
