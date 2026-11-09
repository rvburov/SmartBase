using Avalonia.Controls;
using Avalonia.Threading;
using SmartBase.Core.Models;
using SmartBase.Database;
using SmartBase.Database.Backup;
using SmartBase.Database.Entities;
using SmartBase.Database.Repositories;
using SmartBase.Utils;

namespace SmartBase.Controls.Dialogs;

public partial class RelationIntegrityDialog
{
    /// <summary>Открыть проверку связей базы <paramref name="source"/>: всей базы (настройки) или
    /// одного свойства-связи <paramref name="fieldId"/> с его столбцами данных (окно свойства).
    /// Исправление в обоих случаях идет одним путем: свежая проверка, резервная копия, запись.
    /// Возвращает, было ли подтверждено исправление.</summary>
    public static async Task<bool> ShowForAsync(Window owner, DatabaseManager source, DatabaseRefEntity entry,
        IReadOnlyList<PermissionService> permissions, string title, int? fieldId = null)
    {
        // Снимок параметров делается на UI-потоке. В тексты и журналы они не попадают.
        var pgInfo = entry.ToPgInfo();
        var backupDirectory = AppSettings.ResolveBackupFolder();
        var repo = source.Constructor;
        var options = new RelationIntegrityScanOptions(FieldId: fieldId);
        var dialog = new RelationIntegrityDialog(title,
            (progress, ct) => repo.ScanRelationIntegrityAsync(options, progress, ct),
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
        try { return await dialog.ShowDialog<bool>(owner); }
        finally
        {
            closed = true;
            foreach (var service in permissions) service.Changed -= OnPermissionsChanged;
        }
    }
}
