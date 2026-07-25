using System.Text.Json;

namespace SmartBase.Core.Models;

/// <summary>
/// Настройка поля-связи (тип «relation»): на какую коллекцию ссылаемся, сколько записей можно
/// связать в одной ячейке и каким разделителем склеивать названия при показе. Сами связи (рёбра)
/// хранятся не здесь и не в db_field_values, а в таблице db_record_links (см. DbRecordLinkEntity) —
/// тут только параметры поля из ConfigJson:
/// <c>{ "targetDbId": 0, "targetCollectionId": N, "single": false, "separator": "comma" }</c>.
/// <see cref="TargetCollectionId"/> == 0 — поле-связь ещё не настроено (цель не выбрана).
/// <see cref="TargetDbId"/> — межбазовая цель: 0 = целевая коллекция в ЭТОЙ же БД (обычный случай);
/// &gt;0 = стабильный DbId целевой БД из реестра app.db (коллекция и записи — в другом файле).
/// <see cref="Single"/> — true: ровно одна связанная запись (выбор заменяет прежнюю); false:
/// несколько (many-to-many). <see cref="Separator"/> — ключ формата склейки названий (как у
/// «Мультисписка»: comma/space/semicolon/newline; см. <see cref="MultiSelectValue.SeparatorFor"/>).
/// </summary>
public sealed record RelationConfig(int TargetCollectionId, bool Single, string Separator = "comma", int TargetDbId = 0)
{
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

            var target = root.TryGetProperty("targetCollectionId", out var t) && t.TryGetInt32(out var id) ? id : 0;
            var single = root.TryGetProperty("single", out var s) && s.ValueKind is JsonValueKind.True or JsonValueKind.False
                         && s.GetBoolean();
            var separator = root.TryGetProperty("separator", out var sep) && sep.ValueKind == JsonValueKind.String
                            && sep.GetString() is { Length: > 0 } sk ? sk : "comma";
            var targetDb = root.TryGetProperty("targetDbId", out var td) && td.TryGetInt32(out var dbId) && dbId > 0 ? dbId : 0;
            if (target <= 0) return null;
            return new RelationConfig(target, single, separator, targetDb);
        }
        catch (JsonException) { return null; }
    }

    /// <summary>Строка-склейка названий для показа (из ключа <see cref="Separator"/>).</summary>
    public string SeparatorString => MultiSelectValue.SeparatorFor(Separator);

    /// <summary>Объект для сериализации в ConfigJson поля. targetDbId пишем только для межбазовой
    /// цели — внутрибазовые конфиги остаются как раньше (0 не засоряет JSON).</summary>
    public object ToConfigObject() => TargetDbId > 0
        ? (object)new { targetCollectionId = TargetCollectionId, single = Single, separator = Separator, targetDbId = TargetDbId }
        : new { targetCollectionId = TargetCollectionId, single = Single, separator = Separator };

    /// <summary>ConfigJson-строка целиком (у поля «связь» иных настроек нет).</summary>
    public string ToConfigJson() => JsonSerializer.Serialize(ToConfigObject());
}
