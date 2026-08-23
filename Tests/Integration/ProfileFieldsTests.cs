using SmartBase.Utils;

namespace SmartBase.Tests.Integration;

/// <summary>
/// Поля профиля «Общих данных» (ФИО, подразделение, должность): все заполняются ПО ЖЕЛАНИЮ
/// и живут в app.db дельтами — пустое значение не хранится, а удаляет ключ (тот же инвариант,
/// что у настроек). Подразделение и должность — справочные: они не меняют отображаемое имя
/// и не участвуют в идентичности пользователя (ключ «учетка@компьютер»), поэтому их правка
/// не может внезапно превратить человека в «другого» участника базы.
/// </summary>
public sealed class ProfileFieldsTests
{
    // CurrentUser — статическое состояние процесса: подменяем хранилище на время теста
    // и возвращаем прежние значения (интеграционные тесты идут последовательно, но
    // DisplayName читают соседние тесты — оставлять за собой «Тестова» нельзя).
    private static void WithFakeStore(Action<FakeSettingsDatabase> body)
    {
        var (lastName, firstName, middleName) =
            (CurrentUser.LastName, CurrentUser.FirstName, CurrentUser.MiddleName);
        var (department, position) = (CurrentUser.Department, CurrentUser.Position);

        var store = new FakeSettingsDatabase();
        CurrentUser.AttachStore(store);
        try { body(store); }
        finally
        {
            CurrentUser.LastName   = lastName;
            CurrentUser.FirstName  = firstName;
            CurrentUser.MiddleName = middleName;
            CurrentUser.Department = department;
            CurrentUser.Position   = position;
        }
    }

    [Fact]
    public void Department_and_position_are_saved_and_trimmed()
    {
        WithFakeStore(store =>
        {
            CurrentUser.Department = "  Отдел эксплуатации  ";
            CurrentUser.Position   = "  Главный инженер  ";

            Assert.Equal("Отдел эксплуатации", CurrentUser.Department);
            Assert.Equal("Главный инженер",    CurrentUser.Position);
            Assert.Equal("Отдел эксплуатации", store.Get("profile.department"));
            Assert.Equal("Главный инженер",    store.Get("profile.position"));
        });
    }

    [Fact]
    public void Empty_department_and_position_are_not_stored()
    {
        WithFakeStore(store =>
        {
            // Не заполняли вовсе — в app.db ни одного ключа (хранятся только отличия от пустого).
            Assert.Equal("", CurrentUser.Department);
            Assert.Equal("", CurrentUser.Position);
            Assert.Equal(0, store.Count);

            CurrentUser.Department = "Служба качества";
            CurrentUser.Position   = "Инспектор";
            Assert.Equal(2, store.Count);

            // Стерли обратно (в том числе одними пробелами) — ключи ушли, значение «не задано».
            CurrentUser.Department = "";
            CurrentUser.Position   = "   ";
            Assert.False(store.Has("profile.department"));
            Assert.False(store.Has("profile.position"));
            Assert.Equal("", CurrentUser.Department);
            Assert.Equal("", CurrentUser.Position);
        });
    }

    [Fact]
    public void Department_and_position_do_not_change_display_name()
    {
        WithFakeStore(_ =>
        {
            CurrentUser.LastName   = "";
            CurrentUser.FirstName  = "";
            CurrentUser.MiddleName = "";
            CurrentUser.Department = "Отдел эксплуатации";
            CurrentUser.Position   = "Главный инженер";

            // ФИО не заполнено — подписью остается идентификатор, а не должность.
            Assert.Equal(CurrentUser.Key, CurrentUser.DisplayName);

            CurrentUser.LastName  = "Иванов";
            CurrentUser.FirstName = "Иван";
            Assert.Equal("Иванов Иван", CurrentUser.DisplayName);
        });
    }

    [Fact]
    public void Profile_fields_survive_restart()
    {
        WithFakeStore(store =>
        {
            CurrentUser.LastName   = "Иванов";
            CurrentUser.Department = "Отдел эксплуатации";
            CurrentUser.Position   = "Главный инженер";

            // Следующий запуск приложения: AttachStore поднимает значения из того же app.db.
            CurrentUser.AttachStore(store);

            Assert.Equal("Иванов",             CurrentUser.LastName);
            Assert.Equal("Отдел эксплуатации", CurrentUser.Department);
            Assert.Equal("Главный инженер",    CurrentUser.Position);
        });
    }

    [Fact]
    public void Db_user_snapshot_ignores_department_and_position()
    {
        using var db = new TempDatabase();

        WithFakeStore(_ =>
        {
            CurrentUser.Department = "Отдел эксплуатации";
            CurrentUser.Position   = "Главный инженер";
            db.Permissions.SyncDisplayName();

            // В базу уезжает только имя: подразделение и должность — личные справочные поля,
            // схема db_users их не знает и переименования из-за них не происходит.
            var me = db.Context.DbUsers.First(u => u.UserKey == CurrentUser.Key);
            Assert.Equal(CurrentUser.DisplayName, me.DisplayName);
            Assert.DoesNotContain(db.Context.ChangeLog.ToList(), e => e.Action == "user.rename");
        });
    }
}
