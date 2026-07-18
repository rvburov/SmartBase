using System.Text.Json;

namespace SmartBase.Core.Importers.TableSources;

/// <summary>
/// JSON-источник: массив объектов <c>[{…},{…}]</c> или NDJSON (объект в каждой строке) —
/// раскладка определяется по первому значимому токену. Читается потоково (Utf8JsonReader поверх
/// FileStream, буфер дозаполняется и дорастает по мере необходимости; одно значение больше 64 МБ —
/// дружелюбная ошибка). Ключи объектов → свойства: объединение по всем записям в порядке первого
/// появления. Название записи — ключ «Название», name или title (без учёта регистра), иначе первый
/// ключ первого объекта. Скаляры сохраняются текстом как в файле (числа/булевы — сырой токен),
/// null → пусто, вложенные объекты/массивы → сырой JSON-текст.
/// </summary>
public sealed class JsonTableSource(string path) : ITableSource
{
    private const int MaxValueBytes = 64 * 1024 * 1024;

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

                foreach (var p in doc.RootElement.EnumerateObject())
                {
                    if (!stats.TryGetValue(p.Name, out var st))
                    {
                        st = new KeyStat { Index = keys.Count };
                        stats[p.Name] = st;
                        keys.Add(p.Name);
                        firstKey ??= p.Name;
                    }

                    var nonEmpty = p.Value.ValueKind switch
                    {
                        JsonValueKind.Null or JsonValueKind.Undefined => false,
                        JsonValueKind.String => !string.IsNullOrWhiteSpace(p.Value.GetString()),
                        _ => true,
                    };
                    if (nonEmpty && st.LastDoc != rowCount) { st.NonEmpty++; st.LastDoc = rowCount; }
                }
            }

        if (rowCount == 0)
            throw new ImportException("В файле нет записей — нет данных для импорта.");

        var titleKey = PickTitleKey(keys, firstKey)
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

    // Приоритет ключа названия: «Название» → name → title (без учёта регистра, первый по порядку
    // появления), иначе первый ключ первого объекта.
    private static string? PickTitleKey(List<string> keys, string? firstKey)
    {
        foreach (var cand in new[] { "название", "name", "title" })
        {
            var hit = keys.FirstOrDefault(k => string.Equals(k, cand, StringComparison.OrdinalIgnoreCase));
            if (hit is not null) return hit;
        }
        return firstKey ?? (keys.Count > 0 ? keys[0] : null);
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

                foreach (var p in doc.RootElement.EnumerateObject())
                {
                    if (!index.TryGetValue(p.Name, out var i)) continue;   // ключа не было в проходе 1
                    var s = FormatValue(p.Value);
                    if (i == _titleIdx) title = s;
                    else vals[i < _titleIdx ? i : i - 1] = s;
                }

                yield return new ImportRow(title, vals);
            }
    }

    private static string FormatValue(JsonElement v) => v.ValueKind switch
    {
        JsonValueKind.String                          => v.GetString() ?? "",
        JsonValueKind.Null or JsonValueKind.Undefined => "",
        _                                             => v.GetRawText(),   // числа/булевы как в файле; объект/массив — сырой JSON
    };

    // ── Потоковое чтение значений ─────────────────────────────────────────────

    // Utf8JsonReader — ref struct и не переживает yield, поэтому на каждый шаг он пересоздаётся
    // из сохранённого JsonReaderState + буфера. JsonDocument.TryParseValue при нехватке данных
    // возвращает false и откатывает ридер — буфер дозаполняется (и дорастает под большое значение).
    private IEnumerable<JsonDocument> ReadValues(IProgress<double>? progress, CancellationToken ct)
    {
        using var fs = ImportErrors.OpenRead(path);
        if (fs.Length == 0) throw new ImportException("Файл пустой — нет данных для импорта.");
        var total = Math.Max(1L, fs.Length);

        var opts   = new JsonReaderOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip };
        var state  = new JsonReaderState(opts);
        var buffer = new byte[64 * 1024];
        var start  = 0;
        var len    = 0;
        var final  = false;

        void Fill()
        {
            if (start > 0)
            {
                Buffer.BlockCopy(buffer, start, buffer, 0, len - start);
                len -= start;
                start = 0;
            }
            if (len == buffer.Length)
            {
                if (buffer.Length >= MaxValueBytes)
                    throw new ImportException("Слишком большое значение в JSON (более 64 МБ) — файл не может быть импортирован.");
                Array.Resize(ref buffer, buffer.Length * 2);
            }
            var read = fs.Read(buffer, len, buffer.Length - len);
            if (read == 0) final = true;
            len += read;
        }

        static ImportException Broken() =>
            new("Файл повреждён или имеет некорректную структуру JSON.");

        static bool WhitespaceOnly(byte[] buf, int start, int len)
        {
            for (var i = start; i < len; i++)
                if (buf[i] is not ((byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n')) return false;
            return true;
        }

        // Тип следующего токена, НЕ сдвигая позицию (пробный ридер отбрасывается). None — конец файла.
        JsonTokenType Peek()
        {
            while (true)
            {
                // Хвост из одних пробелов/переводов строк: свежий ридер (NDJSON) на таком
                // остатке при isFinalBlock кидает исключение вместо false — это конец данных.
                if (final && WhitespaceOnly(buffer, start, len)) return JsonTokenType.None;

                var r = new Utf8JsonReader(buffer.AsSpan(start, len - start), final, state);
                try { if (r.Read()) return r.TokenType; }
                catch (JsonException) { throw Broken(); }
                if (final) return JsonTokenType.None;
                Fill();
            }
        }

        // Считать один токен (сдвинуть позицию).
        void Consume()
        {
            while (true)
            {
                var r = new Utf8JsonReader(buffer.AsSpan(start, len - start), final, state);
                bool ok;
                try { ok = r.Read(); }
                catch (JsonException) { throw Broken(); }
                if (ok)
                {
                    state = r.CurrentState;
                    start += (int)r.BytesConsumed;
                    return;
                }
                if (final) throw Broken();
                Fill();
            }
        }

        // Считать одно полное значение (объект/массив/скаляр) как JsonDocument.
        JsonDocument Take()
        {
            while (true)
            {
                var r = new Utf8JsonReader(buffer.AsSpan(start, len - start), final, state);
                try
                {
                    // Сначала встать НА первый токен значения тем же ридером: TryParseValue по
                    // возобновлённому состоянию принял бы «текущим» уже съеденный чужой токен
                    // (StartArray внешнего массива) и распарсил бы не тот диапазон.
                    if (r.Read() && JsonDocument.TryParseValue(ref r, out var doc))
                    {
                        state = r.CurrentState;
                        start += (int)r.BytesConsumed;
                        return doc;
                    }
                }
                catch (JsonException) { throw Broken(); }
                if (final) throw Broken();   // файл оборвался посреди значения
                Fill();
            }
        }

        Fill();
        if (len >= 3 && buffer[0] == 0xEF && buffer[1] == 0xBB && buffer[2] == 0xBF) start = 3;   // UTF-8 BOM

        var first = Peek();
        bool isArray;
        if (first == JsonTokenType.StartArray) { isArray = true; Consume(); }
        else if (first == JsonTokenType.StartObject) isArray = false;
        else throw new ImportException(
            "Некорректный JSON: ожидается массив объектов [{…}] или NDJSON (объект в каждой строке).");

        var lastPct = -1;
        while (true)
        {
            ct.ThrowIfCancellationRequested();

            var t = Peek();
            if (isArray && t == JsonTokenType.EndArray) yield break;
            if (t == JsonTokenType.None)
            {
                if (isArray) throw Broken();   // файл оборвался до закрывающей ]
                yield break;
            }

            var doc = Take();
            // NDJSON — последовательность независимых верхнеуровневых значений: состояние ридера
            // сбрасывается перед каждым следующим (иначе «лишний» токен после первого значения).
            if (!isArray) state = new JsonReaderState(opts);

            if (progress is not null)
            {
                var consumed = fs.Position - (len - start);   // прочитано минус необработанный остаток
                var pct = (int)(consumed * 100 / total);
                if (pct != lastPct) { progress.Report(Math.Min(1.0, consumed / (double)total)); lastPct = pct; }
            }

            yield return doc;
        }
    }
}
