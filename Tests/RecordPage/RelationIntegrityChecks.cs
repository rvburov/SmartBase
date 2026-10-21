using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Microsoft.EntityFrameworkCore;
using SmartBase.Controls.Dialogs;
using SmartBase.Controls.Widgets;
using SmartBase.Core.Models;
using SmartBase.Database;
using SmartBase.Database.Backup;
using SmartBase.Database.Entities;
using SmartBase.Utils;
using SmartBase.Views.Settings;

/// <summary>Настройки и настоящее окно проверки: чтение, выбор, отмена, отказ копии и атомарное исправление.</summary>
internal static class RelationIntegrityChecks
{
    public static int Run(string outDir)
    {
        var folder = Path.Combine(outDir, $"relation-integrity-{Guid.NewGuid():N}");
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, "source.db");
        var registryPath = Path.Combine(folder, "registry.db");
        var backupDirectory = Path.Combine(folder, "backups");
        var formerBackupFolder = AppSettings.LoadBackupFolder();
        DatabaseManager? manager = null;
        DatabaseRegistry? registry = null;
        Window? owner = null;
        var checks = 0;
        var failed = 0;
        try
        {
            registry = DatabaseRegistry.Open(path, registryPath);
            var entry = registry.GetAll().Single(e => e.IsDefault);
            manager = DatabaseManager.Open(entry.Id, "Проверка связей", path);
            var repo = manager.Constructor;
            using var resolver = RelationDbResolver.UseScopedResolver(id => id == entry.Id ? repo : null,
                _ => entry.Id);
            var collection = repo.AddCollection(new DbCollection { Name = "Коллекция проверки" }).Id!.Value;
            var field = repo.AddField(new DbField { CollectionId = collection, Name = "Связь", FieldType = "relation",
                ConfigJson = new RelationConfig(collection, false).ToConfigJson() });
            var record = repo.AddRecord(new DbRecord { CollectionId = collection, Title = "Исходная запись" }).Id!.Value;
            var target = repo.AddRecord(new DbRecord { CollectionId = collection, Title = "Доступная цель" }).Id!.Value;
            repo.SetLinks(field.Id!.Value, record, [target]);
            manager.Context.DbRecordLinks.AddRange(
                new DbRecordLinkEntity { FieldId = field.Id.Value, FromRecordId = record, ToRecordId = record, Position = 2000 },
                new DbRecordLinkEntity { FieldId = field.Id.Value, FromRecordId = record, ToRecordId = 654321, TargetDbId = 7788, Position = 3000 });
            manager.Context.SaveChanges();
            var before = Snapshot();
            AppSettings.SaveBackupFolder(backupDirectory);
            var settings = new SettingsView();
            settings.Initialize(registry, id => id == entry.Id, isDbAdmin: _ => true,
                databases: () => [manager]);
            settings.ShowCategory("database");
            owner = new Window { Width = 1100, Height = 760, Content = settings };
            owner.Show();
            Pump();
            var launch = settings.GetVisualDescendants().OfType<Button>()
                .Single(button => button.Content as string == "Проверить связи" && Equals(button.Tag, entry.Id));
            Expect(launch.IsVisible && launch.IsEnabled, "в настройках подключенной базы доступна кнопка проверки связей");
            var dialog = OpenFromSettings();
            var rows = Rows(dialog);
            var report = Private<RelationIntegrityReport>(dialog, "_report");
            Expect(report.ScannedEdges == 3 && report.UnverifiedIssues > 0,
                "отчет считает все ребра и отдельно отмечает недоступную внешнюю цель");
            Expect(Snapshot() == before && BackupFiles().Length == 0,
                "открытие окна и полный скан не меняют базу и не создают копию");
            Expect(rows.Any(row => Issue(row).Kind == RelationIntegrityIssueKind.TargetUnavailable
                    && !CanRepair(row)) && !dialog.FindControl<Button>("RepairBtn")!.IsEnabled,
                "непроверенная цель не выбрана и не допускается к исправлению");
            SelectSelfLink(dialog);
            Expect(dialog.FindControl<Button>("RepairBtn")!.IsEnabled,
                "выбор подтвержденной ошибки включает предварительный просмотр");
            var bottom = dialog.FindControl<Button>("CloseBtn")!;
            var bottomAt = bottom.TranslatePoint(default, dialog);
            Expect(bottomAt is { } pos && pos.Y + bottom.Bounds.Height <= dialog.Bounds.Height + 1,
                "кнопки остаются видны в окне с ограниченной высотой");
            using (var frame = dialog.CaptureRenderedFrame()) frame?.Save(Path.Combine(outDir, "relation-integrity-report.png"));
            var confirmation = Preview(dialog);
            Expect(Snapshot() == before && BackupFiles().Length == 0,
                "предварительный просмотр и ожидание подтверждения не записывают данные");
            Click(confirmation, confirmation.FindControl<Button>("CancelBtn")!);
            Wait(() => !dialog.OwnedWindows.OfType<ConfirmDialog>().Any(), "отмена подтверждения не закрылась");
            Expect(Snapshot() == before && BackupFiles().Length == 0
                && dialog.FindControl<Button>("RepairBtn")!.IsEnabled,
                "отмена исправления сохраняет данные и выбранный черновик без резервной копии");
            Preview(dialog);
            SetAccess(false);
            Wait(() => !dialog.OwnedWindows.OfType<ConfirmDialog>().Any(), "отзыв прав не закрыл старое подтверждение");
            Expect(Rows(dialog).Length == 0 && Private<RelationIntegrityReport?>(dialog, "_report") is null
                && !dialog.FindControl<Button>("RepairBtn")!.IsEnabled && Snapshot() == before,
                "отзыв прав во время подтверждения удаляет прежние подписи и план без записи или копии");
            SetAccess(true);
            CloseDialog(dialog);

            var blocked = Path.Combine(folder, "not-a-directory");
            File.WriteAllText(blocked, "занято");
            AppSettings.SaveBackupFolder(blocked);
            dialog = OpenFromSettings();
            SelectSelfLink(dialog);
            confirmation = Preview(dialog);
            Click(confirmation, confirmation.FindControl<Button>("ConfirmBtn")!);
            Wait(() => !Private<bool>(dialog, "_busy") && dialog.FindControl<TextBlock>("ResultText")!.IsVisible,
                "отказ резервной копии не завершился");
            Expect(Snapshot() == before && !dialog.FindControl<Button>("RepairBtn")!.IsEnabled
                && dialog.FindControl<NoticeBanner>("NoticeBanner")!.IsVisible,
                "ошибка создания копии оставляет базу целой и требует новой проверки");
            CloseDialog(dialog);

            AppSettings.SaveBackupFolder(backupDirectory);
            dialog = OpenFromSettings();
            SelectSelfLink(dialog);
            confirmation = Preview(dialog);
            Click(confirmation, confirmation.FindControl<Button>("ConfirmBtn")!);
            Wait(() => !Private<bool>(dialog, "_busy") && dialog.FindControl<Button>("BackupLink")!.IsVisible
                && Private<RelationIntegrityReport?>(dialog, "_report") is not null,
                "исправление с копией и повторная проверка не завершились");
            var edges = manager.Context.DbRecordLinks.AsNoTracking().OrderBy(edge => edge.Position).ToList();
            Expect(edges.Count == 2 && edges.Any(edge => edge.ToRecordId == target && edge.TargetDbId == 0)
                && edges.Any(edge => edge.TargetDbId == 7788) && edges.All(edge => edge.ToRecordId != record || edge.TargetDbId != 0),
                "после подтверждения убрана только выбранная ошибочная связь, живая и недоступная цели сохранены");
            var backupPath = Private<string>(dialog, "_backupPath");
            Expect(File.Exists(backupPath) && dialog.FindControl<Button>("BackupLink")!.Content!.ToString()!.Contains(Path.GetFileName(backupPath))
                && dialog.FindControl<TextBlock>("BackupPathText")!.Text == Path.GetDirectoryName(backupPath),
                "готовая копия доступна ссылкой с именем файла и расположением");
            using (var copied = new AppDbContext(backupPath))
                Expect(copied.DbRecordLinks.Count() == 3 && copied.DbRecordLinks.Any(edge => edge.ToRecordId == record && edge.TargetDbId == 0),
                    "настоящая резервная копия содержит состояние до исправления");
            Expect(Rows(dialog).All(row => Issue(row).Kind != RelationIntegrityIssueKind.InvalidSelfLink)
                && Rows(dialog).Any(row => Issue(row).Kind == RelationIntegrityIssueKind.TargetUnavailable),
                "повторная проверка убрала исправленную ошибку и сохранила непроверенную цель в отчете");
            SetAccess(false);
            Expect(!dialog.FindControl<Button>("BackupLink")!.IsVisible
                && !dialog.FindControl<TextBlock>("BackupPathText")!.IsVisible && Rows(dialog).Length == 0,
                "после отзыва прав окно скрывает ссылку на полную копию и прежние строки отчета");
            SetAccess(true);
            CloseDialog(dialog);

            var afterRepair = Snapshot();
            var cancelDialog = new RelationIntegrityDialog("Отменяемая проверка", async (progress, ct) =>
            {
                await Task.Delay(Timeout.Infinite, ct);
                return await repo.ScanRelationIntegrityAsync(progress: progress, ct: ct);
            }, (r, keys, ct) => repo.PreviewRelationIntegrityRepairAsync(r, keys, ct),
                (_, _, _) => throw new InvalidOperationException("Запись не должна вызываться"));
            var cancelledClosed = cancelDialog.ShowDialog<bool>(owner);
            Wait(() => cancelDialog.OwnedWindows.OfType<ProgressDialog>().Any(), "не открылся прогресс проверки");
            var progressDialog = cancelDialog.OwnedWindows.OfType<ProgressDialog>().Single();
            Click(progressDialog, progressDialog.FindControl<Button>("CancelBtn")!);
            Wait(() => !Private<bool>(cancelDialog, "_busy"), "отмена проверки не завершилась");
            Expect(Snapshot() == afterRepair && cancelDialog.FindControl<TextBlock>("SummaryText")!.Text!.Contains("отменена")
                && !cancelDialog.FindControl<Button>("RepairBtn")!.IsEnabled,
                "кнопка отмены прерывает фоновую проверку без доступного плана исправлений");
            Click(cancelDialog, cancelDialog.FindControl<Button>("CloseBtn")!);
            Wait(() => cancelledClosed.IsCompleted, "окно отмененной проверки не закрылось");

            var lateReply = new TaskCompletionSource<RelationIntegrityReport>(TaskCreationOptions.RunContinuationsAsynchronously);
            var lateDialog = new RelationIntegrityDialog("Поздний отчет", (_, _) => lateReply.Task,
                (r, keys, ct) => repo.PreviewRelationIntegrityRepairAsync(r, keys, ct),
                (_, _, _) => throw new InvalidOperationException("Запись не должна вызываться"));
            void InvalidateLate(object? sender, EventArgs args) => lateDialog.InvalidatePermissions();
            manager.Permissions.Changed += InvalidateLate;
            try
            {
                lateDialog.ShowDialog<bool>(owner);
                Wait(() => Private<bool>(lateDialog, "_busy"), "не началось ожидание позднего отчета");
                SetAccess(false);
                lateReply.SetResult(report);
                Wait(() => !Private<bool>(lateDialog, "_busy"), "поздний ответ не завершился");
                Expect(Rows(lateDialog).Length == 0 && Private<RelationIntegrityReport?>(lateDialog, "_report") is null
                    && lateDialog.FindControl<TextBlock>("SummaryText")!.Text!.Contains("Права доступа изменились")
                    && Snapshot() == afterRepair,
                    "отчет, пришедший после отзыва прав, не возвращает в окно старые названия и счетчики");
                CloseDialog(lateDialog);
            }
            finally { manager.Permissions.Changed -= InvalidateLate; }
            var deniedBefore = Snapshot();
            dialog = OpenFromSettings();
            Expect(Rows(dialog).All(row => !CanRepair(row) && !Detail(row).Contains("Исходная запись")
                && !Detail(row).Contains("Доступная цель")) && !dialog.FindControl<Button>("RepairBtn")!.IsEnabled
                && Snapshot() == deniedBefore,
                "отказ исходных прав не раскрывает записи и не предлагает исправления");
            CloseDialog(dialog);

            // Окно свойства-связи проверяет только свое свойство тем же окном отчета. Кнопка
            // есть, когда база подключена в рабочем пространстве.
            SetAccess(true);
            var other = repo.AddField(new DbField { CollectionId = collection, Name = "Другая связь", FieldType = "relation",
                ConfigJson = new RelationConfig(collection, false).ToConfigJson() });
            manager.Context.DbRecordLinks.Add(new DbRecordLinkEntity
                { FieldId = other.Id!.Value, FromRecordId = record, ToRecordId = record, Position = 1000 });
            manager.Context.SaveChanges();
            var hidden = new PropertyEditorDialog(repo, collection, field.Id.Value);
            Expect(!hidden.FindControl<StackPanel>("RelationCheckBlock")!.IsVisible,
                "без подключенной базы окно свойства не предлагает проверку");
            var workspace = Workspace.Attach(registry, manager);
            RelationDbResolver.Configure(workspace);
            try
            {
                var editor = new PropertyEditorDialog(repo, collection, field.Id.Value) { MaxHeight = 600 };
                var editorClosed = editor.ShowDialog<bool>(owner);
                Pump();
                var check = editor.FindControl<Button>("RelationCheckBtn")!;
                Expect(editor.FindControl<StackPanel>("RelationCheckBlock")!.IsVisible, "окно связи показывает «Проверить связи»");
                check.BringIntoView();
                Click(editor, check);
                Wait(() => editor.OwnedWindows.OfType<RelationIntegrityDialog>().Any(), "кнопка окна свойства не открыла отчет");
                var single = editor.OwnedWindows.OfType<RelationIntegrityDialog>().Single();
                Wait(() => !Private<bool>(single, "_busy") && Private<RelationIntegrityReport?>(single, "_report") is not null,
                    "проверка свойства не завершилась");
                using (var frame = single.CaptureRenderedFrame()) frame?.Save(Path.Combine(outDir, "relation-integrity-field.png"));
                var fieldReport = Private<RelationIntegrityReport?>(single, "_report")!;
                Expect(single.FindControl<TextBlock>("DatabaseText")!.Text == $"Связь «Связь» · {entry.Name}"
                    && fieldReport.ScannedFields == 1 && fieldReport.Issues.Count > 0
                    && fieldReport.Issues.All(issue => issue.FieldId == field.Id.Value),
                    "отчет из окна свойства называет связь и содержит только ее ребра");
                Click(single, single.FindControl<Button>("CloseBtn")!);
                Wait(() => !editor.OwnedWindows.Contains(single), "отчет свойства не закрылся");
                Click(editor, editor.FindControl<Button>("CancelBtn")!);
                Wait(() => editorClosed.IsCompleted, "окно свойства не закрылось");
            }
            finally { RelationDbResolver.Configure(null); }
            Console.WriteLine($"Целостность связей UI: проверок {checks}, провалов {failed}.");
            return failed == 0 ? 0 : 2;

            RelationIntegrityDialog OpenFromSettings()
            {
                Click(owner, launch);
                Wait(() => owner.OwnedWindows.OfType<RelationIntegrityDialog>().Any(), "кнопка настроек не открыла отчет");
                var opened = owner.OwnedWindows.OfType<RelationIntegrityDialog>().Single();
                opened.MaxHeight = 560;
                Wait(() => !Private<bool>(opened, "_busy") && Private<RelationIntegrityReport?>(opened, "_report") is not null,
                    "проверка не завершилась");
                return opened;
            }
            void SelectSelfLink(RelationIntegrityDialog opened)
            {
                var row = Rows(opened).Single(r => Issue(r).Kind == RelationIntegrityIssueKind.InvalidSelfLink && CanRepair(r));
                var choice = opened.FindControl<ListBox>("FindingsList")!.GetVisualDescendants().OfType<CheckBox>()
                    .Single(box => ReferenceEquals(box.DataContext, row));
                Click(opened, choice);
            }
            ConfirmDialog Preview(RelationIntegrityDialog opened)
            {
                Click(opened, opened.FindControl<Button>("RepairBtn")!);
                Wait(() => opened.OwnedWindows.OfType<ConfirmDialog>().Any(), "не появился предварительный просмотр");
                return opened.OwnedWindows.OfType<ConfirmDialog>().Single();
            }
            void CloseDialog(RelationIntegrityDialog opened)
            {
                Click(opened, opened.FindControl<Button>("CloseBtn")!);
                Wait(() => !owner.OwnedWindows.Contains(opened), "отчет не закрылся");
            }
            string[] BackupFiles() => Directory.Exists(backupDirectory) ? Directory.GetFiles(backupDirectory) : [];
            void SetAccess(bool allowed)
            {
                var me = manager.Context.DbUsers.First(user => user.UserKey == CurrentUser.Key);
                if (allowed)
                {
                    me.IsAdmin = true;
                    manager.Context.SaveChanges();
                    manager.Permissions.Reload();
                }
                manager.Permissions.SetMask(manager.Permissions.CurrentUserId, null, (int)(allowed ? PermAction.All : PermAction.None));
                me.IsAdmin = allowed;
                me.IsApproved = true;
                manager.Context.SaveChanges();
                manager.Permissions.Reload();
                Pump();
            }
            string Snapshot() => JsonSerializer.Serialize(new
            {
                Links = manager.Context.DbRecordLinks.AsNoTracking().OrderBy(edge => edge.Id).ToList(),
                Records = manager.Context.DbRecords.AsNoTracking().OrderBy(row => row.Id)
                    .Select(row => new { row.Id, row.Title, row.UpdatedAt, row.UpdatedBy }).ToList(),
                Fields = manager.Context.DbFields.AsNoTracking().OrderBy(row => row.Id)
                    .Select(row => new { row.Id, row.FieldType, row.ConfigJson }).ToList(),
                Values = manager.Context.DbFieldValues.AsNoTracking().OrderBy(row => row.RecordId).ThenBy(row => row.FieldId)
                    .Select(row => new { row.RecordId, row.FieldId, row.Value }).ToList(),
            });
            void Expect(bool ok, string message)
            {
                checks++;
                if (!ok) failed++;
                Console.WriteLine($"  {(ok ? "ок    " : "ПРОВАЛ")} {message}");
            }
            void Wait(Func<bool> ready, string message)
            {
                var watch = Stopwatch.StartNew();
                while (!ready() && watch.Elapsed < TimeSpan.FromSeconds(40)) { Pump(); Thread.Sleep(1); }
                if (!ready()) throw new InvalidOperationException(message);
                Pump();
            }
            void Pump()
            {
                Dispatcher.UIThread.RunJobs();
                foreach (var window in Windows(owner).ToArray()) if (window.IsVisible) window.UpdateLayout();
                Dispatcher.UIThread.RunJobs();
                if (owner.IsVisible) { using var frame = owner.CaptureRenderedFrame(); }
                Dispatcher.UIThread.RunJobs();
            }
            void Click(Window window, Control control)
            {
                Pump();
                var point = control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), window)
                    ?? throw new InvalidOperationException("Контрол не прикреплен к окну");
                window.MouseMove(point);
                window.MouseDown(point, MouseButton.Left);
                window.MouseUp(point, MouseButton.Left);
                Pump();
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"ПРОВАЛ проверки целостности связей: {ex}");
            return 2;
        }
        finally
        {
            owner?.Close();
            Dispatcher.UIThread.RunJobs();
            registry?.Dispose();
            manager?.Dispose();
            AppSettings.SaveBackupFolder(formerBackupFolder);
            AppDbContext.ResetPool(path);
        }
    }

    private static T Private<T>(object owner, string name) =>
        (T)owner.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(owner)!;
    private static object[] Rows(RelationIntegrityDialog dialog) =>
        ((System.Collections.IEnumerable)Private<object>(dialog, "_all")).Cast<object>().ToArray();
    private static RelationIntegrityIssue Issue(object row) => (RelationIntegrityIssue)row.GetType().GetProperty("Issue")!.GetValue(row)!;
    private static bool CanRepair(object row) => (bool)row.GetType().GetProperty("CanRepair")!.GetValue(row)!;
    private static string Detail(object row) => (string)row.GetType().GetProperty("Detail")!.GetValue(row)!;
    private static IEnumerable<Window> Windows(Window owner)
    {
        yield return owner;
        foreach (var child in owner.OwnedWindows)
            foreach (var window in Windows(child)) yield return window;
    }
}
