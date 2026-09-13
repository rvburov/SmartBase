using Avalonia;
using Avalonia.Controls;
using SmartBase.Utils;

namespace SmartBase.Controls.Behaviors;

/// <summary>
/// Память размера у окон КЛАССА «данные» (Assets/Styles/WindowMetrics.axaml, четвертый класс).
///
/// Подключается одним атрибутом в разметке окна:
/// <code>b:DialogSize.Remember="binding-in"</code>
///
/// Зачем это нужно только этому классу. Остальные три размера человек не меняет — там форма, и
/// окно ровно по ней. Окна данных он тянет: раскладку колонок на сотню свойств растягивают на
/// весь экран, превью уходящего — тоже. Не запоминай программа этот размер, его подбирали бы
/// заново каждое утро.
///
/// <b>Размер хранится при зуме 100%.</b> Габариты окон умножаются на зум приложения
/// (<see cref="WindowZoom"/>), и запомни мы «как на экране» — при смене масштаба окно росло бы
/// само собой, раз за разом.
///
/// <b>Вернулся к исходному — ключ уходит из базы.</b> Правило настроек целиком: база хранит
/// только дельты, сброс — это удаление ключа (docs/developer/architecture/settings.md).
/// </summary>
public static class DialogSize
{
    /// <summary>Под каким именем помнить размер; пусто — не помнить.</summary>
    public static readonly AttachedProperty<string> RememberProperty =
        AvaloniaProperty.RegisterAttached<Window, string>("Remember", typeof(DialogSize), "");

    /// <summary>Размер, с которого окно начинало, — чтобы отличить «человек тянул» от «как было».</summary>
    private static readonly AttachedProperty<Size> StartedProperty =
        AvaloniaProperty.RegisterAttached<Window, Size>("Started", typeof(DialogSize));

    public static string GetRemember(Window window)             => window.GetValue(RememberProperty);
    public static void   SetRemember(Window window, string key) => window.SetValue(RememberProperty, key);

    static DialogSize()
    {
        RememberProperty.Changed.AddClassHandler<Window>((window, args) =>
        {
            if (args.NewValue is string key && key.Length > 0) Attach(window, key);
        });
    }

    private static void Attach(Window window, string key)
    {
        // Размер ставится ДО инициализации окна — тем же порядком, что габариты из разметки:
        // дальше их умножит на зум WindowZoom, а положение окна посчитается уже от готового
        // размера. Поставь мы его на Opened — окно сначала мигнуло бы исходным.
        window.SetValue(StartedProperty, new Size(window.Width, window.Height));

        if (AppSettings.LoadDialogSize(key) is { } remembered)
        {
            window.Width  = remembered.Width;
            window.Height = remembered.Height;
        }

        window.Closing += (_, _) => Remember(window, key);
    }

    /// <summary>
    /// Запомнить то, что получилось. Размер берется у САМОГО окна (<c>Bounds</c>), а не у
    /// свойств <c>Width</c>/<c>Height</c>: человек тянул за край, а не правил разметку.
    /// </summary>
    private static void Remember(Window window, string key)
    {
        var zoom = Scaling.Current > 0 ? Scaling.Current : 1.0;

        var width  = window.Bounds.Width  / zoom;
        var height = window.Bounds.Height / zoom;

        // Свернутое или еще не показанное окно ничего не говорит о желании человека.
        if (width < 1 || height < 1) return;

        var started = window.GetValue(StartedProperty);

        // Пиксель туда-сюда — это не «человек поменял»: округление зума и рамки окна дают
        // расхождение в доли пикселя на ровном месте.
        if (Same(width, started.Width) && Same(height, started.Height))
        {
            AppSettings.ForgetDialogSize(key);
            return;
        }

        AppSettings.SaveDialogSize(key, (int)Math.Round(width), (int)Math.Round(height));
    }

    private static bool Same(double a, double b) => Math.Abs(a - b) < 2;
}
