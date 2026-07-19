using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;

namespace SmartBase.Views.Settings;

/// <summary>
/// Строка списка «Управление базой данных»: запись реестра + статус. Не смонтированные
/// БД помечаются, а не скрываются: ждущие/монтирующиеся в фоновой очереди — серым ЖИВЫМ
/// статусом («в очереди…» / «Обновление · 45% · ≈ 2 мин» — проценты и время обновляются
/// на лету через <see cref="SetStatus"/>, как у заглушек сайдбара), недоступные — красным
/// с кнопкой «Подключить заново».
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
