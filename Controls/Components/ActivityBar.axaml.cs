using Avalonia.Controls;

namespace MyPlanner.Controls.Components;

public partial class ActivityBar : UserControl
{
    public event EventHandler<bool>? SidebarToggled;
    public event EventHandler<string>? ModuleChanged;

    public ActivityBar()
    {
        InitializeComponent();

        SidebarBtn.IsCheckedChanged += (_, _) =>
            SidebarToggled?.Invoke(this, SidebarBtn.IsChecked == true);

        CalendarBtn.Click += (_, _) => SwitchModule("calendar");
        ConstructorBtn.Click += (_, _) => SwitchModule("constructor");
    }

    public void SetSidebarOpen(bool isOpen)
    {
        SidebarBtn.IsChecked = isOpen;
    }

    public void SetModuleActive(string module)
    {
        CalendarBtn.IsChecked = module == "calendar";
        ConstructorBtn.IsChecked = module == "constructor";
    }

    private void SwitchModule(string module)
    {
        SetModuleActive(module);
        ModuleChanged?.Invoke(this, module);
    }
}
