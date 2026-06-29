using Avalonia.Controls;

namespace DataBase.Controls.Components;

/// <summary>Футер панели «Страница записи»: переключатель режима markdown (редактор ↔ просмотр).
/// Высота/отступы — как у остальных полос (56 / 16). Сам контент режима применяет родитель
/// через событие <see cref="PreviewToggled"/>.</summary>
public partial class FooterPage : UserControl
{
    /// <summary>Режим переключён: true — динамический просмотр, false — редактор исходника.</summary>
    public event EventHandler<bool>? PreviewToggled;

    public FooterPage()
    {
        InitializeComponent();
        PreviewToggle.IsCheckedChanged += (_, _) =>
        {
            UpdateIcon();
            PreviewToggled?.Invoke(this, IsPreview);
        };
        UpdateIcon();
    }

    /// <summary>Режим просмотра (true) или редактора (false).</summary>
    public bool IsPreview
    {
        get => PreviewToggle.IsChecked == true;
        set => PreviewToggle.IsChecked = value;
    }

    private void UpdateIcon()
    {
        var preview = IsPreview;
        // В режиме редактора кнопка предлагает перейти к просмотру (карандаш);
        // в режиме просмотра — вернуться к исходному тексту (code-xml).
        IconToPreview.IsVisible = !preview;
        IconToSource.IsVisible  = preview;
        ToolTip.SetTip(PreviewToggle, preview ? "Просмотр исходного текста" : "Динамический просмотр");
    }
}
