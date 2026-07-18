using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using SmartBase.Core.Models;
using SmartBase.Database.Repositories;
using SmartBase.Utils;

namespace SmartBase.Views.Tasks;

public record QuickAddRequest(string Title, DateValue? Due, string Executor);

public partial class TaskListView : UserControl
{
    public event EventHandler<TaskRecord>?      TaskActivated;
    public event EventHandler<QuickAddRequest>? QuickAddRequested;

    private TimeSpan _quickTime = new(18, 0, 0);

    public TaskListView()
    {
        InitializeComponent();

        CardsHost.ItemTemplate = new FuncDataTemplate<TaskCardVm>((vm, _) => BuildCard(vm), supportsRecycling: false);

        QuickTimeList.ItemsSource = TimeText.Options;
        QuickTimeInput.Text = TimeText.Format(_quickTime);
        QuickTimeInput.TextChanged += (_, _) =>
        {
            if (TimeText.TryParse(QuickTimeInput.Text, out var t)) _quickTime = t;
        };
        QuickTimeArrow.Click += (_, _) =>
        {
            var wasOpen = QuickTimePopup.IsOpen;
            QuickTimePopup.IsOpen = false;
            if (wasOpen) return;
            var idx = TimeText.Options.ToList().IndexOf(TimeText.Format(_quickTime));
            QuickTimeList.SelectedIndex = idx >= 0 ? idx : 0;
            QuickTimePopup.IsOpen = true;
            if (QuickTimeList.SelectedItem is { } sel) QuickTimeList.ScrollIntoView(sel);
        };
        QuickTimeList.SelectionChanged += (_, _) =>
        {
            if (!QuickTimePopup.IsOpen) return;
            if (QuickTimeList.SelectedItem is string s && TimeText.TryParse(s, out var t))
            {
                _quickTime = t;
                QuickTimeInput.Text = s;
            }
            QuickTimePopup.IsOpen = false;
        };

        QuickTitle.KeyDown += (_, e) =>
        {
            if (e.Key != Key.Enter) return;
            e.Handled = true;
            FireQuickAdd();
        };
        QuickAddBtn.Click += (_, _) => FireQuickAdd();
    }

    // ── Публичный API (для контроллера) ──────────────────────────────────────

    public void SetTasks(IReadOnlyList<TaskRecord> tasks)
    {
        var now = DateTime.Now;
        CardsHost.ItemsSource = tasks.Select(t => new TaskCardVm(t, now)).ToList();
        EmptyLabel.IsVisible  = tasks.Count == 0;
    }

    // Опции исполнителя строки быстрого добавления (пользователи целевой БД).
    public void SetUserOptions(IReadOnlyList<string> options, string selected)
    {
        var opts = options.Count > 0 ? options.ToList() : [CurrentUser.DisplayName];
        if (selected.Length > 0 && !opts.Contains(selected)) opts.Add(selected);
        var keep = QuickExecutor.SelectedItem as string;
        QuickExecutor.ItemsSource = opts;
        var idx = keep is not null ? opts.IndexOf(keep) : -1;
        if (idx < 0) idx = opts.IndexOf(selected);
        QuickExecutor.SelectedIndex = idx >= 0 ? idx : 0;
    }

    public void ShowError(string message) => ErrorBanner.Show(message);
    public void HideError()               { if (ErrorBanner.IsVisible) ErrorBanner.Hide(); }

    // ── Быстрое добавление ───────────────────────────────────────────────────

    private void FireQuickAdd()
    {
        HideError();
        var title = QuickTitle.Text?.Trim() ?? "";
        if (title.Length == 0) return;

        // Дата не выбрана — задача «без даты». Время пустое/нечитаемое — весь день.
        DateValue? due = null;
        if (QuickDatePicker.SelectedDate is DateTime day)
        {
            due = TimeText.TryParse(QuickTimeInput.Text, out var t)
                ? new DateValue(day.Date + t, day.Date + t + TimeSpan.FromHours(1), false)
                : new DateValue(day.Date, day.Date + new TimeSpan(23, 59, 0), true);
        }

        var executor = QuickExecutor.SelectedItem as string ?? CurrentUser.DisplayName;
        QuickAddRequested?.Invoke(this, new QuickAddRequest(title, due, executor));
        QuickTitle.Text = "";
    }

    // ── Карточка задачи ──────────────────────────────────────────────────────

    private Control BuildCard(TaskCardVm vm)
    {
        var bold      = (FontWeight)this.FindResource("FontWeightBold")!;
        var secondary = (IBrush)this.FindResource("BrushSecondaryText")!;
        var primary   = (IBrush)this.FindResource("BrushPrimaryText")!;
        var radius    = (CornerRadius)this.FindResource("RadiusMedium")!;

        // Левая колонка: название + «Исполнитель X · Назначил Y».
        var title = new TextBlock
        {
            Text         = vm.Task.DisplayTitle,
            FontWeight   = bold,
            Foreground   = vm.IsDone ? secondary : primary,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        if (vm.IsDone) title.TextDecorations = TextDecorations.Strikethrough;

        var people = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        people.Children.Add(new TextBlock { Text = "Исполнитель", Foreground = secondary });
        people.Children.Add(new TextBlock { Text = vm.Executor, Foreground = secondary, FontWeight = bold });
        if (vm.Task.Assigner.Length > 0)
        {
            people.Children.Add(new TextBlock { Text = "·", Foreground = secondary, Margin = new Thickness(4, 0) });
            people.Children.Add(new TextBlock { Text = "Назначил", Foreground = secondary });
            people.Children.Add(new TextBlock { Text = vm.Task.Assigner, Foreground = secondary, FontWeight = bold });
        }

        var left = new StackPanel { Spacing = 4, VerticalAlignment = VerticalAlignment.Center };
        left.Children.Add(title);
        left.Children.Add(people);

        // Правая колонка: срок сверху, чипы (статус + остаток) снизу.
        var due = new TextBlock
        {
            Text                = vm.DueText,
            Foreground          = vm.Task.DueStart is null ? secondary : primary,
            HorizontalAlignment = HorizontalAlignment.Right,
        };

        var chips = new StackPanel
        {
            Orientation         = Orientation.Horizontal,
            Spacing             = 8,
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        chips.Children.Add(Chip(vm.StatusText, vm.StatusBg, vm.StatusFg));
        if (vm.DueChipText is not null)
            chips.Children.Add(Chip(vm.DueChipText, vm.DueChipBg!, vm.DueChipFg!));

        var right = new StackPanel { Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
        right.Children.Add(due);
        right.Children.Add(chips);

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,12,*,12,Auto") };
        // Цветовая полоска коллекции — как в списке календаря.
        var stripe = new Border
        {
            Width = 4, CornerRadius = new CornerRadius(2),
            Background = new SolidColorBrush(Color.Parse(
                string.IsNullOrWhiteSpace(vm.Task.Color) ? "#8E8E93" :
                vm.Task.Color == "#007AFF" ? "#8E8E93" : vm.Task.Color)),
            VerticalAlignment = VerticalAlignment.Stretch,
        };
        Grid.SetColumn(stripe, 0);
        Grid.SetColumn(left, 2);
        Grid.SetColumn(right, 4);
        grid.Children.Add(stripe);
        grid.Children.Add(left);
        grid.Children.Add(right);

        var card = new Border
        {
            Background   = (IBrush)this.FindResource("BrushBg")!,
            BorderBrush  = (IBrush)this.FindResource("BrushSeparator")!,
            CornerRadius = radius,
            Padding      = new Thickness(14, 10),
            Margin       = new Thickness(0, 0, 0, 8),
            Child        = grid,
        };
        card.Bind(Border.BorderThicknessProperty, card.GetResourceObservable("BorderLine"));

        var hover = (IBrush)this.FindResource("BrushInactiveBg")!;
        card.PointerEntered += (_, _) => card.Background = hover;
        card.PointerExited  += (_, _) => card.Background = (IBrush)this.FindResource("BrushBg")!;
        card.DoubleTapped   += (_, e) =>
        {
            e.Handled = true;
            TaskActivated?.Invoke(this, vm.Task);
        };
        return card;
    }

    private static Border Chip(string text, IBrush bg, IBrush fg) => new()
    {
        Background   = bg,
        CornerRadius = new CornerRadius(99),
        Padding      = new Thickness(10, 3),
        Child        = new TextBlock { Text = text, Foreground = fg, FontWeight = FontWeight.SemiBold },
    };
}

// ── Модель карточки: тексты и кисти чипов считаются один раз при загрузке ────
public class TaskCardVm
{
    private static IBrush Brush(string key, string fallback) =>
        Application.Current!.TryFindResource(key, out var res) && res is IBrush b
            ? b : new SolidColorBrush(Color.Parse(fallback));

    private static readonly string[] Months =
        ["янв", "фев", "мар", "апр", "мая", "июн", "июл", "авг", "сен", "окт", "ноя", "дек"];

    public TaskRecord Task { get; }
    public bool   IsDone      { get; }
    public string Executor    => Task.Executor;
    public string StatusText  { get; }
    public IBrush StatusBg    { get; }
    public IBrush StatusFg    { get; }
    public string DueText     { get; }
    public string? DueChipText { get; }
    public IBrush? DueChipBg   { get; }
    public IBrush? DueChipFg   { get; }

    public TaskCardVm(TaskRecord task, DateTime now)
    {
        Task   = task;
        IsDone = task.Status == ConstructorRepository.TaskStatusDone;

        StatusText = task.Status.Length > 0 ? task.Status : ConstructorRepository.TaskStatusPlanned;
        (StatusBg, StatusFg) = StatusText switch
        {
            ConstructorRepository.TaskStatusActive => (Brush("BrushAccentLight", "#E5F0FF"), Brush("BrushAccent", "#007AFF")),
            ConstructorRepository.TaskStatusDone   => (Brush("BrushGreenLight", "#E3F7E9"), Brush("BrushGreen", "#34C759")),
            _ => (Brush("BrushSecondaryBg", "#F2F2F7"), Brush("BrushSecondaryText", "#8E8E93")),
        };

        DueText = FormatDue(task);

        var chip = DueChip(task, now);
        if (chip is { } c)
        {
            DueChipText = c.Text;
            DueChipBg   = c.Overdue ? Brush("BrushRedLight", "#FFE9E7") : Brush("BrushSecondaryBg", "#F2F2F7");
            DueChipFg   = c.Overdue ? Brush("BrushRed", "#FF3B30")      : Brush("BrushSecondaryText", "#8E8E93");
        }
    }

    private static string FormatDay(DateTime d) => $"{d.Day} {Months[d.Month - 1]}";

    // «5 июл, 15:30» / «5 июл» (весь день) / «5 июл – 7 июл» (диапазон) / «без даты».
    private static string FormatDue(TaskRecord t)
    {
        if (t.DueStart is not DateTime s) return "без даты";
        var e = t.DueEnd ?? s;
        if (s.Date != e.Date) return $"{FormatDay(s)} – {FormatDay(e)}";
        return t.AllDay ? FormatDay(s) : $"{FormatDay(s)}, {s.ToString("HH:mm", CultureInfo.InvariantCulture)}";
    }

    // Чип остатка: просрочка (красный) / «Сегодня» / «Осталось N ч|дн.». У выполненных —
    // только нейтральное «Сегодня», без просрочки и обратного отсчёта.
    private static (string Text, bool Overdue)? DueChip(TaskRecord t, DateTime now)
    {
        if (t.DueStart is not DateTime s) return null;
        var end = t.DueEnd ?? s;

        if (t.Status == ConstructorRepository.TaskStatusDone)
            return end.Date == now.Date ? ("Сегодня", false) : null;

        if (end <= now)
        {
            var days = (int)(now.Date - end.Date).TotalDays;
            if (days >= 1) return ($"Просрочено на {days} дн.", true);
            var hours = Math.Max(1, (int)Math.Ceiling((now - end).TotalHours));
            return ($"Просрочено на {hours} ч", true);
        }

        if (t.AllDay && end.Date == now.Date) return ("Сегодня", false);

        var left = end - now;
        if (left.TotalHours < 24)
            return ($"Осталось {Math.Max(1, (int)Math.Ceiling(left.TotalHours))} ч", false);
        return ($"Осталось {(int)Math.Ceiling((end.Date - now.Date).TotalDays)} дн.", false);
    }
}
