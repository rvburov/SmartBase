using System.Text.Json;

namespace SmartBase.Core.Models;

/// <summary>Пара свойств автоподбора: значение свойства записи-источника совпадает со значением
/// свойства записи-цели.</summary>
public sealed record RelationAutoPair(string SourceField, string TargetField);

/// <summary>Можно ли подбирать связи по правилу. Кроме <see cref="Ready"/> подбор не идет, прежние
/// связи остаются, поле остается автоматическим.</summary>
public enum RelationAutoState
{
    Ready,
    /// <summary>Пары потеряны: правило копировали или меняли в прежней версии программы, и в
    /// настройке осталась одна заглушка.</summary>
    Lost,
    /// <summary>Правило задано более новой версией программы; хранится как есть.</summary>
    Newer,
    /// <summary>Настройка автоподбора повреждена; хранится как есть.</summary>
    Damaged,
}

/// <summary>
/// Правило автоподбора связи: от одной до <see cref="MaxPairs"/> пар свойств, запись связывается,
/// когда совпали все пары. Пустое значение первой пары не связывается; в остальных парах пустое
/// совпадает только с пустым (у ИП нет КПП — он связывается по ИНН). <see cref="Fold"/> — текст
/// сравнивается без учета регистра и пробелов по краям (<see cref="RelationAutoMatch.FoldText"/>).
///
/// Формат 1 — одна пара с точным сравнением, как в прежних версиях:
/// <c>{"sourceField":"12","targetField":"34"}</c>. Остальные правила прежняя версия посчитала бы
/// иначе, поэтому они пишутся форматом 2: в старое место кладется заглушка (свойство цели, которого
/// нет), и прежняя версия отказывается подбирать, не трогая связи; пары лежат рядом:
/// <c>{"sourceField":"__title__","targetField":"2147483647","format":2,"pairs":[...],"text":"fold"}</c>.
/// Любое изменение смысла сопоставления повышает <see cref="Format"/>.
/// </summary>
public sealed class RelationAutoRule : IEquatable<RelationAutoRule>
{
    public const int Format = 2;
    public const int MaxPairs = 3;
    public const string GuardSource = RelationConfig.TitleField;
    public const string GuardTarget = "2147483647";

    public IReadOnlyList<RelationAutoPair> Pairs { get; }
    public bool Fold { get; }
    public RelationAutoState State { get; }
    /// <summary>Исходный JSON правила, которое не прочитано (<see cref="RelationAutoState.Newer"/>,
    /// <see cref="RelationAutoState.Damaged"/>): пишется обратно без изменений.</summary>
    public string? Raw { get; }

    public RelationAutoRule(string sourceField, string targetField) : this([new RelationAutoPair(sourceField, targetField)]) { }

    /// <summary>Готовое правило; точные повторы пар убираются, порядок пользователя сохраняется.</summary>
    public RelationAutoRule(IEnumerable<RelationAutoPair> pairs, bool fold = false)
    {
        Pairs = pairs.Distinct().ToList().AsReadOnly();
        if (Pairs.Count is 0 or > MaxPairs) throw new ArgumentException("В правиле автоподбора — от одной до трех пар свойств.");
        Fold = fold;
        State = RelationAutoState.Ready;
    }

    private RelationAutoRule(RelationAutoState state, string? raw)
    {
        Pairs = [];
        State = state;
        Raw = raw;
    }

    public static RelationAutoRule LostRule() => new(RelationAutoState.Lost, null);

    public bool IsReady => State == RelationAutoState.Ready;
    /// <summary>Правило прежняя версия считает так же: одна пара, точное сравнение.</summary>
    public bool FitsLegacyFormat => IsReady && Pairs.Count == 1 && !Fold;
    public IEnumerable<string> SourceKeys => Pairs.Select(p => p.SourceField);
    public IEnumerable<string> TargetKeys => Pairs.Select(p => p.TargetField);

    /// <summary>Переписать ключи пар (копия коллекции, перенос). Непрочитанное правило в копии
    /// становится потерянным: его номера свойств указали бы на чужие свойства.</summary>
    public RelationAutoRule MapKeys(Func<string, string> source, Func<string, string> target) => IsReady
        ? new(Pairs.Select(p => new RelationAutoPair(source(p.SourceField), target(p.TargetField))), Fold)
        : LostRule();

    /// <summary>Разобрать объект «auto». null — правила нет (старое место нечитаемо): связь ручная.</summary>
    internal static RelationAutoRule? Read(JsonElement auto)
    {
        if (auto.ValueKind != JsonValueKind.Object) return null;
        var source = Key(auto, "sourceField");
        var target = Key(auto, "targetField");
        if (!auto.TryGetProperty("format", out var format))
        {
            if (source is null || target is null) return null;
            return source == GuardSource && target == GuardTarget ? LostRule() : new RelationAutoRule(source, target);
        }
        // В одном виде записи, как его пишет эта версия: с ним сверяется сохранение без изменений.
        var raw = JsonSerializer.Serialize(auto);
        if (format.ValueKind != JsonValueKind.Number || !format.TryGetInt32(out var version) || version < Format)
            return new(RelationAutoState.Damaged, raw);
        if (version > Format) return new(RelationAutoState.Newer, raw);
        if (source != GuardSource || target != GuardTarget) return new(RelationAutoState.Damaged, raw);
        var fold = false;
        List<RelationAutoPair>? pairs = null;
        foreach (var property in auto.EnumerateObject())
            switch (property.Name)
            {
                case "sourceField" or "targetField" or "format":
                    break;
                case "text" when property.Value.ValueKind == JsonValueKind.String && property.Value.GetString() is "exact" or "fold":
                    fold = property.Value.GetString() == "fold";
                    break;
                case "pairs" when property.Value.ValueKind == JsonValueKind.Array:
                    pairs = [];
                    foreach (var item in property.Value.EnumerateArray())
                    {
                        if (item.ValueKind != JsonValueKind.Object || item.EnumerateObject().Count() != 2
                            || Key(item, "sourceField") is not { } s || Key(item, "targetField") is not { } t)
                            return new(RelationAutoState.Damaged, raw);
                        pairs.Add(new RelationAutoPair(s, t));
                    }
                    break;
                default:
                    return new(RelationAutoState.Damaged, raw);
            }
        // Лишняя пара — отказ, а не обрезка: правило шире настоящего дало бы неверные связи.
        if (pairs is null || pairs.Count is 0 or > MaxPairs) return new(RelationAutoState.Damaged, raw);
        return new RelationAutoRule(pairs, fold);
    }

    private static string? Key(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
        && value.GetString() is { } key && RelationConfig.IsFieldKey(key) ? key : null;

    internal object ToConfigObject()
    {
        if (Raw is not null) return JsonDocument.Parse(Raw).RootElement.Clone();
        if (State == RelationAutoState.Lost) return new Dictionary<string, object> { ["sourceField"] = GuardSource, ["targetField"] = GuardTarget };
        if (FitsLegacyFormat)
            return new Dictionary<string, object> { ["sourceField"] = Pairs[0].SourceField, ["targetField"] = Pairs[0].TargetField };
        // Порядок ключей постоянный: с сериализованной настройкой сверяется предварительный просмотр.
        var value = new Dictionary<string, object>
        {
            ["sourceField"] = GuardSource, ["targetField"] = GuardTarget, ["format"] = Format,
            ["pairs"] = Pairs.Select(p => new Dictionary<string, string> { ["sourceField"] = p.SourceField, ["targetField"] = p.TargetField }).ToList(),
        };
        if (Fold) value["text"] = "fold";
        return value;
    }

    public bool Equals(RelationAutoRule? other) => other is not null && State == other.State && Fold == other.Fold
        && Raw == other.Raw && Pairs.SequenceEqual(other.Pairs);

    public override bool Equals(object? obj) => Equals(obj as RelationAutoRule);

    public override int GetHashCode() => HashCode.Combine(State, Fold, Raw, Pairs.Count > 0 ? Pairs[0] : null, Pairs.Count);

    public static bool operator ==(RelationAutoRule? a, RelationAutoRule? b) => a is null ? b is null : a.Equals(b);
    public static bool operator !=(RelationAutoRule? a, RelationAutoRule? b) => !(a == b);
}
