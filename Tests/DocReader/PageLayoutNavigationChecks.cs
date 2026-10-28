using System.Collections;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using SmartBase.Core.Documents;
using SmartBase.Core.Models;
using SmartBase.Utils;
using SmartBase.Views.Library;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Writer;

// Переход к совпадению должен работать и пока родитель только изменил раскладку.
// Сначала выполняется действие пользователя, затем кадр: принудительный UpdateLayout
// перед кликом здесь скрыл бы тот порядок событий, который нужно проверить.
internal static class PageLayoutNavigationChecks
{
    private const string Needle = "LayoutNeedle";
    private const int PageCount = 64;

    public static void Run(string directory, Action<bool, string> check)
    {
        var pdf = Path.Combine(directory, "разные-размеры-страниц.pdf");
        WritePdf(pdf);
        var replacement = Path.Combine(directory, "новый-документ.txt");
        File.WriteAllText(replacement, "Другой документ без совпадений прежнего PDF");
        var errors = new ConcurrentQueue<Exception>();
        void CaptureUi(object? sender, DispatcherUnhandledExceptionEventArgs e)
        {
            errors.Enqueue(e.Exception);
            e.Handled = true; // Только стенд: каждое исключение ниже делает результат неуспешным.
        }
        void CaptureTask(object? sender, UnobservedTaskExceptionEventArgs e)
        {
            errors.Enqueue(e.Exception);
            e.SetObserved();
        }

        var reader = new DocReaderView();
        var host = new Grid { ColumnDefinitions = new ColumnDefinitions("*,420") };
        // Та же нижняя граница, что у панели чтения в MainWindow.ApplyColumns.
        host.ColumnDefinitions[1].MinWidth = AppSettings.LibraryReaderWidthMin;
        host.Children.Add(new Border());
        Grid.SetColumn(reader, 1);
        host.Children.Add(reader);
        var scaleHost = new LayoutTransformControl { Child = host, LayoutTransform = new ScaleTransform(1, 1) };
        var window = new Window { Width = 900, Height = 720, Content = scaleHost };
        var originalScale = Scaling.Current;
        Dispatcher.UIThread.UnhandledException += CaptureUi;
        TaskScheduler.UnobservedTaskException += CaptureTask;
        try
        {
            window.Show();
            reader.Show(new DocRecord { Title = "Разные размеры", Path = pdf }, Needle);
            Require(() => Field<bool>(reader, "_pagesScanned") && Hits(reader).Count == 2,
                    "64 страницы и оба совпадения не подготовились", errors);

            var pages = Find<ListBox>(reader, "PagesList");
            var docMode = Find<ToggleButton>(reader, "DocModeBtn");
            var textMode = Find<ToggleButton>(reader, "TextModeBtn");
            var next = Find<Button>(reader, "NextBtn");
            var previous = Find<Button>(reader, "PrevBtn");
            check(pages.ItemCount == PageCount, "в разноразмерном документе потеряны страницы");

            Console.WriteLine("Первое совпадение на странице 1, второе на далекой странице 64");
            docMode.IsChecked = true;
            Require(() => IsCurrentWordVisible(reader), "первое совпадение не попало в окно", errors, () => DescribeCurrentWord(reader));
            CheckGeometry(reader, check);

            // Следующий клик везет на еще не реализованную далекую страницу. Ширина
            // изменилась, но обычная раскладка еще не успела завершиться: обработчик
            // клика не должен принудительно запускать ее внутри перехода к совпадению.
            check(pages.ContainerFromIndex(PageCount - 1) is null,
                  "далекая страница уже реализована: проверка принудительной раскладки не действует");
            host.ColumnDefinitions[1].Width = new GridLength(400);
            var synchronousLayout = false;
            void LayoutDuringClick(object? sender, EventArgs e) => synchronousLayout = true;
            window.LayoutUpdated += LayoutDuringClick;
            Click(next);
            window.LayoutUpdated -= LayoutDuringClick;
            check(!synchronousLayout, "переход к совпадению принудительно запустил раскладку внутри клика");
            Require(() => IsCurrentWordVisible(reader), "далекое совпадение не попало в окно", errors, () => DescribeCurrentWord(reader));
            check(Field<int>(reader, "_current") == 1, "переход не выбрал второе совпадение");
            CheckGeometry(reader, check);

            foreach (var scale in new[] { 0.7, 1.0, 1.3 })
            foreach (var width in new[] { 320.0, 400.0, 460.0 })
            {
                Console.WriteLine($"Смена раскладки до перехода: масштаб {scale}, ширина {width}");
                Scaling.SetScale(scale);
                scaleHost.LayoutTransform = new ScaleTransform(scale, scale);
                host.ColumnDefinitions[1].Width = new GridLength(width);
                Click(previous); // Никакого Pump/UpdateLayout между сменой раскладки и кликом.
                Require(() => IsCurrentWordVisible(reader), "предыдущее совпадение потерялось при смене раскладки", errors, () => DescribeCurrentWord(reader));
                CheckGeometry(reader, check);

                textMode.IsChecked = true;
                docMode.IsChecked = true;
                Click(next);
                Require(() => IsCurrentWordVisible(reader), "совпадение потерялось после быстрого переключения режима", errors,
                        () => DescribeCurrentWord(reader));
                CheckGeometry(reader, check);
            }

            host.ColumnDefinitions[1].MinWidth = 0;
            host.ColumnDefinitions[1].Width = new GridLength(0);
            reader.IsVisible = false;
            Frame();
            host.ColumnDefinitions[1].MinWidth = AppSettings.LibraryReaderWidthMin;
            host.ColumnDefinitions[1].Width = new GridLength(360);
            reader.IsVisible = true;
            Click(previous);
            Require(() => IsCurrentWordVisible(reader), "совпадение не восстановилось после показа панели", errors, () => DescribeCurrentWord(reader));
            CheckGeometry(reader, check);

            // Отложенное доведение слова не должно трогать уже открытый другой документ.
            Click(next);
            reader.Show(new DocRecord { Title = "Новый", Path = replacement }, "Другой");
            Require(() => Find<TextBlock>(reader, "CounterLabel").Text == "1 из 1",
                    "новый документ не открылся после поставленного перехода", errors);
            Frames(3);
            check(reader.Document?.Path == replacement && pages.ItemsSource is null,
                  "старый переход изменил состояние нового документа");

            reader.Show(new DocRecord { Title = "Перед снятием", Path = pdf }, Needle);
            Require(() => Field<bool>(reader, "_pagesScanned") && Hits(reader).Count == 2,
                    "документ повторно не подготовился", errors);
            docMode.IsChecked = true;
            host.Children.Remove(reader);
            Frames(3);
            check(reader.Document is null && pages.ItemsSource is null,
                  "снятая панель сохранила прежний документ");
            host.Children.Add(reader);
            reader.Show(new DocRecord { Title = "После снятия", Path = replacement }, "Другой");
            Require(() => Find<TextBlock>(reader, "CounterLabel").Text == "1 из 1",
                    "панель не работает после снятия с ожидающим переходом", errors);
            CheckErrors(errors);
        }
        finally
        {
            window.Close();
            Scaling.SetScale(originalScale);
            GC.Collect();
            GC.WaitForPendingFinalizers();
            Dispatcher.UIThread.RunJobs();
            Dispatcher.UIThread.UnhandledException -= CaptureUi;
            TaskScheduler.UnobservedTaskException -= CaptureTask;
            foreach (var error in errors) check(false, "ошибка UI/фоновой задачи: " + error);
        }
    }

    private static void WritePdf(string path)
    {
        using var builder = new PdfDocumentBuilder();
        var font = builder.AddStandard14Font(Standard14Font.Helvetica);
        for (var i = 0; i < PageCount; i++)
        {
            var (width, height) = i == PageCount - 1 ? (300.0, 2000.0) : (i % 3) switch
            {
                0 => (595.0, 842.0),
                1 => (842.0, 595.0),
                _ => (300.0, 2000.0),
            };
            var page = builder.AddPage(width, height);
            page.AddText($"Page {i + 1}", 12, new PdfPoint(24, height - 24), font);
            if (i == 0) page.AddText(Needle, 12, new PdfPoint(24, height - 60), font);
            if (i == PageCount - 1) page.AddText(Needle, 12, new PdfPoint(24, 24), font);
        }
        File.WriteAllBytes(path, builder.Build());
    }

    private static void CheckGeometry(DocReaderView reader, Action<bool, string> check)
    {
        var list = Find<ListBox>(reader, "PagesList");
        var scroll = Field<ScrollViewer>(reader, "_pagesScroll");
        check(Finite(scroll.Offset.X, scroll.Offset.Y, scroll.Extent.Width, scroll.Extent.Height,
                     scroll.Viewport.Width, scroll.Viewport.Height), "геометрия прокрутки содержит нечисловое значение");
        var realized = list.GetVisualDescendants().OfType<ListBoxItem>().ToArray();
        check(realized.Length > 0 && realized.Length < PageCount,
              $"виртуализация страниц не работает: контейнеров {realized.Length} из {PageCount}");
        foreach (var item in realized)
            check(Finite(item.Bounds.X, item.Bounds.Y, item.Bounds.Width, item.Bounds.Height),
                  "геометрия контейнера страницы содержит нечисловое значение");
    }

    private static bool IsCurrentWordVisible(DocReaderView reader)
    {
        var current = Field<int>(reader, "_current");
        var hits = Hits(reader);
        if (current < 0 || current >= hits.Count) return false;
        var (pageIndex, wordIndex) = hits[current];
        var list = Find<ListBox>(reader, "PagesList");
        if (list.ContainerFromIndex(pageIndex) is not Control container) return false;
        var view = container.GetVisualDescendants().OfType<Control>()
                            .FirstOrDefault(control => control.GetType().Name == "PageItemView");
        if (view is null || view.Bounds.Width <= 0) return false;
        var page = Field<IList>(reader, "_pages")[pageIndex]!;
        var words = Property<IReadOnlyList<DocWord>>(page, "Words");
        if (wordIndex < 0 || wordIndex >= words.Count) return false;
        var size = Property<DocPageSize>(page, "Size");
        var box = DocPageGeometry.ToScreen(words[wordIndex].Box, size.Height,
                                         DocPageGeometry.ScaleFor(view.Bounds.Width, size.Width), padding: 0.5);
        var scroll = Field<ScrollViewer>(reader, "_pagesScroll");
        if (view.TranslatePoint(new Point(box.X, box.Y), scroll) is not { } point) return false;
        return Finite(point.X, point.Y, box.Width, box.Height, scroll.Viewport.Height)
               && scroll.Viewport.Height > 0 && point.Y >= -1
               && point.Y + box.Height <= scroll.Viewport.Height + 1;
    }

    private static string DescribeCurrentWord(DocReaderView reader)
    {
        var current = Field<int>(reader, "_current");
        var hits = Hits(reader);
        var list = Find<ListBox>(reader, "PagesList");
        var scroll = Field<ScrollViewer>(reader, "_pagesScroll");
        var state = $"current={current}, hits={hits.Count}, counter={Find<TextBlock>(reader, "CounterLabel").Text}, "
                  + $"list={list.Bounds}, offset={scroll.Offset}, extent={scroll.Extent}, viewport={scroll.Viewport}";
        if (current < 0 || current >= hits.Count) return state;
        var (index, wordIndex) = hits[current];
        if (list.ContainerFromIndex(index) is not Control container) return state + $", page={index}: container absent";
        var view = container.GetVisualDescendants().OfType<Control>()
                            .FirstOrDefault(control => control.GetType().Name == "PageItemView");
        if (view is null) return state + $", page={index}: view absent";
        var page = Field<IList>(reader, "_pages")[index]!;
        var words = Property<IReadOnlyList<DocWord>>(page, "Words");
        var size = Property<DocPageSize>(page, "Size");
        var box = DocPageGeometry.ToScreen(words[wordIndex].Box, size.Height,
                                         DocPageGeometry.ScaleFor(view.Bounds.Width, size.Width), padding: 0.5);
        var point = view.TranslatePoint(new Point(box.X, box.Y), scroll);
        return state + $", page={index}, container={container.Bounds}, view={view.Bounds}, box={box}, point={point}";
    }

    private static void Require(Func<bool> until, string complaint, ConcurrentQueue<Exception> errors,
                                Func<string>? diagnostic = null)
    {
        var timer = Stopwatch.StartNew();
        do
        {
            Frame();
            CheckErrors(errors);
            if (until()) return;
            Thread.Sleep(10);
        } while (timer.Elapsed < TimeSpan.FromSeconds(15));
        throw new InvalidOperationException(complaint + (diagnostic is null ? "" : "\n" + diagnostic()));
    }

    private static void CheckErrors(ConcurrentQueue<Exception> errors)
    {
        if (errors.TryPeek(out var error)) throw new InvalidOperationException("Ошибка отложенной раскладки", error);
    }

    private static void Frames(int count) { for (var i = 0; i < count; i++) Frame(); }
    private static void Frame()
    {
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();
    }

    private static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    private static bool Finite(params double[] values) => values.All(double.IsFinite);
    private static List<(int Page, int Index)> Hits(DocReaderView reader) => Field<List<(int Page, int Index)>>(reader, "_pageHits");
    private static T Find<T>(Control parent, string name) where T : Control =>
        parent.GetVisualDescendants().OfType<T>().Single(control => control.Name == name);
    private static T Field<T>(object target, string name) =>
        (T)target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(target)!;
    private static T Property<T>(object target, string name) =>
        (T)target.GetType().GetProperty(name, BindingFlags.Instance | BindingFlags.Public)!.GetValue(target)!;
}
