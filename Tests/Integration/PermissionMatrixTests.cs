using SmartBase.Core.Models;
using SmartBase.Database.Repositories;
using SmartBase.Utils;

namespace SmartBase.Tests.Integration;

/// <summary>
/// Матрица «право → операция»: каждая галка панели администратора обязана блокировать
/// ИМЕННО свое действие и не задевать соседние. Точечные тесты на отдельные права были
/// и раньше (DbAccessLifecycleTests, PermissionServiceTests), но они не ловят главный
/// класс ошибок — перепутанный бит в новом мутаторе: «Удалять записи» отбивает
/// дублирование, «Изменять свойства» — создание и т.п.
///
/// Метод один: снимаем ровно один бит из полной маски, зовем операцию этого права
/// и проверяем, что она отбита с правильным <see cref="PermAction"/>; затем возвращаем
/// маску и убеждаемся, что та же операция проходит.
/// </summary>
public sealed class PermissionMatrixTests
{
    // Что делает каждое право. Данные для операции готовит Arrange (см. Scenario).
    private static readonly PermAction[] Actions =
    [
        PermAction.EditCollection, PermAction.DeleteCollection, PermAction.DuplicateCollection,
        PermAction.CreateCollection,
        PermAction.CreateRecord, PermAction.EditRecord, PermAction.DeleteRecord,
        PermAction.DuplicateRecord,
        PermAction.CreateField, PermAction.EditField, PermAction.DeleteField,
        PermAction.DuplicateField,
    ];

    public static TheoryData<PermAction> AllActions()
    {
        var data = new TheoryData<PermAction>();
        foreach (var a in Actions) data.Add(a);
        return data;
    }

    // Свежая коллекция с полем и записью — на них выполняются операции проверяемого права.
    private sealed record Fixture(int CollectionId, int FieldId, int RecordId);

    private static Fixture Arrange(TempDatabase db)
    {
        var col    = SampleData.AddCollection(db.Constructor, $"К{Guid.NewGuid():N}").Id!.Value;
        var field  = SampleData.AddField(db.Constructor, col, "Поле").Id!.Value;
        var record = SampleData.AddRecord(db.Constructor, col, "Запись").Id!.Value;
        return new Fixture(col, field, record);
    }

    // Операция, которую разрешает ровно это право. Каждая — публичная точка входа
    // репозитория, то есть тот же путь, которым идет интерфейс.
    private static Action Operation(TempDatabase db, PermAction action, Fixture f)
    {
        var repo = db.Constructor;
        return action switch
        {
            PermAction.EditCollection => () =>
            {
                var col = repo.GetAllCollections().First(c => c.Id == f.CollectionId);
                col.Name = "Переименована " + Guid.NewGuid().ToString("N")[..6];
                repo.UpdateCollection(col);
            },
            PermAction.DeleteCollection    => () => repo.DeleteCollection(f.CollectionId),
            PermAction.DuplicateCollection => () => repo.DuplicateCollection(f.CollectionId),
            PermAction.CreateCollection    => () => SampleData.AddCollection(repo, $"Новая{Guid.NewGuid():N}"),
            PermAction.CreateRecord        => () => SampleData.AddRecord(repo, f.CollectionId, "Еще запись"),
            PermAction.EditRecord          => () => repo.SetFieldValue(f.RecordId, f.FieldId, "значение"),
            PermAction.DeleteRecord        => () => repo.DeleteRecord(f.RecordId),
            PermAction.DuplicateRecord     => () => repo.DuplicateRecord(f.RecordId),
            PermAction.CreateField         => () => SampleData.AddField(repo, f.CollectionId, $"П{Guid.NewGuid():N}"),
            PermAction.EditField           => () =>
            {
                var field = repo.GetFields(f.CollectionId).First(x => x.Id == f.FieldId);
                field.Name = "Поле " + Guid.NewGuid().ToString("N")[..6];
                repo.UpdateField(field);
            },
            PermAction.DeleteField    => () => repo.DeleteField(f.FieldId),
            PermAction.DuplicateField => () => repo.DuplicateField(f.FieldId),
            _ => throw new ArgumentOutOfRangeException(nameof(action), action, "нет операции для права"),
        };
    }

    // Понижает текущего пользователя до допущенного не-админа с заданной маской на всю базу.
    private static void SetRights(TempDatabase db, PermAction mask)
    {
        var me = db.Context.DbUsers.First(u => u.UserKey == CurrentUser.Key);
        me.IsAdmin    = true;    // маски пишет только администратор
        db.Context.SaveChanges();
        db.Permissions.Reload(notify: false);
        db.Permissions.SetMask(db.Permissions.CurrentUserId, null, (int)mask);

        me.IsAdmin    = false;   // теперь маска действует: администратору можно все
        me.IsApproved = true;
        db.Context.SaveChanges();
        db.Permissions.Reload(notify: false);
    }

    [Theory]
    [MemberData(nameof(AllActions))]
    public void Action_is_blocked_without_its_own_right(PermAction action)
    {
        using var db = new TempDatabase();
        var fixture  = Arrange(db);

        // Все, кроме проверяемого права: если операция все же пройдет — значит мутатор
        // спрашивает не тот бит (или не спрашивает вовсе).
        SetRights(db, PermAction.All & ~action);

        var denied = Assert.Throws<PermissionDeniedException>(Operation(db, action, fixture));
        Assert.Equal(action, denied.Action);
        Assert.Contains("Недостаточно прав", denied.Message);
    }

    [Theory]
    [MemberData(nameof(AllActions))]
    public void Action_passes_with_its_own_right(PermAction action)
    {
        using var db = new TempDatabase();
        var fixture  = Arrange(db);

        SetRights(db, PermAction.All);
        Operation(db, action, fixture)();   // не бросает
    }

    /// <summary>
    /// Право просмотра мутаторов не имеет — оно решает, видно ли содержимое коллекции
    /// (сайдбар рисует замок, экспорт пропускает такие коллекции). Поэтому проверяем
    /// снимок прав, а не вызов репозитория.
    /// </summary>
    [Fact]
    public void View_right_is_visible_in_permission_snapshot()
    {
        using var db = new TempDatabase();
        var col = SampleData.AddCollection(db.Constructor).Id!.Value;

        SetRights(db, PermAction.All & ~PermAction.ViewCollection);
        Assert.False(db.Permissions.Can(PermAction.ViewCollection, col));
        Assert.True(db.Permissions.Can(PermAction.EditRecord, col));   // соседние права целы

        SetRights(db, PermAction.All);
        Assert.True(db.Permissions.Can(PermAction.ViewCollection, col));
    }

    /// <summary>
    /// Администратор не подчиняется маскам: даже пустая строка прав не должна его
    /// ограничивать (иначе он запер бы сам себя и потерял управление базой).
    /// </summary>
    [Fact]
    public void Admin_ignores_masks()
    {
        using var db = new TempDatabase();
        var fixture  = Arrange(db);

        db.Permissions.SetMask(db.Permissions.CurrentUserId, null, (int)PermAction.None);
        db.Permissions.Reload(notify: false);

        Assert.True(db.Permissions.IsAdmin);
        foreach (var action in Actions)
            Assert.True(db.Permissions.Can(action, fixture.CollectionId));

        // И на деле, а не только в снимке.
        repoWorks(db, fixture);

        static void repoWorks(TempDatabase db, Fixture f) =>
            db.Constructor.SetFieldValue(f.RecordId, f.FieldId, "админ пишет");
    }

    /// <summary>
    /// Права коллекции перекрывают права базы поштучно: запрет в базе + разрешение
    /// в коллекции = действие доступно именно в ней и запрещено в соседней.
    /// </summary>
    [Fact]
    public void Collection_mask_overrides_database_mask_per_collection()
    {
        using var db = new TempDatabase();
        var allowed = Arrange(db);
        var denied  = Arrange(db);

        var userId = db.Permissions.CurrentUserId;
        db.Permissions.SetMask(userId, null, (int)(PermAction.All & ~PermAction.DeleteRecord));
        db.Permissions.SetMask(userId, allowed.CollectionId, (int)PermAction.All);

        var me = db.Context.DbUsers.First(u => u.UserKey == CurrentUser.Key);
        me.IsAdmin = false;
        db.Context.SaveChanges();
        db.Permissions.Reload(notify: false);

        db.Constructor.DeleteRecord(allowed.RecordId);                       // переопределение коллекции
        Assert.Throws<PermissionDeniedException>(() => db.Constructor.DeleteRecord(denied.RecordId));
    }

    /// <summary>
    /// Нет строк в db_permissions — допущенному разрешено все: администратор снимает
    /// лишнее, а не раздает по одному (в базе хранятся только отличия).
    /// </summary>
    [Fact]
    public void Approved_user_without_rows_can_everything()
    {
        using var db = new TempDatabase();
        var fixture  = Arrange(db);

        var me = db.Context.DbUsers.First(u => u.UserKey == CurrentUser.Key);
        me.IsAdmin    = false;
        me.IsApproved = true;
        db.Context.SaveChanges();
        db.Permissions.Reload(notify: false);

        Assert.Empty(db.Context.DbPermissions.ToList());
        foreach (var action in Actions)
            Assert.True(db.Permissions.Can(action, fixture.CollectionId));

        db.Constructor.SetFieldValue(fixture.RecordId, fixture.FieldId, "можно");
    }

    /// <summary>
    /// Дублирование свойства раньше спрашивало право создания — теперь у него свой бит.
    /// Тест держит разделение: снятое «Создание свойства» не должно запирать дубль,
    /// иначе новая колонка панели ничего не решала бы.
    /// </summary>
    [Fact]
    public void Duplicating_a_field_does_not_need_the_create_right()
    {
        using var db = new TempDatabase();
        var fixture  = Arrange(db);

        SetRights(db, PermAction.All & ~PermAction.CreateField);

        db.Constructor.DuplicateField(fixture.FieldId);   // свое право на месте — проходит
        var denied = Assert.Throws<PermissionDeniedException>(
            () => SampleData.AddField(db.Constructor, fixture.CollectionId, "Новое"));
        Assert.Equal(PermAction.CreateField, denied.Action);
    }

    /// <summary>
    /// Пакетные пути (массовое удаление и дублирование записей) идут мимо одиночных
    /// мутаторов — у них своя проверка, и ее тоже нужно держать: именно ими удаляют
    /// сотни строк одним движением.
    /// </summary>
    [Fact]
    public void Batch_paths_demand_the_same_rights()
    {
        using var db = new TempDatabase();
        var fixture  = Arrange(db);
        var extra    = SampleData.AddRecord(db.Constructor, fixture.CollectionId, "Вторая").Id!.Value;
        int[] ids    = [fixture.RecordId, extra];

        SetRights(db, PermAction.All & ~PermAction.DeleteRecord);
        var deleteDenied = Assert.Throws<PermissionDeniedException>(
            () => db.Constructor.DeleteRecordsIsolated(ids, null));
        Assert.Equal(PermAction.DeleteRecord, deleteDenied.Action);

        SetRights(db, PermAction.All & ~PermAction.DuplicateRecord);
        var duplicateDenied = Assert.Throws<PermissionDeniedException>(
            () => db.Constructor.DuplicateRecordsIsolated(fixture.CollectionId, ids, null));
        Assert.Equal(PermAction.DuplicateRecord, duplicateDenied.Action);
    }
}
