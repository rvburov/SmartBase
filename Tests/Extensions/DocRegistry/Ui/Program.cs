using System.Diagnostics;
using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Avalonia.VisualTree;
using SmartBase.Controls.Dialogs;
using SmartBase.Extensions;
using SmartBase.Extensions.DocRegistry;
using SmartBase.Utils;
using SmartBase.Views.Settings;

namespace SmartBase.Tests.Extensions.DocRegistry.Ui;

/// <summary>Настройки и рассылка на настоящих контролах. IActionReplies заменен стендом.</summary>
internal static class Program
{
    private const string Participants = "1001 Иванов Иван; 1002 Петров Петр; 1003 Сидоров Сидор";
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
                Console.Error.WriteLine("Запуск: dotnet run --project Tests/Extensions/DocRegistry/Ui -- [--out ПАПКА]");
                return 2;
            }
            _output = Path.GetFullPath(args[1]);
            Directory.CreateDirectory(_output);
        }
        AppSettings.AttachStore(Store);
        AppBuilder.Configure<SmartBase.App>().UseSkia()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
            .SetupWithoutStarting();
        FontScale.Init();

        Case("Кнопка инструкции справа, редактор видит несохраненный список", SettingsLayout);
        Case("Черновик, выбор адресатов, поиск и применение галочек", DraftAndSelection);
        Case("Новый список исключает удаленных и не выбирает новых сам", UpdatedParticipants);
        Case("Большой список виртуализирован и доступен поиском", LargeList);
        Case("Отмена подтверждения не отправляет сообщение", CancelConfirmation);
        Case("Отправка показывает выбранных и экранирует текст", SuccessfulSend);
        Case("Ошибки адресатов не мешают остальным участникам", PartialSend);
        Case("Остановка прерывает ожидание и не начинает следующих", CancelSending);
        Case("Нет пути отправки: причина в окне, вызовов отправки нет", MissingRoute);
        Case("Удаленный адресат исключается перед отправкой", RemovedBeforeSend);
        Case("Длинное подтверждение прокручивается, кнопки остаются видны", LongConfirmation);
        Case("Длинный отчет прокручивается, результат можно закрыть", LongReport);

        Console.WriteLine($"ИТОГ: пройдено {_passed}, ошибок {_failed}.");
        return _failed == 0 ? 0 : 1;
    }

    private static void SettingsLayout()
    {
        Store.Values.Clear();
        AppSettings.SaveExtensionSetting(DocRegistryExtension.ExtensionId, DocRegistryExtension.SendersKey, Participants);
        var settings = new SettingsView();
        using var owner = new ShownWindow(settings, 1100, 800);
        settings.ShowCategory("extensions");
        Pump();
        var source = settings.GetVisualDescendants().OfType<TextBox>()
            .Single(box => Equals(box.Tag, DocRegistryExtension.SendersKey));
        var instruction = settings.GetVisualDescendants().OfType<Button>()
            .Single(button => Equals(button.Content, SenderGreeting.ButtonText));
        Expect(source.Parent is Grid && ReferenceEquals(source.Parent, instruction.Parent),
            "поле участников и инструкция находятся в общей строке");
        Expect(Grid.GetColumn(source) == 0 && Grid.GetColumn(instruction) == 2,
            "кнопка инструкции стоит в правой колонке");
        Expect(source.Bounds.Width > 80 && instruction.Bounds.Width > 60
            && instruction.Bounds.Left >= source.Bounds.Right
            && Math.Abs(source.Bounds.Center.Y - instruction.Bounds.Center.Y) <= 1,
            "измеренные прямоугольники поля и кнопки расположены на одной линии");
        var editor = settings.GetVisualDescendants().OfType<ParticipantBroadcastEditor>().Single();
        var picker = Find<ParticipantPicker>(editor, "BroadcastRecipients");
        if (_output is not null)
        {
            editor.GetVisualAncestors().OfType<Border>().First(border => border.Classes.Contains("settingRow")).BringIntoView();
            Pump();
            Capture(owner.Window, "doc-registry-settings");
        }
        // Фокус не менялся: чтение текущего TextBox должно работать и до LostFocus.
        source.Text = "1004 Новый участник";
        picker.RefreshParticipants();
        using var panel = new PickerPanel(picker);
        Expect(Rows(panel.Control).Select(RowLabel).SequenceEqual(["1004 Новый участник"]),
            "выбор получает актуальный ввод поля, а не старую запись в хранилище");
    }

    private static void DraftAndSelection()
    {
        using var fixture = new EditorFixture(draft: "Первая строка\nВторая строка");
        Expect(fixture.Message.AcceptsReturn && fixture.Message.TextWrapping == Avalonia.Media.TextWrapping.Wrap,
            "сообщение вводится в многострочном поле с переносом");
        Expect(fixture.SaveCalls == 0 && fixture.Message.Text!.Contains('\n'),
            "восстановление черновика не создает ложной записи TextChanged");
        Expect(fixture.Picker.SelectedIds.Count == 0 && !fixture.Send.IsEnabled,
            "при открытии получатели не выбраны и отправка выключена");
        using (var panel = new PickerPanel(fixture.Picker))
        {
            RowCheck(panel.Control, "1001", true);
            Expect(fixture.Picker.SelectedIds.Count == 0, "галочки до Готово остаются черновым выбором");
            Click(Find<Button>(panel.Control, "RecipientDone"));
        }
        Expect(fixture.Picker.SelectedIds.SequenceEqual(["1001"]) && fixture.Send.IsEnabled,
            "Готово применяет выбор и включает отправку");
        using (var panel = new PickerPanel(fixture.Picker))
        {
            Find<TextBox>(panel.Control, "RecipientSearch").Text = "пЕТР";
            Pump();
            Expect(Rows(panel.Control).Select(RowId).SequenceEqual(["1002"]), "поиск имени не зависит от регистра");
            var all = Find<CheckBox>(panel.Control, "RecipientSelectAll");
            Expect(Equals(all.Content, "(Выделить все найденное)"), "шапка объясняет выделение найденного");
            all.SetCurrentValue(ToggleButton.IsCheckedProperty, true);
            Find<TextBox>(panel.Control, "RecipientSearch").Text = "";
            Pump();
            Expect(RowChecked(panel.Control, "1001") && RowChecked(panel.Control, "1002")
                && !RowChecked(panel.Control, "1003"), "поиск сохраняет старый выбор и меняет только найденных");
            Capture(panel.Window, "doc-registry-recipients");
            Click(Find<Button>(panel.Control, "RecipientDone"));
        }
        Expect(fixture.Picker.SelectedIds.SequenceEqual(["1001", "1002"]), "применены оба выбранных участника");
        using (var panel = new PickerPanel(fixture.Picker))
            Find<CheckBox>(panel.Control, "RecipientSelectAll").SetCurrentValue(ToggleButton.IsCheckedProperty, true);
        Expect(fixture.Picker.SelectedIds.SequenceEqual(["1001", "1002"]), "закрытие без Готово отменяет правки");

        fixture.Message.Text = "  \n ";
        Pump();
        Expect(!fixture.Send.IsEnabled, "сообщение из пробелов не отправляется");
        fixture.Message.Text = "Обновленный\nчерновик";
        Pump();
        Expect(fixture.Values[ParticipantBroadcastEditor.DraftKey] == "Обновленный\nчерновик"
            && fixture.SaveCalls == 2 && fixture.Send.IsEnabled, "ввод сохраняет черновик без потери переносов");
    }

    private static void UpdatedParticipants()
    {
        using var fixture = new EditorFixture();
        Select(fixture.Picker, "1001", "1002");
        fixture.Values[DocRegistryExtension.SendersKey] = "1001 Иванов после изменения; 1004 Новый";
        fixture.Picker.RefreshParticipants();
        Expect(fixture.Picker.SelectedIds.SequenceEqual(["1001"]), "удаленный участник исключен, новый не выбран");
        using (var panel = new PickerPanel(fixture.Picker))
        {
            Expect(Rows(panel.Control).Select(RowLabel).SequenceEqual(["1001 Иванов после изменения", "1004 Новый"]),
                "имена участников обновляются из текущего списка");
            RowCheck(panel.Control, "1004", true);
            fixture.Values[DocRegistryExtension.SendersKey] = "1001 Иванов после изменения";
            Click(Find<Button>(panel.Control, "RecipientDone"));
        }
        Expect(fixture.Picker.SelectedIds.SequenceEqual(["1001"]), "Готово повторно исключает адресата, удаленного при открытом списке");
    }

    private static void LargeList()
    {
        var source = string.Join(';', Enumerable.Range(1, 5000).Select(i => $"U{i:D4} Пользователь {i:D4}"));
        var picker = new ParticipantPicker(() => source);
        using var owner = new ShownWindow(picker);
        using var panel = new PickerPanel(picker);
        var list = Find<ListBox>(panel.Control, "RecipientValues");
        Expect(list.ItemCount == 5000 && list.GetRealizedContainers().Count() is > 0 and < 100,
            "пять тысяч адресатов не создают пять тысяч визуальных строк");
        Find<TextBox>(panel.Control, "RecipientSearch").Text = "U4999";
        Pump();
        Expect(Rows(panel.Control).Select(RowId).SequenceEqual(["U4999"]), "поиск находит адресата в конце большого списка по идентификатору");
        Find<CheckBox>(panel.Control, "RecipientSelectAll").SetCurrentValue(ToggleButton.IsCheckedProperty, true);
        Click(Find<Button>(panel.Control, "RecipientDone"));
        Expect(picker.SelectedIds.SequenceEqual(["U4999"]), "из большого списка отправитель выбрал только найденного");
    }

    private static void CancelConfirmation()
    {
        using var fixture = new EditorFixture();
        Select(fixture.Picker, "1001", "1003");
        var pending = Start(fixture);
        var confirm = Dialog(fixture.Owner.Window);
        Expect(!fixture.Message.IsEnabled && !fixture.Picker.IsEnabled && !fixture.Send.IsEnabled,
            "во время подтверждения поля и повторная отправка выключены");
        var shown = Find<TextBlock>(confirm, "MessageLabel").Text ?? "";
        Expect(shown.Contains("1001 Иванов", StringComparison.Ordinal)
            && shown.Contains("1003 Сидоров", StringComparison.Ordinal)
            && !shown.Contains("1002 Петров", StringComparison.Ordinal), "подтверждение перечисляет только выбранных");
        Click(fixture.Send);
        Expect(ReferenceEquals(pending, Pending(fixture.Editor)), "повторное событие не запускает вторую операцию");
        Click(Find<Button>(confirm, "CancelBtn"));
        Wait(() => pending.IsCompleted, "не завершилась отмена подтверждения");
        pending.GetAwaiter().GetResult();
        Expect(fixture.Replies.Sent.Count == 0 && fixture.Send.IsEnabled && fixture.Message.IsEnabled,
            "Отмена не отправляет ни одного сообщения и возвращает поля");
    }

    private static void SuccessfulSend()
    {
        const string message = "Проверка <данные> & текст\nВторая строка";
        using var fixture = new EditorFixture(draft: message);
        Select(fixture.Picker, "1001", "1003");
        var pending = Start(fixture);
        Click(Find<Button>(Dialog(fixture.Owner.Window), "ConfirmBtn"));
        var report = Dialog(fixture.Owner.Window);
        Expect(Find<TextBlock>(report, "MessageLabel").Text!.Contains("Отправлено: 2 из 2.", StringComparison.Ordinal),
            "успешный отчет содержит число отправленных");
        Expect(fixture.Replies.Sent.Select(s => s.Recipient).SequenceEqual(["1001", "1003"])
            && fixture.Replies.Sent.All(s => s.Text == "Проверка &lt;данные&gt; &amp; текст\nВторая строка"),
            "отправка использует выбранных адресатов и ровно одно экранирование");
        CloseReport(fixture, pending, report);
        Expect(fixture.Message.Text == message && fixture.Values[ParticipantBroadcastEditor.DraftKey] == message,
            "после успеха черновик остается");
    }

    private static void PartialSend()
    {
        using var fixture = new EditorFixture();
        Select(fixture.Picker, "1001", "1002", "1003");
        fixture.Replies.Handler = (recipient, _) => Task.FromResult(recipient == "1002"
            ? new ReplyResult(false, "Тестовая ошибка доставки") : new ReplyResult(true, ""));
        var pending = Start(fixture);
        Click(Find<Button>(Dialog(fixture.Owner.Window), "ConfirmBtn"));
        var report = Dialog(fixture.Owner.Window);
        var text = Find<TextBlock>(report, "MessageLabel").Text ?? "";
        Expect(text.Contains("Отправлено: 2 из 3.", StringComparison.Ordinal)
            && text.Contains("1002 Петров", StringComparison.Ordinal)
            && text.Contains("Тестовая ошибка доставки", StringComparison.Ordinal), "отчет называет неуспешного участника и причину");
        Expect(fixture.Replies.Sent.Count == 3, "ошибка среднего адресата не блокирует последнего");
        CloseReport(fixture, pending, report);
    }

    private static void CancelSending()
    {
        using var fixture = new EditorFixture();
        Select(fixture.Picker, "1001", "1002", "1003");
        fixture.Replies.Handler = async (recipient, ct) =>
        {
            if (recipient == "1002") await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            return new ReplyResult(true, "");
        };
        var pending = Start(fixture);
        Click(Find<Button>(Dialog(fixture.Owner.Window), "ConfirmBtn"));
        Wait(() => fixture.Replies.Sent.Count == 2, "не началась вторая отправка");
        var progress = fixture.Owner.Window.OwnedWindows.OfType<ProgressDialog>().Single();
        Expect(!fixture.Send.IsEnabled, "повторная отправка выключена во время работы");
        Click(Find<Button>(progress, "CancelBtn"));
        var report = Dialog(fixture.Owner.Window);
        var text = Find<TextBlock>(report, "MessageLabel").Text ?? "";
        Expect(text.Contains("Отправлено: 1 из 3.", StringComparison.Ordinal)
            && text.Contains("Доставка не подтверждена: 1002", StringComparison.Ordinal)
            && text.Contains("Отправка остановлена", StringComparison.Ordinal), "остановка не выдает прерванный запрос за успешную доставку");
        Expect(fixture.Replies.Sent.Count == 2 && !fixture.Replies.Sent.Any(s => s.Recipient == "1003"),
            "после остановки последний адресат не получает запрос");
        CloseReport(fixture, pending, report);
    }

    private static void MissingRoute()
    {
        using var fixture = new EditorFixture();
        Select(fixture.Picker, "1001");
        fixture.Replies.RouteName = null;
        var pending = Start(fixture);
        var report = Dialog(fixture.Owner.Window);
        Expect(Find<TextBlock>(report, "MessageLabel").Text!.Contains("Сценарий не настроен", StringComparison.Ordinal)
            && fixture.Replies.Sent.Count == 0, "без пути интеграции показана причина и ничего не отправлено");
        CloseReport(fixture, pending, report);
    }

    private static void RemovedBeforeSend()
    {
        using var fixture = new EditorFixture();
        Select(fixture.Picker, "1001");
        fixture.Values[DocRegistryExtension.SendersKey] = "1002 Петров Петр";
        var pending = Start(fixture);
        Wait(() => pending.IsCompleted, "удаленный адресат оставил незавершенную операцию");
        pending.GetAwaiter().GetResult();
        Expect(fixture.Replies.Sent.Count == 0 && fixture.Owner.Window.OwnedWindows.Count == 0
            && fixture.Picker.SelectedIds.Count == 0 && !fixture.Send.IsEnabled,
            "перед отправкой перечитывается текущий список и удаленный выбор блокирует кнопку");
    }

    private static void LongConfirmation()
    {
        using var fixture = new EditorFixture(string.Join('\n', Enumerable.Range(1, 500).Select(i => $"Строка сообщения {i}")), height: 420);
        fixture.Values[DocRegistryExtension.SendersKey] = string.Join(';', Enumerable.Range(1, 1000).Select(i => $"U{i:D4} Участник {i}"));
        fixture.Picker.RefreshParticipants();
        using (var panel = new PickerPanel(fixture.Picker))
        {
            Find<CheckBox>(panel.Control, "RecipientSelectAll").SetCurrentValue(ToggleButton.IsCheckedProperty, true);
            Click(Find<Button>(panel.Control, "RecipientDone"));
        }
        var pending = Start(fixture);
        var confirm = Dialog(fixture.Owner.Window);
        BoundedDialog(confirm, fixture.Owner.Window);
        Capture(confirm, "doc-registry-long-confirmation");
        Expect(fixture.Replies.Sent.Count == 0, "даже длинный список до подтверждения ничего не отправляет");
        Click(Find<Button>(confirm, "CancelBtn"));
        Wait(() => pending.IsCompleted, "не отменилось длинное подтверждение");
        pending.GetAwaiter().GetResult();
    }

    private static void LongReport()
    {
        using var fixture = new EditorFixture(height: 720);
        Select(fixture.Picker, "1001", "1002");
        var problem = string.Join('\n', Enumerable.Range(1, 500).Select(i => $"Строка тестовой ошибки {i}"));
        fixture.Replies.Handler = (_, _) => Task.FromResult(new ReplyResult(false, problem));
        var pending = Start(fixture);
        Click(Find<Button>(Dialog(fixture.Owner.Window), "ConfirmBtn"));
        var report = Dialog(fixture.Owner.Window);
        BoundedDialog(report, fixture.Owner.Window);
        Capture(report, "doc-registry-long-report");
        CloseReport(fixture, pending, report);
    }

    private static void BoundedDialog(ConfirmDialog dialog, Window owner)
    {
        Pump();
        dialog.UpdateLayout();
        var scroll = Find<ScrollViewer>(dialog, "BroadcastReviewScroll");
        var button = Find<Button>(dialog, "ConfirmBtn");
        var point = button.TranslatePoint(default, dialog);
        Expect(scroll.Viewport.Height > 0 && scroll.Extent.Height > scroll.Viewport.Height,
            "длинное содержимое имеет ограниченную область прокрутки");
        Expect(dialog.ClientSize.Height <= owner.ClientSize.Height + 1 && point is { } p
            && p.Y >= 0 && p.Y + button.Bounds.Height <= dialog.ClientSize.Height + 1,
            "кнопка остается в пределах видимого окна подтверждения или отчета");
    }

    private static Task Start(EditorFixture fixture)
    {
        Click(fixture.Send);
        return Pending(fixture.Editor) ?? throw new InvalidOperationException("Нажатие не создало задачи отправки");
    }

    private static Task? Pending(ParticipantBroadcastEditor editor) =>
        (Task?)typeof(ParticipantBroadcastEditor).GetProperty("PendingSend", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(editor);

    private static ConfirmDialog Dialog(Window owner)
    {
        Wait(() => owner.OwnedWindows.OfType<ConfirmDialog>().Any(), "не появилось окно подтверждения или отчета");
        return owner.OwnedWindows.OfType<ConfirmDialog>().Single();
    }

    private static void CloseReport(EditorFixture fixture, Task pending, ConfirmDialog report)
    {
        Click(Find<Button>(report, "ConfirmBtn"));
        Wait(() => pending.IsCompleted, "после закрытия отчета операция не закончилась");
        pending.GetAwaiter().GetResult();
        Expect(fixture.Message.IsEnabled && fixture.Picker.IsEnabled && fixture.Send.IsEnabled,
            "после отчета поля и кнопка снова доступны");
    }

    private static void Select(ParticipantPicker picker, params string[] ids)
    {
        using var panel = new PickerPanel(picker);
        foreach (var id in ids) RowCheck(panel.Control, id, true);
        Click(Find<Button>(panel.Control, "RecipientDone"));
        if (!picker.SelectedIds.Order().SequenceEqual(ids.Order()))
            throw new InvalidOperationException("Стенд не смог выбрать адресатов через чекбоксы");
    }

    private static object[] Rows(Control panel) => Find<ListBox>(panel, "RecipientValues").Items.Cast<object>().ToArray();
    private static string RowId(object row) => (string)row.GetType().GetProperty("Id")!.GetValue(row)!;
    private static string RowLabel(object row) => (string)row.GetType().GetProperty("Label")!.GetValue(row)!;
    private static bool RowChecked(Control panel, string id) =>
        (bool)Rows(panel).Single(row => RowId(row) == id).GetType().GetProperty("Checked")!
            .GetValue(Rows(panel).Single(row => RowId(row) == id))!;

    private static void RowCheck(Control panel, string id, bool value)
    {
        var list = Find<ListBox>(panel, "RecipientValues");
        var row = Rows(panel).Single(item => RowId(item) == id);
        var label = RowLabel(row);
        var check = list.GetVisualDescendants().OfType<CheckBox>()
            .Single(box => box.Content is TextBlock text && text.Text == label);
        check.SetCurrentValue(ToggleButton.IsCheckedProperty, value);
        Pump();
    }

    private static T Find<T>(Control root, string name) where T : Control =>
        root.GetVisualDescendants().OfType<T>().FirstOrDefault(c => c.Name == name)
        ?? root.GetLogicalDescendants().OfType<T>().FirstOrDefault(c => c.Name == name)
        ?? throw new InvalidOperationException($"Не найден {typeof(T).Name} {name}");

    private static void Click(Button button) { button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Pump(); }
    private static void Capture(Window window, string name)
    {
        if (_output is null) return;
        Pump();
        window.UpdateLayout();
        using var image = window.CaptureRenderedFrame()
            ?? throw new InvalidOperationException("Не удалось получить кадр окна стенда");
        var path = Path.Combine(_output, name + ".png");
        image.Save(path);
        Console.WriteLine("  Кадр: " + path);
    }
    private static void Pump() { AvaloniaHeadlessPlatform.ForceRenderTimerTick(); Dispatcher.UIThread.RunJobs(); }
    private static void Wait(Func<bool> check, string problem)
    {
        var elapsed = Stopwatch.StartNew();
        while (!check() && elapsed.Elapsed < TimeSpan.FromSeconds(8)) { Pump(); Thread.Sleep(2); }
        if (!check()) throw new TimeoutException(problem);
    }

    private static void Expect(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
        Console.WriteLine("  PASS: " + message);
        _passed++;
    }

    private static void Case(string name, Action action)
    {
        try { action(); }
        catch (Exception error) { _failed++; Console.WriteLine($"  FAIL: {name}\n{error}"); }
    }

    private sealed class ShownWindow : IDisposable
    {
        public Window Window { get; }
        public ShownWindow(Control content, double width = 760, double height = 520)
        {
            Window = new Window { Width = width, Height = height, Content = content };
            Window.Show();
            Pump();
            Window.UpdateLayout();
            Pump();
        }

        public void Dispose()
        {
            foreach (var child in Window.OwnedWindows.ToArray())
                if (child is ProgressDialog progress) progress.Done(); else child.Close();
            if (Window.IsVisible) Window.Close();
            Pump();
        }
    }

    private sealed class PickerPanel : IDisposable
    {
        private readonly ShownWindow _window;
        public Control Control { get; }
        public Window Window => _window.Window;
        public PickerPanel(ParticipantPicker picker)
        {
            picker.RefreshParticipants();
            ShownWindow? window = null;
            Control = (Control)typeof(ParticipantPicker).GetMethod("BuildPanel", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(picker, [picker, (Action)(() => window?.Window.Close())])!;
            _window = window = new ShownWindow(Control, 340, 420);
        }
        public void Dispose() => _window.Dispose();
    }

    private sealed class EditorFixture : IDisposable
    {
        public Dictionary<string, string> Values { get; } = new()
        {
            [DocRegistryExtension.SendersKey] = Participants,
        };
        public FakeReplies Replies { get; } = new();
        public ParticipantBroadcastEditor Editor { get; }
        public ShownWindow Owner { get; }
        public TextBox Message => Find<TextBox>(Editor, "BroadcastMessage");
        public ParticipantPicker Picker => Find<ParticipantPicker>(Editor, "BroadcastRecipients");
        public Button Send => Find<Button>(Editor, "BroadcastSend");
        public int SaveCalls { get; private set; }

        public EditorFixture(string draft = "Сообщение участникам", double height = 520)
        {
            Values[ParticipantBroadcastEditor.DraftKey] = draft;
            ShownWindow? owner = null;
            Editor = new ParticipantBroadcastEditor(new ExtensionSettingEditorContext(
                key => Values.GetValueOrDefault(key, ""),
                (key, value) => { Values[key] = value; SaveCalls++; },
                () => owner?.Window, () => Replies));
            Owner = owner = new ShownWindow(Editor, height: height);
        }
        public void Dispose() => Owner.Dispose();
    }

    private sealed record SentMessage(string Recipient, string Text);
    private sealed class FakeReplies : IActionReplies
    {
        public List<SentMessage> Sent { get; } = [];
        public string? RouteName { get; set; } = "Тестовая интеграция";
        public Func<string, CancellationToken, Task<ReplyResult>> Handler { get; set; } = (_, _) => Task.FromResult(new ReplyResult(true, ""));
        public string? Route(string extensionId, IExtensionAction action, string senderField, out string problem)
        {
            if (extensionId != DocRegistryExtension.ExtensionId || action.Key != RegisterMessageAction.ActionKey
                || senderField != RegisterMessageAction.SenderField) throw new InvalidOperationException("Неверный путь рассылки");
            problem = RouteName is null ? "Сценарий не настроен" : "";
            return RouteName;
        }

        public Task<ReplyResult> SendAsync(string extensionId, IExtensionAction action, string senderField,
            string recipient, string text, CancellationToken ct = default)
        {
            _ = Route(extensionId, action, senderField, out _);
            Sent.Add(new SentMessage(recipient, text));
            return Handler(recipient, ct);
        }
    }

    private sealed class MemoryStore : ISettingsDatabase
    {
        public Dictionary<string, string> Values { get; } = new();
        public string? Get(string key) => Values.GetValueOrDefault(key);
        public void Set(string key, string value) => Values[key] = value;
        public void Remove(string key) => Values.Remove(key);
        public IReadOnlyList<string> KeysWithPrefix(string prefix) => Values.Keys.Where(key => key.StartsWith(prefix, StringComparison.Ordinal)).ToArray();
    }
}
