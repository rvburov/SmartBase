using System.Text.Json;

namespace SmartBase.Core.Models;

/// <summary>
/// Настройка поля-связи (тип «relation»): на какую коллекцию ссылаемся, сколько записей можно
/// связать в одной ячейке и каким разделителем склеивать названия при показе. Сами связи (рёбра)
/// хранятся не здесь и не в db_field_values, а в таблице db_record_links (см. DbRecordLinkEntity) —
/// тут только параметры поля из ConfigJson:
/// <c>{ "targetCollectionId": N, "single": false, "separator": "comma" }</c>.
/// <see cref="TargetCollectionId"/> == 0 — поле-связь ещё не настроено (цель не выбрана).
/// <see cref="Single"/> — true: ровно одна связанная запись (выбор заменяет прежнюю); false:
/// несколько (many-to-many). <see cref="Separator"/> — ключ формата склейки названий (как у
/// «Мультисписка»: comma/space/semicolon/newline; см. <see cref="MultiSelectValue.SeparatorFor"/>).
/// </summary>
public sealed record RelationConfig(int TargetCollectionId, bool Single, string Separator = "comma")
{
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
            if (target <= 0) return null;
            return new RelationConfig(target, single, separator);
        }
        catch (JsonException) { return null; }
    }

    /// <summary>Строка-склейка названий для показа (из ключа <see cref="Separator"/>).</summary>
    public string SeparatorString => MultiSelectValue.SeparatorFor(Separator);

    /// <summary>Объект для сериализации в ConfigJson поля.</summary>
    public object ToConfigObject() => new { targetCollectionId = TargetCollectionId, single = Single, separator = Separator };

    /// <summary>ConfigJson-строка целиком (у поля «связь» иных настроек нет).</summary>
    public string ToConfigJson() => JsonSerializer.Serialize(ToConfigObject());
}
