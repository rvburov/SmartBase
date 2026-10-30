using Avalonia.Controls;
using Avalonia.Interactivity;
using SmartBase.Controls.Dialogs;

namespace SmartBase.Views.Settings;

public partial class SettingsView
{
    // Проверка связей всей базы. Окно свойства-связи проверяет одно свойство тем же путем
    // (RelationIntegrityDialog.ShowForAsync).
    private async void OnRelationIntegrityClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: int dbId } || TopLevel.GetTopLevel(this) is not Window owner) return;
        var source = _databases?.Invoke().FirstOrDefault(db => db.DbId == dbId);
        var entry = _registry?.GetAll().FirstOrDefault(db => db.Id == dbId);
        if (source is null || entry is null)
        {
            await ConfirmDialog.Info("Для проверки связей подключите базу данных.").ShowDialog(owner);
            return;
        }
        await RelationIntegrityDialog.ShowForAsync(owner, source, entry, entry.Name);
    }
}
