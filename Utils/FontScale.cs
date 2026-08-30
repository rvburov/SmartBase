using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

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
    private static double _baseSmall = 11, _baseBase = 12, _baseMedium = 16, _baseLarge = 20;
    private static double _baseH3 = 14, _baseMono = 11;
    private static bool _ready;

    /// <summary>Текущая дельта в пикселях к стандарту (0 = стандарт).</summary>
    public static int Delta { get; private set; }

    /// <summary>Стандартный базовый размер текста (FontSizeBase из Typography, без дельты).</summary>
    public static double BaseFontSize => _baseBase;

    /// <summary>Размер изменился — C#-видам пора пересобраться.</summary>
    public static event Action? Changed;

    /// <summary>
    /// Старт: считать стандартные размеры из Typography и применить сохраненную дельту.
    /// Вызывать после AppSettings.AttachStore и до создания MainWindow.
    /// </summary>
    public static void Init()
    {
        // Стандарт читаем ДО первой записи в Application.Resources (иначе прочитаем уже override).
        _baseSmall  = Read("FontSizeSmall",  11);
        _baseBase   = Read("FontSizeBase",   12);
        _baseMedium = Read("FontSizeMedium", 16);
        _baseLarge  = Read("FontSizeLarge",  20);
        _baseH3     = Read("FontSizeH3",     14);
        _baseMono   = Read("FontSizeMono",   11);
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
        r["FontSizeSmall"]  = _baseSmall  + Delta;
        r["FontSizeBase"]   = _baseBase   + Delta;
        r["FontSizeMedium"] = _baseMedium + Delta;
        r["FontSizeLarge"]  = _baseLarge  + Delta;
        // Дельта одна на все размеры, в том числе на markdown-овские: разница между ними
        // задумана постоянной («код на единицу мельче текста»), а не долей от кегля.
        r["FontSizeH3"]     = _baseH3     + Delta;
        r["FontSizeMono"]   = _baseMono   + Delta;
    }

    private static double Read(string key, double fallback) =>
        Application.Current!.TryFindResource(key, out var v) && v is double d ? d : fallback;
}
