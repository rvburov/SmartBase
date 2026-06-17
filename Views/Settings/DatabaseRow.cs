namespace DataBase.Views.Settings;

/// <summary>
/// Строка списка «Управление базой данных»: запись реестра + признак, смонтирована ли
/// она сейчас (файл доступен). Недоступные БД помечаются, а не скрываются.
/// </summary>
public sealed class DatabaseRow
{
    public int    Id        { get; init; }
    public string Name      { get; init; } = "";
    public string Path      { get; init; } = "";
    public bool   IsDefault { get; init; }
    public bool   IsMounted { get; init; }

    public bool ShowDefaultBadge => IsDefault;
    public bool ShowDeleteButton => !IsDefault;
    public bool ShowNotMounted   => !IsDefault && !IsMounted;
}
