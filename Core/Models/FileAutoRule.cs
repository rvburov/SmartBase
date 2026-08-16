using System.Text.Json;

namespace SmartBase.Core.Models;

/// <summary>
/// Правило авто-привязки файлов для поля типа «файл»: файл берётся не выбором пользователя, а
/// сопоставлением по имени. Для каждой записи имя = значение столбца-источника
/// (<see cref="SourceKey"/>) + «.» + <see cref="Format"/>; ищется в <see cref="Folder"/>
/// (регистр учитывается). <see cref="Recursive"/> = искать и во вложенных папках.
/// Найденный путь материализуется в db_field_values (чтобы работали фильтр/сортировка),
/// сам столбец становится read-only.
///
/// Источник — строковый ключ колонки (как в сортировке/фильтре/экспорте): системное
/// «Название» (<see cref="TitleSource"/>, значение в db_records.Title) либо числовой Id
/// текст-поля (значения в db_field_values). «Описание» — обычное текст-поле, поэтому берётся
/// вторым путём, по своему Id.
///
/// Хранится в ConfigJson поля:
/// <c>{ "auto": { "source": "…", "folder": "…", "format": "pdf", "recursive": true } }</c>.
/// null (правила нет) — поле работает как обычный ручной «файл».
/// </summary>
/// <param name="Recursive">
/// Искать во вложенных папках. Ключа нет в конфиге — <c>false</c>: правила, созданные до
/// появления настройки, работали по верхнему уровню, и молча расширять их охват нельзя —
/// у поля разом поменялись бы значения. Новые правила диалог создаёт с включённой галочкой
/// (<see cref="DefaultRecursive"/>).
/// </param>
public sealed record FileAutoRule(string SourceKey, string Folder, string Format, bool Recursive = false)
{
    /// <summary>Состояние галочки «Искать во вложенных папках» у нового свойства.</summary>
    public const bool DefaultRecursive = true;

    /// <summary>Ключ системной колонки «Название» (значение — db_records.Title).</summary>
    public const string TitleSource = "__title__";

    /// <summary>Источник — системная колонка «Название».</summary>
    public bool SourceIsTitle => SourceKey == TitleSource;
    /// <summary>Числовой Id текст-поля-источника; 0 — источник системный («Название»).</summary>
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
            // Ключа нет — правило из баз, где настройки ещё не было: там искали только по
            // верхнему уровню, таким и оставляем (см. комментарий к Recursive).
            var recursive = a.TryGetProperty("recursive", out var r) && r.ValueKind == JsonValueKind.True;

            source = source.Trim();
            // Нераспознанный ключ (например, «__desc__» из баз, где описание было системной
            // колонкой) — правило неполно: подставлять вместо него «Название» нельзя, это молча
            // переписало бы значения чужим столбцом.
            if (source != TitleSource && !(int.TryParse(source, out var srcId) && srcId > 0)) return null;
            folder = folder.Trim();
            format = NormalizeFormat(format);
            if (source.Length == 0 || folder.Length == 0 || format.Length == 0) return null;
            return new FileAutoRule(source, folder, format, recursive);
        }
        catch (JsonException) { return null; }
    }

    /// <summary>Объект для сериализации в ConfigJson (под ключом «auto»).</summary>
    public static object ToConfigObject(FileAutoRule rule) => new
    {
        auto = new { source = rule.SourceKey, folder = rule.Folder, format = rule.Format, recursive = rule.Recursive },
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
