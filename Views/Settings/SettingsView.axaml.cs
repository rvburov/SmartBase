using Avalonia.Controls;
using MyPlanner.Utils;

namespace MyPlanner.Views.Settings;

public partial class SettingsView : UserControl
{
    public SettingsView()
    {
        InitializeComponent();
        Scaling.Load();
        UpdateLabel();

        ZoomInBtn.Click  += (_, _) => { Scaling.ZoomIn();  UpdateLabel(); };
        ZoomOutBtn.Click += (_, _) => { Scaling.ZoomOut(); UpdateLabel(); };
    }

    private void UpdateLabel() =>
        ZoomLabel.Text = $"{(int)(Scaling.Current * 100)}%";
}
