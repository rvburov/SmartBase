using System.Text.Json;
using System.Text.Json.Nodes;

namespace SmartBase.Core.Models;

/// <summary>
/// Метка конфиденциальности. Порядок значений значим: чем больше число, тем строже, и на этом
/// держится выбор строжайшего при наследовании.
/// </summary>
public enum ConfidentialityLabel
{
    /// <summary>Открыто — обычные данные, отправлять можно.</summary>
    Open = 0,

    /// <summary>Для служебного пользования — пометка для людей и журнала.</summary>
    Restricted = 1,

    /// <summary>Закрытый контур — наружу не уходит ни при каких настройках.</summary>
    Closed = 2,
}

/// <summary>
/// Метки конфиденциальности базы и свойства: чему разрешено покидать программу, а чему нет.
/// Метка живет вместе с данными, а не рядом со сценарием, — как и в сторонних системах меток
/// (см. docs/ROADMAP-API.md, раздел 10.6): база уезжает в другую папку вместе со своей меткой,
/// поле — вместе со своей.
///
/// Два уровня, и они делают разное:
///
/// <list type="bullet">
///   <item><b>База</b> — ключ <c>confidentiality</c> в <c>db_meta</c>, то есть внутри самого
///   файла базы. <see cref="ConfidentialityLabel.Closed"/> запрещает базе исходящие целиком;
///   <see cref="ConfidentialityLabel.Restricted"/> — пометка, которую видно на экранах и в
///   журнале, но обмен она не запрещает.</item>
///   <item><b>Свойство</b> — ключ <c>confidentiality</c> в его <c>ConfigJson</c>. Любая метка
///   строже «Открыто» означает, что поле нельзя подставить в исходящий шаг.</item>
/// </list>
///
/// Почему метка базы «Для служебного пользования» НЕ запрещает поля разом: база «Заказы» может
/// быть служебной и при этом обмениваться с учетной системой — иначе метку просто не поставят
/// ни на одну рабочую базу, и весь механизм окажется бумажным. Грубый рубеж — «Закрытый
/// контур»; точный — метка на поле.
///
/// Незнакомое значение метки читается как <see cref="ConfidentialityLabel.Closed"/>: чужая
/// пометка из более новой версии или опечатка не должны означать «отправлять можно».
/// </summary>
public static class Confidentiality
{
    /// <summary>Ключ метки базы в <c>db_meta</c> и метки свойства в его <c>ConfigJson</c>.</summary>
    public const string Key = "confidentiality";

    private const string OpenValue       = "open";
    private const string RestrictedValue = "restricted";
    private const string ClosedValue     = "closed";

    /// <summary>Подпись для интерфейса.</summary>
    public static string Title(ConfidentialityLabel label) => label switch
    {
        ConfidentialityLabel.Restricted => "Для служебного пользования",
        ConfidentialityLabel.Closed     => "Закрытый контур",
        _                               => "Открыто",
    };

    /// <summary>Как метка ложится в хранилище.</summary>
    public static string ToStorage(ConfidentialityLabel label) => label switch
    {
        ConfidentialityLabel.Restricted => RestrictedValue,
        ConfidentialityLabel.Closed     => ClosedValue,
        _                               => OpenValue,
    };

    /// <summary>
    /// Разобрать хранимое значение. Пусто — метки нет, то есть «Открыто»: это обычное
    /// состояние, а не потеря данных. А вот значение, которое есть, но не разобралось, —
    /// «Закрытый контур»: метку ставили осознанно, и трактовать непонятную пометку как
    /// разрешение нельзя.
    /// </summary>
    public static ConfidentialityLabel Parse(string? stored)
    {
        if (string.IsNullOrWhiteSpace(stored)) return ConfidentialityLabel.Open;

        return stored.Trim().ToLowerInvariant() switch
        {
            OpenValue       => ConfidentialityLabel.Open,
            RestrictedValue => ConfidentialityLabel.Restricted,
            ClosedValue     => ConfidentialityLabel.Closed,
            _               => ConfidentialityLabel.Closed,
        };
    }

    /// <summary>Строжайшая из двух меток. Наследование только сужает: поле может быть строже базы, но не свободнее.</summary>
    public static ConfidentialityLabel Strictest(ConfidentialityLabel first, ConfidentialityLabel second) =>
        first > second ? first : second;

    /// <summary>
    /// Метка свойства из его <c>ConfigJson</c>. Ключа нет — «Открыто». Конфиг не разобрался —
    /// «Закрытый контур» и след в диагностике: у прочих настроек свойства сломанный конфиг
    /// откатывается к значению по умолчанию, но здесь «по умолчанию» означало бы разрешение,
    /// а сломанный конфиг не повод отправлять данные наружу.
    /// </summary>
    public static ConfidentialityLabel FromConfig(string? configJson)
    {
        if (string.IsNullOrWhiteSpace(configJson)) return ConfidentialityLabel.Open;

        try
        {
            using var document = JsonDocument.Parse(configJson);
            if (document.RootElement.ValueKind != JsonValueKind.Object) return ConfidentialityLabel.Closed;

            if (!document.RootElement.TryGetProperty(Key, out var value)) return ConfidentialityLabel.Open;
            if (value.ValueKind != JsonValueKind.String) return ConfidentialityLabel.Closed;

            return Parse(value.GetString());
        }
        catch (JsonException ex)
        {
            ConfigJsonLog.Broken("метка конфиденциальности", configJson, ex);
            return ConfidentialityLabel.Closed;
        }
    }

    /// <summary>
    /// Вписать метку в <c>ConfigJson</c> свойства, сохранив остальные его настройки: метка
    /// живет рядом с настройками типа свойства, а не вместо них. «Открыто» — не значение,
    /// а его отсутствие: ключ убирается, как это принято с настройками, равными стандарту.
    ///
    /// Сломанный конфиг переписывается целиком: восстановить из него нечего, а оставить поле
    /// без метки нельзя.
    /// </summary>
    public static string WriteToConfig(string? configJson, ConfidentialityLabel label)
    {
        JsonObject root;
        try
        {
            root = string.IsNullOrWhiteSpace(configJson)
                ? []
                : JsonNode.Parse(configJson) as JsonObject ?? [];
        }
        catch (JsonException)
        {
            root = [];
        }

        if (label == ConfidentialityLabel.Open) root.Remove(Key);
        else                                    root[Key] = ToStorage(label);

        return root.ToJsonString();
    }
}
