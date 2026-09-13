namespace SmartBase.Settings;

/// <summary>Раздел профиля для сайдбара: ключ (для ShowCategory) + подпись + иконка
/// (ключ x:Key из Assets/Styles/Icons.axaml, рисуется в SidebarList).</summary>
public sealed record ProfileCategory(string Key, string Label, string Icon);

/// <summary>
/// Реестр разделов модуля «Профиль» — зеркало <c>SettingsRegistry</c>: фиксированный список
/// разделов, которые показывает сайдбар (SidebarList) и открывает ProfileView по ключу.
/// Добавить раздел — дописать строку здесь (и обработать ключ в ProfileView.ShowCategory).
///
/// <b>Почему «Безопасность» — свой раздел, а не строка панели администратора.</b> Контур
/// безопасности отвечает на вопрос «что этой ПРОГРАММЕ разрешено уносить наружу» — он про
/// машину, а не про выбранную базу, и спрашивает его любой, у кого не уходит отправка, а не
/// только администратор. Панель администратора неадминистратору не показывает ничего, и контур
/// внутри нее оказался бы закрыт от тех, кому он нужнее всего. Менять с него все равно нечего:
/// экран только читает.
/// </summary>
public static class ProfileRegistry
{
    public static IReadOnlyList<ProfileCategory> All { get; } =
    [
        new("general",  "Общие данные",           "IconUser"),
        new("admin",    "Панель администратора",  "IconShieldUser"),
        new("security", "Безопасность",           "IconLock"),
        new("history",  "История изменений",      "IconHistory"),
    ];

    /// <summary>Раздел по ключу или null, если такого нет.</summary>
    public static ProfileCategory? FindByKey(string key) =>
        All.FirstOrDefault(c => c.Key == key);
}
