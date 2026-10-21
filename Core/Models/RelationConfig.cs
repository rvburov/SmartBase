using System.Text.Json;

namespace SmartBase.Core.Models;

/// <summary>
/// Настройка поля-связи (тип «relation»): на какую коллекцию ссылаемся, сколько записей можно
/// связать в одной ячейке и каким разделителем склеивать названия при показе. Сами связи (ребра)
/// хранятся не здесь и не в db_field_values, а в таблице db_record_links (см. DbRecordLinkEntity) —
/// тут только параметры поля из ConfigJson:
/// <c>{ "targetDbId": 0, "targetCollectionId": N, "single": false, "separator": "comma" }</c>.
/// <see cref="TargetCollectionId"/> == 0 — поле-связь еще не настроено (цель не выбрана).
/// <see cref="TargetDbId"/> — межбазовая цель: 0 = целевая коллекция в ЭТОЙ же БД (обычный случай);
/// &gt;0 = стабильный DbId целевой БД из реестра app.db (коллекция и записи — в другом файле).
/// <see cref="Single"/> — true: не более одной связанной записи (выбор заменяет прежнюю); false:
/// несколько (many-to-many); дефолт диалога — «Одну» (true). <see cref="Separator"/> — ключ формата
/// склейки названий (общий с «Мультисписком», 7 вариантов: comma/dot/space/semicolon/newline/
/// comma_tight/dot_tight; см. <see cref="MultiSelectValue.SeparatorFor"/>).
/// </summary>
public sealed record RelationConfig(int TargetCollectionId, bool Single, string Separator = "comma", int TargetDbId = 0,
    string DisplayField = "__title__", string? SearchField = null, RelationAutoRule? Auto = null,
    bool RestrictDelete = false)
{
    public const string TitleField = "__title__";

    /// <summary>Разделитель составного ключа свойств: показ и поиск по нескольким свойствам цели
    /// хранятся в <see cref="DisplayField"/>/<see cref="SearchField"/> одной строкой «123|__title__».
    /// Читатели свойств (ReadRelationPropertyValues и поиск) понимают такой ключ сами.</summary>
    public const char KeySeparator = '|';
    public const int MaxDisplayFields = 3;
    public const int MaxSearchFields = 5;

    public string EffectiveSearchField => SearchField ?? DisplayField;
    public IReadOnlyList<string> DisplayKeys => SplitKeys(DisplayField);
    public IReadOnlyList<string> SearchKeys => SplitKeys(EffectiveSearchField);

    public static IReadOnlyList<string> SplitKeys(string? composite) =>
        string.IsNullOrEmpty(composite) ? [] : composite.Split(KeySeparator);

    public static string JoinKeys(IEnumerable<string> keys) => string.Join(KeySeparator, keys.Distinct(StringComparer.Ordinal));

    /// <summary>Все ключи свойств цели, на которые ссылается настройка: показ, поиск, автоподбор.</summary>
    public IEnumerable<string> TargetPropertyKeys =>
        DisplayKeys.Concat(SearchKeys).Concat(Auto is null ? [] : [Auto.TargetField]).Distinct(StringComparer.Ordinal);
    /// <summary>Цель — в другой БД (межбазовая связь).</summary>
    public bool IsCrossDb => TargetDbId > 0;

    /// <summary>Разобрать настройку из ConfigJson поля. null — конфиг нечитаем или цель не задана
    /// (поле-связь без целевой коллекции ничего не связывает).</summary>
    public static RelationConfig? FromConfig(string? configJson)
    {
        if (string.IsNullOrWhiteSpace(configJson)) return null;
        try
        {
            using var doc = JsonDocument.Parse(configJson);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;

            var target = root.TryGetProperty("targetCollectionId", out var t) && t.ValueKind == JsonValueKind.Number && t.TryGetInt32(out var id) ? id : 0;
            var single = root.TryGetProperty("single", out var s) && s.ValueKind is JsonValueKind.True or JsonValueKind.False
                         && s.GetBoolean();
            var separator = root.TryGetProperty("separator", out var sep) && sep.ValueKind == JsonValueKind.String
                            && sep.GetString() is { Length: > 0 } sk ? sk : "comma";
            var targetDb = 0;
            if (root.TryGetProperty("targetDbId", out var td))
            {
                if (td.ValueKind != JsonValueKind.Number || !td.TryGetInt32(out var dbId)) return null;
                if (dbId > 0) targetDb = dbId;
            }
            // Несколько свойств — массив рядом с первым ключом: старая версия программы читает
            // свой «displayField» и показывает первое свойство.
            var display = ReadFields(root, "displayFields", MaxDisplayFields) ?? ReadField(root, "displayField") ?? TitleField;
            var search = ReadFields(root, "searchFields", MaxSearchFields) ?? ReadField(root, "searchField");
            RelationAutoRule? auto = null;
            if (root.TryGetProperty("auto", out var a) && a.ValueKind == JsonValueKind.Object
                && ReadField(a, "sourceField") is { } source && ReadField(a, "targetField") is { } destination)
                auto = new RelationAutoRule(source, destination);
            // Что делать с записью-целью при ее удалении: «clear» (по умолчанию) снимает связь,
            // «restrict» запрещает удалять цель, пока на нее ссылаются.
            var restrict = root.TryGetProperty("onDelete", out var onDelete) && onDelete.ValueKind == JsonValueKind.String
                           && onDelete.GetString() == "restrict";
            if (target <= 0) return null;
            return new RelationConfig(target, single, separator, targetDb, display, search, auto, restrict);
        }
        catch (JsonException ex)
        {
            ConfigJsonLog.Broken("связь", configJson, ex);
            return null;
        }
    }

    private static string? ReadField(JsonElement root, string name) =>
        root.TryGetProperty(name, out var field) && field.ValueKind == JsonValueKind.String
        && field.GetString() is { } key && IsFieldKey(key) ? key : null;

    private static string? ReadFields(JsonElement root, string name, int max)
    {
        if (!root.TryGetProperty(name, out var list) || list.ValueKind != JsonValueKind.Array) return null;
        var keys = new List<string>();
        foreach (var item in list.EnumerateArray())
            if (item.ValueKind == JsonValueKind.String && item.GetString() is { } key && IsFieldKey(key) && !keys.Contains(key))
                keys.Add(key);
        return keys.Count == 0 ? null : JoinKeys(keys.Take(max));
    }

    public static bool IsFieldKey(string? key) => key == TitleField
        || int.TryParse(key, out var id) && id > 0;

    /// <summary>Строка-склейка названий для показа (из ключа <see cref="Separator"/>).</summary>
    public string SeparatorString => MultiSelectValue.SeparatorFor(Separator);

    /// <summary>Объект для сериализации в ConfigJson поля. targetDbId пишем только для межбазовой
    /// цели — внутрибазовые конфиги остаются как раньше (0 не засоряет JSON).</summary>
    public object ToConfigObject()
    {
        var value = new Dictionary<string, object>
        {
            ["targetCollectionId"] = TargetCollectionId, ["single"] = Single, ["separator"] = Separator,
        };
        if (TargetDbId > 0) value["targetDbId"] = TargetDbId;
        if (DisplayField != TitleField) value["displayField"] = DisplayKeys[0];
        if (DisplayKeys.Count > 1) value["displayFields"] = DisplayKeys;
        if (SearchField is not null)
        {
            var search = SplitKeys(SearchField);
            value["searchField"] = search[0];
            if (search.Count > 1) value["searchFields"] = search;
        }
        if (Auto is not null) value["auto"] = new { sourceField = Auto.SourceField, targetField = Auto.TargetField };
        if (RestrictDelete) value["onDelete"] = "restrict";
        return value;
    }

    /// <summary>ConfigJson-строка целиком (у поля «связь» иных настроек нет).</summary>
    public string ToConfigJson() => JsonSerializer.Serialize(ToConfigObject());
}
