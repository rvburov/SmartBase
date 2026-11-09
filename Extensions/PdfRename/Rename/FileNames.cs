using System.Text;
using SmartBase.Extensions.PdfRename.Recognition;

namespace SmartBase.Extensions.PdfRename.Rename;

/// <summary>
/// Итоговое имя файла «Обозначение Наименование.pdf». Правила одинаковы на всех ОС —
/// по самой строгой (Windows): документы лежат и на общих сетевых папках.
/// <list type="bullet">
/// <item>прямые двойные кавычки заменяются на одинарные (смысл не меняется);</item>
/// <item>символы <c>&lt; &gt; : / \ | ? *</c> и управляющие — имя не формируется, файл
/// пропускается: заменять их значило бы менять наименование;</item>
/// <item>имя не длиннее 255 символов и 255 байт UTF-8 (предел имени файла в NTFS/ext4);</item>
/// <item>зарезервированные имена Windows (CON, NUL, COM1…) запрещены.</item>
/// </list>
/// </summary>
internal static class FileNames
{
    public const string Extension = ".pdf";
    public const int MaxNameChars = 255;
    public const int MaxNameBytes = 255;

    private static readonly char[] Forbidden = ['<', '>', ':', '/', '\\', '|', '?', '*'];

    private static readonly HashSet<string> Reserved = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    public static (string? Name, string? Problem) Build(FieldValues values)
    {
        var designation = values.Designation.Trim();
        var name = values.Name.Replace('"', '\'').Trim();
        if (designation.Length == 0) return (null, "пустое обозначение");
        if (name.Length == 0) return (null, "пустое наименование");

        foreach (var part in new[] { designation, name })
        {
            var bad = part.FirstOrDefault(c => Forbidden.Contains(c) || char.IsControl(c));
            if (bad != default)
                return (null, $"в {(part == name ? "наименовании" : "обозначении")} символ «{bad}», недопустимый в имени файла");
        }

        var stem = $"{designation} {name}".TrimEnd(' ', '.');
        if (Reserved.Contains(stem)) return (null, "имя совпадает с зарезервированным именем Windows");
        var file = stem + Extension;
        if (file.Length > MaxNameChars) return (null, $"итоговое имя длиннее {MaxNameChars} символов ({file.Length})");
        if (Encoding.UTF8.GetByteCount(file) > MaxNameBytes)
            return (null, $"итоговое имя длиннее {MaxNameBytes} байт — предел имени файла в файловой системе");
        return (file, null);
    }

    /// <summary>Имена сравниваются без учета регистра на Windows и macOS (там так устроены
    /// файловые системы по умолчанию), с учетом — на Linux.</summary>
    public static StringComparison PathComparison =>
        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
}
