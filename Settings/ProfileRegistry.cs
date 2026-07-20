namespace SmartBase.Settings;

/// <summary>Раздел профиля для сайдбара: ключ (для ShowCategory) + подпись + иконка
/// (ключ x:Key из Assets/Styles/Icons.axaml, рисуется в SidebarList).</summary>
public sealed record ProfileCategory(string Key, string Label, string Icon);

/// <summary>
/// Реестр разделов модуля «Профиль» — зеркало <c>SettingsRegistry</c>: фиксированный список
/// разделов, которые показывает сайдбар (SidebarList) и открывает ProfileView по ключу.
/// Добавить раздел — дописать строку здесь (и обработать ключ в ProfileView.ShowCategory).
/// </summary>
public static class ProfileRegistry
{
    public static IReadOnlyList<ProfileCategory> All { get; } =
    [
        new("general", "Общие данные",           "IconUser"),
        new("admin",   "Панель администратора",  "IconShieldUser"),
        new("history", "История изменений",      "IconHistory"),
        new("logs",    "Диагностика",            "IconCode"),
    ];

    /// <summary>Раздел по ключу или null, если такого нет.</summary>
    public static ProfileCategory? FindByKey(string key) =>
        All.FirstOrDefault(c => c.Key == key);
}
