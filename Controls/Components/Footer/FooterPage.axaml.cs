using Avalonia.Controls;

namespace SmartBase.Controls.Components;

/// <summary>Футер панели «Страница записи»: переключатель режима markdown (редактор ↔ просмотр)
/// и действия обмена над записью. Высота/отступы — как у остальных полос (56 / 16). Сам контент
/// режима применяет родитель через событие <see cref="PreviewToggled"/>.</summary>
public partial class FooterPage : UserControl
{
    /// <summary>Режим переключен: true — динамический просмотр, false — редактор исходника.</summary>
    public event EventHandler<bool>? PreviewToggled;

    /// <summary>Нажата кнопка «Действия обмена»: список собирает и показывает панель записи.</summary>
    public event EventHandler<Control>? ActionsRequested;

    public FooterPage()
    {
        InitializeComponent();
        PreviewToggle.IsCheckedChanged += (_, _) =>
        {
            UpdateIcon();
            PreviewToggled?.Invoke(this, IsPreview);
        };
        ActionsBtn.Click += (_, _) => ActionsRequested?.Invoke(this, ActionsBtn);
        UpdateIcon();
    }

    /// <summary>
    /// Показать кнопку действий. Скрывается, когда для коллекции ничего не настроено: кнопка,
    /// которая всегда есть и всегда пуста, читается как поломка.
    /// </summary>
    public void ShowActions(bool show) => ActionsBtn.IsVisible = show;

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
