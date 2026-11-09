using System.Collections;
using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;
using Microsoft.EntityFrameworkCore;
using SmartBase.Controls.Components;
using SmartBase.Core.Models;
using SmartBase.Database;
using SmartBase.Utils;

namespace SmartBase.Tests.Sections;

/// <summary>Самостоятельная проверка корней баз при отборе коллекций календаря, задач и библиотеки.</summary>
internal static class SidebarFilterChecks
{
    public static int Run()
    {
        AppBuilder.Configure<SmartBase.App>()
            .UseSkia()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
            .SetupWithoutStarting();
        SQLitePCL.Batteries_V2.Init();
        var settings = new MemorySettings();
        AppSettings.AttachStore(settings);
        CurrentUser.AttachStore(settings);
        FontScale.Init();
        ConstructorSettings.Init();
        CalendarSettings.Init();
        TaskSettings.Init();
        LibrarySettings.Init();

        var directory = Path.Combine(Path.GetTempPath(), "sb-sidebar-filter-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var databases = new List<DatabaseManager>();
        Window? window = null;
        var checks = 0;
        var failures = 0;
        try
        {
            var eligible = Open(7101, "Подключенная");
            var plain = Open(7102, "Без подходящих полей");
            var empty = Open(7103, "Только пустые группы");
            var locked = Open(7104, "Без допуска");
            var busy = Open(7105, "Временно не читается", shared: true);
            var outer = eligible.Constructor.AddGroup(new DbGroup { Name = "Группа" }).Id!.Value;
            var inner = eligible.Constructor.AddGroup(new DbGroup { Name = "Вложенная", ParentId = outer }).Id!.Value;
            var collection = eligible.Constructor.AddCollection(new DbCollection { Name = "Без записей", GroupId = inner }).Id!.Value;
            var date = eligible.Constructor.AddField(new DbField
            {
                CollectionId = collection, Name = "Дата", FieldType = "date", IsCalStart = true,
            }).Id!.Value;
            var executor = eligible.Constructor.AddField(new DbField
            {
                CollectionId = collection, Name = "Исполнитель", FieldType = "user",
            }).Id!.Value;
            eligible.Constructor.SetUserFieldTaskExec(executor, true);
            var file = eligible.Constructor.AddField(new DbField
            {
                CollectionId = collection, Name = "Файл", FieldType = "file",
            }).Id!.Value;
            eligible.Constructor.SetFileFieldInLibrary(file, true);
            var scan = eligible.Constructor.AddField(new DbField
            {
                CollectionId = collection, Name = "Скан", FieldType = "file",
            }).Id!.Value;
            plain.Constructor.AddCollection(new DbCollection { Name = "Обычная коллекция" });
            empty.Constructor.AddGroup(new DbGroup { Name = "Пустая группа" });
            var user = locked.Context.DbUsers.Single(u => u.UserKey == CurrentUser.Key);
            user.IsAdmin = false;
            user.IsApproved = false;
            locked.Context.SaveChanges();
            locked.Permissions.Reload();
            busy.Context.Database.SetCommandTimeout(1);
            var pending = new[]
            {
                new PendingDb(7106, "Подключается", "В очереди", false),
                new PendingDb(7107, "Нет связи", "Недоступна", true, isPostgres: true),
            };

            var sidebar = new SidebarTree();
            window = new Window { Width = 340, Height = 650, Content = sidebar };
            window.Show();
            Pump();
            foreach (var mode in new[] { SidebarMode.Calendar, SidebarMode.Tasks, SidebarMode.Library })
            {
                Console.WriteLine($"Режим {mode}:");
                SetFilter(mode, false);
                sidebar.InitializeWorkspace(databases, mode, pending);
                Pump();
                Expect(HasRoots(7101, 7102, 7103, 7104, 7105, 7106, 7107), "выключенный фильтр показывает все базы");
                var changes = 0;
                EventHandler<Dictionary<int, HashSet<int>>> changed = (_, _) => changes++;
                sidebar.ActiveIdsChanged += changed;
                SetFilter(mode, true);
                Pump();
                Expect(changes == 1, "изменение настройки само перестраивает открытую панель и сообщает набор");
                sidebar.ActiveIdsChanged -= changed;
                Expect(HasRoots(7101, 7104, 7106, 7107), "пустые и неподходящие базы скрыты, служебные корни остались");
                Expect(Walk(Roots()).Any(n => n.DbId == eligible.DbId && n.Id == collection && !n.IsGroup && !n.IsDatabase),
                    "подходящая коллекция без записей во вложенной группе видна");
                Expect(Roots().Single(n => n.DbId == locked.DbId).IsLocked, "запертая база доступна для запроса допуска");
                Expect(Roots().Count(n => n.IsPending) == 2 && Roots().Any(n => n.IsPending && n.Failed),
                    "подключаемая и недоступная базы сохраняют статусы");

                var uncheckedIds = (HashSet<(int Db, int Id)>)Get(sidebar, "_uncheckedIds");
                uncheckedIds.Add((eligible.DbId, collection));
                sidebar.Refresh();
                Expect(Roots().Any(n => n.DbId == eligible.DbId), "снятая галочка коллекции не прячет ее базу");
                Expect(sidebar.GetActiveIds()[eligible.DbId].Count == 0, "снятая галочка по-прежнему исключает коллекцию из раздела");
                uncheckedIds.Remove((eligible.DbId, collection));
                sidebar.Refresh();

                if (mode == SidebarMode.Library)
                {
                    eligible.Constructor.SetFileFieldInLibrary(scan, true);
                    SetRole(mode, false);
                    sidebar.Refresh();
                    Expect(Roots().Any(n => n.DbId == eligible.DbId), "другое включенное файловое поле сохраняет коллекцию и базу");
                    SetRole(mode, true);
                    eligible.Constructor.SetFileFieldInLibrary(scan, false);
                    sidebar.Refresh();
                }

                Roots().Single(n => n.DbId == eligible.DbId).IsExpanded = false;
                SetRole(mode, false);
                sidebar.Refresh();
                Expect(!Roots().Any(n => n.DbId == eligible.DbId), "выключение последней роли скрывает корень базы");
                SetRole(mode, true);
                sidebar.Refresh();
                Expect(Roots().Any(n => n.DbId == eligible.DbId), "включение роли возвращает базу");
                Expect(!Roots().Single(n => n.DbId == eligible.DbId).IsExpanded, "возвращенная база помнит свернутое состояние");

                var menu = new ContextMenu();
                menu.Items.Add(new MenuItem { Header = "Проверка закрепления" });
                Call(sidebar, "PinWhileMenuOpen", menu, (eligible.DbId, collection));
                menu.Open(sidebar);
                Pump();
                SetRole(mode, false);
                sidebar.Refresh();
                Expect(Roots().Any(n => n.DbId == eligible.DbId), "открытое меню удерживает последнюю коллекцию вместе с базой");
                menu.Close();
                Pump();
                Expect(!Roots().Any(n => n.DbId == eligible.DbId), "закрытие меню снимает закрепление и скрывает пустую базу");
                SetRole(mode, true);
                sidebar.Refresh();

                using (var writer = SqliteGate.NewConnection($"Data Source={busy.Path};Pooling=False;Default Timeout=1"))
                {
                    writer.Open();
                    using var command = writer.CreateCommand();
                    command.CommandText = "BEGIN EXCLUSIVE";
                    command.ExecuteNonQuery();
                    try
                    {
                        sidebar.Refresh();
                        Expect(Roots().Any(n => n.DbId == busy.DbId), "ошибка чтения не принимается за отсутствие подходящих коллекций");
                    }
                    finally
                    {
                        command.CommandText = "ROLLBACK";
                        command.ExecuteNonQuery();
                    }
                }
                sidebar.Refresh();
                Expect(!Roots().Any(n => n.DbId == busy.DbId), "после успешного чтения фильтр снова скрывает неподходящую базу");
                SetFilter(mode, false);
                Pump();
                Expect(HasRoots(7101, 7102, 7103, 7104, 7105, 7106, 7107), "выключение настройки возвращает все базы");
            }

            CalendarSettings.SetSidebarOnlyInSection(true);
            TaskSettings.SetSidebarOnlyInSection(true);
            LibrarySettings.SetSidebarOnlyInSection(true);
            sidebar.InitializeWorkspace(databases, SidebarMode.Constructor, pending);
            Expect(HasRoots(7101, 7102, 7103, 7104, 7105, 7106, 7107), "настройки календаря, задач и библиотеки не меняют конструктор");

            List<SidebarNode> Roots() => ((IEnumerable)Get(sidebar, "_roots")).Cast<SidebarNode>().ToList();
            bool HasRoots(params int[] ids) => Roots().Select(n => n.DbId).Order().SequenceEqual(ids.Order());
            void SetRole(SidebarMode mode, bool on)
            {
                if (mode == SidebarMode.Calendar) eligible.Constructor.SetDateFieldVisible(date, on);
                else if (mode == SidebarMode.Tasks) eligible.Constructor.SetUserFieldTaskExec(executor, on);
                else eligible.Constructor.SetFileFieldInLibrary(file, on);
            }
        }
        catch (Exception error)
        {
            Expect(false, $"непредвиденная ошибка стенда: {error}");
        }
        finally
        {
            if (window is not null) { window.Content = null; window.Close(); }
            foreach (var db in databases) db.Dispose();
            try { Directory.Delete(directory, recursive: true); }
            catch (IOException) { /* Занятые временные файлы останутся в папке стенда. */ }
            catch (UnauthorizedAccessException) { /* Временная папка не влияет на исход проверки. */ }
        }
        Console.WriteLine($"Боковая панель: проверок {checks}, расхождений {failures}.");
        return failures == 0 ? 0 : 1;

        DatabaseManager Open(int id, string name, bool shared = false)
        {
            var path = Path.Combine(directory, id + ".db");
            if (shared) NetworkPath.ForceNetwork(path);
            var db = DatabaseManager.Open(id, name, path);
            databases.Add(db);
            return db;
        }
        void Expect(bool condition, string text)
        {
            checks++;
            if (!condition) failures++;
            Console.WriteLine($"  {(condition ? "OK" : "РАСХОЖДЕНИЕ")}: {text}");
        }
    }

    private static IEnumerable<SidebarNode> Walk(IEnumerable<SidebarNode> nodes) =>
        nodes.SelectMany(n => new[] { n }.Concat(Walk(n.Children)));
    private static object Get(object target, string name) =>
        target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(target)!;
    private static void Call(object target, string name, params object[] args) =>
        target.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(target, args);
    private static void Pump() => Dispatcher.UIThread.RunJobs();
    private static void SetFilter(SidebarMode mode, bool on)
    {
        if (mode == SidebarMode.Calendar) CalendarSettings.SetSidebarOnlyInSection(on);
        else if (mode == SidebarMode.Tasks) TaskSettings.SetSidebarOnlyInSection(on);
        else LibrarySettings.SetSidebarOnlyInSection(on);
    }

    private sealed class MemorySettings : ISettingsDatabase
    {
        private readonly Dictionary<string, string> _values = [];
        public bool Has(string key) => _values.ContainsKey(key);
        public string? Get(string key) => _values.GetValueOrDefault(key);
        public void Set(string key, string value) => _values[key] = value;
        public void Remove(string key) => _values.Remove(key);
        public IReadOnlyList<string> KeysWithPrefix(string prefix) =>
            _values.Keys.Where(k => k.StartsWith(prefix, StringComparison.Ordinal)).ToList();
    }
}
