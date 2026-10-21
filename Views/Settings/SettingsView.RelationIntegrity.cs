using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using SmartBase.Controls.Dialogs;
using SmartBase.Core.Models;
using SmartBase.Database;
using SmartBase.Database.Backup;
using SmartBase.Utils;

namespace SmartBase.Views.Settings;

public partial class SettingsView
{
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
        // Снимок параметров делается на UI-потоке. В тексты и журналы они не попадают.
        var pgInfo = entry.ToPgInfo();
        var backupDirectory = AppSettings.ResolveBackupFolder();
        var repo = source.Constructor;
        var dialog = new RelationIntegrityDialog(entry.Name,
            (progress, ct) => repo.ScanRelationIntegrityAsync(progress: progress, ct: ct),
            (report, keys, ct) => repo.PreviewRelationIntegrityRepairAsync(report, keys, ct),
            async (plan, progress, ct) =>
            {
                RelationIntegrityBackup? createdBackup = null;
                try
                {
                    var result = await repo.ApplyRelationIntegrityRepairAsync(plan, async (request, token) =>
                    {
                        progress.Report("Создание и проверка резервной копии…");
                        createdBackup = await RelationIntegrityBackupService.CreateVerifiedAsync(source, request,
                            backupDirectory, pgInfo, (message, _) => progress.Report(message), token);
                        progress.Report("Проверка выбранных исправлений и запись…");
                        return createdBackup;
                    }, ct);
                    return result with { Backup = result.Backup ?? createdBackup };
                }
                catch (OperationCanceledException)
                {
                    return new RelationIntegrityRepairResult(RelationIntegrityStatus.Cancelled, 0,
                        "Исправление отменено. Запустите проверку перед следующей попыткой.", createdBackup);
                }
                catch (Exception ex)
                {
                    Serilog.Log.Warning("Исправление связей не завершено: {ErrorType}", ex.GetType().Name);
                    return new RelationIntegrityRepairResult(RelationIntegrityStatus.Unavailable, 0,
                        "Операция не завершилась штатно. Повторная проверка покажет текущее состояние связей.", createdBackup);
                }
            });
        var permissions = (_databases?.Invoke() ?? []).Select(db => db.Permissions).Distinct().ToArray();
        var closed = false;
        void OnPermissionsChanged(object? changed, EventArgs args)
        {
            void Invalidate()
            {
                if (!closed) dialog.InvalidatePermissions();
            }
            if (Dispatcher.UIThread.CheckAccess()) Invalidate();
            else Dispatcher.UIThread.Post(Invalidate);
        }
        foreach (var service in permissions) service.Changed += OnPermissionsChanged;
        try { await dialog.ShowDialog<bool>(owner); }
        finally
        {
            closed = true;
            foreach (var service in permissions) service.Changed -= OnPermissionsChanged;
        }
    }
}
