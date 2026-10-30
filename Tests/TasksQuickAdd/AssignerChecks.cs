using System.Collections;
using System.Reflection;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Microsoft.EntityFrameworkCore;
using SmartBase.Controls.Widgets;
using SmartBase.Core.Models;
using SmartBase.Database;
using SmartBase.Database.Entities;
using SmartBase.Utils;
using SmartBase.Views.Tasks;

namespace SmartBase.Tests.TasksQuickAdd;

/// <summary>Форма автоматически сохраняет текущего пользователя в роли назначившего задачи.</summary>
internal static class AssignerChecks
{
    // Avalonia уже поднята общим Program. Свою маленькую базу и профиль заводим только на этот прогон.
    internal static int Run()
    {
        var directory = Path.Combine(Path.GetTempPath(), "sb-quick-assigner-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var databases = new List<DatabaseManager>();
        var checks = 0;
        var failures = 0;
        try
        {
            var store = new MemorySettings();
            AppSettings.AttachStore(store);
            CurrentUser.AttachStore(store);
            CurrentUser.LastName = "Создатель";
            CurrentUser.FirstName = "Задач";
            TaskSettings.Init();
            SQLitePCL.Batteries_V2.Init();

            var first = Open(7201, "Первая", "Первый исполнитель");
            var second = Open(7202, "Вторая", "Второй исполнитель");
            var firstCollection = Prepare(first, "Задачи первой базы");
            var secondCollection = Prepare(second, "Задачи второй базы");
            var firstTarget = new QuickCollectionOption(first.DbId, firstCollection, "Первая");
            var secondTarget = new QuickCollectionOption(second.DbId, secondCollection, "Вторая");

            // Скрыт только список: после записи контроллер не запускает лишнюю фоновую перезагрузку.
            // Создание идет тем же событием формы и тем же QuickAdd, что у настоящего раздела.
            var controller = new TasksControllerView { IsVisible = false };
            controller.Initialize(databases);
            var list = controller.FindControl<TaskListView>("TaskList")!;
            list.SetCollectionOptions([firstTarget, secondTarget], firstTarget);
            Call(controller, "UpdateQuickUserOptions", first.DbId);
            var title = list.FindControl<TextBox>("QuickTitle")!;
            var executor = list.FindControl<ComboBox>("QuickExecutor")!;
            var collection = list.FindControl<ComboBox>("QuickCollection")!;
            var add = list.FindControl<Button>("QuickAddBtn")!;
            list.FindControl<DateField>("QuickDateField")!.Date = null;

            executor.SelectedItem = "Первый исполнитель";
            Submit("Задача другому исполнителю");
            var chosen = Saved(first, firstCollection, "Задача другому исполнителю");
            Expect(chosen.Assigner == CurrentUser.DisplayName && chosen.Executor == "Первый исполнитель",
                "назначивший заполняется текущим пользователем независимо от исполнителя");

            executor.SelectedItem = TaskListView.UnassignedLabel;
            Submit("Без исполнителя");
            var unassigned = Saved(first, firstCollection, "Без исполнителя");
            Expect(unassigned.Assigner == CurrentUser.DisplayName && unassigned.Executor == "",
                "нераспределенная задача также сохраняет текущего пользователя в Назначил");

            // Программный запрос без назначившего использует то же умолчание, что и форма.
            var legacy = new QuickAddRequest("Старый запрос", null, CurrentUser.DisplayName, firstTarget);
            var pending = (Task)Call(controller, "QuickAdd", legacy)!;
            Wait(pending);
            Expect(Saved(first, firstCollection, "Старый запрос").Assigner == CurrentUser.DisplayName,
                "старый запрос без назначившего сохраняет текущего пользователя");

            var explicitRequest = new QuickAddRequest("Явный назначивший", null, "", firstTarget, "Другой участник");
            Wait((Task)Call(controller, "QuickAdd", explicitRequest)!);
            Expect(Saved(first, firstCollection, "Явный назначивший").Assigner == "Другой участник",
                "программный запрос сохраняет явно заданного назначившего");

            collection.SelectedItem = secondTarget;
            var names = ((IEnumerable)executor.ItemsSource!).Cast<string>().ToArray();
            Expect(names.Contains("Второй исполнитель") && !names.Contains("Первый исполнитель"),
                "смена целевой базы обновила список исполнителей и убрала чужого пользователя");
            executor.SelectedItem = "Второй исполнитель";
            Submit("Задача второй базы");
            var movedTarget = Saved(second, secondCollection, "Задача второй базы");
            Expect(movedTarget.Assigner == CurrentUser.DisplayName && movedTarget.Executor == "Второй исполнитель"
                && !first.Context.DbRecords.Any(record => record.Title == "Задача второй базы"),
                "смена базы сохраняет автоматического назначившего и выбранного исполнителя");
            Expect(first.Context.DbRecords.Count() == 4 && second.Context.DbRecords.Count() == 1,
                "каждое добавление создало ровно одну запись");

            void Submit(string value)
            {
                list.FindControl<DateField>("QuickDateField")!.Date = null;
                title.Text = value;
                add.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            }
        }
        catch (Exception ex)
        {
            failures++;
            Console.WriteLine("ОШИБКА: " + ex);
        }
        finally
        {
            foreach (var db in databases) db.Dispose();
            Directory.Delete(directory, recursive: true);
        }
        Console.WriteLine($"Назначивший при быстром создании: проверок {checks}, провалов {failures}.");
        return failures == 0 ? 0 : 1;

        DatabaseManager Open(int id, string name, string otherUser)
        {
            var db = DatabaseManager.Open(id, name, Path.Combine(directory, id + ".db"));
            databases.Add(db);
            db.Context.DbUsers.Add(new DbUserEntity
            {
                UserKey = "other-" + id, DisplayName = otherUser, IsApproved = true,
            });
            db.Context.SaveChanges();
            return db;
        }
        void Expect(bool condition, string message)
        {
            checks++;
            if (!condition) failures++;
            Console.WriteLine($"  {(condition ? "OK" : "ПРОВАЛ")}: {message}");
        }
    }

    private static int Prepare(DatabaseManager db, string name)
    {
        var id = db.Constructor.AddCollection(new DbCollection { Name = name }).Id!.Value;
        db.Constructor.EnsureTaskCollection(id);
        return id;
    }

    private static TaskRecord Saved(DatabaseManager db, int collectionId, string title)
    {
        var recordId = db.Context.DbRecords.AsNoTracking().Single(record => record.Title == title).Id;
        return db.Constructor.GetTaskRecordsByIds(collectionId, [recordId]).Single();
    }

    private static object? Call(object target, string name, params object[] arguments) =>
        target.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(target, arguments);

    private static void Wait(Task task)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!task.IsCompleted && DateTime.UtcNow < deadline)
        {
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(1);
        }
        if (!task.IsCompleted) throw new TimeoutException("Создание задачи не завершилось");
        task.GetAwaiter().GetResult();
    }

    private sealed class MemorySettings : ISettingsDatabase
    {
        private readonly Dictionary<string, string> _values = [];
        public string? Get(string key) => _values.GetValueOrDefault(key);
        public void Set(string key, string value) => _values[key] = value;
        public void Remove(string key) => _values.Remove(key);
        public IReadOnlyList<string> KeysWithPrefix(string prefix) =>
            _values.Keys.Where(key => key.StartsWith(prefix, StringComparison.Ordinal)).ToArray();
    }
}
