using SmartBase.Core.Models;
using SmartBase.Notifications;

namespace SmartBase.Tests.Integration;

/// <summary>
/// Служба уведомлений поверх настоящей БД: источник событий — календарная проекция
/// (<c>GetCalendarRecordsByRange</c>), а показ/значок/метка проверяются через фейковые
/// <see cref="INativeNotifier"/>/<see cref="IAppBadge"/> и управляемые часы — без таймера и
/// платформы. Проверяем: наступившее показывается, будущее ждет, значок копится и гаснет при
/// активации, метка сохраняется, выключенная служба молчит.
/// </summary>
public sealed class NotificationServiceTests
{
    private sealed class FakeNotifier : INativeNotifier
    {
        public List<(string Title, string Body, string Tag)> Shown { get; } = new();
        public event Action<string>? Activated;
        public void Show(string title, string body, string tag) => Shown.Add((title, body, tag));
        public void RaiseActivated(string tag) => Activated?.Invoke(tag);
    }

    private sealed class FakeBadge : IAppBadge
    {
        public int Last { get; private set; } = -1;
        public void SetCount(int count) => Last = count;
    }

    // Записывает событие с полем-датой и таймовым значением start–(start+1ч).
    private static void AddEvent(TempDatabase db, int collectionId, int dateFieldId,
                                 string title, DateTime start)
    {
        var repo  = db.Constructor;
        var rec   = SampleData.AddRecord(repo, collectionId, title);
        var value = new DateValue(start, start.AddHours(1), false).ToStorage();
        repo.SetFieldValue(rec.Id!.Value, dateFieldId, value);
    }

    [Fact]
    public void Fires_due_events_accumulates_badge_and_clears_on_activation()
    {
        using var db = new TempDatabase();
        var col  = SampleData.AddCollection(db.Constructor, "Дела");
        var date = SampleData.AddField(db.Constructor, col.Id!.Value, "Дата", "date");
        AddEvent(db, col.Id!.Value, date.Id!.Value, "Событие1", new DateTime(2026, 7, 1, 10, 0, 0));
        AddEvent(db, col.Id!.Value, date.Id!.Value, "Событие2", new DateTime(2026, 7, 1, 23, 0, 0));

        var notifier = new FakeNotifier();
        var badge    = new FakeBadge();
        DateTime?    savedWm = null;
        var clock    = new DateTime(2026, 7, 1, 12, 0, 0);

        var svc = new NotificationService(
            notifier, badge,
            (from, to) => db.Constructor.GetCalendarRecordsByRange(from, to, null),
            w => savedWm = w,
            () => clock,
            maxBanners: 5);
        svc.Configure(enabled: true, watermark: new DateTime(2026, 7, 1, 9, 0, 0));

        // Первый проход: наступило только «Событие1» (10:00), «Событие2» (23:00) — впереди.
        svc.Tick();
        Assert.Single(notifier.Shown);
        Assert.Equal("Событие1", notifier.Shown[0].Title);
        Assert.Equal(1, badge.Last);
        Assert.Equal(clock, savedWm);   // метка сохранена в «сейчас»

        // Часы перешли за 23:00 — второй проход показывает «Событие2», значок копится.
        clock = new DateTime(2026, 7, 2, 0, 1, 0);
        svc.Tick();
        Assert.Contains(notifier.Shown, x => x.Title == "Событие2");
        Assert.Equal(2, badge.Last);

        // Пользователь активировал окно — значок гаснет.
        svc.NotifyWindowActivated();
        Assert.Equal(0, badge.Last);
    }

    [Fact]
    public void Disabled_service_shows_nothing()
    {
        using var db = new TempDatabase();
        var col  = SampleData.AddCollection(db.Constructor, "Дела");
        var date = SampleData.AddField(db.Constructor, col.Id!.Value, "Дата", "date");
        AddEvent(db, col.Id!.Value, date.Id!.Value, "Событие", new DateTime(2026, 7, 1, 10, 0, 0));

        var notifier = new FakeNotifier();
        var badge    = new FakeBadge();

        var svc = new NotificationService(
            notifier, badge,
            (from, to) => db.Constructor.GetCalendarRecordsByRange(from, to, null),
            _ => { },
            () => new DateTime(2026, 7, 1, 12, 0, 0),
            maxBanners: 5);
        svc.Configure(enabled: false, watermark: new DateTime(2026, 7, 1, 9, 0, 0));

        var decision = svc.Tick();

        Assert.Empty(notifier.Shown);
        Assert.Equal(0, decision.FiredCount);
    }

    [Fact]
    public void Clicking_notification_routes_the_full_record()
    {
        using var db = new TempDatabase();
        var col  = SampleData.AddCollection(db.Constructor, "Дела");
        var date = SampleData.AddField(db.Constructor, col.Id!.Value, "Дата", "date");
        AddEvent(db, col.Id!.Value, date.Id!.Value, "Событие1", new DateTime(2026, 7, 1, 10, 0, 0));

        var notifier = new FakeNotifier();
        var svc = new NotificationService(
            notifier, new FakeBadge(),
            (from, to) => db.Constructor.GetCalendarRecordsByRange(from, to, null),
            _ => { },
            () => new DateTime(2026, 7, 1, 12, 0, 0),
            maxBanners: 5);
        svc.Configure(enabled: true, watermark: new DateTime(2026, 7, 1, 9, 0, 0));

        CalendarRecord? activated = null;
        svc.RecordActivated += r => activated = r;

        svc.Tick();
        Assert.Single(notifier.Shown);

        // Клик по баннеру с его меткой → служба отдает полную запись подписчику.
        notifier.RaiseActivated(notifier.Shown[0].Tag);

        Assert.NotNull(activated);
        Assert.Equal("Событие1", activated!.Title);
    }

    // Фоновый тик (TickBackground) читает наступившие события НЕ на UI-потоке, через свежее
    // подключение (GetCalendarRecordsByRangeIsolated) — иначе скан calendar_index «морозит»
    // интерфейс на больших БД. Здесь фиксируем контракт этого чтения: с фонового потока оно
    // отдает ровно тот же результат, что и обычное чтение с общего контекста.
    [Fact]
    public async Task Isolated_read_matches_shared_and_runs_off_thread()
    {
        using var db = new TempDatabase();
        var col  = SampleData.AddCollection(db.Constructor, "Дела");
        var date = SampleData.AddField(db.Constructor, col.Id!.Value, "Дата", "date");
        AddEvent(db, col.Id!.Value, date.Id!.Value, "Событие1", new DateTime(2026, 7, 1, 10, 0, 0));
        AddEvent(db, col.Id!.Value, date.Id!.Value, "Событие2", new DateTime(2026, 7, 1, 11, 0, 0));

        var from = new DateTime(2026, 7, 1, 0, 0, 0);
        var to   = new DateTime(2026, 7, 2, 0, 0, 0);

        var shared = db.Constructor.GetCalendarRecordsByRange(from, to, null);
        // Свежее подключение с фонового потока — общий однопоточный контекст не трогаем.
        var isolated = await Task.Run(() => db.Constructor.GetCalendarRecordsByRangeIsolated(from, to, null));

        Assert.Equal(2, isolated.Count);
        Assert.Equal(shared.Select(r => r.Id), isolated.Select(r => r.Id));
        Assert.Equal(shared.Select(r => r.StartDt), isolated.Select(r => r.StartDt));
    }

    // ── Второй источник: события задач ────────────────────────────────────────
    //
    // Служба одна на оба источника (значок и счетчик непросмотренного — общий ресурс), и
    // здесь проверяется именно стык: каждый вид гейтится своим тумблером, курсоры очереди
    // фиксируются после прохода, а клик по метке задачи не путается с календарной.

    private sealed class FakeTaskSource : ITaskEventSource
    {
        public List<TaskEvent> Next { get; } = new();
        public int Reads { get; private set; }
        public int Commits { get; private set; }
        public int Reseeds { get; private set; }

        public IReadOnlyList<TaskEvent> Read()
        {
            Reads++;
            var batch = Next.ToList();
            Next.Clear();
            return batch;
        }

        public void Commit() => Commits++;
        public void Reseed() { Reseeds++; Next.Clear(); }
    }

    private static TaskEvent TaskEv(string kind = TaskEventKinds.Assigned, long id = 1,
                                    string title = "Задача") =>
        new()
        {
            DbId = 1, EventId = id, Kind = kind, Ts = new DateTime(2026, 7, 1, 12, 0, 0),
            ActorName = "Петров Петр", RecordId = 5, CollectionId = 2, FieldId = 3,
            ExecFieldId = 3, Title = title, CollectionName = "Дела",
            OldValue = "Запланировано", NewValue = "В работе",
        };

    private static NotificationService TaskService(FakeNotifier notifier, FakeBadge badge,
                                                   ITaskEventSource source) =>
        new(notifier, badge,
            (_, _) => [],                       // календарь в этих тестах молчит
            _ => { },
            () => new DateTime(2026, 7, 1, 12, 0, 0),
            maxBanners: 5,
            taskSource: source);

    [Fact]
    public void Task_events_show_a_banner_count_on_the_badge_and_commit_the_cursor()
    {
        var notifier = new FakeNotifier();
        var badge    = new FakeBadge();
        var source   = new FakeTaskSource();
        source.Next.Add(TaskEv());

        var svc = TaskService(notifier, badge, source);
        svc.Configure(enabled: false, watermark: new DateTime(2026, 7, 1, 9, 0, 0));
        svc.ConfigureTasks(assignedOn: true, statusOn: true);

        svc.Tick();

        var shown = Assert.Single(notifier.Shown);
        Assert.Equal("Вам назначена задача", shown.Title);
        Assert.Equal("task:1:1", shown.Tag);
        Assert.Equal(1, badge.Last);
        // Курсор фиксируется каждым проходом: иначе чужие события перечитывались бы вечно.
        Assert.Equal(1, source.Commits);
    }

    [Fact]
    public void Each_task_kind_is_gated_by_its_own_toggle()
    {
        var notifier = new FakeNotifier();
        var source   = new FakeTaskSource();
        source.Next.AddRange([TaskEv(), TaskEv(TaskEventKinds.Status, id: 2)]);

        var svc = TaskService(notifier, new FakeBadge(), source);
        svc.Configure(enabled: false, watermark: new DateTime(2026, 7, 1, 9, 0, 0));
        svc.ConfigureTasks(assignedOn: false, statusOn: true);

        svc.Tick();

        var shown = Assert.Single(notifier.Shown);
        Assert.Equal("Задача сменила стадию", shown.Title);
    }

    [Fact]
    public void All_sources_off_reads_nothing_at_all()
    {
        var source = new FakeTaskSource();
        source.Next.Add(TaskEv());

        var svc = TaskService(new FakeNotifier(), new FakeBadge(), source);
        svc.Configure(enabled: false, watermark: new DateTime(2026, 7, 1, 9, 0, 0));
        svc.ConfigureTasks(assignedOn: false, statusOn: false);

        svc.Tick();

        Assert.Equal(0, source.Reads);
    }

    [Fact]
    public void Turning_a_task_toggle_on_reseeds_instead_of_replaying_history()
    {
        var notifier = new FakeNotifier();
        var source   = new FakeTaskSource();

        var svc = TaskService(notifier, new FakeBadge(), source);
        svc.Configure(enabled: false, watermark: new DateTime(2026, 7, 1, 9, 0, 0));
        svc.ConfigureTasks(assignedOn: false, statusOn: false);

        // Пока тумблер был выключен, в очереди накопилось.
        source.Next.Add(TaskEv());
        svc.SetTaskAssignedEnabled(true);

        svc.Tick();
        Assert.Equal(1, source.Reseeds);
        Assert.Empty(notifier.Shown);   // накопленное не вываливаем

        // Следующее событие — уже показываем.
        source.Next.Add(TaskEv(id: 2));
        svc.Tick();
        Assert.Single(notifier.Shown);
    }

    [Fact]
    public void Badge_survives_turning_off_only_one_source()
    {
        var badge  = new FakeBadge();
        var source = new FakeTaskSource();
        source.Next.Add(TaskEv());

        var svc = TaskService(new FakeNotifier(), badge, source);
        svc.Configure(enabled: true, watermark: new DateTime(2026, 7, 1, 9, 0, 0));
        svc.ConfigureTasks(assignedOn: true, statusOn: true);
        svc.Tick();
        Assert.Equal(1, badge.Last);

        // Выключили календарь — счет, накопленный задачами, остается.
        svc.SetEnabled(false);
        Assert.Equal(1, badge.Last);

        // Выключили последние источники — значок гаснет.
        svc.SetTaskAssignedEnabled(false);
        svc.SetTaskStatusEnabled(false);
        Assert.Equal(0, badge.Last);
    }

    [Fact]
    public void Clicking_a_task_banner_routes_the_event_not_the_record()
    {
        var notifier = new FakeNotifier();
        var source   = new FakeTaskSource();
        source.Next.Add(TaskEv(TaskEventKinds.Status, id: 4, title: "Заменить насос"));

        var svc = TaskService(notifier, new FakeBadge(), source);
        svc.Configure(enabled: false, watermark: new DateTime(2026, 7, 1, 9, 0, 0));
        svc.ConfigureTasks(assignedOn: true, statusOn: true);

        TaskEvent?      activatedTask   = null;
        CalendarRecord? activatedRecord = null;
        svc.TaskActivated   += e => activatedTask = e;
        svc.RecordActivated += r => activatedRecord = r;

        svc.Tick();
        notifier.RaiseActivated(notifier.Shown[0].Tag);

        Assert.NotNull(activatedTask);
        Assert.Equal("Заменить насос", activatedTask!.Title);
        Assert.Null(activatedRecord);   // маршруты не перепутаны
    }
}
