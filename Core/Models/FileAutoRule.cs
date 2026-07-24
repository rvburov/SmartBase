using System.Text.Json;

namespace SmartBase.Core.Models;

/// <summary>
/// Правило авто-привязки файлов для поля типа «файл»: файл берётся не выбором пользователя, а
/// сопоставлением по имени. Для каждой записи имя = значение текст-столбца
/// (<see cref="SourceFieldId"/>) + «.» + <see cref="Format"/>; ищется в <see cref="Folder"/>
/// (только верхний уровень, регистр учитывается). Найденный путь материализуется в
/// db_field_values (чтобы работали фильтр/сортировка), сам столбец становится read-only.
/// Хранится в ConfigJson поля: <c>{ "auto": { "sourceFieldId": N, "folder": "…", "format": "pdf" } }</c>.
/// null (правила нет) — поле работает как обычный ручной «файл».
/// </summary>
public sealed record FileAutoRule(int SourceFieldId, string Folder, string Format)
{
    /// <summary>Разобрать правило из ConfigJson поля. null — правила нет / конфиг нечитаем /
    /// не заданы папка или формат (без них правило неполно и не применяется).</summary>
    public static FileAutoRule? FromConfig(string? configJson)
    {
        if (string.IsNullOrWhiteSpace(configJson)) return null;
        try
        {
            using var doc = JsonDocument.Parse(configJson);
            if (!doc.RootElement.TryGetProperty("auto", out var a) || a.ValueKind != JsonValueKind.Object)
                return null;

            var src    = a.TryGetProperty("sourceFieldId", out var s) && s.TryGetInt32(out var id) ? id : 0;
            var folder = a.TryGetProperty("folder", out var f) && f.ValueKind == JsonValueKind.String ? f.GetString() ?? "" : "";
            var format = a.TryGetProperty("format", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() ?? "" : "";

            folder = folder.Trim();
            format = NormalizeFormat(format);
            if (src <= 0 || folder.Length == 0 || format.Length == 0) return null;
            return new FileAutoRule(src, folder, format);
        }
        catch (JsonException) { return null; }
    }

    /// <summary>Объект для сериализации в ConfigJson (под ключом «auto»).</summary>
    public static object ToConfigObject(FileAutoRule rule) => new
    {
        auto = new { sourceFieldId = rule.SourceFieldId, folder = rule.Folder, format = rule.Format },
    };

    /// <summary>ConfigJson-строка целиком (у поля «файл» иных настроек нет).</summary>
    public string ToConfigJson() => JsonSerializer.Serialize(ToConfigObject(this));

    /// <summary>Имя искомого файла для значения столбца: «значение.формат». Пустое значение
    /// столбца → пустое имя (совпадений нет → «файл не найден»).</summary>
    public string TargetFileName(string? sourceValue)
    {
        var v = sourceValue ?? "";
        return v.Length == 0 ? "" : $"{v}.{Format}";
    }

    // Нормализуем формат: без ведущей точки и пробелов, нижний регистр расширения не форсируем —
    // сравнение имён идёт как есть (регистр учитывается пользователем при вводе значения).
    private static string NormalizeFormat(string format) => format.Trim().TrimStart('.');
}
