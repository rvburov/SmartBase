using System.Text.Json;
using System.Text.Json.Serialization;

namespace SmartBase.Utils;

/// <summary>
/// Запомненные фильтры и сортировка коллекций для настройки «Запоминать фильтры и сортировку».
///
/// Живет в app.db ОДНОЙ строкой JSON, а не в схеме пользовательской базы. Причины две.
/// Во-первых, это личная раскладка экрана, а не данные: в общей базе она навязывалась бы всем,
/// кто ее открыл. Во-вторых, колонка в db_collections потребовала бы миграции каждой базы
/// ради состояния, которое не жалко потерять.
///
/// Размер намеренно ограничен: помним последние <see cref="MaxEntries"/> коллекций (самая
/// старая вытесняется) и не сохраняем фильтр колонки, в котором больше
/// <see cref="MaxValuesPerColumn"/> отмеченных значений — иначе отбор по колонке с сотней
/// тысяч уникальных значений раздул бы app.db на мегабайты. Сортировка в таком случае
/// запоминается, а фильтр — нет.
/// </summary>
public static class ConstructorFilterMemory
{
    public const int MaxEntries          = 20;
    public const int MaxValuesPerColumn  = 100;

    /// <summary>Что помним про одну коллекцию: отбор по колонкам и сортировка.</summary>
    public sealed record Snapshot(
        Dictionary<string, HashSet<string>> Filters,
        string?                             SortKey,
        bool                                SortAsc);

    // Формат хранения. Имена полей короткие: строка целиком лежит в одном значении app.db.
    private sealed class Entry
    {
        [JsonPropertyName("k")] public string Key { get; set; } = "";
        [JsonPropertyName("s")] public string? Sort { get; set; }
        [JsonPropertyName("a")] public bool Asc { get; set; } = true;
        [JsonPropertyName("f")] public Dictionary<string, List<string>> Filters { get; set; } = [];
    }

    /// <summary>Запомненное для коллекции; null — ничего не помним (или в хранилище мусор).</summary>
    public static Snapshot? Load(string dbPath, int collectionId)
    {
        var key = KeyOf(dbPath, collectionId);
        var entry = ReadAll().FirstOrDefault(e => e.Key == key);
        if (entry is null) return null;

        var filters = entry.Filters
            .Where(p => p.Value.Count > 0)
            .ToDictionary(p => p.Key, p => new HashSet<string>(p.Value));

        // Пустая запись (все фильтры отвалились, сортировки нет) — то же, что ее отсутствие.
        return filters.Count == 0 && entry.Sort is null ? null : new Snapshot(filters, entry.Sort, entry.Asc);
    }

    /// <summary>
    /// Запомнить отбор коллекции. Пустой отбор без сортировки запись СТИРАЕТ: иначе «снял все
    /// фильтры» не пережило бы перезапуск — коллекция снова открывалась бы отфильтрованной.
    /// </summary>
    public static void Save(string dbPath, int collectionId,
                            IReadOnlyDictionary<string, HashSet<string>> filters,
                            string? sortKey, bool sortAsc)
    {
        var key  = KeyOf(dbPath, collectionId);
        var list = ReadAll();

        var kept = filters
            .Where(p => p.Value.Count is > 0 and <= MaxValuesPerColumn)
            .ToDictionary(p => p.Key, p => p.Value.ToList());

        // Запись в app.db — это SaveChanges на UI-потоке, а зовут нас с каждой перечиткой строк,
        // в том числе по тику опроса чужих правок (раз в 3 секунды на общей базе). Поэтому
        // сначала сверяем с тем, что уже лежит, и на совпадении не делаем ничего.
        var existing = list.FirstOrDefault(e => e.Key == key);
        if (Same(existing, kept, sortKey, sortAsc)) return;

        list.RemoveAll(e => e.Key == key);

        if (kept.Count > 0 || sortKey is not null)
        {
            // В конец: список работает как LRU, вытесняем с головы.
            list.Add(new Entry { Key = key, Sort = sortKey, Asc = sortAsc, Filters = kept });
            while (list.Count > MaxEntries) list.RemoveAt(0);
        }

        Write(list);
    }

    /// <summary>Забыть все запомненное (выключение настройки).</summary>
    public static void Clear() => AppSettings.SaveConstructorFilters("");

    // Совпадает ли то, что просят сохранить, с уже лежащим. Отсутствие записи и пустой отбор
    // без сортировки — тоже «совпадает»: писать нечего и не было.
    private static bool Same(Entry? existing, Dictionary<string, List<string>> kept,
                             string? sortKey, bool sortAsc)
    {
        if (existing is null) return kept.Count == 0 && sortKey is null;
        if (existing.Sort != sortKey) return false;
        // Направление сортировки сравниваем, только когда сортировка есть: без нее оно не
        // значит ничего, и его дрейф не должен считаться изменением.
        if (sortKey is not null && existing.Asc != sortAsc) return false;
        if (existing.Filters.Count != kept.Count) return false;

        foreach (var (column, values) in kept)
        {
            if (!existing.Filters.TryGetValue(column, out var old)) return false;
            if (old.Count != values.Count) return false;
            // Наборы значений: порядок внутри колонки роли не играет (источник — HashSet).
            if (!new HashSet<string>(old).SetEquals(values)) return false;
        }
        return true;
    }

    // Ключ — путь базы плюс id коллекции: реестровый DbId у одной и той же базы на разных
    // машинах разный, а путь — то, чем база открыта здесь.
    private static string KeyOf(string dbPath, int collectionId) => $"{dbPath}|{collectionId}";

    private static List<Entry> ReadAll()
    {
        var raw = AppSettings.LoadConstructorFilters();
        if (string.IsNullOrWhiteSpace(raw)) return [];
        // Мусор в хранилище (правка руками, откат версии) читаем как «ничего не помним»:
        // раскладка экрана не стоит того, чтобы на ней падать.
        try { return JsonSerializer.Deserialize<List<Entry>>(raw) ?? []; }
        catch (JsonException) { return []; }
    }

    private static void Write(List<Entry> list) =>
        AppSettings.SaveConstructorFilters(list.Count == 0 ? "" : JsonSerializer.Serialize(list));
}
