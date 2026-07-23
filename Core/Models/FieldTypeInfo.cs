namespace SmartBase.Core.Models;

/// <summary>
/// Классификация кодов типов свойств (FieldType) — доменная логика без привязки к UI.
/// «Вычисляемые» типы (created_at/updated_at/created_by/updated_by) не хранят значение в
/// db_field_values: их ячейка read-only и заполняется из аудит-метаданных записи
/// (CreatedAt/UpdatedAt/CreatedBy/UpdatedBy). Единый источник правды для таблицы, панели
/// записи и диалога свойства.
/// </summary>
public static class FieldTypeInfo
{
    /// <summary>true — значение авто из метаданных записи, ячейка не редактируется.</summary>
    public static bool IsComputed(string? fieldType) => fieldType is
        "created_at" or "updated_at" or "created_by" or "updated_by";

    /// <summary>true — тип-дата (обычная «date» или вычисляемые даты): значение форматируется как дата.</summary>
    public static bool IsDateLike(string? fieldType) => fieldType is
        "date" or "created_at" or "updated_at";

    /// <summary>true — тип с редактируемым набором опций в ConfigJson: «select»/«status» (одно
    /// значение) и «multiselect» (несколько). Управляет показом блока «Пункты списка» в диалоге
    /// свойства. «status» отличается тремя опциями по умолчанию; «multiselect» хранит значение
    /// JSON-массивом (см. <see cref="MultiSelectValue"/>), остальные — обычной строкой.</summary>
    public static bool IsOptionList(string? fieldType) => fieldType is
        "select" or "status" or "multiselect";

    /// <summary>Русская подпись типа свойства — единый источник для диалога свойства, шапки
    /// меню свойства и т.п. Неизвестный тип → «Текст».</summary>
    public static string Label(string? fieldType) => fieldType switch
    {
        "text"       => "Текст",
        "number"     => "Число",
        "date"       => "Дата",
        "select"     => "Список",
        "status"     => "Статус",
        "multiselect"=> "Мультисписок",
        "checkbox"   => "Чекбокс",
        "url"        => "Ссылка",
        "user"       => "Пользователь",
        "created_at" => "Дата создания",
        "updated_at" => "Дата изменения",
        "created_by" => "Создание пользователем",
        "updated_by" => "Изменение пользователем",
        _            => "Текст",
    };

    /// <summary>Значение «вычисляемого» типа из аудит-метаданных записи (пусто, если тип не вычисляемый
    /// или метаданных нет — например, у старых записей автор неизвестен).</summary>
    public static string ComputedValue(DbRecord? record, string? fieldType) => fieldType switch
    {
        "created_at" => record?.CreatedAt ?? "",
        "updated_at" => record?.UpdatedAt ?? "",
        "created_by" => record?.CreatedBy ?? "",
        "updated_by" => record?.UpdatedBy ?? "",
        _            => "",
    };

    /// <summary>Отображение «вычисляемого» значения: даты (ISO "o", с долями секунды/смещением —
    /// DateValue его не парсит) форматируем round-trip как «дд.MM.гггг чч:мм»; имя пользователя —
    /// как есть; пусто → пусто. Общий формат для таблицы и панели записи.</summary>
    public static string FormatComputedDisplay(string? raw, string? fieldType)
    {
        var s = raw ?? "";
        if (string.IsNullOrEmpty(s)) return "";
        if (fieldType is "created_by" or "updated_by") return s;
        return System.DateTime.TryParse(s, System.Globalization.CultureInfo.InvariantCulture,
                                        System.Globalization.DateTimeStyles.RoundtripKind, out var dt)
            ? dt.ToLocalTime().ToString("dd.MM.yyyy HH:mm", System.Globalization.CultureInfo.InvariantCulture)
            : s;
    }
}
