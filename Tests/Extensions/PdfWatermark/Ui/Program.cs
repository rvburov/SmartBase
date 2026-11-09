using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Interactivity;
using Avalonia.Threading;
using SmartBase.Controls.Dialogs;
using SmartBase.Controls.Widgets;
using SmartBase.Database;
using SmartBase.Extensions.PdfWatermark;
using SmartBase.Utils;
using UglyToad.PdfPig.Writer;

namespace SmartBase.Tests.Extensions.PdfWatermark.Ui;

/// <summary>Ввод, переключение режимов, реальная обработка PDF и доступность формы без экрана.</summary>
internal static class Program
{
    private static readonly MemoryStore Store = new();
    private static int _passed;
    private static int _failed;
    private static string? _output;

    [STAThread]
    public static int Main(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        if (args.Length > 0)
        {
            if (args.Length != 2 || args[0] != "--out")
            {
                Console.Error.WriteLine("Запуск: dotnet run --project Tests/Extensions/PdfWatermark/Ui -- [--out ПАПКА]");
                return 2;
            }
            _output = Path.GetFullPath(args[1]);
            Directory.CreateDirectory(_output);
        }

        using var watchdog = new Timer(_ =>
        {
            Console.Error.WriteLine("ПРОВАЛ: стенд окна водяного знака не завершился за 45 секунд.");
            Environment.Exit(2);
        }, null, TimeSpan.FromSeconds(45), Timeout.InfiniteTimeSpan);

        AppSettings.AttachStore(Store);
        RelationDbResolver.Configure(null);
        AppBuilder.Configure<WatermarkTestApp>().UseSkia()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
            .SetupWithoutStarting();
        FontScale.Init();

        Case("Свободный режим работает без базы, получателя и даты", FreeModeWithoutDatabase);
        Case("Пустой текст и неверные границы блокируют запуск", InputValidation);
        Case("Промежутки нужны только при повторении блоков", PlacementSwitch);
        Case("Свободный текст не остается в настройках и новом окне", FreeTextIsNotRemembered);
        Case("Нажатие запуска создает PDF без базы и не записывает текст в журнал", ExecuteFreeText);
        Case("Форма прокручивается, действия и журнал доступны при разных размерах", SmallWindowLayout);

        Console.WriteLine($"ИТОГ: пройдено {_passed}, ошибок {_failed}.");
        return _failed == 0 ? 0 : 1;
    }

    private static void FreeModeWithoutDatabase()
    {
        using var fixture = new Fixture();
        Expect(fixture.Mode.SelectedIndex == 1, "при открытии сохранен режим выдачи с журналом");
        Expect(fixture.Find<TextBlock>("HolderLabel").Text == "3. Выдан", "пункт 3 называется Выдан");
        fixture.PrepareFreeText("Первая строка\nВторая строка");
        fixture.Find<TextBox>("HolderBox").Text = "";
        fixture.Find<DateField>("DateBox").Date = null;
        Pump(fixture.Window);
        Expect(fixture.FreeText.AcceptsReturn && fixture.FreeText.TextWrapping == Avalonia.Media.TextWrapping.Wrap,
            "свободная надпись вводится несколькими строками с переносом");
        Expect(fixture.Find<Panel>("FreeTextPanel").IsVisible && !fixture.Find<Panel>("JournalPanel").IsVisible,
            "свободный режим показывает текст и скрывает реквизиты выдачи");
        Expect(fixture.Find<ComboBox>("DbCombo").ItemCount == 0 && fixture.Execute.IsEnabled,
            "пустые база, получатель и дата не мешают свободному режиму");

        fixture.Mode.SelectedIndex = 1;
        fixture.Find<TextBox>("HolderBox").Text = "Иванов Иван Иванович";
        fixture.Find<DateField>("DateBox").Date = DateTime.Today;
        Pump(fixture.Window);
        Expect(!fixture.Execute.IsEnabled && fixture.Status.Contains("баз", StringComparison.OrdinalIgnoreCase),
            "режим выдачи требует базу журнала даже после заполненного свободного режима");
    }

    private static void InputValidation()
    {
        using var fixture = new Fixture();
        fixture.PrepareFreeText("  \n  ");
        Expect(!fixture.Execute.IsEnabled, "надпись из пробелов не запускается");
        fixture.FreeText.Text = "Для внутреннего использования";
        Pump(fixture.Window);
        Expect(fixture.Execute.IsEnabled, "ввод текста снимает блокировку");

        foreach (var (name, invalid) in new[]
        {
            ("BlockWidthBox", "0"), ("BlockWidthBox", "NaN"),
            ("BlockHeightBox", "-1"), ("BlockHeightBox", "Infinity"),
            ("PageMarginBox", "-2"), ("PageMarginBox", "не число"),
        })
        {
            var box = fixture.Find<TextBox>(name);
            var saved = box.Text;
            box.Text = invalid;
            Pump(fixture.Window);
            Expect(!fixture.Execute.IsEnabled, $"{name}: значение «{invalid}» блокирует запуск");
            box.Text = saved;
            Pump(fixture.Window);
            Expect(fixture.Execute.IsEnabled, $"{name}: исправление границы возвращает запуск");
        }
    }

    private static void PlacementSwitch()
    {
        using var fixture = new Fixture();
        fixture.PrepareFreeText("Контрольная копия");
        var gaps = fixture.Find<Panel>("GapPanel");
        Expect(!gaps.IsVisible, "при одном блоке промежутки скрыты");
        fixture.Find<ComboBox>("PlacementCombo").SelectedIndex = 1;
        Pump(fixture.Window);
        Expect(gaps.IsVisible && fixture.Execute.IsEnabled, "повторение блоков открывает настройку промежутков");
        fixture.Find<TextBox>("GapXBox").Text = "-1";
        Pump(fixture.Window);
        Expect(!fixture.Execute.IsEnabled, "отрицательный промежуток не допускается");
        fixture.Find<ComboBox>("PlacementCombo").SelectedIndex = 0;
        Pump(fixture.Window);
        Expect(!gaps.IsVisible && fixture.Execute.IsEnabled, "скрытый промежуток не блокирует один центральный блок");
    }

    private static void FreeTextIsNotRemembered()
    {
        const string privateText = "Одноразовая надпись 5173\nВторая строка 8426";
        using (var fixture = new Fixture())
        {
            fixture.PrepareFreeText(privateText);
            fixture.Find<ComboBox>("PlacementCombo").SelectedIndex = 1;
            fixture.Mode.SelectedIndex = 1;
            fixture.Mode.SelectedIndex = 0;
            Pump(fixture.Window);
            Expect(fixture.FreeText.Text == privateText, "переключение режима сохраняет текущий ввод в открытом окне");
        }
        Expect(Store.Values.Values.All(value => !value.Contains("5173", StringComparison.Ordinal)),
            "текст надписи не попал в хранилище настроек");
        using var reopened = new Fixture();
        reopened.Mode.SelectedIndex = 0;
        Pump(reopened.Window);
        Expect(string.IsNullOrEmpty(reopened.FreeText.Text), "повторное открытие не восстанавливает чужую надпись");
    }

    private static void ExecuteFreeText()
    {
        const string privateText = "Проверка интерфейса 9624\nДля внутреннего использования";
        using var fixture = new Fixture();
        fixture.PrepareFreeText(privateText);
        Capture(fixture.Window, "watermark-free-center");
        Click(fixture.Execute);
        Wait(() => fixture.Window.OwnedWindows.OfType<ConfirmDialog>().Any()
                   || (!fixture.Find<Button>("StopBtn").IsEnabled && fixture.Status.Contains("не удалось", StringComparison.OrdinalIgnoreCase)),
            fixture.Window, "не получен итог обработки PDF");
        var confirmation = fixture.Window.OwnedWindows.OfType<ConfirmDialog>().SingleOrDefault();
        Expect(confirmation is not null, "обработка завершилась итоговым окном: " + fixture.Find<TextBox>("LogBox").Text);
        Click(confirmation!.FindControl<Button>("ConfirmBtn")!);
        Wait(() => fixture.Execute.IsEnabled, fixture.Window, "форма не вернулась в готовое состояние");

        var files = Directory.GetFiles(fixture.Output, "*.pdf");
        Expect(files.Length == 1, "кнопка запуска создала один выходной PDF без базы данных");
        using var pdf = UglyToad.PdfPig.PdfDocument.Open(files[0]);
        Expect(pdf.NumberOfPages == 1 && string.IsNullOrEmpty(pdf.GetPage(1).Text),
            "выходной PDF содержит страницу с впечатанной надписью без текстового слоя");
        Expect(!Path.GetFileName(files[0]).Contains("экз.", StringComparison.OrdinalIgnoreCase),
            "свободный режим не приписывает номер экземпляра к имени файла");
        Expect(!fixture.Find<TextBox>("LogBox").Text!.Contains("9624", StringComparison.Ordinal)
               && Store.Values.Values.All(value => !value.Contains("9624", StringComparison.Ordinal)),
            "обработка не сохранила введенную надпись в журнал или настройки");
    }

    private static void SmallWindowLayout()
    {
        foreach (var (width, height) in new[] { (620, 620), (720, 820) })
        foreach (var mode in new[] { 0, 1 })
        {
            using var fixture = new Fixture(width, height);
            fixture.Mode.SelectedIndex = mode;
            fixture.Find<ComboBox>("PlacementCombo").SelectedIndex = 1;
            Pump(fixture.Window);
            Inside(fixture.Find<Button>("ExecuteBtn"), fixture.Window);
            Inside(fixture.Find<Button>("StopBtn"), fixture.Window);
            Inside(fixture.Find<TextBox>("LogBox"), fixture.Window);
            var scroll = fixture.Find<ScrollViewer>("FormScroll");
            Expect(scroll.Bounds.Height > 100 && scroll.Extent.Height > scroll.Viewport.Height,
                $"{width}×{height}, режим {mode}: форма имеет доступную область прокрутки");
            fixture.Find<TextBox>("GapYBox").BringIntoView();
            Pump(fixture.Window);
            var gap = fixture.Find<TextBox>("GapYBox");
            var position = gap.TranslatePoint(default, scroll) ?? throw new InvalidOperationException("поле вне дерева окна");
            Expect(position.Y >= -1 && position.Y + gap.Bounds.Height <= scroll.Bounds.Height + 1,
                $"{width}×{height}, режим {mode}: нижнее поле настроек доступно прокруткой");
            Inside(fixture.Execute, fixture.Window);
            Inside(fixture.Find<TextBox>("LogBox"), fixture.Window);
            Capture(fixture.Window, $"watermark-{width}x{height}-mode{mode}-tiled");
        }
    }

    private static void Inside(Control control, Window window)
    {
        var origin = control.TranslatePoint(default, window) ?? throw new InvalidOperationException("контрол вне окна");
        Expect(control.Bounds.Width > 0 && control.Bounds.Height > 0 && origin.X >= -1 && origin.Y >= -1
               && origin.X + control.Bounds.Width <= window.ClientSize.Width + 1
               && origin.Y + control.Bounds.Height <= window.ClientSize.Height + 1,
            $"{control.Name}: полностью виден внутри окна {window.ClientSize}");
    }

    private static void Click(Button button)
    {
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Pump();
    }

    private static void Capture(Window window, string name)
    {
        if (_output is null) return;
        Pump(window);
        using var image = window.CaptureRenderedFrame()
                          ?? throw new InvalidOperationException("не удалось получить кадр окна");
        var path = Path.Combine(_output, name + ".png");
        image.Save(path);
        Console.WriteLine("  Кадр: " + path);
    }

    private static void Pump(Window? window = null)
    {
        for (var i = 0; i < 4; i++)
        {
            Dispatcher.UIThread.RunJobs();
            window?.UpdateLayout();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        }
    }

    private static void Wait(Func<bool> condition, Window window, string problem)
    {
        var watch = Stopwatch.StartNew();
        while (!condition() && watch.Elapsed < TimeSpan.FromSeconds(15))
        {
            Pump(window);
            Thread.Sleep(2);
        }
        if (!condition()) throw new TimeoutException(problem);
    }

    private static void Expect(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
        Console.WriteLine("  PASS: " + message);
        _passed++;
    }

    private static void Case(string name, Action action)
    {
        Store.Values.Clear();
        try { action(); }
        catch (Exception error) { _failed++; Console.WriteLine($"  FAIL: {name}\n{error}"); }
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string _directory = Directory.CreateTempSubdirectory("sb-watermark-ui-").FullName;
        public string Source { get; }
        public string Output { get; }
        public PdfWatermarkWindow Window { get; }
        public ComboBox Mode => Find<ComboBox>("ModeCombo");
        public TextBox FreeText => Find<TextBox>("FreeTextBox");
        public Button Execute => Find<Button>("ExecuteBtn");
        public string Status => Find<TextBlock>("StatusText").Text ?? "";

        public Fixture(int width = 720, int height = 820)
        {
            Source = Path.Combine(_directory, "source.pdf");
            Output = Directory.CreateDirectory(Path.Combine(_directory, "output")).FullName;
            using (var builder = new PdfDocumentBuilder())
            {
                builder.AddPage(595.3, 841.9);
                File.WriteAllBytes(Source, builder.Build());
            }
            Window = new PdfWatermarkWindow { Width = width, Height = height };
            Window.Show();
            Pump(Window);
        }

        public T Find<T>(string name) where T : Control => Window.FindControl<T>(name)
            ?? throw new InvalidOperationException($"Не найден {typeof(T).Name} {name}");

        public void PrepareFreeText(string text)
        {
            Mode.SelectedIndex = 0;
            Find<TextBox>("SourceBox").Text = Source;
            Find<TextBox>("OutputBox").Text = Output;
            FreeText.Text = text;
            Pump(Window);
        }

        public void Dispose()
        {
            foreach (var child in Window.OwnedWindows.ToArray()) child.Close();
            Window.Close();
            Pump();
            Directory.Delete(_directory, recursive: true);
        }
    }

    private sealed class WatermarkTestApp : SmartBase.App
    {
        public WatermarkTestApp() { }
        public override void OnFrameworkInitializationCompleted() { }
    }

    private sealed class MemoryStore : ISettingsDatabase
    {
        public Dictionary<string, string> Values { get; } = new();
        public string? Get(string key) => Values.GetValueOrDefault(key);
        public void Set(string key, string value) => Values[key] = value;
        public void Remove(string key) => Values.Remove(key);
        public IReadOnlyList<string> KeysWithPrefix(string prefix) =>
            Values.Keys.Where(key => key.StartsWith(prefix, StringComparison.Ordinal)).ToArray();
    }
}
