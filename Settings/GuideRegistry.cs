namespace SmartBase.Settings;

/// <summary>Документ инструкции: ключ (для якорей оглавления) + имя файла в docs/user.</summary>
public sealed record GuidePage(string Id, string File);

/// <summary>
/// Реестр документов инструкции пользователя — зеркало <c>SettingsRegistry</c>: фиксированный
/// список в порядке показа. Заголовок пункта оглавления в реестре НЕ дублируется — он берется
/// из первого заголовка (<c>#</c>) самого документа, иначе две подписи разъезжались бы.
///
/// Сами файлы лежат в <c>docs/user/</c> и попадают в сборку как AvaloniaResource
/// (см. SmartBase.csproj): та же папка читается на GitHub — источник правды один.
/// <c>README.md</c> из папки в список не входит: это оглавление для GitHub, а в программе
/// его роль играет панель «Содержание».
///
/// Добавить документ — положить файл в docs/user и дописать строку здесь.
/// </summary>
public static class GuideRegistry
{
    /// <summary>Базовый путь ресурсов инструкции (см. AvaloniaResource в SmartBase.csproj).</summary>
    public const string ResourceRoot = "avares://SmartBase/docs/user/";

    public static IReadOnlyList<GuidePage> All { get; } =
    [
        new("overview",       "overview.md"),
        new("data-structure", "data-structure.md"),
        new("table",          "table.md"),
        new("record-page",    "record-page.md"),
        new("properties",     "properties.md"),
        new("filters",        "filters-sorting.md"),
        new("calendar",       "calendar.md"),
        new("tasks",          "tasks.md"),
        new("import-export",  "import-export.md"),
        new("extensions",     "extensions.md"),
        new("settings",       "settings.md"),
        new("profile",        "profile-access.md"),
        new("postgresql",     "postgresql.md"),
        new("install",        "install-update.md"),
        new("shortcuts",      "shortcuts.md"),
    ];

    /// <summary>Полный avares-адрес документа.</summary>
    public static Uri UriOf(GuidePage page) => new(ResourceRoot + page.File);
}
