using Avalonia.Controls;

namespace SmartBase.Controls.Components;

/// <summary>Футер панели «Страница записи»: переключатели режима markdown (редактор ↔ просмотр)
/// и показа пустых свойств, действия обмена над записью. Высота/отступы — как у остальных полос (56 / 16). Сам контент
/// режима применяет родитель через событие <see cref="PreviewToggled"/>.</summary>
public partial class FooterPage : UserControl
{
    /// <summary>Режим переключен: true — динамический просмотр, false — редактор исходника.</summary>
    public event EventHandler<bool>? PreviewToggled;

    /// <summary>Показ свойств переключен: true — скрыть пустые, false — показать все.</summary>
    public event EventHandler<bool>? HideEmptyPropertiesToggled;

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
        HideEmptyToggle.IsCheckedChanged += (_, _) =>
        {
            UpdatePropertiesIcon();
            HideEmptyPropertiesToggled?.Invoke(this, HideEmptyProperties);
        };
        ActionsBtn.Click += (_, _) => ActionsRequested?.Invoke(this, ActionsBtn);
        UpdateIcon();
        UpdatePropertiesIcon();
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

    /// <summary>Скрывать пустые свойства; состояние живет вместе с панелью записи.</summary>
    public bool HideEmptyProperties
    {
        get => HideEmptyToggle.IsChecked == true;
        set => HideEmptyToggle.IsChecked = value;
    }

    private void UpdatePropertiesIcon()
    {
        var hide = HideEmptyProperties;
        IconAllProperties.IsVisible    = !hide;
        IconFilledProperties.IsVisible = hide;
        ToolTip.SetTip(HideEmptyToggle, hide ? "Показать пустые свойства" : "Скрыть пустые свойства");
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
