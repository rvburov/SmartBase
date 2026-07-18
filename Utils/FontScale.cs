using Avalonia;
using Avalonia.Controls;

namespace SmartBase.Utils;

/// <summary>
/// Живой размер шрифта приложения. Хранит стандартные размеры из Typography.axaml
/// (FontSizeSmall/Base/Large) и пишет в <see cref="Application"/>.Resources «эффективные»
/// (стандарт + дельта). Потребители на DynamicResource обновляются мгновенно; виды,
/// строящие текст в C# (календарь, таблица), слушают <see cref="Changed"/> и пересобираются.
/// Диапазон дельты и хранение — в <see cref="AppSettings"/>.
/// </summary>
public static class FontScale
{
    private static double _baseSmall = 11, _baseBase = 12, _baseLarge = 20;
    private static bool _ready;

    /// <summary>Текущая дельта в пикселях к стандарту (0 = стандарт).</summary>
    public static int Delta { get; private set; }

    /// <summary>Стандартный базовый размер текста (FontSizeBase из Typography, без дельты).</summary>
    public static double BaseFontSize => _baseBase;

    /// <summary>Размер изменился — C#-видам пора пересобраться.</summary>
    public static event Action? Changed;

    /// <summary>
    /// Старт: считать стандартные размеры из Typography и применить сохранённую дельту.
    /// Вызывать после AppSettings.AttachStore и до создания MainWindow.
    /// </summary>
    public static void Init()
    {
        // Стандарт читаем ДО первой записи в Application.Resources (иначе прочитаем уже override).
        _baseSmall = Read("FontSizeSmall", 11);
        _baseBase  = Read("FontSizeBase",  12);
        _baseLarge = Read("FontSizeLarge", 20);
        _ready = true;

        Delta = AppSettings.LoadFontDelta();
        Write();
    }

    /// <summary>Установить дельту (с зажимом в допустимый диапазон), сохранить и применить вживую.</summary>
    public static void SetDelta(int delta)
    {
        delta = Math.Clamp(delta, AppSettings.FontDeltaMin, AppSettings.FontDeltaMax);
        if (delta == Delta) return;
        Delta = delta;
        AppSettings.SaveFontDelta(delta);
        Write();
        Changed?.Invoke();
    }

    private static void Write()
    {
        if (!_ready) return;
        var r = Application.Current!.Resources;
        r["FontSizeSmall"] = _baseSmall + Delta;
        r["FontSizeBase"]  = _baseBase  + Delta;
        r["FontSizeLarge"] = _baseLarge + Delta;
    }

    private static double Read(string key, double fallback) =>
        Application.Current!.TryFindResource(key, out var v) && v is double d ? d : fallback;
}
