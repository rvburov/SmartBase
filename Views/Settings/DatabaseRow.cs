using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using SmartBase.Database;
using SmartBase.Utils;

namespace SmartBase.Views.Settings;

/// <summary>
/// Строка списка «Управление базой данных»: запись реестра + статус. Не смонтированные
/// БД помечаются, а не скрываются: ждущие/монтирующиеся в фоновой очереди — серым ЖИВЫМ
/// статусом («в очереди…» / «Обновление · 45% · ≈ 2 мин» — проценты и время обновляются
/// на лету через <see cref="SetStatus"/>, как у заглушек сайдбара), недоступные — красным
/// с кнопкой «Подключить заново».
///
/// У смонтированной SQLite-базы — третья строка со сведениями о файле («файл 1,7 ГБ ·
/// свободно 1,1 ГБ (64 %)»): это ответ на «почему база весит столько». Когда свободных
/// страниц больше порога (<see cref="DatabaseManager.NeedsCompaction"/>), строка красная —
/// сжатие живет в окне «Настройки базы данных» (шестеренка строки, DbSettingsDialog),
/// резервная копия — в разделе «Резервное копирование»; из действий в строке только
/// шестеренка и корзинка.
/// </summary>
public sealed partial class DatabaseRow : ObservableObject
{
    public int    Id         { get; init; }
    public string Name       { get; init; } = "";
    public string Path       { get; init; } = "";
    public bool   IsDefault  { get; init; }
    public bool   IsMounted  { get; init; }
    /// <summary>Серверная БД PostgreSQL: бейдж в строке, Path — вид без пароля.</summary>
    public bool   IsPostgres { get; init; }
    /// <summary>Сведения о файле смонтированной SQLite-базы (null — не смонтирована / PostgreSQL).</summary>
    public DatabaseManager.StorageStats? Storage { get; init; }
    /// <summary>Текущий пользователь может обслуживать файл (сжать/копия): база смонтирована,
    /// это SQLite и он администратор базы.</summary>
    public bool   CanMaintain { get; init; }

    /// <summary>Статусная строка (null — подключена, строки нет).</summary>
    [ObservableProperty] private string? _statusText;
    /// <summary>Статус — ошибка (красный + «Подключить заново»), иначе серый статус очереди.</summary>
    [ObservableProperty] private bool    _statusIsError;

    /// <summary>
    /// Метка справа: чем база хранится и, у базы по умолчанию, пометка об этом в скобках —
    /// «SQLite», «PostgreSQL», «SQLite (По умолчанию)».
    ///
    /// Одной меткой, а не двумя: раньше движок писался только у PostgreSQL, и строка SQLite
    /// стояла вовсе без пометки. Читалось это не как «SQLite», а как «тут чего-то не хватает» —
    /// тем более рядом со строкой, у которой метка есть. Теперь движок назван у каждой строки.
    /// </summary>
    public string EngineBadge =>
        EngineName(IsPostgres) + (IsDefault ? " (По умолчанию)" : "");

    /// <summary>
    /// Чем база хранится, одним словом. Живет здесь, потому что пишется в трех местах: строка
    /// списка, заголовок окна настроек базы и список в «Резервном копировании». Три отдельных
    /// тернарника разошлись бы на первой же правке написания.
    /// </summary>
    public static string EngineName(bool isPostgres) => isPostgres ? "PostgreSQL" : "SQLite";

    /// <summary>
    /// Может ли человек править название в списке и пароль подключения (окно «Настройки базы
    /// данных»): это дело администратора базы, у остальных поля серые.
    ///
    /// Пока база НЕ подключена, прав не видно вовсе — и поля открыты. Иначе база с устаревшим
    /// паролем становится мертвой навсегда: войти нельзя, а раз войти нельзя, то и прав она
    /// никогда не покажет, и вписать верный пароль будет некому.
    /// </summary>
    public static bool CanManage(bool mounted, bool isAdmin) => !mounted || isAdmin;

    public bool ShowDeleteButton => !IsDefault;
    public bool ShowStatus       => StatusText is not null;
    public bool ShowReconnect    => StatusIsError;

    public IBrush StatusBrush => Brush(
        StatusIsError ? "BrushRed" : "BrushSecondaryText",
        StatusIsError ? "#FF3B30" : "#8E8E93");

    // ── Сведения о файле ──────────────────────────────────────────────────────

    public bool ShowStorage => Storage is not null;

    /// <summary>«файл 1,7 ГБ · свободно 1,1 ГБ (64 %)»; без свободных страниц — только размер.</summary>
    public string StorageText => Storage is null ? "" : FormatStorage(Storage);

    /// <summary>Свободных страниц больше порога — стоит сжать (красная строка + кнопка).</summary>
    public bool StorageIsWarning => Storage?.NeedsCompaction == true;

    public IBrush StorageBrush => Brush(
        StorageIsWarning ? "BrushRed" : "BrushSecondaryText",
        StorageIsWarning ? "#FF3B30" : "#8E8E93");

    /// <summary>
    /// «размер файла 1,7 ГБ · свободно 1,1 ГБ (64 %)» — для строки списка и для вопроса перед
    /// сжатием: там рядом нет заголовка, и величину приходится называть словами. Прежнее
    /// «файл 1,7 ГБ» называло предмет, а не величину, и читалось как обрывок.
    /// </summary>
    public static string FormatStorage(DatabaseManager.StorageStats s) =>
        "размер файла " + StorageValue(s);

    /// <summary>
    /// То же без слов «размер файла» — для окна настроек базы, где они уже стоят заголовком
    /// над строкой и вторым разом читались бы как заикание.
    /// </summary>
    public static string StorageValue(DatabaseManager.StorageStats s)
    {
        var text = Size(s.FileBytes);
        if (s.FreeBytes > 0)
            text += $" · свободно {Size(s.FreeBytes)} ({s.FreeRatio * 100:0} %)";
        if (s.NeedsCompaction)
            text += " — стоит сжать";
        return text;
    }

    private static string Size(long bytes) => Sizes.Text(bytes);

    /// <summary>Живое обновление статуса из очереди монтирования (тик ~2 раза в секунду).</summary>
    public void SetStatus(string? text, bool isError)
    {
        StatusText    = text;
        StatusIsError = isError;
    }

    partial void OnStatusTextChanged(string? value)  => OnPropertyChanged(nameof(ShowStatus));
    partial void OnStatusIsErrorChanged(bool value)
    {
        OnPropertyChanged(nameof(StatusBrush));
        OnPropertyChanged(nameof(ShowReconnect));
    }

    private static IBrush Brush(string key, string fallbackHex) =>
        Application.Current!.TryFindResource(key, out var res) && res is IBrush b
            ? b
            : new SolidColorBrush(Color.Parse(fallbackHex));
}
