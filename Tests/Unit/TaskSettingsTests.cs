using SmartBase.Database.Repositories;
using SmartBase.Utils;

namespace SmartBase.Tests.Unit;

/// <summary>
/// Настройки раздела «Задачи» в AppSettings и живой сервис TaskSettings: инвариант «в базе
/// хранятся только отличия от стандарта» (дефолт = отсутствие ключа), разбор мусора, событие
/// Changed только на настоящую смену, последний фильтр — состояние без события.
/// </summary>
public sealed class TaskSettingsTests
{
    private sealed class FakeStore : ISettingsDatabase
    {
        private readonly Dictionary<string, string> _d = new();
        public bool Has(string key) => _d.ContainsKey(key);
        public string? Get(string key) => _d.TryGetValue(key, out var v) ? v : null;
        public void Set(string key, string value) => _d[key] = value;
        public void Remove(string key) => _d.Remove(key);
        public IReadOnlyList<string> KeysWithPrefix(string prefix) =>
            [.. _d.Keys.Where(k => k.StartsWith(prefix, StringComparison.Ordinal))];
    }

    private static FakeStore Attach()
    {
        var store = new FakeStore();
        AppSettings.AttachStore(store);
        TaskSettings.Init();
        return store;
    }

    private static int CountChanges(Action act)
    {
        var n = 0;
        void On() => n++;
        TaskSettings.Changed += On;
        try { act(); }
        finally { TaskSettings.Changed -= On; }
        return n;
    }

    // ── Дефолты ───────────────────────────────────────────────────────────────

    [Fact]
    public void Nothing_saved_reads_as_defaults()
    {
        Attach();

        Assert.False(TaskSettings.OverdueNextDay);
        Assert.True(TaskSettings.ShowDone);
        Assert.Equal("All", TaskSettings.StartFilterKey);
        Assert.Equal("All", TaskSettings.InitialFilterKey);
        Assert.Equal("", TaskSettings.LastFilterKey);
        Assert.True(TaskSettings.QuickExecutorMe);
        Assert.Equal(TaskQuickDue.None, TaskSettings.QuickDue);
        Assert.Equal(ConstructorRepository.TaskStatusPlanned, TaskSettings.DefaultStage);
        Assert.Null(TaskSettings.QuickDueDate(new DateTime(2026, 9, 7)));
    }

    // ── Дельты ────────────────────────────────────────────────────────────────

    [Fact]
    public void Overdue_and_show_done_survive_save_and_default_removes_the_key()
    {
        var store = Attach();

        TaskSettings.SetOverdueNextDay(true);
        Assert.True(store.Has("tasksOverdueNextDay"));
        Assert.True(AppSettings.LoadTasksOverdueNextDay());
        TaskSettings.SetOverdueNextDay(false);
        Assert.False(store.Has("tasksOverdueNextDay"));

        TaskSettings.SetShowDone(false);
        Assert.True(store.Has("tasksShowDone"));
        TaskSettings.Init();
        Assert.False(TaskSettings.ShowDone);
        TaskSettings.SetShowDone(true);
        Assert.False(store.Has("tasksShowDone"));
    }

    [Fact]
    public void Quick_add_defaults_survive_save_and_load()
    {
        var store = Attach();

        TaskSettings.SetQuickExecutorMe(false);
        TaskSettings.SetQuickDue(TaskQuickDue.Tomorrow);
        TaskSettings.SetDefaultStage("В работе");
        Assert.Equal("tomorrow", store.Get("tasksQuickDue"));

        TaskSettings.Init();
        Assert.False(TaskSettings.QuickExecutorMe);
        Assert.Equal(TaskQuickDue.Tomorrow, TaskSettings.QuickDue);
        Assert.Equal("В работе", TaskSettings.DefaultStage);
        Assert.Equal(new DateTime(2026, 9, 8), TaskSettings.QuickDueDate(new DateTime(2026, 9, 7, 15, 30, 0)));

        TaskSettings.SetQuickExecutorMe(true);
        TaskSettings.SetQuickDue(TaskQuickDue.None);
        TaskSettings.SetDefaultStage(ConstructorRepository.TaskStatusPlanned);
        Assert.False(store.Has("tasksQuickExecutorMe"));
        Assert.False(store.Has("tasksQuickDue"));
        Assert.False(store.Has("tasksDefaultStage"));
    }

    // ── Стартовый и последний фильтр ─────────────────────────────────────────

    [Fact]
    public void Start_filter_is_the_setting_unless_it_says_last()
    {
        Attach();

        TaskSettings.SetStartFilter("Overdue");
        Assert.Equal("Overdue", TaskSettings.InitialFilterKey);

        TaskSettings.SetStartFilter(TaskSettings.StartFilterLast);
        Assert.Equal("All", TaskSettings.InitialFilterKey);   // ничего еще не выбирали

        TaskSettings.RememberFilter("Stage:В работе");
        Assert.Equal("Stage:В работе", TaskSettings.InitialFilterKey);

        TaskSettings.SetStartFilter("Today");
        Assert.Equal("Today", TaskSettings.InitialFilterKey);   // запомненный фильтр не мешает
    }

    /// <summary>Последний фильтр — состояние: пишется всегда и без события Changed, иначе каждый
    /// клик по пункту списка перечитывал бы раздел заново.</summary>
    [Fact]
    public void Remembering_the_last_filter_is_state_without_an_event()
    {
        var store = Attach();

        var fired = CountChanges(() => TaskSettings.RememberFilter("NoDue"));

        Assert.Equal(0, fired);
        Assert.Equal("NoDue", store.Get("tasksLastFilter"));
        TaskSettings.Init();
        Assert.Equal("NoDue", TaskSettings.LastFilterKey);
    }

    // ── Мусор ─────────────────────────────────────────────────────────────────

    [Fact]
    public void Garbage_in_the_store_falls_back_to_defaults()
    {
        var store = Attach();
        store.Set("tasksOverdueNextDay", "мусор");
        store.Set("tasksStartFilter", "Done");        // прежний пункт, стадией он стать не может
        store.Set("tasksQuickDue", "later");
        store.Set("tasksDefaultStage", "   ");

        TaskSettings.Init();

        Assert.False(TaskSettings.OverdueNextDay);
        Assert.Equal("All", TaskSettings.StartFilterKey);
        Assert.Equal(TaskQuickDue.None, TaskSettings.QuickDue);
        Assert.Equal(ConstructorRepository.TaskStatusPlanned, TaskSettings.DefaultStage);
    }

    // ── Событие ───────────────────────────────────────────────────────────────

    [Fact]
    public void Changed_fires_once_per_real_change_and_not_for_the_same_value()
    {
        Attach();
        var stamp = TaskSettings.Stamp;

        var fired = CountChanges(() =>
        {
            TaskSettings.SetOverdueNextDay(true);
            TaskSettings.SetOverdueNextDay(true);    // то же значение — тишина
            TaskSettings.SetStartFilter("мусор");    // разобралось в стандарт, он и стоит — тишина
            TaskSettings.SetQuickDue(TaskQuickDue.Today);
        });

        Assert.Equal(2, fired);
        Assert.Equal(stamp + 2, TaskSettings.Stamp);
    }
}
