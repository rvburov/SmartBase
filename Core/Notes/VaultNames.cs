using System.Text.RegularExpressions;

namespace SmartBase.Core.Notes;

/// <summary>
/// Имена файлов и папок хранилища: проверка введенного и подбор свободного.
/// </summary>
public static partial class VaultNames
{
    public const int MaxNameLength = 200;

    // Проверяем ЕДИНЫЙ (самый строгий) набор на всех системах, а не тот, что запрещает текущая
    // ОС. Хранилище заметок обычно лежит в облачной папке и уезжает на другие машины: файл
    // «а?б.md», созданный на macOS, на Windows просто не появится — и человек решит, что
    // программа потеряла заметку.
    private static readonly char[] Invalid = ['\\', '/', ':', '*', '?', '"', '<', '>', '|', '\0'];

    private static readonly string[] Reserved =
    [
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    ];

    /// <summary>null — имя годное; иначе текст ошибки для баннера диалога.</summary>
    public static string? Validate(string? name)
    {
        var n = (name ?? "").Trim();

        if (n.Length == 0)  return "Укажите название";
        if (n.Length > MaxNameLength) return $"Слишком длинное название (не больше {MaxNameLength} символов)";
        if (n is "." or "..") return "Недопустимое название";
        // Пробелы по краям срезаны выше — остается точка: Windows молча отбрасывает ее в конце
        // имени, и «Итог.» стал бы «Итог», а следующая проверка занятости соврала бы.
        if (n.EndsWith('.')) return "Название не может заканчиваться точкой";
        if (n.StartsWith('.')) return "Название не может начинаться с точки — такие файлы скрыты";
        if (n.IndexOfAny(Invalid) >= 0) return "В названии нельзя использовать  \\ / : * ? \" < > |";
        if (Reserved.Contains(HeadOf(n), StringComparer.OrdinalIgnoreCase))
            return $"«{n}» — служебное имя Windows, выберите другое";

        return null;
    }

    /// <summary>Часть имени до первой точки — именно ее Windows считает служебным именем
    /// («NUL.md» запрещен так же, как «NUL»).</summary>
    private static string HeadOf(string name)
    {
        var i = name.IndexOf('.');
        return i < 0 ? name : name[..i];
    }

    /// <summary>Дописывает «.md», если расширения markdown еще нет.</summary>
    public static string EnsureMdExtension(string? name)
    {
        var n = (name ?? "").Trim();
        return n.Length > 0 && VaultRules.IsMarkdown(n) ? n : n + ".md";
    }

    /// <summary>Расширение с точкой («.md») или пустая строка. У папок расширения не бывает:
    /// точка в имени папки — часть имени.</summary>
    public static string ExtensionOf(string name, bool isFolder)
    {
        if (isFolder) return "";
        var ext = Path.GetExtension(name);
        // «.gitignore» GetExtension отдает целиком — тогда основы не остается, считаем без расширения.
        return ext.Length >= name.Length ? "" : ext;
    }

    /// <summary>
    /// Свободное имя: если <paramref name="desired"/> не занято — оно само, иначе
    /// «(копия)»-вариант. Тот же формат, что у дубля в конструкторе: человек видит одинаковые
    /// имена в обоих разделах.
    /// </summary>
    public static string NextFreeName(string desired, bool isFolder, Func<string, bool> exists) =>
        exists(desired) ? NextCopyName(desired, isFolder, exists) : desired;

    /// <summary>
    /// Имя копии в стиле macOS, без стакания слова «копия»: «X (копия)», «X (копия) 2» …
    /// У файла суффикс встает ПЕРЕД расширением — иначе «Заметка.md (копия)» перестанет быть
    /// markdown и не откроется.
    /// </summary>
    public static string NextCopyName(string sourceName, bool isFolder, Func<string, bool> exists)
    {
        var ext  = ExtensionOf(sourceName, isFolder);
        var body = sourceName[..^ext.Length];
        var stem = StripCopySuffix(body);
        if (stem.Length == 0) stem = body;

        string Make(int n) => n <= 1 ? $"{stem} (копия){ext}" : $"{stem} (копия) {n}{ext}";

        var name = Make(1);
        var k = 2;
        while (exists(name)) name = Make(k++);
        return name;
    }

    /// <summary>Убирает хвостовые суффиксы копий — «(копия)» и «(копия) N», один или несколько:
    /// «Отчет (копия) 2» → «Отчет», «Отчет (копия) (копия)» → «Отчет».</summary>
    public static string StripCopySuffix(string? name) =>
        CopySuffix().Replace(name ?? "", "").TrimEnd();

    [GeneratedRegex(@"(\s+\(копия\)(\s+\d+)?)+$")]
    private static partial Regex CopySuffix();
}
