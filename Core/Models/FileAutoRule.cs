using System.Text.Json;

namespace SmartBase.Core.Models;

/// <summary>
/// Правило авто-привязки файлов для поля типа «файл»: файл берётся не выбором пользователя, а
/// сопоставлением по имени. Для каждой записи имя = значение столбца-источника
/// (<see cref="SourceKey"/>) + «.» + <see cref="Format"/>; ищется в <see cref="Folder"/>
/// (только верхний уровень, регистр учитывается). Найденный путь материализуется в
/// db_field_values (чтобы работали фильтр/сортировка), сам столбец становится read-only.
///
/// Источник — строковый ключ колонки (как в сортировке/фильтре/экспорте): системные
/// «Название»/«Описание» (<see cref="TitleSource"/>/<see cref="DescSource"/>, значения в
/// db_records) либо числовой Id текст-поля (значения в db_field_values). Так на источник можно
/// брать и системные колонки, которые в db_field_values не хранятся.
///
/// Хранится в ConfigJson поля: <c>{ "auto": { "source": "…", "folder": "…", "format": "pdf" } }</c>.
/// null (правила нет) — поле работает как обычный ручной «файл».
/// </summary>
public sealed record FileAutoRule(string SourceKey, string Folder, string Format)
{
    /// <summary>Ключ системной колонки «Название» (значение — db_records.Title).</summary>
    public const string TitleSource = "__title__";
    /// <summary>Ключ системной колонки «Описание» (значение — db_records.Description).</summary>
    public const string DescSource = "__desc__";

    /// <summary>Источник — системная колонка «Название».</summary>
    public bool SourceIsTitle => SourceKey == TitleSource;
    /// <summary>Источник — системная колонка «Описание».</summary>
    public bool SourceIsDesc => SourceKey == DescSource;
    /// <summary>Числовой Id текст-поля-источника; 0 — источник системный (Название/Описание).</summary>
    public int SourceFieldId => int.TryParse(SourceKey, out var id) ? id : 0;

    /// <summary>Разобрать правило из ConfigJson поля. null — правила нет / конфиг нечитаем /
    /// не заданы источник, папка или формат (без них правило неполно и не применяется).</summary>
    public static FileAutoRule? FromConfig(string? configJson)
    {
        if (string.IsNullOrWhiteSpace(configJson)) return null;
        try
        {
            using var doc = JsonDocument.Parse(configJson);
            if (!doc.RootElement.TryGetProperty("auto", out var a) || a.ValueKind != JsonValueKind.Object)
                return null;

            // Новый формат — строковый ключ «source»; старый (совместимость) — числовой «sourceFieldId».
            string source;
            if (a.TryGetProperty("source", out var sc) && sc.ValueKind == JsonValueKind.String)
                source = sc.GetString() ?? "";
            else if (a.TryGetProperty("sourceFieldId", out var s) && s.TryGetInt32(out var id) && id > 0)
                source = id.ToString();
            else
                source = "";

            var folder = a.TryGetProperty("folder", out var f) && f.ValueKind == JsonValueKind.String ? f.GetString() ?? "" : "";
            var format = a.TryGetProperty("format", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() ?? "" : "";

            source = source.Trim();
            folder = folder.Trim();
            format = NormalizeFormat(format);
            if (source.Length == 0 || folder.Length == 0 || format.Length == 0) return null;
            return new FileAutoRule(source, folder, format);
        }
        catch (JsonException) { return null; }
    }

    /// <summary>Объект для сериализации в ConfigJson (под ключом «auto»).</summary>
    public static object ToConfigObject(FileAutoRule rule) => new
    {
        auto = new { source = rule.SourceKey, folder = rule.Folder, format = rule.Format },
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
