using Avalonia.Controls;

namespace DataBase.Controls.Components;

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
        ConstructorBtn.Click += (_, _) => SwitchModule("constructor");
        SettingsBtn.Click += (_, _) => SwitchModule("settings");
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
        ConstructorBtn.IsChecked = module == "constructor";
        SettingsBtn.IsChecked = module == "settings";
    }

    private void SwitchModule(string module)
    {
        SetModuleActive(module);
        ModuleChanged?.Invoke(this, module);
    }
}
