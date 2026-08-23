using SmartBase.Core.Models;
using SmartBase.Notifications;

namespace SmartBase.Tests.Unit;

/// <summary>
/// Ядро уведомлений по задачам (<see cref="TaskEventScheduler"/>) — чистая логика: отбор по
/// включенным видам, свертка повторов по одной задаче, сводка при переполнении и тексты
/// плашек. Календарный брат — <see cref="NotificationSchedulerTests"/>.
/// </summary>
public sealed class TaskEventSchedulerTests
{
    private static TaskEvent Ev(string kind = TaskEventKinds.Assigned, long id = 1,
                                int db = 1, int record = 10, int field = 3,
                                string title = "Задача", string oldValue = "",
                                string newValue = "Петров Петр", string actor = "Иванов Иван",
                                string collection = "Дела") =>
        new()
        {
            DbId = db, EventId = id, Kind = kind, Ts = new DateTime(2026, 8, 1, 10, 0, 0),
            ActorName = actor, RecordId = record, CollectionId = 7, FieldId = field,
            ExecFieldId = 3, Title = title, CollectionName = collection,
            OldValue = oldValue, NewValue = newValue,
        };

    [Fact]
    public void Assigned_banner_says_who_and_carries_the_event()
    {
        var e = Ev();
        var d = TaskEventScheduler.Decide([e], assignedOn: true, statusOn: true, maxBanners: 5);

        Assert.Equal(1, d.FiredCount);
        var b = Assert.Single(d.Banners);
        Assert.Equal("Вам назначена задача", b.Title);
        Assert.Equal("Задача · Иванов Иван", b.Body);
        // Метка несет базу и событие — по ней служба откроет карточку задачи.
        Assert.Equal("task:1:1", b.Tag);
        Assert.Equal(e, b.Event);
    }

    [Fact]
    public void Assigned_body_falls_back_to_collection_without_actor()
    {
        var d = TaskEventScheduler.Decide([Ev(actor: "")], true, true, 5);
        Assert.Equal("Задача · Дела", d.Banners[0].Body);
    }

    [Fact]
    public void Status_banner_shows_transition()
    {
        var d = TaskEventScheduler.Decide(
            [Ev(TaskEventKinds.Status, oldValue: "Запланировано", newValue: "В работе")],
            true, true, 5);

        Assert.Equal("Задача сменила стадию", d.Banners[0].Title);
        Assert.Equal("Задача: Запланировано → В работе", d.Banners[0].Body);
    }

    [Fact]
    public void Empty_title_uses_the_common_placeholder()
    {
        var d = TaskEventScheduler.Decide([Ev(title: "")], true, true, 5);
        Assert.StartsWith(CalendarRecord.UntitledPlaceholder, d.Banners[0].Body);
    }

    [Fact]
    public void Multiline_title_is_shown_as_a_single_line()
    {
        var d = TaskEventScheduler.Decide([Ev(title: "Собрать отчет\nи отправить")], true, true, 5);
        Assert.StartsWith("Собрать отчет ·", d.Banners[0].Body);
    }

    [Fact]
    public void Disabled_kind_is_filtered_out()
    {
        IReadOnlyList<TaskEvent> events = [Ev(), Ev(TaskEventKinds.Status, id: 2, oldValue: "А")];

        var onlyStatus = TaskEventScheduler.Decide(events, assignedOn: false, statusOn: true, 5);
        Assert.Equal("Задача сменила стадию", Assert.Single(onlyStatus.Banners).Title);

        var onlyAssigned = TaskEventScheduler.Decide(events, assignedOn: true, statusOn: false, 5);
        Assert.Equal("Вам назначена задача", Assert.Single(onlyAssigned.Banners).Title);

        var nothing = TaskEventScheduler.Decide(events, assignedOn: false, statusOn: false, 5);
        Assert.Empty(nothing.Banners);
        Assert.Equal(0, nothing.FiredCount);
    }

    [Fact]
    public void Repeated_status_changes_collapse_into_one_honest_transition()
    {
        // За время простоя задача прошла три стадии: показываем одну плашку, но «было»
        // берем от ПЕРВОГО события — иначе текст соврет, откуда задача пришла.
        IReadOnlyList<TaskEvent> chain =
        [
            Ev(TaskEventKinds.Status, id: 1, oldValue: "Запланировано", newValue: "В работе"),
            Ev(TaskEventKinds.Status, id: 2, oldValue: "В работе",      newValue: "Проверка"),
            Ev(TaskEventKinds.Status, id: 3, oldValue: "Проверка",      newValue: "Выполнено"),
        ];

        var d = TaskEventScheduler.Decide(chain, true, true, 5);

        Assert.Equal(1, d.FiredCount);
        Assert.Equal("Задача: Запланировано → Выполнено", Assert.Single(d.Banners).Body);
        // Метка — от последнего события: клик ведет на актуальное состояние.
        Assert.Equal("task:1:3", d.Banners[0].Tag);
    }

    [Fact]
    public void Same_record_id_in_different_databases_stays_separate()
    {
        // Id записей между базами пересекаются — без DbId в ключе свертки две разные задачи
        // схлопнулись бы в одну плашку.
        IReadOnlyList<TaskEvent> two = [Ev(db: 1, id: 1), Ev(db: 2, id: 2)];

        var d = TaskEventScheduler.Decide(two, true, true, 5);

        Assert.Equal(2, d.FiredCount);
        Assert.Equal(2, d.Banners.Count);
    }

    [Fact]
    public void Assigned_and_status_of_one_task_are_two_banners()
    {
        IReadOnlyList<TaskEvent> both =
        [
            Ev(id: 1, field: 3),
            Ev(TaskEventKinds.Status, id: 2, field: 4, oldValue: "Запланировано", newValue: "В работе"),
        ];

        var d = TaskEventScheduler.Decide(both, true, true, 5);
        Assert.Equal(2, d.Banners.Count);
    }

    [Fact]
    public void Summary_replaces_banners_and_names_the_mix()
    {
        var many = Enumerable.Range(1, 4)
            .Select(i => Ev(id: i, record: i))
            .Concat(Enumerable.Range(5, 3)
                .Select(i => Ev(TaskEventKinds.Status, id: i, record: i, oldValue: "А", newValue: "Б")))
            .ToList();

        var d = TaskEventScheduler.Decide(many, true, true, maxBanners: 5);

        Assert.Equal(7, d.FiredCount);
        var b = Assert.Single(d.Banners);
        Assert.Equal("Обновления по задачам", b.Title);
        Assert.Equal("Назначено задач: 4 · Смен стадии: 3", b.Body);
        // Сводка ни к чему не ведет: ни метки, ни события.
        Assert.Equal("", b.Tag);
        Assert.Null(b.Event);
    }

    [Fact]
    public void Summary_of_one_kind_does_not_mention_the_other()
    {
        var six = Enumerable.Range(1, 6).Select(i => Ev(id: i, record: i)).ToList();
        Assert.Equal("Назначено задач: 6",
                     TaskEventScheduler.Decide(six, true, true, 5).Banners[0].Body);

        var status = Enumerable.Range(1, 6)
            .Select(i => Ev(TaskEventKinds.Status, id: i, record: i, oldValue: "А", newValue: "Б"))
            .ToList();
        Assert.Equal("Смен стадии: 6",
                     TaskEventScheduler.Decide(status, true, true, 5).Banners[0].Body);
    }

    [Fact]
    public void Task_tags_never_collide_with_calendar_tags()
    {
        var task = TaskEventScheduler.TagOf(Ev(db: 2, id: 5));
        var calendar = NotificationScheduler.TagOf(
            new CalendarRecord { DbId = 2, Id = 5, FieldId = 3 });

        Assert.NotEqual(calendar, task);
        Assert.StartsWith("task:", task);
    }

    [Fact]
    public void Texts_never_use_the_letter_yo()
    {
        // Правило проекта: буквы «е с точками» в текстах нет. Проверяем все, что служба
        // отдает в ОС, — заголовки, тела и сводку.
        var sources = new List<string>();
        foreach (var kind in new[] { TaskEventKinds.Assigned, TaskEventKinds.Status })
        {
            var e = Ev(kind, oldValue: "Запланировано", newValue: "В работе");
            sources.Add(TaskEventScheduler.TitleOf(e));
            sources.Add(TaskEventScheduler.BodyOf(e));
            sources.Add(TaskEventScheduler.BodyOf(Ev(kind, actor: "", collection: "", title: "")));
        }

        var many = Enumerable.Range(1, 9).Select(i => Ev(id: i, record: i)).ToList();
        var summary = TaskEventScheduler.Decide(many, true, true, 5).Banners[0];
        sources.Add(summary.Title);
        sources.Add(summary.Body);

        foreach (var text in sources)
            Assert.DoesNotContain(text, s => s is 'ё' or 'Ё');
    }
}
