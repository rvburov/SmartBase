using System.Text.Json;
using System.Collections.ObjectModel;
using System.Runtime.CompilerServices;

namespace SmartBase.Core.Models;

/// <summary>
/// Свойство типа «файл»: одиночный путь хранится как прежде, несколько — объектом с массивом
/// paths (ничего не копируется). Здесь — единый показ имен: путь сворачивается до имени файла
/// (базового), произвольное значение (после смены типа с любого другого) остается как есть.
/// Используется и ячейкой/страницей записи (FileEditor), и списком значений фильтра таблицы.
/// </summary>
public static class FileValue
{
    // Значение строки живет в модели строки таблицы. Повторный кадр получает тот же разбор,
    // а удаленная из модели строка вместе с массивом может быть собрана GC.
    private static readonly ConditionalWeakTable<string, ReadOnlyCollection<string>> Decoded = new();

    /// <summary>Пути свойства. Старое значение остается одиночным путем; только объект с массивом
    /// строк paths считается списком. Запятые в именах не служат разделителями.</summary>
    public static IReadOnlyList<string> ReadPaths(string? raw)
    {
        if (string.IsNullOrEmpty(raw)) return [];
        return Decoded.GetValue(raw, value => Array.AsReadOnly(ParsePaths(value)));
    }

    private static string[] ParsePaths(string raw)
    {
        if (raw[0] == '{')
        {
            try
            {
                using var doc = JsonDocument.Parse(raw);
                if (doc.RootElement.TryGetProperty("paths", out var paths) && paths.ValueKind == JsonValueKind.Array
                    && paths.EnumerateArray().All(p => p.ValueKind == JsonValueKind.String))
                    return paths.EnumerateArray().Select(p => p.GetString()!)
                        .Where(p => p.Length > 0).ToArray();
            }
            catch (Exception ex) when (ex is JsonException or InvalidOperationException)
            { } // поврежденный JSON или строка Unicode после смены типа остаются как есть
        }
        return [raw];
    }

    /// <summary>Пустой список и один путь сохраняют прежний формат; несколько путей — JSON.
    /// Порядок сохраняется, повтор одного пути не создает вторую ссылку.</summary>
    public static string Serialize(IEnumerable<string> paths)
    {
        var values = paths.Where(p => !string.IsNullOrEmpty(p)).Distinct(StringComparer.Ordinal).ToArray();
        return values.Length switch
        {
            0 => "",
            1 => values[0],
            _ => JsonSerializer.Serialize(new { paths = values }),
        };
    }

    /// <summary>Имя файла для показа: из пути — базовое имя (напр. «Отчет.xlsx»); значение без
    /// разделителей пути (в т.ч. доставшееся от прежнего типа) — как есть; пусто → пусто.
    /// Разбираем оба разделителя ('/' и '\') вручную, а не через <c>Path.GetFileName</c> — тот
    /// зависит от ОС (на macOS/Linux '\' не разделитель), и путь с Windows не свернулся бы.</summary>
    public static string DisplayName(string? raw)
    {
        var s = raw ?? "";
        if (s.Length > 0 && s[0] == '{') return string.Join(", ", ReadPaths(s).Select(BaseName));
        return BaseName(s);
    }

    private static string BaseName(string s)
    {
        if (s.Length == 0) return "";
        var i = s.LastIndexOfAny(['/', '\\']);
        if (i < 0) return s;                      // нет разделителей — значение как есть
        var name = s[(i + 1)..];
        return name.Length == 0 ? s : name;       // путь оканчивается разделителем — показываем как есть
    }
}
