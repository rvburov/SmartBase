using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using SmartBase.Controls.Dialogs;
using SmartBase.Controls.Widgets;
using SmartBase.Integrations;
using SmartBase.Utils;
using ScenarioModel = SmartBase.Integrations.Scenario;

namespace SmartBase.Tests.Sections;

/// <summary>Получение файла на настоящих окнах: настройка, отказ, отмена и сохранение сценария.</summary>
internal static class IntegrationDownloadChecks
{
    private static readonly (int Id, string Name, bool Bound)[] Requests =
    [
        (17, "Вложение сообщения", false),
        (29, "Другой файл", true),
    ];

    public static int Run(string? output)
    {
        var settings = new MemorySettings();
        AppSettings.AttachStore(settings);
        CurrentUser.AttachStore(settings);
        AppBuilder.Configure<SmartBase.App>()
            .UseSkia()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
            .SetupWithoutStarting();
        FontScale.Init();
        if (output is not null) Directory.CreateDirectory(output);

        Window? owner = null;
        var checks = 0;
        var failures = 0;
        try
        {
            owner = new Window { Width = 1280, Height = 960, Title = "Стенд получения файла" };
            owner.Show();
            Pump();
            ValidateAndCancel();
            SaveDownloadSettings();
            AddAndEditScenario();
            StreamScenarioGeometry();
            NoRequests();
        }
        catch (Exception error)
        {
            failures++;
            Console.WriteLine("ОШИБКА: " + error);
        }
        finally
        {
            if (owner is not null) CloseTree(owner);
            Pump();
        }
        Console.WriteLine($"Получение файла UI: проверок {checks}, провалов {failures}.");
        return failures == 0 ? 0 : 1;

        void ValidateAndCancel()
        {
            var dialog = new ScenarioDownloadDialog(ScenarioStep.OfDownload(0, ""), Requests);
            var closed = dialog.ShowDialog<ScenarioStep?>(owner!);
            Pump();
            var request = Find<ComboBox>(dialog, "RequestCombo");
            var name = Find<TextBox>(dialog, "NameBox");
            var notice = Find<NoticeBanner>(dialog, "NoticeBanner");
            Expect(request.SelectedIndex == -1 && string.IsNullOrEmpty(name.Text),
                "новая настройка не выбирает случайный запрос и не придумывает имя");
            Expect(request.Items.Cast<string>().SequenceEqual(Requests.Select(item => item.Name)),
                "список запросов не требует привязки к коллекции");
            Click(Find<Button>(dialog, "SaveBtn"));
            Expect(!closed.IsCompleted && notice.IsVisible
                && notice.Message!.Contains("запрос", StringComparison.OrdinalIgnoreCase),
                "сохранение без запроса оставляет окно открытым и объясняет причину");
            request.SelectedIndex = 0;
            Pump();
            Click(Find<Button>(dialog, "SaveBtn"));
            Expect(!closed.IsCompleted && notice.Message!.Contains("имя файла", StringComparison.OrdinalIgnoreCase),
                "выбранный запрос без имени файла не сохраняется");
            name.Text = "Черновик.xlsx";
            Click(Find<Button>(dialog, "CancelBtn"));
            Wait(() => closed.IsCompleted, "Окно настройки не закрылось по Отмена");
            Expect(closed.GetAwaiter().GetResult() is null, "Отмена возвращает пустой результат даже после ввода");
        }

        void SaveDownloadSettings()
        {
            var dialog = new ScenarioDownloadDialog(ScenarioStep.OfDownload(29, "{{Вложение.Имя}}"), Requests);
            var closed = dialog.ShowDialog<ScenarioStep?>(owner!);
            Pump();
            Expect(Find<ComboBox>(dialog, "RequestCombo").SelectedIndex == 1
                && Find<TextBox>(dialog, "NameBox").Text == "{{Вложение.Имя}}",
                "повторное открытие восстанавливает запрос по ID и выражение имени");
            Geometry(dialog);
            Capture(dialog, "integration-download-settings");
            Find<ComboBox>(dialog, "RequestCombo").SelectedIndex = 0;
            Find<TextBox>(dialog, "NameBox").Text = "  {{Документ.Название}}  ";
            Click(Find<Button>(dialog, "SaveBtn"));
            Wait(() => closed.IsCompleted, "Корректная настройка не сохранилась");
            var saved = closed.GetAwaiter().GetResult();
            Expect(saved is { Kind: ScenarioStepKind.Download, RequestId: 17, Text: "{{Документ.Название}}" },
                "Готово возвращает Download с выбранным запросом и очищенным выражением");

            dialog = new ScenarioDownloadDialog(ScenarioStep.OfDownload(999, "Список.xlsx"), Requests);
            closed = dialog.ShowDialog<ScenarioStep?>(owner!);
            Pump();
            Expect(Find<ComboBox>(dialog, "RequestCombo").SelectedIndex == -1,
                "удаленный запрос не заменяется молча первым доступным");
            Click(Find<Button>(dialog, "SaveBtn"));
            Expect(!closed.IsCompleted && Find<NoticeBanner>(dialog, "NoticeBanner").IsVisible,
                "удаленный запрос нужно выбрать заново до сохранения");
            Click(Find<Button>(dialog, "CancelBtn"));
            Wait(() => closed.IsCompleted, "Настройка удаленного запроса не закрылась");
        }

        void AddAndEditScenario()
        {
            var scenario = ScenarioModel.New() with { Name = "Прием файлов" };
            var dialog = new ScenarioDialog(scenario, Requests, [], []);
            var closed = dialog.ShowDialog<ScenarioModel?>(owner!);
            Pump();
            var add = Find<Button>(dialog, "AddDownloadBtn");
            Expect(add.IsEnabled && Inside(add, dialog),
                "Получить файл доступно без открытых баз и кнопка находится внутри окна");
            Click(add);
            var editor = Editor(dialog);
            Expect(!DownloadCards(dialog).Any(), "до подтверждения настройки пустой шаг в список не добавляется");
            Click(Find<Button>(editor, "CancelBtn"));
            Wait(() => dialog.OwnedWindows.Count == 0, "Отмена добавления не закрыла редактор");
            Expect(!DownloadCards(dialog).Any(), "отмена настройки не добавляет шаг в сценарий");
            Click(add);
            editor = Editor(dialog);
            Find<ComboBox>(editor, "RequestCombo").SelectedIndex = 0;
            Find<TextBox>(editor, "NameBox").Text = "{{Вложение.Имя}}";
            Click(Find<Button>(editor, "SaveBtn"));
            Wait(() => DownloadCards(dialog).Count == 1, "Настроенный шаг не появился в списке");
            var card = DownloadCards(dialog).Single();
            Expect(card.Content is string text && text.Contains("Вложение сообщения", StringComparison.Ordinal)
                && text.Contains("{{Вложение.Имя}}", StringComparison.Ordinal),
                "карточка показывает запрос и выражение имени получаемого файла");
            Expect(Inside(card, dialog) && Inside(Find<Button>(dialog, "SaveBtn"), dialog),
                "карточка шага и кнопка сохранения помещаются в обычное окно сценария");
            ScenarioGeometry(dialog);
            Capture(dialog, "integration-download-scenario");
            Click(Find<Button>(dialog, "SaveBtn"));
            Wait(() => closed.IsCompleted, "Сценарий с получением файла не сохранился");
            var saved = closed.GetAwaiter().GetResult() ?? throw new InvalidOperationException("Сценарий вернул null");
            Expect(saved.Steps is [{ Kind: ScenarioStepKind.Download, RequestId: 17, Text: "{{Вложение.Имя}}" }],
                "сохранение сценария не превращает получение файла в другой вид шага");

            // Получение стоит между другими шагами: редактирование сохраняет порядок и их текст.
            scenario = saved with
            {
                Steps = [ScenarioStep.OfNotice("Начало"), saved.Steps[0], ScenarioStep.OfNotice("Конец")],
            };
            dialog = new ScenarioDialog(scenario, Requests, [], []);
            closed = dialog.ShowDialog<ScenarioModel?>(owner!);
            Pump();
            Click(DownloadCards(dialog).Single());
            editor = Editor(dialog);
            Expect(Find<ComboBox>(editor, "RequestCombo").SelectedIndex == 0
                && Find<TextBox>(editor, "NameBox").Text == "{{Вложение.Имя}}",
                "нажатие карточки открывает сохраненную настройку скачивания");
            Find<ComboBox>(editor, "RequestCombo").SelectedIndex = 1;
            Find<TextBox>(editor, "NameBox").Text = "Другой список.xlsx";
            Click(Find<Button>(editor, "SaveBtn"));
            Wait(() => dialog.OwnedWindows.Count == 0, "Измененный шаг не закрыл редактор");
            Expect(DownloadCards(dialog).Single().Content is string updated
                && updated.Contains("Другой файл", StringComparison.Ordinal)
                && updated.Contains("Другой список.xlsx", StringComparison.Ordinal),
                "изменение запроса и имени обновляет карточку");
            Click(DownloadCards(dialog).Single());
            editor = Editor(dialog);
            Find<TextBox>(editor, "NameBox").Text = "Не сохранять.xlsx";
            Click(Find<Button>(editor, "CancelBtn"));
            Wait(() => dialog.OwnedWindows.Count == 0, "Отмена правки не закрыла редактор");
            Click(Find<Button>(dialog, "SaveBtn"));
            Wait(() => closed.IsCompleted, "Сценарий после правки не сохранился");
            saved = closed.GetAwaiter().GetResult() ?? throw new InvalidOperationException("Правка вернула null");
            Expect(saved.Steps is
                [{ Kind: ScenarioStepKind.Notify, Text: "Начало" },
                 { Kind: ScenarioStepKind.Download, RequestId: 29, Text: "Другой список.xlsx" },
                 { Kind: ScenarioStepKind.Notify, Text: "Конец" }],
                "сохранение удерживает правку, отменяет черновик и сохраняет порядок соседних шагов");
        }

        void StreamScenarioGeometry()
        {
            var scenario = ScenarioModel.New() with
            {
                Name = "Файлы из потока",
                Trigger = ScenarioTrigger.OnStream(3, 17, "Ошибка", "<br>"),
                Steps = [ScenarioStep.OfDownload(17, "{{Вложение.Имя}}")],
            };
            var dialog = new ScenarioDialog(scenario, Requests, [], [], [(3, "Сообщения бота")], [(17, "Ответ боту")]);
            var closed = dialog.ShowDialog<ScenarioModel?>(owner!);
            Pump();
            ScenarioGeometry(dialog);
            var breaks = Find<TextBox>(dialog, "BreakBox");
            breaks.BringIntoView();
            Pump();
            Expect(VisibleRect(breaks, dialog).Height >= breaks.Bounds.Height - 1,
                "нижние настройки потока доступны прокруткой независимо от списка шагов");
            Capture(dialog, "integration-download-stream");
            Click(DownloadCards(dialog).Single());
            var editor = Editor(dialog);
            Expect(Find<TextBox>(editor, "NameBox").Text == "{{Вложение.Имя}}",
                "карточка файла доступна нажатию при развернутых настройках потока");
            Click(Find<Button>(editor, "CancelBtn"));
            Wait(() => dialog.OwnedWindows.Count == 0, "Редактор файла в потоке не закрылся");
            Click(Find<Button>(dialog, "CancelBtn"));
            Wait(() => closed.IsCompleted, "Сценарий потока не закрылся");
        }

        void ScenarioGeometry(ScenarioDialog dialog)
        {
            var card = DownloadCards(dialog).Single();
            var scroll = Find<ScrollViewer>(dialog, "StepsScroll");
            Expect(scroll.Viewport.Height >= card.Bounds.Height + 16,
                "списку шагов отведено место хотя бы для целой карточки с отступами");
            card.BringIntoView();
            Pump();
            Expect(VisibleRect(card, dialog).Height >= card.Bounds.Height - 1,
                "вся карточка файла видна после прокрутки списка шагов");
        }

        void NoRequests()
        {
            var dialog = new ScenarioDialog(ScenarioModel.New(), [], [], []);
            var closed = dialog.ShowDialog<ScenarioModel?>(owner!);
            Pump();
            Expect(!Find<Button>(dialog, "AddDownloadBtn").IsEnabled,
                "без карточек запросов добавление скачивания недоступно");
            Click(Find<Button>(dialog, "CancelBtn"));
            Wait(() => closed.IsCompleted, "Сценарий без запросов не закрылся");
            Expect(closed.GetAwaiter().GetResult() is null, "Отмена сценария возвращает пустой результат");
        }

        void Geometry(ScenarioDownloadDialog dialog)
        {
            var request = Find<ComboBox>(dialog, "RequestCombo");
            var name = Find<TextBox>(dialog, "NameBox");
            var save = Find<Button>(dialog, "SaveBtn");
            var cancel = Find<Button>(dialog, "CancelBtn");
            Expect(new Control[] { request, name, save, cancel }.All(control => Inside(control, dialog)),
                "оба редактора и обе кнопки находятся в клиентской области диалога");
            var requestPoint = request.TranslatePoint(default, dialog)!.Value;
            var namePoint = name.TranslatePoint(default, dialog)!.Value;
            var savePoint = save.TranslatePoint(default, dialog)!.Value;
            Expect(requestPoint.Y + request.Bounds.Height <= namePoint.Y
                && namePoint.Y + name.Bounds.Height < savePoint.Y,
                "редакторы и кнопки не накладываются друг на друга");
            Expect(Math.Abs(request.Bounds.Width - name.Bounds.Width) < 1 && name.Bounds.Width > 300,
                "запрос и выражение имени используют одинаковую доступную ширину");
        }

        void Capture(Window window, string name)
        {
            if (output is null) return;
            Pump();
            using var image = window.CaptureRenderedFrame()
                ?? throw new InvalidOperationException("Не получен кадр " + name);
            image.Save(Path.Combine(output, name + ".png"));
        }

        void Expect(bool condition, string message)
        {
            checks++;
            if (!condition) failures++;
            Console.WriteLine($"{(condition ? "PASS" : "ПРОВАЛ")}: {message}");
        }
    }

    private static ScenarioDownloadDialog Editor(ScenarioDialog owner)
    {
        Wait(() => owner.OwnedWindows.OfType<ScenarioDownloadDialog>().Any(), "Не открылось окно получения файла");
        return owner.OwnedWindows.OfType<ScenarioDownloadDialog>().Single();
    }

    private static List<Button> DownloadCards(ScenarioDialog dialog) =>
        Find<StackPanel>(dialog, "StepsPanel").GetVisualDescendants().OfType<Button>()
            .Where(button => button.Content is string text && text.StartsWith("Получить файл:", StringComparison.Ordinal))
            .ToList();

    private static bool Inside(Control control, Window window)
    {
        var point = control.TranslatePoint(default, window);
        return control.IsEffectivelyVisible && point is { } p && control.Bounds.Width > 0 && control.Bounds.Height > 0
            && p.X >= -1 && p.Y >= -1
            && p.X + control.Bounds.Width <= window.ClientSize.Width + 1
            && p.Y + control.Bounds.Height <= window.ClientSize.Height + 1;
    }

    private static void Click(Button button)
    {
        Pump();
        var window = TopLevel.GetTopLevel(button) as Window
            ?? throw new InvalidOperationException("Кнопка не подключена к окну");
        // Карточка может лежать ниже видимой части списка: сначала прокручиваем к ней,
        // затем нажимаем в видимую часть, а не в панель под обрезанным списком.
        button.BringIntoView();
        Pump();
        var visible = VisibleRect(button, window);
        if (visible.Width <= 0 || visible.Height <= 0)
            throw new InvalidOperationException("Кнопка не видна после прокрутки: " + button.Content);
        var point = visible.Center;
        var hit = window.InputHitTest(point) as Visual;
        if (hit != button && hit?.GetVisualAncestors().Contains(button) != true)
            throw new InvalidOperationException("Точка клика перекрыта другим контролом: " + button.Content);
        window.MouseDown(point, MouseButton.Left);
        window.MouseUp(point, MouseButton.Left);
        Pump();
    }

    private static Rect VisibleRect(Control control, Window window)
    {
        var start = control.TranslatePoint(default, window)
            ?? throw new InvalidOperationException("У кнопки нет положения в окне");
        var visible = new Rect(start, control.Bounds.Size).Intersect(new Rect(window.ClientSize));
        foreach (var clip in control.GetVisualAncestors().OfType<Control>().Where(ancestor => ancestor.ClipToBounds))
        {
            var clipStart = clip.TranslatePoint(default, window)
                ?? throw new InvalidOperationException("У области отсечения нет положения в окне");
            visible = visible.Intersect(new Rect(clipStart, clip.Bounds.Size));
        }
        return visible;
    }

    private static T Find<T>(Control parent, string name) where T : Control =>
        parent.FindControl<T>(name) ?? throw new InvalidOperationException("Не найден контрол " + name);

    private static void Pump()
    {
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();
    }

    private static void Wait(Func<bool> condition, string message)
    {
        var timer = Stopwatch.StartNew();
        while (!condition() && timer.Elapsed < TimeSpan.FromSeconds(8))
        {
            Pump();
            Thread.Sleep(2);
        }
        if (!condition()) throw new TimeoutException(message);
        Pump();
    }

    private static void CloseTree(Window window)
    {
        foreach (var child in window.OwnedWindows.ToArray()) CloseTree(child);
        window.Close();
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
