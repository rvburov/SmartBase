using System.Text.Json;

namespace SmartBase.Core.Models;

/// <summary>
/// Правило авто-привязки файлов для поля типа «файл»: файл берется не выбором пользователя, а
/// сопоставлением по имени. Для каждой записи имя = значение столбца-источника
/// (<see cref="SourceKey"/>) + «.» + <see cref="Format"/>; ищется в <see cref="Folder"/>
/// (регистр учитывается). <see cref="PartialMatch"/> — значение входит в имя без расширения,
/// привязываются все совпадения. <see cref="Recursive"/> = искать и во вложенных папках.
/// Найденный путь материализуется в db_field_values (чтобы работали фильтр/сортировка),
/// сам столбец становится read-only, пока правило включено.
///
/// Источник — строковый ключ колонки (как в сортировке/фильтре/экспорте): системное
/// «Название» (<see cref="TitleSource"/>, значение в db_records.Title) либо числовой Id
/// текст-поля (значения в db_field_values). «Описание» — обычное текст-поле, поэтому берется
/// вторым путем, по своему Id.
///
/// Хранится в ConfigJson поля:
/// <c>{ "auto": { "source": "…", "folder": "…", "format": "pdf", "recursive": true, "partialMatch": false, "enabled": true } }</c>.
/// Без правила или при enabled:false поле работает как обычный ручной «файл».
/// </summary>
/// <param name="Recursive">
/// Искать во вложенных папках. Ключа нет в конфиге — <c>false</c>: правила, созданные до
/// появления настройки, работали по верхнему уровню, и молча расширять их охват нельзя —
/// у поля разом поменялись бы значения. Новые правила диалог создает с включенной галочкой
/// (<see cref="DefaultRecursive"/>).
/// </param>
/// <param name="PartialMatch">Искать значение внутри имени файла. По умолчанию выключено:
/// существующие правила сохраняют точное сопоставление.</param>
/// <param name="Enabled">Применять правило. Выключенное правило сохраняет настройки,
/// но не меняет значения и оставляет поле доступным для ручного выбора.</param>
public sealed record FileAutoRule(string SourceKey, string Folder, string Format, bool Recursive = false,
                                  bool PartialMatch = false, bool Enabled = true)
{
    /// <summary>Состояние галочки «Искать во вложенных папках» у нового свойства.</summary>
    public const bool DefaultRecursive = true;

    /// <summary>Ключ системной колонки «Название» (значение — db_records.Title).</summary>
    public const string TitleSource = "__title__";

    /// <summary>Источник — системная колонка «Название».</summary>
    public bool SourceIsTitle => SourceKey == TitleSource;
    /// <summary>Числовой Id текст-поля-источника; 0 — источник системный («Название»).</summary>
    public int SourceFieldId => int.TryParse(SourceKey, out var id) ? id : 0;

    /// <summary>Применяемое правило: включено и заданы источник, папка и формат.
    /// Выключенные и неполные настройки доступны через <see cref="ReadConfigured"/>.</summary>
    public static FileAutoRule? FromConfig(string? configJson)
    {
        var rule = ReadConfigured(configJson);
        return rule is { Enabled: true } && rule.Folder.Length > 0 && rule.Format.Length > 0
            && (rule.SourceIsTitle || rule.SourceFieldId > 0) ? rule : null;
    }

    /// <summary>Сохраненные настройки, включая выключенный неполный черновик.
    /// Старый JSON без enabled остается включенным; чтение не запускает поиск файлов.</summary>
    public static FileAutoRule? ReadConfigured(string? configJson)
    {
        if (string.IsNullOrWhiteSpace(configJson)) return null;
        try
        {
            using var doc = JsonDocument.Parse(configJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Object
                || !doc.RootElement.TryGetProperty("auto", out var a) || a.ValueKind != JsonValueKind.Object)
                return null;
            var enabled = true;
            if (a.TryGetProperty("enabled", out var e))
            {
                if (e.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) return null;
                enabled = e.ValueKind == JsonValueKind.True;
            }

            // Новый формат — строковый ключ «source»; старый (совместимость) — числовой «sourceFieldId».
            string source;
            if (a.TryGetProperty("source", out var sc) && sc.ValueKind == JsonValueKind.String)
                source = sc.GetString() ?? "";
            else if (a.TryGetProperty("sourceFieldId", out var s) && s.ValueKind == JsonValueKind.Number
                     && s.TryGetInt32(out var id) && id > 0)
                source = id.ToString();
            else
                source = "";

            var folder = a.TryGetProperty("folder", out var f) && f.ValueKind == JsonValueKind.String ? f.GetString() ?? "" : "";
            var format = a.TryGetProperty("format", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() ?? "" : "";
            // Ключа нет — правило из баз, где настройки еще не было: там искали только по
            // верхнему уровню, таким и оставляем (см. комментарий к Recursive).
            var recursive = a.TryGetProperty("recursive", out var r) && r.ValueKind == JsonValueKind.True;
            var partialMatch = a.TryGetProperty("partialMatch", out var p) && p.ValueKind == JsonValueKind.True;

            source = source.Trim();
            folder = folder.Trim();
            format = NormalizeFormat(format);
            // Нераспознанный ключ не становится «Названием»: FromConfig не применит такое
            // правило, а диалог покажет незаполненный источник и позволит исправить настройки.
            return new FileAutoRule(source, folder, format, recursive, partialMatch, enabled);
        }
        catch (JsonException ex)
        {
            ConfigJsonLog.Broken("файл: авто-привязка", configJson, ex);
            return null;
        }
    }

    /// <summary>Объект для сериализации в ConfigJson (под ключом «auto»).</summary>
    public static object ToConfigObject(FileAutoRule rule) => new
    {
        auto = new { source = rule.SourceKey, folder = rule.Folder, format = rule.Format,
                     recursive = rule.Recursive, partialMatch = rule.PartialMatch, enabled = rule.Enabled },
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

    /// <summary>Сопоставление базового имени файла с источником: точное либо подстрока без
    /// расширения. Формат и регистр проверяются в обоих режимах; шаблонов с * или ? нет.</summary>
    public bool MatchesFileName(string? sourceValue, string fileName)
    {
        if (!PartialMatch)
            return !string.IsNullOrEmpty(sourceValue)
                && string.Equals(fileName, TargetFileName(sourceValue), StringComparison.Ordinal);
        if (string.IsNullOrWhiteSpace(sourceValue)) return false;
        var suffix = "." + Format;
        return fileName.EndsWith(suffix, StringComparison.Ordinal)
            && fileName.AsSpan(0, fileName.Length - suffix.Length)
                .Contains(sourceValue.AsSpan(), StringComparison.Ordinal);
    }

    // Нормализуем формат: без ведущей точки и пробелов, нижний регистр расширения не форсируем —
    // сравнение имен идет как есть (регистр учитывается пользователем при вводе значения).
    private static string NormalizeFormat(string format) => format.Trim().TrimStart('.');
}
