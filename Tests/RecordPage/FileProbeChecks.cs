using System.Collections;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using SmartBase.Controls.Behaviors;
using SmartBase.Controls.DrawnGrid;
using SmartBase.Controls.Widgets;
using SmartBase.Core.Models;

/// <summary>Медленный диск: ограниченная очередь, свежие ответы и отзывчивый файловый редактор.</summary>
internal static class FileProbeChecks
{
    public static int Run(string outDir)
    {
        var prefix = Path.Combine(outDir, $"file-probe-{Guid.NewGuid():N}");
        var gates = new List<ManualResetEventSlim>();
        var synchronousBefore = FileProbe.Synchronous;
        var probeBefore = FileProbe.ProbeOverride;
        var failures = 0;
        var checks = 0;
        var notifications = 0;
        var notificationThread = 0;
        var uiThread = Environment.CurrentManagedThreadId;
        Window? window = null;
        Action resolved = () => { notifications++; notificationThread = Environment.CurrentManagedThreadId; };
        try
        {
            Wait(() => FileProbe.Counts.Pending == 0, "не завершились прежние проверки файлов");
            FileProbe.Synchronous = false;
            FileProbe.Resolved += resolved;

            var releaseFirst = Gate();
            var releaseOthers = Gate();
            var calls = new ConcurrentDictionary<string, int>(StringComparer.Ordinal);
            var active = 0;
            var maximumActive = 0;
            var first = prefix + "/first.pdf";
            FileProbe.ProbeOverride = (path, _) =>
            {
                var count = calls.AddOrUpdate(path, 1, (_, previous) => previous + 1);
                var current = Interlocked.Increment(ref active);
                int old;
                do { old = Volatile.Read(ref maximumActive); }
                while (current > old && Interlocked.CompareExchange(ref maximumActive, current, old) != old);
                try
                {
                    // Таймаут удерживает мутант с синхронным IO от бесконечного зависания стенда.
                    if (count == 1)
                        (path == first ? releaseFirst : releaseOthers).Wait(TimeSpan.FromSeconds(5));
                    return true;
                }
                finally { Interlocked.Decrement(ref active); }
            };
            var watch = Stopwatch.StartNew();
            var unknown = FileProbe.FileExists(first);
            for (var i = 0; i < 1000; i++) FileProbe.FileExists(first);
            watch.Stop();
            Expect(unknown is null && watch.Elapsed < TimeSpan.FromMilliseconds(500),
                   "чтение неизвестного пути и тысячи повторов не ждут медленный диск");
            Wait(() => calls.ContainsKey(first), "первый путь не передан фоновому обработчику");
            Expect(calls[first] == 1, "один выполняющийся путь не проверяется повторно");

            for (var i = 0; i < FileProbe.QueueCapacity * 4; i++) FileProbe.FileExists(prefix + $"/queued-{i}.pdf");
            Wait(() => Volatile.Read(ref active) >= FileProbe.WorkerCount,
                 "не стартовало фиксированное число фоновых обработчиков");
            var bounded = FileProbe.Counts;
            Expect(maximumActive <= FileProbe.WorkerCount && bounded.Queued <= FileProbe.QueueCapacity
                   && bounded.Pending <= FileProbe.QueueCapacity + FileProbe.WorkerCount,
                   "число обработчиков, очередь и незавершенные пути остаются ограниченными");
            releaseFirst.Set();
            Wait(() => notifications > 0 && FileProbe.FileExists(first) == true,
                 "готовый ответ не дошел до UI, пока другие пути заблокированы");
            Expect(FileProbe.Counts.Pending > 0 && notificationThread == uiThread,
                   "уведомление приходит на UI до опустошения медленной очереди");
            releaseOthers.Set();
            Wait(() => FileProbe.Counts.Pending == 0, "очередь не дочиталась после освобождения диска");
            Expect(calls[first] == 1, "повторные кадры не создали вторую проверку готового пути");

            FileProbe.Synchronous = true;
            FileProbe.ProbeOverride = (_, folder) => !folder;
            var synchronous = prefix + "/sync";
            Expect(FileProbe.FileExists(synchronous) == true && FileProbe.FolderExists(synchronous) == false,
                   "синхронный режим стендов сохраняет точные отдельные ответы файла и папки");
            for (var i = 0; i < FileProbe.CacheCapacity + 128; i++) FileProbe.FileExists(prefix + $"/cached-{i}.pdf");
            Expect(FileProbe.Counts.Cached <= FileProbe.CacheCapacity, "кеш не растет сверх установленного предела");

            var aged = prefix + "/ttl.pdf";
            FileProbe.FileExists(aged);
            AgeCachedAnswer(aged);
            FileProbe.Synchronous = false;
            var releaseAged = Gate();
            var agedEntered = Gate();
            FileProbe.ProbeOverride = (_, _) =>
            {
                agedEntered.Set();
                releaseAged.Wait(TimeSpan.FromSeconds(5));
                return false;
            };
            Expect(FileProbe.FileExists(aged) == true, "просроченный ответ показывается, пока идет фоновое обновление");
            Wait(() => agedEntered.IsSet, "TTL не запустил перепроверку");
            var refreshed = FileProbe.FileExistsAsync(aged);
            releaseAged.Set();
            Wait(() => refreshed.IsCompleted, "перепроверка просроченного ответа не завершилась");
            Expect(!refreshed.Result && FileProbe.FileExists(aged) == false,
                   "после TTL новый ответ заменяет прежний без блокировки UI");
            Wait(() => FileProbe.Counts.Pending == 0, "не завершилась проверка TTL");

            var appeared = prefix + "/appeared";
            FileProbe.Synchronous = true;
            FileProbe.ProbeOverride = (_, _) => false;
            Expect(FileProbe.FileExists(appeared) == false && FileProbe.FolderExists(appeared) == false,
                   "до обновления кеш хранит отсутствие файла и папки");
            FileProbe.Synchronous = false;
            var afterRefreshCalls = 0;
            FileProbe.ProbeOverride = (_, _) => { Interlocked.Increment(ref afterRefreshCalls); return true; };
            FileProbe.ForgetAll();
            Expect(FileProbe.Counts.Cached == 0 && afterRefreshCalls == 0,
                   "явное обновление очищает кеш без обращения к диску");
            var appearedFile = FileProbe.FileExistsAsync(appeared);
            var appearedFolder = FileProbe.FolderExistsAsync(appeared);
            Wait(() => appearedFile.IsCompleted && appearedFolder.IsCompleted,
                 "после явного обновления не перечитаны файл и папка");
            Expect(appearedFile.Result && appearedFolder.Result && afterRefreshCalls == 2,
                   "после обновления новый файл и папка видны сразу, без ожидания TTL");
            Wait(() => FileProbe.Counts.Pending == 0, "не завершилась проверка обновленного кеша");

            foreach (var all in new[] { false, true })
            {
                var operation = all ? "ForgetAll" : "Forget";
                var forgotten = prefix + $"/forgotten-{operation}.pdf";
                var oldEntered = Gate();
                var releaseOld = Gate();
                var freshEntered = Gate();
                var releaseFresh = Gate();
                var attempts = 0;
                FileProbe.ProbeOverride = (_, _) =>
                {
                    if (Interlocked.Increment(ref attempts) == 1)
                    {
                        oldEntered.Set();
                        releaseOld.Wait(TimeSpan.FromSeconds(5));
                        return false;
                    }
                    freshEntered.Set();
                    releaseFresh.Wait(TimeSpan.FromSeconds(5));
                    return true;
                };
                var afterForget = FileProbe.FileExistsAsync(forgotten);
                Wait(() => oldEntered.IsSet, $"не началась проверка перед {operation}");
                if (all) FileProbe.ForgetAll();
                else FileProbe.Forget(forgotten);
                Expect(FileProbe.FileExists(forgotten) is null && attempts == 1,
                       $"{operation} не создает параллельный запрос того же пути");
                releaseOld.Set();
                Wait(() => freshEntered.IsSet, $"после {operation} не запрошен свежий ответ");
                Expect(!afterForget.IsCompleted, $"устаревший ответ после {operation} не завершает ожидание новым значением");
                releaseFresh.Set();
                Wait(() => afterForget.IsCompleted, $"свежая проверка после {operation} не завершилась");
                Expect(afterForget.Result && attempts == 2, $"{operation} отбрасывает старый ответ и принимает новый");
                Wait(() => FileProbe.Counts.Pending == 0, $"не закончилась проверка {operation}");
            }

            var releaseCancelled = Gate();
            var cancelEntered = Gate();
            var cancelCalls = 0;
            var shared = prefix + "/cancelled.pdf";
            FileProbe.ProbeOverride = (_, _) =>
            {
                Interlocked.Increment(ref cancelCalls);
                cancelEntered.Set();
                releaseCancelled.Wait(TimeSpan.FromSeconds(5));
                return true;
            };
            using var cancellation = new CancellationTokenSource();
            var cancelled = FileProbe.FileExistsAsync(shared, cancellation.Token);
            Wait(() => cancelEntered.IsSet, "не началась отменяемая проверка");
            cancellation.Cancel();
            Wait(() => cancelled.IsCompleted, "отмена не сняла ожидание редактора");
            Expect(cancelled.IsCanceled && FileProbe.FileExists(shared) is null && cancelCalls == 1,
                   "отмена ожидания не дублирует общую проверку, нужную другим ячейкам");
            releaseCancelled.Set();
            Wait(() => FileProbe.Counts.Pending == 0, "не завершился общий запрос после отмены ожидания");
            Expect(FileProbe.FileExists(shared) == true, "результат общей проверки сохраняется после отмены одного ожидания");

            var previewCalls = new ConcurrentDictionary<string, int>(StringComparer.Ordinal);
            FileProbe.ProbeOverride = (path, _) => { previewCalls.AddOrUpdate(path, 1, (_, count) => count + 1); return true; };
            var paths = Enumerable.Range(0, 2000).Select(i => prefix + $"/preview-{i:D4}.pdf").ToArray();
            var storage = FileValue.Serialize(paths);
            var subscribersBeforeEditor = ProbeSubscribers();
            var editor = new FileEditor { AutoFolder = prefix, Storage = storage, Margin = new Thickness(16) };
            window = new Window { Width = 900, Height = 200, Content = editor };
            window.Show();
            Pump();
            Wait(() => FileProbe.Counts.Pending == 0, "не завершились проверки видимого предпросмотра");
            var links = editor.FindControl<StackPanel>("MultiLinks")!;
            var more = editor.FindControl<Button>("MoreFiles")!;
            Expect(links.Children.Count <= 15 && more.IsVisible && editor.Storage == storage,
                   "две тысячи путей сохраняются полностью, но в строке создается не больше восьми ссылок");
            Expect(previewCalls.Keys.Count(p => p.Contains("/preview-", StringComparison.Ordinal)) <= 8,
                   "диск не спрашивается о невидимых путях большого списка");
            var popupCountBefore = TransientPopups.Count;
            Click(more);
            var allFiles = (more.Flyout as Flyout)?.Content as ListBox
                ?? throw new InvalidOperationException("не открылся список всех сохраненных файлов");
            Wait(() => allFiles.Items.Count == paths.Length, "список не получил все сохраненные пути");
            Expect(editor.IsInteractionBusy, "открытый список файлов удерживает активный редактор");
            Expect(TransientPopups.Count == popupCountBefore + 1,
                   "список файлов зарегистрирован для закрытия при потере фокуса окна");
            Expect(allFiles.GetVisualDescendants().OfType<ListBoxItem>().Count() < 64,
                   "полный список создает только видимые строки вместо двух тысяч контролов");
            allFiles.ScrollIntoView(paths[^1]);
            Wait(() => allFiles.ContainerFromIndex(paths.Length - 1) is not null,
                 "последний сохраненный файл недоступен прокруткой");
            var lastContainer = allFiles.ContainerFromIndex(paths.Length - 1)!;
            Expect(lastContainer.GetVisualDescendants().OfType<TextBlock>()
                       .Any(t => Equals(t.Tag, paths[^1]) && t.Classes.Contains("fileProbeLink"))
                   && allFiles.GetVisualDescendants().OfType<ListBoxItem>().Count() < 64,
                   "последний путь имеет свою ссылку, а число созданных строк остается ограниченным");
            TransientPopups.CloseAll();
            Pump();
            Expect(!more.Flyout!.IsOpen && !editor.IsInteractionBusy && TransientPopups.Count == 0,
                   "общее закрытие панелей скрывает список и освобождает редактор");
            Click(more);
            Expect(more.Flyout.IsOpen && TransientPopups.Count == 1,
                   "повторно открытый список снова регистрируется для закрытия окном");
            more.Flyout.Hide();
            Pump();
            Expect(!editor.IsInteractionBusy && TransientPopups.Count == 0,
                   "закрытие списка без ожидающего клика освобождает редактор и реестр");

            editor.AutoFolder = "";
            editor.Storage = prefix + "/manual-start.pdf";
            var releaseValidation = Gate();
            var validationEntered = Gate();
            var stale = prefix + "/manual-stale.pdf";
            var currentPath = prefix + "/manual-current.pdf";
            var missing = prefix + "/manual-missing.pdf";
            FileProbe.ProbeOverride = (path, _) =>
            {
                if (path == stale)
                {
                    validationEntered.Set();
                    releaseValidation.Wait(TimeSpan.FromSeconds(5));
                    return false;
                }
                return path != missing;
            };
            editor.BeginManualEdit();
            var input = editor.FindControl<TextBox>("Input")!;
            watch.Restart();
            input.Text = stale;
            Pump();
            watch.Stop();
            Expect(watch.Elapsed < TimeSpan.FromMilliseconds(500), "ввод пути и кадр не ждут проверку сетевого файла");
            Wait(() => validationEntered.IsSet, "ручной ввод не запросил фоновую проверку");
            input.Text = currentPath;
            Pump();
            Wait(() => FileProbe.FileExists(currentPath) == true, "не прочитан текущий введенный путь");
            releaseValidation.Set();
            Wait(() => FileProbe.Counts.Pending == 0, "не завершилась устаревшая проверка ручного ввода");
            Expect(input.Text == currentPath && !editor.FindControl<Popup>("ErrPop")!.IsOpen,
                   "поздний ответ старого пути не показывает ошибку у нового значения");
            input.Text = missing;
            Wait(() => editor.FindControl<Popup>("ErrPop")!.IsOpen,
                 "готовый отрицательный ответ не показал ошибку текущего пути");
            Expect(input.Text == missing, "ошибка существования относится к текущему введенному пути");
            input.Text = currentPath;
            Wait(() => !editor.FindControl<Popup>("ErrPop")!.IsOpen,
                 "верный путь не убрал прежнюю ошибку");
            Expect(input.Text == currentPath, "готовый положительный ответ убирает ошибку без смены введенного текста");
            editor.CancelManualEdit();

            var clickPath = prefix + "/click-missing.pdf";
            var clickEntered = Gate();
            var releaseClick = Gate();
            FileProbe.ProbeOverride = (path, _) =>
            {
                if (path == clickPath)
                {
                    clickEntered.Set();
                    releaseClick.Wait(TimeSpan.FromSeconds(5));
                    return false;
                }
                return true;
            };
            editor.Storage = clickPath;
            typeof(FileEditor).GetMethod("OnSingleClick", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(editor, [null]);
            Wait(() => clickEntered.IsSet, "клик не запросил проверку отсутствующего файла");
            Expect(editor.IsInteractionBusy && !input.IsVisible,
                   "ожидающий диск клик удерживает редактор, не открывая ввод сырого пути");
            window.Content = null;
            Pump();
            Wait(() => !editor.IsInteractionBusy, "снятие редактора не отменило ожидающий клик");
            releaseClick.Set();
            Wait(() => FileProbe.Counts.Pending == 0, "не закончилась поздняя проверка после снятия редактора");
            Expect(editor.Storage == clickPath && !input.IsVisible && !editor.FindControl<Popup>("ErrPop")!.IsOpen,
                   "поздний отрицательный ответ снятого редактора не открывает ввод и не показывает плашку");
            Expect(ProbeSubscribers() == subscribersBeforeEditor, "снятый редактор отписывается от фоновых уведомлений");

            Console.WriteLine($"Проверки файлов в фоне: проверок {checks}, провалов {failures}.");
            return failures == 0 ? 0 : 2;

            ManualResetEventSlim Gate()
            {
                var gate = new ManualResetEventSlim(false);
                gates.Add(gate);
                return gate;
            }

            void Expect(bool condition, string message)
            {
                checks++;
                Console.WriteLine($"  {(condition ? "ок    " : "ПРОВАЛ")} {message}");
                if (!condition) failures++;
            }

            void Pump()
            {
                Dispatcher.UIThread.RunJobs();
                if (window is { IsVisible: true })
                {
                    window.UpdateLayout();
                    using var frame = window.CaptureRenderedFrame();
                }
                Dispatcher.UIThread.RunJobs();
            }

            void Wait(Func<bool> ready, string message)
            {
                var timeout = Stopwatch.StartNew();
                while (!ready() && timeout.Elapsed < TimeSpan.FromSeconds(10))
                {
                    Pump();
                    Thread.Sleep(1);
                }
                if (!ready()) throw new InvalidOperationException(message);
                Pump();
            }

            void Click(Control control)
            {
                Pump();
                var center = control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), window!)
                    ?? throw new InvalidOperationException("кнопка списка не прикреплена к окну");
                window!.MouseMove(center);
                window.MouseDown(center, MouseButton.Left);
                window.MouseUp(center, MouseButton.Left);
                Pump();
            }
        }
        catch (Exception exception)
        {
            Console.WriteLine($"ПРОВАЛ проверки фоновых файлов: {exception}");
            return 2;
        }
        finally
        {
            window?.Close();
            foreach (var gate in gates) gate.Set();
            var timeout = Stopwatch.StartNew();
            while (FileProbe.Counts.Pending > 0 && timeout.Elapsed < TimeSpan.FromSeconds(10))
            {
                Dispatcher.UIThread.RunJobs();
                Thread.Sleep(1);
            }
            FileProbe.Resolved -= resolved;
            FileProbe.ProbeOverride = probeBefore;
            FileProbe.Synchronous = synchronousBefore;
            if (FileProbe.Counts.Pending == 0)
                foreach (var gate in gates) gate.Dispose();
            Dispatcher.UIThread.RunJobs();
        }
    }

    // Подписка снятого редактора не должна удерживать его через статическое событие.
    private static int ProbeSubscribers() =>
        (typeof(FileProbe).GetField("Resolved", BindingFlags.Static | BindingFlags.NonPublic)!
            .GetValue(null) as Delegate)?.GetInvocationList().Length ?? 0;

    // Изменяем только возраст готового ответа: минуту реального ожидания стенду не нужно.
    private static void AgeCachedAnswer(string path)
    {
        const BindingFlags flags = BindingFlags.Static | BindingFlags.NonPublic;
        var gate = typeof(FileProbe).GetField("Gate", flags)!.GetValue(null)!;
        var cache = (IDictionary)typeof(FileProbe).GetField("Cache", flags)!.GetValue(null)!;
        lock (gate)
            foreach (DictionaryEntry entry in cache)
                if ((string?)entry.Key.GetType().GetProperty("Path")!.GetValue(entry.Key) == path)
                {
                    var age = Stopwatch.GetTimestamp() - (long)((FileProbe.Ttl.TotalSeconds + 1) * Stopwatch.Frequency);
                    entry.Value!.GetType().GetField("At")!.SetValue(entry.Value, age);
                    return;
                }
        throw new InvalidOperationException("не найден ответ для проверки TTL");
    }
}
