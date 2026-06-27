namespace DataBase.Settings;

/// <summary>Раздел настроек для сайдбара: ключ (для ShowCategory) + подпись + иконка
/// (геометрия lucide-пути, рисуется в SidebarList).</summary>
public sealed record SettingsCategory(string Key, string Label, string Icon);

/// <summary>
/// Реестр разделов настроек — зеркало <c>ExtensionRegistry</c>: фиксированный список разделов,
/// которые показывает сайдбар (SidebarList) и открывает SettingsView по ключу. Добавить раздел —
/// дописать строку здесь (и обработать ключ в SettingsView.ShowCategory).
/// </summary>
public static class SettingsRegistry
{
    public static IReadOnlyList<SettingsCategory> All { get; } =
    [
        new("general", "Общие",
            "M21 4 H14 M10 4 H3 M21 12 H12 M8 12 H3 M21 20 H16 M12 20 H3 M14 2 V6 M8 10 V14 M16 18 V22"),
        new("appearance", "Оформление",
            "M12 8 A4 4 0 1 0 12 16 A4 4 0 1 0 12 8 Z M12 2 V4 M12 20 V22 M4.93 4.93 L6.34 6.34 M17.66 17.66 L19.07 19.07 M2 12 H4 M20 12 H22 M4.93 19.07 L6.34 17.66 M17.66 6.34 L19.07 4.93"),
        new("database", "Управление базой данных",
            "M3 5 A9 3 0 1 0 21 5 A9 3 0 1 0 3 5 Z M3 5 V19 A9 3 0 0 0 21 19 V5 M3 12 A9 3 0 0 0 21 12"),
        new("about", "О программе",
            "M12 2 A10 10 0 1 0 12 22 A10 10 0 1 0 12 2 Z M12 16 V11 M11.99 8 H12.01"),
    ];

    /// <summary>Раздел по ключу или null, если такого нет.</summary>
    public static SettingsCategory? FindByKey(string key) =>
        All.FirstOrDefault(c => c.Key == key);
}
