using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Microsoft.EntityFrameworkCore;
using SmartBase.Controls.DrawnGrid;
using SmartBase.Core.Models;
using SmartBase.Database;
using SmartBase.Database.Entities;
using SmartBase.Settings;
using SmartBase.Utils;
using SmartBase.Views.Profile;

/// <summary>Профиль и история работают поверх старой базы без экранов доступа.</summary>
internal static class ProfileAvailabilityChecks
{
    public static int Run(string outDir)
    {
        var dir = Path.Combine(outDir, $"profile-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "data.db");
        using var registry = DatabaseRegistry.Open(path, Path.Combine(dir, "app.db"));
        var entry = registry.GetAll().Single(e => e.IsDefault);
        using var manager = DatabaseManager.Open(entry.Id, "История старой базы", path);
        var repo = manager.Constructor;
        var failed = 0;
        var checks = 0;
        var firstName = CurrentUser.FirstName;
        Window? window = null;
        try
        {
            var user = manager.Context.DbUsers.Single(u => u.UserKey == CurrentUser.Key);
            user.IsAdmin = false;
            user.IsApproved = false;
            user.AccessDecided = true;
            manager.Context.DbPermissions.Add(new DbPermissionEntity { UserId = user.Id, ActionsMask = 0 });
            manager.Context.DbMeta.Add(new DbMetaEntity { Key = "password.hash", Value = "старый-хэш" });
            manager.Context.DbMeta.Add(new DbMetaEntity { Key = "confidentiality", Value = "closed" });
            manager.Context.SaveChanges();
            var collection = repo.AddCollection(new DbCollection { Name = "Сохраненная коллекция" }).Id!.Value;
            var person = repo.AddField(new DbField { CollectionId = collection, Name = "Участник", FieldType = "user" }).Id!.Value;
            var record = repo.AddRecord(new DbRecord { CollectionId = collection, Title = "Историческая запись" }).Id!.Value;
            repo.SetFieldValue(record, person, "Прежний участник");
            var author = manager.Context.DbRecords.AsNoTracking().Single(r => r.Id == record).CreatedBy;
            repo.SetChangeLogRetention(true, 6);
            var profile = new ProfileView();
            profile.Initialize(Workspace.Attach(registry, manager));
            window = new Window { Width = 1100, Height = 760, Content = profile };
            window.Show();
            profile.ShowCategory("general");
            Pump();
            Expect(ProfileRegistry.All.Select(c => c.Key).SequenceEqual(new[] { "general", "history" }),
                "в навигации профиля остались общие данные и история");
            Expect(new[] { "AdminPanel", "SecurityPanel", "SecurityLogPanel", "SecretsPanel" }
                    .All(name => profile.FindControl<Control>(name) is null),
                "старые панели допуска, безопасности, журнала и паролей отсутствуют в интерфейсе");
            foreach (var oldSection in new[] { "admin", "security", "securitylog", "secrets" })
            {
                profile.ShowCategory(oldSection);
                Expect(profile.FindControl<Control>("GeneralPanel")!.IsVisible
                    && !profile.FindControl<Control>("HistoryPanel")!.IsVisible,
                    $"устаревший переход «{oldSection}» возвращает в общие данные");
            }
            profile.FindControl<TextBox>("FirstNameBox")!.Text = "Участник";
            Pump();
            profile.ShowCategory("history");
            var grid = profile.FindControl<ServiceGrid>("HistoryGrid")!;
            Wait(() => grid.RowCount > 0, "история не загрузилась");
            Expect(profile.FindControl<Control>("HistoryPanel")!.IsVisible && grid.RowCount > 0,
                "история старой базы с нулевой маской и отозванным допуском открывается");
            Expect(!window.OwnedWindows.Any(), "при открытии профиля и истории не появляется окно входа");
            Expect(manager.Users.GetUsers().Single(u => u.Id == user.Id).DisplayName == CurrentUser.DisplayName,
                "изменение профиля синхронизирует имя участника без роли и допуска");
            Expect(repo.GetFieldValues(record).GetValueOrDefault(person) == "Прежний участник"
                && manager.Context.DbRecords.AsNoTracking().Single(r => r.Id == record).CreatedBy == author,
                "смена имени сохраняет прежнего исполнителя и автора существующей записи");
            var retention = profile.FindControl<ComboBox>("RetentionPeriodCombo")!;
            Expect((retention.SelectedItem as ComboBoxItem)?.Tag as string == "6",
                "история показывает срок из прежних ключей метаданных");
            retention.SelectedItem = retention.Items.OfType<ComboBoxItem>().Single(i => i.Tag as string == "3");
            Pump();
            Expect(repo.GetChangeLogRetention() == (true, 3), "срок истории меняется без административной роли");
            retention.SelectedItem = retention.Items.OfType<ComboBoxItem>().Single(i => i.Tag as string == "0");
            Pump();
            Expect(repo.GetChangeLogRetention() == (false, 3), "бессрочная история сохраняет выбранное число месяцев");
            Console.WriteLine($"Профиль без контроля доступа: проверок {checks}, провалов {failed}.");
            return failed == 0 ? 0 : 2;
        }
        catch (Exception error)
        {
            Console.WriteLine(error);
            return 2;
        }
        finally
        {
            window?.Close();
            CurrentUser.FirstName = firstName;
            manager.Users.SyncDisplayName();
        }

        void Expect(bool condition, string description)
        {
            checks++;
            Console.WriteLine($"  {(condition ? "OK" : "FAIL")}: {description}");
            if (!condition) failed++;
        }
    }

    private static void Pump() => Dispatcher.UIThread.RunJobs();

    private static void Wait(Func<bool> condition, string failure)
    {
        var clock = Stopwatch.StartNew();
        while (!condition() && clock.Elapsed < TimeSpan.FromSeconds(10))
        {
            using var timeout = new CancellationTokenSource(20);
            Dispatcher.UIThread.MainLoop(timeout.Token);
            Pump();
        }
        if (!condition()) throw new InvalidOperationException(failure);
    }
}
