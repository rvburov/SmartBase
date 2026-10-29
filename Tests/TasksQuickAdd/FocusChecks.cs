using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Input.Raw;
using Avalonia.Threading;
using SmartBase.Utils;
using SmartBase.Views.Tasks;

namespace SmartBase.Tests.TasksQuickAdd;

/// <summary>Настоящие клики мышью: ввод заканчивается вне названия, остальные контролы работают.</summary>
internal static class FocusChecks
{
    public static int Run(string output)
    {
        Window? window = null;
        var checks = 0;
        var failures = 0;
        try
        {
            var view = new TaskListView();
            var target = new QuickCollectionOption(42, 61, "Задачи · Основная");
            view.SetCollectionOptions([target], target);
            view.SetUserOptions([CurrentUser.DisplayName, "Другой пользователь"], "");
            var heading = new TextBlock { Text = "Задачи", Margin = new Thickness(8), Width = 160 };
            var outside = new TextBox { Width = 220, Watermark = "Поиск" };
            var header = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 12 };
            header.Children.Add(heading);
            header.Children.Add(outside);
            var root = new Grid { RowDefinitions = new RowDefinitions("Auto,*") };
            root.Children.Add(header);
            Grid.SetRow(view, 1);
            root.Children.Add(view);
            window = new Window { Width = 1200, Height = 700, Content = root };
            window.Show();
            Pump();
            var title = view.FindControl<TextBox>("QuickTitle")!;
            var executor = view.FindControl<ComboBox>("QuickExecutor")!;
            var assigner = view.FindControl<ComboBox>("QuickAssigner")!;
            var add = view.FindControl<Button>("QuickAddBtn")!;
            title.Text = "Черновик задачи";
            var lost = 0;
            title.LostFocus += (_, _) => lost++;

            Click(title);
            Expect(title.IsFocused, "клик мышью начинает ввод названия");
            var before = lost;
            Click(title);
            Expect(title.IsFocused && lost == before, "повторный клик внутри поля не прерывает ввод");
            Click(heading);
            Expect(!title.IsFocused && lost == before + 1,
                "клик по заголовку вне TaskList снимает фокус ровно один раз");
            Expect(title.Text == "Черновик задачи", "выход из ввода сохраняет черновик");

            Click(title);
            ClickPoint(view.TranslatePoint(new Point(80, view.Bounds.Height - 40), window)!.Value);
            Expect(!title.IsFocused, "клик по пустому списку задач снимает фокус");
            Click(title);
            Click(heading, RawInputModifiers.Shift);
            Expect(!title.IsFocused, "Shift-клик вне поля тоже завершает ввод");

            Click(title);
            Click(outside);
            Expect(!title.IsFocused && outside.IsFocused,
                "другое поле приложения получает фокус обычным путем");
            Click(title);
            Click(executor);
            Expect(!title.IsFocused && executor.IsDropDownOpen,
                "клик по исполнителю завершает ввод и открывает список");
            executor.IsDropDownOpen = false;
            Pump();
            Click(title);
            Click(assigner);
            Expect(!title.IsFocused && assigner.IsDropDownOpen,
                "клик по назначившему открывает его список");
            assigner.IsDropDownOpen = false;
            Pump();

            var requests = new List<QuickAddRequest>();
            view.QuickAddRequested += (_, request) => requests.Add(request);
            title.Text = "Задача по кнопке";
            Click(title);
            Click(add);
            Expect(!title.IsFocused && requests.Count == 1 && requests[0].Title == "Задача по кнопке",
                "клик по Добавить снимает фокус и создает ровно одну задачу");
            title.Text = "Задача по Enter";
            Click(title);
            window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
            window.KeyReleaseQwerty(PhysicalKey.Enter, RawInputModifiers.None);
            Pump();
            Expect(requests.Count == 2 && requests[1].Title == "Задача по Enter",
                "Enter продолжает добавлять задачу из поля названия");

            // После возврата того же контрола клик снаружи по-прежнему завершает ввод.
            root.Children.Remove(view);
            Pump();
            root.Children.Add(view);
            Pump();
            title.Text = "После повторного открытия";
            Click(title);
            before = lost;
            Click(heading);
            Expect(!title.IsFocused && lost == before + 1,
                "после отсоединения и возврата поля обработчик клика работает один раз");
            using var image = window.CaptureRenderedFrame();
            image?.Save(Path.Combine(output, "task-entry-unfocused.png"));
            Click(title);
            using var focused = window.CaptureRenderedFrame();
            focused?.Save(Path.Combine(output, "task-entry-focused.png"));

            void Click(Control control, RawInputModifiers modifiers = RawInputModifiers.None)
            {
                var point = control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), window)
                    ?? throw new InvalidOperationException("Контрол вне окна");
                ClickPoint(point, modifiers);
            }

            void ClickPoint(Point point, RawInputModifiers modifiers = RawInputModifiers.None)
            {
                if (!new Rect(window.ClientSize).Contains(point))
                    throw new InvalidOperationException("Точка клика вне окна: " + point);
                window.MouseDown(point, MouseButton.Left, modifiers);
                window.MouseUp(point, MouseButton.Left, modifiers);
                Pump();
            }
        }
        catch (Exception ex)
        {
            failures++;
            Console.WriteLine("FAIL: " + ex);
        }
        finally
        {
            window?.Close();
            Pump();
        }
        Console.WriteLine($"Фокус быстрого добавления: проверок {checks}, провалов {failures}.");
        return failures == 0 ? 0 : 1;

        void Expect(bool condition, string description)
        {
            checks++;
            if (!condition) failures++;
            Console.WriteLine((condition ? "PASS: " : "FAIL: ") + description);
        }
    }

    private static void Pump()
    {
        for (var i = 0; i < 3; i++)
        {
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();
        }
    }
}
