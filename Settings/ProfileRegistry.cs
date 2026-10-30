namespace SmartBase.Settings;

/// <summary>Ключ, подпись и иконка раздела профиля.</summary>
public sealed record ProfileCategory(string Key, string Label, string Icon);

/// <summary>Разделы профиля, доступные в боковой панели.</summary>
public static class ProfileRegistry
{
    public static IReadOnlyList<ProfileCategory> All { get; } =
    [
        new("general", "Общие данные", "IconUser"),
        new("history", "История изменений", "IconHistory"),
    ];

    public static ProfileCategory? FindByKey(string key) =>
        All.FirstOrDefault(c => c.Key == key);
}
