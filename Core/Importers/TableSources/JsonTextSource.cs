using System.Text.Json;

namespace SmartBase.Core.Importers.TableSources;

/// <summary>
/// JSON-источник поверх текста, который УЖЕ в памяти, — ответа чужой программы. Правила те же,
/// что у файлового <see cref="JsonTableSource"/>: ключи объектов становятся свойствами
/// (объединение по всем записям в порядке первого появления), название берется по общему правилу
/// <see cref="SourceHelpers.PickJsonTitleKey"/>, значения переводятся в текст
/// <see cref="SourceHelpers.FormatJsonValue"/>. Одинаковость не случайна: один и тот же набор
/// записей, пришедший файлом и по сети, обязан ложиться в коллекцию одинаково.
///
/// Потоковости здесь нет и не нужно: ответ уже прочитан целиком и обрезан по потолку
/// отправляющим слоем — именно этот потолок и делает разбор в памяти безопасным.
///
/// <paramref name="itemsPath"/> — путь к массиву записей внутри ответа, точками:
/// <c>data.items</c>. Пусто — массивом считается сам ответ; если ответ оказался одиночным
/// объектом, он читается как единственная запись (частый случай у справочных вызовов).
/// </summary>
public sealed class JsonTextSource(string json, string itemsPath = "") : ITableSource
{
    private List<string> _keys     = [];
    private int          _titleIdx = -1;

    public ImportSchema Analyze(IProgress<double>? progress, CancellationToken ct)
    {
        var keys     = new List<string>();
        var index    = new Dictionary<string, int>(StringComparer.Ordinal);
        var nonEmpty = new List<long>();
        var seen     = new List<long>();

        long rowCount = 0;
        string? firstKey = null;

        using var document = Parse();

        foreach (var item in Items(document.RootElement))
        {
            ct.ThrowIfCancellationRequested();
            rowCount++;

            if (item.ValueKind != JsonValueKind.Object)
                throw new ImportException(
                    $"Запись {rowCount} в ответе не является объектом — ожидался список объектов.");

            foreach (var property in item.EnumerateObject())
            {
                if (!index.TryGetValue(property.Name, out var i))
                {
                    i = keys.Count;
                    index[property.Name] = i;
                    keys.Add(property.Name);
                    nonEmpty.Add(0);
                    seen.Add(0);
                    firstKey ??= property.Name;
                }

                // seen — защита от повторного ключа внутри одного объекта: JSON это допускает,
                // а считать одну запись дважды нельзя.
                if (Filled(property.Value) && seen[i] != rowCount)
                {
                    nonEmpty[i] = nonEmpty[i] + 1;
                    seen[i]     = rowCount;
                }
            }

            progress?.Report(0.5);
        }

        if (rowCount == 0)
            throw new ImportException(PathNote("В ответе нет записей — импортировать нечего."));

        var titleKey = SourceHelpers.PickJsonTitleKey(keys, firstKey)
            ?? throw new ImportException("В записях ответа нет ни одного поля — импортировать нечего.");

        var named     = keys.Select((k, i) => (Col: i, Raw: k.Trim())).ToList();
        var nameByIdx = SourceHelpers.AssignUniqueNames(named);

        _keys     = keys;
        _titleIdx = index[titleKey];

        var fieldNames = new List<string>(Math.Max(0, keys.Count - 1));
        for (var i = 0; i < keys.Count; i++)
            if (i != _titleIdx)
                fieldNames.Add(nameByIdx[i]);

        progress?.Report(1);

        return new ImportSchema(nameByIdx[_titleIdx], fieldNames, rowCount,
                                rowCount - nonEmpty[_titleIdx]);
    }

    public IEnumerable<ImportRow> ReadRows(CancellationToken ct)
    {
        var index = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < _keys.Count; i++) index[_keys[i]] = i;

        using var document = Parse();

        foreach (var item in Items(document.RootElement))
        {
            ct.ThrowIfCancellationRequested();
            if (item.ValueKind != JsonValueKind.Object) continue;   // проверено в Analyze

            var title  = "";
            var values = new string[Math.Max(0, _keys.Count - 1)];
            Array.Fill(values, "");

            foreach (var property in item.EnumerateObject())
            {
                if (!index.TryGetValue(property.Name, out var i)) continue;   // ключа не было в проходе 1

                var text = SourceHelpers.FormatJsonValue(property.Value);
                if (i == _titleIdx) title = text;
                else values[i < _titleIdx ? i : i - 1] = text;
            }

            yield return new ImportRow(title, values);
        }
    }

    // ── Разбор ответа ─────────────────────────────────────────────────────────

    private JsonDocument Parse()
    {
        if (string.IsNullOrWhiteSpace(json))
            throw new ImportException("Ответ пустой — импортировать нечего.");

        try
        {
            return JsonDocument.Parse(json, new JsonDocumentOptions
            {
                AllowTrailingCommas = true,
                CommentHandling     = JsonCommentHandling.Skip,
            });
        }
        catch (JsonException error)
        {
            // Текст ответа в сообщение не попадает: в нем может оказаться что угодно, вплоть до
            // секрета, а сообщение уходит в баннер и в журнал.
            throw new ImportException("Ответ не является JSON: " + error.Message);
        }
    }

    /// <summary>
    /// Записи ответа. Путь ведет вглубь по точкам; если по нему лежит объект, а не массив, он
    /// читается как единственная запись — справочные вызовы часто отдают именно объект.
    /// </summary>
    private IEnumerable<JsonElement> Items(JsonElement root)
    {
        var node = root;

        foreach (var step in (itemsPath ?? "").Split('.', StringSplitOptions.RemoveEmptyEntries))
        {
            if (node.ValueKind != JsonValueKind.Object || !node.TryGetProperty(step, out var next))
                throw new ImportException(
                    $"В ответе нет пути «{itemsPath}» — не найдена часть «{step}». "
                    + "Проверьте путь к массиву записей на карточке запроса.");

            node = next;
        }

        if (node.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in node.EnumerateArray()) yield return item;
            yield break;
        }

        if (node.ValueKind == JsonValueKind.Object)
        {
            yield return node;
            yield break;
        }

        throw new ImportException(PathNote(
            "По указанному пути в ответе лежит не список записей, а одиночное значение."));
    }

    /// <summary>Подсказка про путь: без нее человек не поймет, где именно смотреть.</summary>
    private string PathNote(string message) =>
        string.IsNullOrWhiteSpace(itemsPath)
            ? message + " Если записи лежат внутри ответа, укажите путь к ним — например, data.items."
            : message + $" Путь: «{itemsPath}».";

    private static bool Filled(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Null or JsonValueKind.Undefined => false,
        JsonValueKind.String => !string.IsNullOrWhiteSpace(value.GetString()),
        _ => true,
    };
}
