using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using SmartBase.Controls.Dialogs;
using SmartBase.Core.Models;
using SmartBase.Core.Notes;
using SmartBase.Database.Entities;
using SmartBase.Database.Repositories;
using SmartBase.Integrations;
using SmartBase.Utils;

namespace SmartBase.Tests.WindowPlacement;

/// <summary>Реальные окна с авторазмером: повторные изменения формы и баннеров.
/// Сеть, сохранение документов и запуск/остановка сервера не используются.</summary>
internal static class CoreWindowChecks
{
    private const int DatabaseId = 9761;
    private const string LongTitle = "Первая строка\nВторая строка\nТретья строка";
    private static readonly BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    internal static void Run(Window owner, ConstructorRepository repository, int collection,
        Action<bool, string> check, Action<Window> settle, Func<Window, IDisposable> nativeShift)
    {
        var runner = new Runner(owner, check, settle, nativeShift);
        var databases = new[] { new DbOption(DatabaseId, "Стенд окон", repository) };
        var calendarDatabases = new[] { (DatabaseId, "Стенд окон", repository) };

        // Вторая дата дает настоящий выбор между скрытой и видимой календарной подсказкой.
        var hiddenDate = repository.AddField(new DbField
        {
            CollectionId = collection, Name = "Дата для проверки скрытой подсказки",
            FieldType = "date", IsCalStart = false,
        });
        var visibleDate = repository.GetFields(collection).First(f => f.FieldType == "date" && f.IsCalStart);

        runner.Run("Соединение", () => new IntegrationConnectionDialog(IntegrationConnection.New()),
            Enumerable.Range(0, 5).Select(index => new State($"авторизация {index}", w =>
                Required<ComboBox>(w, "AuthCombo").SelectedIndex = index)).ToArray());

        var connections = new[]
        {
            IntegrationConnection.New() with { Id = 1, Name = "Короткий адрес", Address = "https://example.invalid" },
            IntegrationConnection.New() with
            {
                Id = 2, Name = "Длинный адрес",
                Address = "https://example.invalid/" + string.Join('/', Enumerable.Repeat("длинный-путь", 18)),
            },
        };
        runner.Run("Поток событий", () => new IntegrationStreamDialog(IntegrationStream.New(), connections, _ => ""),
            new State("обычный короткий", w => SetStream(w, 0, 0, "/events")),
            new State("подписка короткая", w => SetStream(w, 1, 0, "/events")),
            new State("подписка длинная", w => SetStream(w, 1, 1, "/events/" + new string('a', 220))),
            new State("обычный длинный", w => SetStream(w, 0, 1, "/events/" + new string('a', 220))));

        runner.Run("Событие календаря", () => new RecordDialog(calendarDatabases, DatabaseId),
            new State("видимая дата", w => SetEvent(w, visibleDate.Name, false, "Коротко")),
            new State("скрытая дата", w => SetEvent(w, hiddenDate.Name, false, "Коротко")),
            new State("скрытая дата и весь день", w => SetEvent(w, hiddenDate.Name, true, "Коротко")),
            new State("многострочное название", w => SetEvent(w, visibleDate.Name, false, LongTitle)));

        runner.Run("Новая задача", () => new TaskDialog(calendarDatabases, DatabaseId, presetCollectionId: collection),
            new State("со сроком", w => SetTask(w, false, false, "Коротко")),
            new State("весь день", w => SetTask(w, false, true, "Коротко")),
            new State("без срока", w => SetTask(w, true, false, "Коротко")),
            new State("многострочное название", w => SetTask(w, false, false, LongTitle)));

        var editTask = new TaskRecord
        {
            Id = 1, DbId = DatabaseId, CollectionId = collection, Title = "Существующая задача",
            DueStart = DateTime.Today.AddHours(9), DueEnd = DateTime.Today.AddHours(10),
        };
        runner.Run("Изменение задачи", () => new TaskDialog(calendarDatabases, DatabaseId, editTask),
            new State("со сроком", w => SetTask(w, false, false, "Коротко")),
            new State("подсказка запрета переноса", w => SetTask(w, true, false, "Коротко")),
            new State("весь день", w => SetTask(w, false, true, "Коротко")));

        runner.Run("cURL", () => new CurlImportDialog(),
            new State("результат", w =>
            {
                Required<TextBox>(w, "CurlBox").Text = "curl https://example.invalid/api";
                // TextChanged доставляется через очередь: сначала завершаем редактирование,
                // затем нажимаем кнопку, как это происходит при отдельном клике человека.
                settle(w);
                Click(w, "ParseBtn");
            }),
            new State("правка скрывает результат", w =>
                Required<TextBox>(w, "CurlBox").Text = "curl https://example.invalid/edited"),
            new State("ошибка разбора", w =>
            {
                Required<TextBox>(w, "CurlBox").Text = "это не команда curl";
                settle(w);
                Click(w, "ParseBtn");
            }));

        // Валидация порта в Opened завершает проверку до создания сетевого запроса.
        // План подставлен только в объект стенда: сервисы и установленные пакеты не нужны.
        runner.Run("Помощник PostgreSQL", () =>
        {
            var window = new PgServerHelpDialog("remote.invalid", 0);
            typeof(PgServerHelpDialog).GetField("_plan", Private)!.SetValue(window,
                new PgControlPlan(false, "Стенд", "не выполнять", "не выполнять"));
            return window;
        },
            new State("локальное управление", w => Required<TextBox>(w, "HostBox").Text = "localhost"),
            new State("удаленный сервер", w => Required<TextBox>(w, "HostBox").Text = "remote.invalid"));

        var pgEntry = new DatabaseRefEntity
        {
            Id = DatabaseId, Name = "Стенд PG", Provider = "postgres", Path = "postgres://stand/db",
            PgHost = "127.0.0.1", PgPort = -1, PgDatabase = "db", PgUser = "stand",
        };
        // Отрицательный порт отвергается TcpClient до сети. Повторно вызываем настоящий
        // метод проверки уже после Show, чтобы ответ изменил высоту открытой формы.
        runner.Run("Настройки PostgreSQL", () => new DbSettingsDialog(pgEntry, null, false, null),
            new State("короткий статус", w => Probe(w, pgEntry, "127.0.0.1")),
            new State("длинный статус", w => Probe(w, pgEntry,
                string.Join('-', Enumerable.Repeat("длинный-адрес-сервера", 18)))),
            new State("ошибка имени", w =>
            {
                Required<TextBox>(w, "NameBox").Text = "";
                settle(w);
                Click(w, "SaveBtn");
            }));

        runner.Banner("Коллекция", () => new CollectionDialog(databases, DatabaseId));
        runner.Banner("Создание элемента", () => new CreateItemDialog(databases, DatabaseId));
        runner.Banner("Группа", () => new GroupDialog(databases, DatabaseId));
        runner.Banner("Создание базы SQLite", () => new CreateDatabaseDialog(Path.GetTempPath()));
        runner.Banner("Заметка", () => NoteItemDialog.ForCreate([], ""));
        runner.Banner("Подключение PostgreSQL", () => new PgConnectDialog(), "HostBox");
        runner.Banner("Создание базы PostgreSQL", () => new PgCreateDatabaseDialog(), "HostBox");
        runner.Banner("Настройки SQLite", () => new DbSettingsDialog(
            new DatabaseRefEntity { Name = "Стенд SQLite", Path = "/stand/database.db" }, null, false, null),
            confirm: "SaveBtn");

        // В Headless нет гарантии наличия Clipboard. Вызываем тот же обработчик статуса,
        // который вызывается после CopyBtn; системный буфер пользователя не затрагивается.
        runner.Run("Данные PostgreSQL", () => new PgDetailsDialog("localhost", 5432, "db", "stand", "Prefer"),
            new State("статус копирования", w => Invoke(w, "ShowStatus", "Скопировано")),
            new State("статус скрыт", w => Required<TextBlock>(w, "StatusText").IsVisible = false));
    }

    private static void SetStream(Window w, int protocol, int connection, string path)
    {
        Required<ComboBox>(w, "ProtocolCombo").SelectedIndex = protocol;
        Required<ComboBox>(w, "ConnectionCombo").SelectedIndex = connection;
        Required<TextBox>(w, "PathBox").Text = path;
    }

    private static void SetEvent(Window w, string dateName, bool allDay, string title)
    {
        var dates = Required<ComboBox>(w, "EventDateCombo");
        dates.SelectedIndex = dates.Items.Cast<object>().ToList().FindIndex(item =>
            item.ToString()!.StartsWith(dateName, StringComparison.Ordinal));
        if (dates.SelectedIndex < 0) throw new InvalidOperationException("В стенде не найдено поле даты: " + dateName);
        Required<CheckBox>(w, "AllDayCheck").IsChecked = allDay;
        Required<TextBox>(w, "TitleBox").Text = title;
    }

    private static void SetTask(Window w, bool noDate, bool allDay, string title)
    {
        Required<CheckBox>(w, "NoDateCheck").IsChecked = noDate;
        Required<CheckBox>(w, "AllDayCheck").IsChecked = allDay;
        Required<TextBox>(w, "TitleBox").Text = title;
    }

    private static void Probe(Window w, DatabaseRefEntity entry, string host)
    {
        entry.PgHost = host;
        Required<TextBox>(w, "NameBox").Text = "Стенд PG";
        ((Task)Invoke(w, "ProbeServerAsync")!).GetAwaiter().GetResult();
    }

    private static object? Invoke(Window window, string method, params object[] args) =>
        window.GetType().GetMethod(method, Private)!.Invoke(window, args);

    private static T Required<T>(Window window, string name) where T : Control =>
        window.FindControl<T>(name) ?? throw new InvalidOperationException($"Нет {name} в {window.GetType().Name}.");

    private static void Click(Window window, string name) =>
        Required<Button>(window, name).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

    private sealed record State(string Name, Action<Window> Apply);

    private sealed class Runner(Window owner, Action<bool, string> check, Action<Window> settle,
        Func<Window, IDisposable> nativeShift)
    {
        internal void Banner(string name, Func<Window> create, string field = "NameBox", string confirm = "ConfirmBtn") =>
            Run(name, create,
                new State("ошибка", w =>
                {
                    Required<TextBox>(w, field).Text = "";
                    // Иначе отложенный TextChanged скроет уже показанную кликом ошибку.
                    settle(w);
                    Click(w, confirm);
                }),
                new State("исправление скрывает ошибку", w => Required<TextBox>(w, field).Text = "Стенд"));

        internal void Run(string name, Func<Window> create, params State[] states)
        {
            var window = create();
            try
            {
                window.Show(owner);
                settle(window);

                // Сначала снимаем эталон каждой формы без эмуляции платформенного сдвига.
                // Вертикаль разных форм может штатно меняться при автоцентрировании.
                var positions = new Dictionary<string, PixelPoint>();
                var heights = new HashSet<double>();
                foreach (var state in states)
                {
                    state.Apply(window);
                    settle(window);
                    positions.Add(state.Name, window.Position);
                    heights.Add(window.ClientSize.Height);
                }
                check(heights.Count > 1, $"{name}: действия не изменили высоту реального окна.");

                using var native = nativeShift(window);
                for (var cycle = 0; cycle < 3; cycle++)
                foreach (var state in states)
                {
                    state.Apply(window);
                    settle(window);
                    check(window.Position == positions[state.Name],
                        $"{name}: авторазмер «{state.Name}», цикл {cycle + 1}: {window.Position}, ожидалось {positions[state.Name]}.");
                }

                var area = window.Screens.Primary!.WorkingArea;
                var manual = new PixelPoint(area.X + 40, area.Y + 40);
                window.Position = manual;
                settle(window);
                check(window.Position == manual, $"{name}: не установилась ручная позиция.");
                for (var cycle = 0; cycle < 2; cycle++)
                foreach (var state in states)
                {
                    state.Apply(window);
                    settle(window);
                    check(window.Position == manual,
                        $"{name}: ручная позиция после «{state.Name}», цикл {cycle + 1}: {window.Position}, ожидалось {manual}.");
                }
            }
            finally
            {
                window.Close();
                settle(window);
            }
        }
    }
}
