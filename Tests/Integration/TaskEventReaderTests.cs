using Microsoft.EntityFrameworkCore;
using SmartBase.Core.Models;
using SmartBase.Database;
using SmartBase.Database.Entities;
using SmartBase.Database.Repositories;
using SmartBase.Notifications;
using SmartBase.Utils;

namespace SmartBase.Tests.Integration;

/// <summary>
/// Читатель очередей событий задач (<see cref="TaskEventReader"/>): курсор на базу, чужие
/// события пропускаются, но курсор через них перешагивает, и первое знакомство с базой
/// историю не проигрывает.
///
/// Курсоры персистентные — именно на них держится догон: события, случившиеся при закрытом
/// приложении, доезжают при следующем запуске. Ошибка здесь не падает, а тихо превращается
/// либо в «уведомления не приходят», либо в «одно и то же приходит каждые полминуты».
/// </summary>
public sealed class TaskEventReaderTests
{
    private static (int ExecId, int AssignerId, int StatusId) EnableTasks(
        ConstructorRepository repo, int collectionId)
    {
        var exec = SampleData.AddField(repo, collectionId, "Исполнитель", "user", position: 1000);
        repo.SetUserFieldTaskExec(exec.Id!.Value, true);
        var roles = repo.TaskRoleFields(collectionId);
        return (exec.Id!.Value, roles.AssignerId!.Value, roles.StatusId!.Value);
    }

    // Кладет событие, адресованное текущему пользователю (как это делает правка исполнителя
    // соседом), либо чужое — минуя SetFieldValue, чтобы стенд не зависел от второй машины.
    private static void Enqueue(TempDatabase db, string recipientKey, string recipientName,
                                int recordId = 1, string kind = TaskEventKinds.Assigned)
    {
        db.Context.TaskEvents.Add(new TaskEventEntity
        {
            Ts            = DateTime.UtcNow.ToString("o"),
            Kind          = kind,
            RecipientKey  = recipientKey,
            RecipientName = recipientName,
            ActorKey      = "petrov@pc2",
            ActorName     = "Петров Петр",
            RecordId      = recordId,
            CollectionId  = 1,
            FieldId       = 1,
            ExecFieldId   = 1,
            TaskTitle     = "Задача",
            NewValue      = CurrentUser.DisplayName,
        });
        db.Context.SaveChanges();
    }

    private static TaskEventReader ReaderFor(params TempDatabase[] dbs)
    {
        IReadOnlyList<DatabaseManager> mounted = dbs.Select(d => d.Manager).ToList();
        return new TaskEventReader(() => mounted);
    }

    [Fact]
    public void First_acquaintance_seeds_the_cursor_and_replays_nothing()
    {
        AppSettings.AttachStore(new FakeSettingsDatabase());
        using var db = new TempDatabase();
        Enqueue(db, CurrentUser.Key, CurrentUser.DisplayName);

        var reader = ReaderFor(db);

        // История в очереди уже есть, но базу видим впервые — молчим.
        Assert.Empty(reader.Read());
        reader.Commit();
        Assert.Equal(1, AppSettings.LoadTaskEventCursors()[db.Manager.DbId]);

        // А вот следующее событие — уже наше.
        Enqueue(db, CurrentUser.Key, CurrentUser.DisplayName, recordId: 2);
        var e = Assert.Single(reader.Read());
        Assert.Equal(2, e.RecordId);
        Assert.Equal(db.Manager.DbId, e.DbId);   // проставляется на чтении, в базе его нет
    }

    [Fact]
    public void Foreign_events_are_skipped_but_the_cursor_steps_over_them()
    {
        AppSettings.AttachStore(new FakeSettingsDatabase());
        using var db = new TempDatabase();

        var reader = ReaderFor(db);
        reader.Read();      // посев курсора на пустой очереди
        reader.Commit();

        Enqueue(db, "petrov@pc2", "Петров Петр");                       // чужое
        Enqueue(db, CurrentUser.Key, CurrentUser.DisplayName, recordId: 7);   // мое

        var mine = Assert.Single(reader.Read());
        Assert.Equal(7, mine.RecordId);
        reader.Commit();

        // Курсор ушел за ОБЕ строки — иначе чужое событие перечитывалось бы каждым тиком.
        Assert.Equal(2, AppSettings.LoadTaskEventCursors()[db.Manager.DbId]);
        Assert.Empty(reader.Read());
    }

    [Fact]
    public void Event_without_a_resolved_key_is_matched_by_name()
    {
        AppSettings.AttachStore(new FakeSettingsDatabase());
        using var db = new TempDatabase();

        var reader = ReaderFor(db);
        reader.Read();
        reader.Commit();

        // Имя вписали руками — ключа у события нет; себя опознаем по имени.
        Enqueue(db, "", CurrentUser.DisplayName, recordId: 3);
        Enqueue(db, "", "Сидоров Семен", recordId: 4);

        var mine = Assert.Single(reader.Read());
        Assert.Equal(3, mine.RecordId);
    }

    [Fact]
    public void Cursors_of_two_databases_are_independent()
    {
        AppSettings.AttachStore(new FakeSettingsDatabase());
        using var first  = new TempDatabase(dbId: 1, name: "first");
        using var second = new TempDatabase(dbId: 2, name: "second");

        var reader = ReaderFor(first, second);
        reader.Read();
        reader.Commit();

        Enqueue(second, CurrentUser.Key, CurrentUser.DisplayName, recordId: 9);

        var e = Assert.Single(reader.Read());
        Assert.Equal(2, e.DbId);
        Assert.Equal(9, e.RecordId);
        reader.Commit();

        var cursors = AppSettings.LoadTaskEventCursors();
        Assert.Equal(0, cursors[1]);   // пустая база — курсор на нуле
        Assert.Equal(1, cursors[2]);
    }

    [Fact]
    public void Reseed_drops_everything_pending_and_moves_cursors_to_the_tail()
    {
        AppSettings.AttachStore(new FakeSettingsDatabase());
        using var db = new TempDatabase();

        var reader = ReaderFor(db);
        reader.Read();
        reader.Commit();

        Enqueue(db, CurrentUser.Key, CurrentUser.DisplayName, recordId: 5);
        Assert.Single(reader.Read());    // прочитали, но показать не успели

        // Включили тумблер: накопленное не вываливаем — курсоры на хвост.
        reader.Reseed();
        reader.Commit();

        Assert.Empty(reader.Read());
        Assert.Equal(1, AppSettings.LoadTaskEventCursors()[db.Manager.DbId]);
    }

    [Fact]
    public void Real_edit_reaches_the_recipient_end_to_end()
    {
        AppSettings.AttachStore(new FakeSettingsDatabase());
        using var db = new TempDatabase();
        var repo = db.Constructor;

        var col = SampleData.AddCollection(repo, "Заявки").Id!.Value;
        var (execId, assignerId, statusId) = EnableTasks(repo, col);
        var rec = SampleData.AddRecord(repo, col, "Заменить насос").Id!.Value;
        repo.SetFieldValue(rec, assignerId, CurrentUser.DisplayName);
        repo.SetFieldValue(rec, statusId,   ConstructorRepository.TaskStatusPlanned);

        var reader = ReaderFor(db);
        reader.Read();
        reader.Commit();

        // Исполнитель двигает стадию — назначивший (я) обязан получить событие целиком.
        repo.SetFieldValue(rec, statusId, ConstructorRepository.TaskStatusActive);

        var e = Assert.Single(reader.Read());
        Assert.Equal(TaskEventKinds.Status, e.Kind);
        Assert.Equal("Заменить насос", e.Title);
        Assert.Equal("Заявки", e.CollectionName);
        Assert.Equal(ConstructorRepository.TaskStatusPlanned, e.OldValue);
        Assert.Equal(ConstructorRepository.TaskStatusActive,  e.NewValue);
        Assert.Equal(execId, e.ExecFieldId);
        Assert.Equal(col,    e.CollectionId);
    }

}
