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

    public bool ShowDefaultBadge => IsDefault;
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

    public static string FormatStorage(DatabaseManager.StorageStats s)
    {
        var text = $"файл {Size(s.FileBytes)}";
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
