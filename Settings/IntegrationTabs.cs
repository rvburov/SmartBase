namespace SmartBase.Settings;

/// <summary>Вкладка раздела «Интеграции»: ключ (для показа) и подпись в выпадающем списке.</summary>
public sealed record IntegrationTab(string Key, string Label);

/// <summary>
/// Реестр вкладок раздела «Интеграции» — зеркало <see cref="SettingsRegistry"/>, только уровнем
/// ниже: раздел один, а внутри переключатель в навбаре, как у видов календаря.
///
/// В списке лежат ТОЛЬКО готовые вкладки: показывать пустую вкладку с обещанием нельзя — человек
/// открывает ее, ничего не находит и считает раздел сломанным. Каждая появляется здесь строкой в
/// тот день, когда за ней есть что показать.
///
/// Порядок — из плана (docs/ROADMAP-API.md, раздел 4): «Сценарии» стоят сразу за «Запросами»,
/// потому что собираются из них. Не заведена пока одна вкладка плана — «Доступ извне»: своего
/// слушателя у программы еще нет.
/// </summary>
public static class IntegrationTabs
{
    public static IReadOnlyList<IntegrationTab> All { get; } =
    [
        new("connections", "Соединения"),
        new("requests",    "Запросы"),
        new("scenarios",   "Сценарии"),
        new("inbox",       "Прием"),
        new("security",    "Безопасность"),
        new("journal",     "Журнал"),
    ];

    /// <summary>Вкладка по ключу; неизвестный ключ — первая вкладка.</summary>
    public static IntegrationTab Resolve(string? key) =>
        All.FirstOrDefault(t => t.Key == key) ?? All[0];
}
