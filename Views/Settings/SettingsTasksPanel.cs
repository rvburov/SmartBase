using Avalonia.Controls;
using SmartBase.Database.Repositories;
using SmartBase.Utils;

namespace SmartBase.Views.Settings;

/// <summary>
/// Раздел «Настройки → Задачи» (partial SettingsView, без ViewModel — как «Календарь»).
/// Строки стартового вида и видимости видов обслуживает общий InitAppearance (контролы
/// просто живут в этой панели); остальные строки наполняются здесь и пишут в живой сервис
/// <see cref="TaskSettings"/> — раздел «Задачи» перечитывается сразу, по событию Changed.
/// Тумблеры уведомлений по задачам остаются в «Общее».
/// </summary>
public partial class SettingsView
{
    // Пункты закрытых списков; индексы совпадают с ComboBox.SelectedIndex.
    private static readonly string[] TasksStartFilterLabels =
        ["Все задачи", "Сегодня", "Мне назначено", "Без срока", "Просрочено", "Последний выбранный"];

    private static readonly bool[]   TasksOverdueOptions = [false, true];
    private static readonly string[] TasksOverdueLabels  = ["С момента срока", "Со следующего дня"];

    private static readonly bool[]   TasksQuickExecutorOptions = [true, false];
    private static readonly string[] TasksQuickExecutorLabels  = ["Я", "Не назначен"];

    private static readonly TaskQuickDue[] TasksQuickDueOptions =
        [TaskQuickDue.None, TaskQuickDue.Today, TaskQuickDue.Tomorrow];
    private static readonly string[] TasksQuickDueLabels = ["Без срока", "Сегодня", "Завтра"];

    // Стадии всех допущенных баз для списка «Стадия новой задачи» — отдает главное окно
    // (у экрана настроек нет доступа к рабочему пространству). null — стандартные три.
    private Func<IReadOnlyList<string>>? _taskStages;

    // Пункты списка стадий в текущей сборке панели; индексы совпадают с ComboBox.
    private List<string> _tasksStageItems = [];

    // Гасит обработчики, пока контролы наполняются кодом (как _calendarBuilding у «Календаря»).
    private bool _tasksBuilding;

    // ── Построение раздела ────────────────────────────────────────────────────

    private void RebuildTasksPanel()
    {
        _tasksBuilding = true;

        TasksStartFilterCombo.ItemsSource   = TasksStartFilterLabels;
        TasksStartFilterCombo.SelectedIndex = Math.Max(0, Array.IndexOf(TaskSettings.StartFilterKeys, TaskSettings.StartFilterKey));

        TasksShowDoneToggle.IsChecked = TaskSettings.ShowDone;

        TasksOverdueCombo.ItemsSource   = TasksOverdueLabels;
        TasksOverdueCombo.SelectedIndex = Math.Max(0, Array.IndexOf(TasksOverdueOptions, TaskSettings.OverdueNextDay));

        // Стадии: три стандартные всегда первыми, дальше — заведенные в базах, и сохраненная
        // стадия, даже если такой уже нигде нет (иначе список молча показал бы другую).
        var stages = new List<string>(ConstructorRepository.TaskStatusOptions);
        foreach (var s in SafeTaskStages())
            if (!stages.Contains(s)) stages.Add(s);
        if (!stages.Contains(TaskSettings.DefaultStage)) stages.Add(TaskSettings.DefaultStage);
        _tasksStageItems = stages;
        TasksDefaultStageCombo.ItemsSource   = stages;
        TasksDefaultStageCombo.SelectedIndex = Math.Max(0, stages.IndexOf(TaskSettings.DefaultStage));

        TasksQuickExecutorCombo.ItemsSource   = TasksQuickExecutorLabels;
        TasksQuickExecutorCombo.SelectedIndex = Math.Max(0, Array.IndexOf(TasksQuickExecutorOptions, TaskSettings.QuickExecutorMe));

        TasksQuickDueCombo.ItemsSource   = TasksQuickDueLabels;
        TasksQuickDueCombo.SelectedIndex = Math.Max(0, Array.IndexOf(TasksQuickDueOptions, TaskSettings.QuickDue));

        _tasksBuilding = false;
    }

    // База могла отвалиться на ходу — экран настроек от этого не падает, стадий просто нет.
    private IReadOnlyList<string> SafeTaskStages()
    {
        try { return _taskStages?.Invoke() ?? []; }
        catch { return []; }
    }

    // ── Обработчики ───────────────────────────────────────────────────────────

    private void OnTasksStartFilterChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_tasksBuilding) return;
        var i = TasksStartFilterCombo.SelectedIndex;
        if (i >= 0 && i < TaskSettings.StartFilterKeys.Length)
            TaskSettings.SetStartFilter(TaskSettings.StartFilterKeys[i]);
    }

    private void OnTasksShowDoneToggled(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_tasksBuilding) return;
        TaskSettings.SetShowDone(TasksShowDoneToggle.IsChecked == true);
    }

    private void OnTasksOverdueChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_tasksBuilding) return;
        var i = TasksOverdueCombo.SelectedIndex;
        if (i >= 0 && i < TasksOverdueOptions.Length)
            TaskSettings.SetOverdueNextDay(TasksOverdueOptions[i]);
    }

    private void OnTasksDefaultStageChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_tasksBuilding) return;
        var i = TasksDefaultStageCombo.SelectedIndex;
        if (i >= 0 && i < _tasksStageItems.Count)
            TaskSettings.SetDefaultStage(_tasksStageItems[i]);
    }

    private void OnTasksQuickExecutorChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_tasksBuilding) return;
        var i = TasksQuickExecutorCombo.SelectedIndex;
        if (i >= 0 && i < TasksQuickExecutorOptions.Length)
            TaskSettings.SetQuickExecutorMe(TasksQuickExecutorOptions[i]);
    }

    private void OnTasksQuickDueChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_tasksBuilding) return;
        var i = TasksQuickDueCombo.SelectedIndex;
        if (i >= 0 && i < TasksQuickDueOptions.Length)
            TaskSettings.SetQuickDue(TasksQuickDueOptions[i]);
    }
}
