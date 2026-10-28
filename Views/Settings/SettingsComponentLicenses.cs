using Avalonia.Controls;
using Avalonia.Interactivity;
using SmartBase.Utils;

namespace SmartBase.Views.Settings;

public partial class SettingsView
{
    private bool _componentLicensesLoaded;

    private void OnComponentLicensesExpanded(object? sender, RoutedEventArgs e)
    {
        if (_componentLicensesLoaded) return;
        ComponentLicensesText.Text = ComponentLicenses.Read();
        _componentLicensesLoaded = true;
    }

    private async void OnCopyComponentLicenses(object? sender, RoutedEventArgs e)
    {
        if (TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard)
            await clipboard.SetTextAsync(ComponentLicensesText.Text);
    }
}
