using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using SmartBase.Controls.Components.Security;
using SmartBase.Controls.DrawnGrid;
using SmartBase.Core.Models;
using SmartBase.Database;
using SmartBase.Settings;
using SmartBase.Utils;
using SmartBase.Views.Profile;

/// <summary>
/// Разделы модуля «Профиль» после снятия контура доступа: «Панель администратора» и «Менеджер
/// паролей» на месте и открываются, разделов «Безопасность» и «Журнал безопасности» нет, меню
/// «Добавить пароль» предлагает оба пункта.
/// </summary>
internal static class ProfileAvailabilityChecks
{
    public static int Run(string outDir)
    {
        var dir = Path.Combine(outDir, $"profile-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "data.db");
        using var registry = DatabaseRegistry.Open(path, Path.Combine(dir, "app.db"));
        var entry = registry.GetAll().Single(e => e.IsDefault);
        using var manager = DatabaseManager.Open(entry.Id, "Профиль стенда", path);
        var repo = manager.Constructor;
        var failed = 0;
        var checks = 0;
        Window? window = null;
        try
        {
            var collection = repo.AddCollection(new DbCollection { Name = "Коллекция стенда" }).Id!.Value;
            repo.AddRecord(new DbRecord { CollectionId = collection, Title = "Запись стенда" });
            manager.Permissions.SetChangeLogRetention(true, 6);

            var profile = new ProfileView();
            profile.Initialize(Workspace.Attach(registry, manager));
            window = new Window { Width = 1100, Height = 760, Content = profile };
            window.Show();
            profile.ShowCategory("general");
            Pump();

            Expect(ProfileRegistry.All.Select(c => c.Key)
                    .SequenceEqual(new[] { "general", "admin", "secrets", "history" }),
                "в навигации профиля: общие данные, панель администратора, менеджер паролей, история");
            Expect(new[] { "SecurityPanel", "SecurityLogPanel" }
                    .All(name => profile.FindControl<Control>(name) is null),
                "панелей «Безопасность» и «Журнал безопасности» нет");
            Expect(manager.Permissions.IsAdmin, "первый открывший базу — ее администратор");

            profile.ShowCategory("admin");
            Pump();
            Expect(profile.FindControl<Control>("AdminPanel")!.IsVisible,
                "«Панель администратора» открывается");
            var retention = profile.FindControl<ComboBox>("RetentionPeriodCombo")!;
            Expect((retention.SelectedItem as ComboBoxItem)?.Tag as string == "6",
                "панель показывает сохраненный срок хранения истории");
            retention.SelectedItem = retention.Items.OfType<ComboBoxItem>().Single(i => i.Tag as string == "3");
            Pump();
            Expect(manager.Permissions.RetentionEnabled && manager.Permissions.RetentionMonths == 3,
                "администратор меняет срок хранения истории");

            profile.ShowCategory("secrets");
            Pump();
            Expect(profile.FindControl<Panel>("SecretsPanel") is { IsVisible: true } secrets
                   && secrets.Children.OfType<SecretsView>().Any(),
                "«Менеджер паролей» открывается и строит опись");
            var add = profile.GetVisualDescendants().OfType<ComboBox>()
                .FirstOrDefault(c => c.Name == "AddSecretCombo");
            Expect(add is { IsVisible: true } && add.Items.Count == 2,
                "меню «Добавить пароль» видно администратору и предлагает соединение и токен");

            profile.ShowCategory("history");
            var grid = profile.FindControl<ServiceGrid>("HistoryGrid")!;
            Wait(() => grid.RowCount > 0, "история не загрузилась");
            Expect(profile.FindControl<Control>("HistoryPanel")!.IsVisible && grid.RowCount > 0,
                "история изменений открывается");
            Expect(!window.OwnedWindows.Any(), "при обходе разделов не появилось окно входа");

            Console.WriteLine($"Разделы профиля: проверок {checks}, провалов {failed}.");
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
