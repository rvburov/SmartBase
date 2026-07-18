using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SmartBase.Utils;

namespace SmartBase.ViewModels.Settings;

public partial class SettingsViewModel : ViewModelBase
{
    [ObservableProperty] private double _scale = AppSettings.ScaleDefault;

    public SettingsViewModel()
    {
        _scale = Scaling.Current;
        Scaling.ScaleChanged += s => Scale = s;
    }

    [RelayCommand] private void ZoomIn()  => Scaling.ZoomIn();
    [RelayCommand] private void ZoomOut() => Scaling.ZoomOut();
}
