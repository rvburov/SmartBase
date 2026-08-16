namespace SmartBase.Settings;

/// <summary>Раздел настроек для сайдбара: ключ (для ShowCategory) + подпись + иконка
/// (ключ x:Key из Assets/Styles/Icons.axaml, рисуется в SidebarList).</summary>
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
        new("general",    "Общее",                     "IconSliders"),
        new("appearance", "Оформление",                "IconSun"),
        new("database",   "Управление базой данных",   "IconDatabase"),
        new("guide",      "Инструкция пользователя",   "IconBook"),
        new("logs",       "Диагностика",               "IconCode"),
        new("about",      "О программе",               "IconInfo"),
    ];

    /// <summary>Раздел по ключу или null, если такого нет.</summary>
    public static SettingsCategory? FindByKey(string key) =>
        All.FirstOrDefault(c => c.Key == key);
}
