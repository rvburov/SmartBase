using System.Text.Json;

namespace SmartBase.Core.Importers.TableSources;

/// <summary>
/// JSON-источник: массив объектов <c>[{…},{…}]</c> или NDJSON (объект в каждой строке) —
/// раскладка определяется по первому значимому токену. Читается потоково (Utf8JsonReader поверх
/// FileStream, буфер дозаполняется и дорастает по мере необходимости; одно значение больше 64 МБ —
/// дружелюбная ошибка). Ключи объектов → свойства: объединение по всем записям в порядке первого
/// появления. Название записи — ключ «Название», name или title (без учета регистра), иначе первый
/// ключ первого объекта. Скаляры сохраняются текстом как в файле (числа/булевы — сырой токен),
/// null → пусто, массивы → сырой JSON-текст, а вложенные объекты разворачиваются ТОЧКОЙ
/// (<c>клиент.название</c>, до трех уровней — см. <see cref="SourceHelpers.JsonLeaves"/>).
///
/// <paramref name="itemsPath"/> — путь к массиву записей внутри файла, точками: <c>rows</c>,
/// <c>data.items</c>. Пусто — записями считается сам файл. Написание пути то же, что у
/// <see cref="JsonTextSource"/>, а разница в том, что здесь спуск идет ПОТОКОМ: поля до нужного
/// пропускаются по одному, а сам массив записей никогда не оказывается в памяти целиком. Ради
/// этого путь и заведен — им читается конверт сообщения из папки обмена, где записи лежат под
/// ключом <c>rows</c>, и файл на сотни мегабайт перестает требовать столько же памяти.
/// </summary>
public sealed class JsonTableSource(string path, string itemsPath = "") : ITableSource
{
    private sealed class KeyStat
    {
        public int  Index;      // позиция в union-порядке
        public long NonEmpty;   // записей с непустым значением ключа
        public long LastDoc;    // защита от дублей ключа внутри одного объекта
    }

    private List<string> _keys = [];
    private int _titleIdx = -1;

    public ImportSchema Analyze(IProgress<double>? progress, CancellationToken ct)
    {
        var keys  = new List<string>();
        var stats = new Dictionary<string, KeyStat>(StringComparer.Ordinal);
        long rowCount = 0;
        string? firstKey = null;

        foreach (var doc in ReadValues(progress, ct))
            using (doc)
            {
                if (doc.RootElement.ValueKind != JsonValueKind.Object)
                    throw new ImportException(
                        $"Запись {rowCount + 1} не является объектом JSON — ожидается массив объектов или NDJSON.");
                rowCount++;

                // Вложенные объекты разворачиваются точкой: «клиент.название». Правило общее с
                // сетевым источником — один и тот же набор записей, пришедший файлом и по сети,
                // обязан ложиться в коллекцию одинаково.
                foreach (var (name, text) in SourceHelpers.JsonLeaves(doc.RootElement))
                {
                    if (!stats.TryGetValue(name, out var st))
                    {
                        st = new KeyStat { Index = keys.Count };
                        stats[name] = st;
                        keys.Add(name);
                        firstKey ??= name;
                    }

                    if (!string.IsNullOrWhiteSpace(text) && st.LastDoc != rowCount) { st.NonEmpty++; st.LastDoc = rowCount; }
                }
            }

        if (rowCount == 0)
            throw new ImportException("В файле нет записей — нет данных для импорта.");

        var titleKey = SourceHelpers.PickJsonTitleKey(keys, firstKey)
            ?? throw new ImportException("В объектах JSON нет ни одного ключа — нечего импортировать.");

        var named = keys.Select((k, i) => (Col: i, Raw: k.Trim())).ToList();
        var nameByIdx = SourceHelpers.AssignUniqueNames(named);

        _keys     = keys;
        _titleIdx = stats[titleKey].Index;

        var fieldNames = new List<string>(keys.Count - 1);
        for (var i = 0; i < keys.Count; i++)
            if (i != _titleIdx)
                fieldNames.Add(nameByIdx[i]);

        return new ImportSchema(nameByIdx[_titleIdx], fieldNames, rowCount,
                                rowCount - stats[titleKey].NonEmpty);
    }

    public IEnumerable<ImportRow> ReadRows(CancellationToken ct)
    {
        var index = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < _keys.Count; i++) index[_keys[i]] = i;

        foreach (var doc in ReadValues(null, ct))
            using (doc)
            {
                if (doc.RootElement.ValueKind != JsonValueKind.Object) continue;   // валидировано в Analyze

                var title = "";
                var vals  = new string[Math.Max(0, _keys.Count - 1)];
                Array.Fill(vals, "");

                foreach (var (name, text) in SourceHelpers.JsonLeaves(doc.RootElement))
                {
                    if (!index.TryGetValue(name, out var i)) continue;   // ключа не было в проходе 1
                    if (i == _titleIdx) title = text;
                    else vals[i < _titleIdx ? i : i - 1] = text;
                }

                yield return new ImportRow(title, vals);
            }
    }

    // ── Потоковое чтение значений ─────────────────────────────────────────────

    // Весь обход токенов живет в JsonCursor: тем же обходом читается конверт входящего сообщения,
    // и вторая его копия означала бы второе понимание того, где в файле кончается значение.
    private IEnumerable<JsonDocument> ReadValues(IProgress<double>? progress, CancellationToken ct)
    {
        using var fs = ImportErrors.OpenRead(path);
        if (fs.Length == 0) throw new ImportException("Файл пустой — нет данных для импорта.");
        var total = Math.Max(1L, fs.Length);

        var cursor = new JsonCursor(fs);

        // Спуск к массиву записей по пути. Поля, встреченные до нужного, пропускаются, не попадая
        // в память. Сам массив записей так не пропускается: до него мы доходим и дальше читаем
        // поштучно, ради чего весь этот спуск и написан.
        cursor.Descend(itemsPath);

        var first = cursor.Peek();
        bool isArray;
        if (first == JsonTokenType.StartArray) { isArray = true; cursor.Consume(); }
        else if (first == JsonTokenType.StartObject) isArray = false;
        else throw new ImportException(
            "Некорректный JSON: ожидается массив объектов [{…}] или NDJSON (объект в каждой строке).");

        var lastPct = -1;
        while (true)
        {
            ct.ThrowIfCancellationRequested();

            var t = cursor.Peek();
            if (isArray && t == JsonTokenType.EndArray) yield break;
            if (t == JsonTokenType.None)
            {
                if (isArray) throw JsonCursor.Broken();   // файл оборвался до закрывающей ]
                yield break;
            }

            var doc = cursor.Take();
            // NDJSON — последовательность независимых верхнеуровневых значений: состояние ридера
            // сбрасывается перед каждым следующим (иначе «лишний» токен после первого значения).
            if (!isArray) cursor.Restart();

            if (progress is not null)
            {
                var consumed = cursor.Position;
                var pct = (int)(consumed * 100 / total);
                if (pct != lastPct) { progress.Report(Math.Min(1.0, consumed / (double)total)); lastPct = pct; }
            }

            yield return doc;
        }
    }
}
