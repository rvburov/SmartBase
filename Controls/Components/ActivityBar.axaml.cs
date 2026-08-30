using Avalonia.Controls;
using SmartBase.Settings;

namespace SmartBase.Controls.Components;

public partial class ActivityBar : UserControl
{
    public event EventHandler<bool>? SidebarToggled;
    public event EventHandler<string>? ModuleChanged;

    public ActivityBar()
    {
        InitializeComponent();

        SidebarBtn.IsCheckedChanged += (_, _) =>
        {
            UpdateSidebarIcon();
            SidebarToggled?.Invoke(this, SidebarBtn.IsChecked == true);
        };

        CalendarBtn.Click += (_, _) => SwitchModule("calendar");
        TasksBtn.Click += (_, _) => SwitchModule("tasks");
        ConstructorBtn.Click += (_, _) => SwitchModule("constructor");
        ExtensionsBtn.Click += (_, _) => SwitchModule("extensions");
        NotesBtn.Click += (_, _) => SwitchModule("notes");
        ProfileBtn.Click += (_, _) => SwitchModule("profile");
        SettingsBtn.Click += (_, _) => SwitchModule("settings");

        ApplyModuleVisibility();
    }

    // Разделы, скрытые в «Оформление → Отображение раздела навигации», убираем из панели.
    // «Настройки» и «Профиль» здесь не участвуют: их нет в ViewCatalog.Navigation, скрыть
    // настройки значило бы отрезать путь обратно.
    private void ApplyModuleVisibility()
    {
        var visible = ViewCatalog.Visible(ViewCatalog.ScopeNavigation)
                                 .Select(v => v.Key)
                                 .ToHashSet(StringComparer.Ordinal);

        CalendarBtn.IsVisible    = visible.Contains("calendar");
        TasksBtn.IsVisible       = visible.Contains("tasks");
        ConstructorBtn.IsVisible = visible.Contains("constructor");
        ExtensionsBtn.IsVisible  = visible.Contains("extensions");
        NotesBtn.IsVisible       = visible.Contains("notes");
    }

    public void SetSidebarOpen(bool isOpen)
    {
        SidebarBtn.IsChecked = isOpen;
        UpdateSidebarIcon();
    }

    // Открыт → panel-right-open (←, свернуть); закрыт → panel-left-open (→, развернуть)
    private void UpdateSidebarIcon()
    {
        var open = SidebarBtn.IsChecked == true;
        PanelOpenIcon.IsVisible  = !open;
        PanelCloseIcon.IsVisible = open;
        ToolTip.SetTip(SidebarBtn, open ? "Свернуть боковую панель" : "Развернуть боковую панель");
    }

    public void SetModuleActive(string module)
    {
        CalendarBtn.IsChecked = module == "calendar";
        TasksBtn.IsChecked = module == "tasks";
        ConstructorBtn.IsChecked = module == "constructor";
        ExtensionsBtn.IsChecked = module == "extensions";
        NotesBtn.IsChecked = module == "notes";
        ProfileBtn.IsChecked = module == "profile";
        SettingsBtn.IsChecked = module == "settings";
    }

    private void SwitchModule(string module)
    {
        SetModuleActive(module);
        ModuleChanged?.Invoke(this, module);
    }
}
