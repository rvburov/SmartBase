namespace SmartBase.Utils;

/// <summary>
/// Текущий пользователь приложения. Идентичность определяется автоматически, без входа:
/// ключ = «учетка@компьютер» (Environment.UserName + MachineName, нижний регистр) —
/// различает и разных людей на одном компьютере (разные учетки ОС), и одинаковые учетки
/// на разных машинах. ФИО, подразделение и должность заполняются вручную в
/// «Профиль → Общие данные» и хранятся в app.db (<see cref="ISettingsDatabase"/>,
/// ключи profile.*) — как настройки, только отличия от пустого. Ключ и ФИО попадают
/// в db_users каждой открытой базы конструктора и в журнал изменений;
/// подразделение и должность — справочные поля самого пользователя, в базы не уезжают.
///
/// Значения кешируются в полях: чтение <see cref="DisplayName"/> потокобезопасно
/// (журнал пишут и фоновые потоки массовых операций), app.db трогают только запись
/// из UI-потока и загрузка при старте.
/// </summary>
public static class CurrentUser
{
    private static ISettingsDatabase? _store;
    private static string _lastName = "", _firstName = "", _middleName = "";
    private static string _department = "", _position = "";

    /// <summary>Подключить хранилище (app.db) и загрузить поля профиля. Вызывается на старте.</summary>
    public static void AttachStore(ISettingsDatabase store)
    {
        _store      = store;
        _lastName   = store.Get(KeyLastName)   ?? "";
        _firstName  = store.Get(KeyFirstName)  ?? "";
        _middleName = store.Get(KeyMiddleName) ?? "";
        _department = store.Get(KeyDepartment) ?? "";
        _position   = store.Get(KeyPosition)   ?? "";
    }

    /// <summary>Стабильный ключ пользователя: «учетка@компьютер» в нижнем регистре.</summary>
    public static string Key => _acting.Value?.Key ?? OwnKey;

    /// <summary>Ключ человека, сидящего за машиной, — без подмены на робота.</summary>
    public static string OwnKey { get; } =
        $"{Environment.UserName}@{Environment.MachineName}".ToLowerInvariant();

    public static string LastName
    {
        get => _lastName;
        set => _lastName = SaveField(KeyLastName, value);
    }

    public static string FirstName
    {
        get => _firstName;
        set => _firstName = SaveField(KeyFirstName, value);
    }

    public static string MiddleName
    {
        get => _middleName;
        set => _middleName = SaveField(KeyMiddleName, value);
    }

    // Подразделение и должность — необязательные справочные поля профиля (как ФИО:
    // пусто = «не задано», ключ из app.db удаляется). На идентичность и отображаемое
    // имя не влияют: пользователь остается тем же ключом «учетка@компьютер».
    public static string Department
    {
        get => _department;
        set => _department = SaveField(KeyDepartment, value);
    }

    public static string Position
    {
        get => _position;
        set => _position = SaveField(KeyPosition, value);
    }

    /// <summary>«Фамилия Имя Отчество» (заполненные части) или ключ, если ФИО не задано.</summary>
    public static string DisplayName
    {
        get
        {
            if (_acting.Value is { } robot) return robot.Name;

            var name = string.Join(" ",
                new[] { _lastName, _firstName, _middleName }.Where(p => p.Length > 0));
            return name.Length > 0 ? name : OwnKey;
        }
    }

    // ── Действие от чужого имени ───────────────────────────────────────────────
    //
    // Входящий канал пишет записи от имени ПОЛЬЗОВАТЕЛЯ-РОБОТА, а не того, кто сидит за машиной
    // (docs/ROADMAP-API.md, раздел 7): в истории изменений должно стоять «Обмен учетной системы
    // создал запись», иначе чужие записи неотличимы от своих и разбирать потом нечего.
    //
    // Подмена живет в AsyncLocal, а не в поле: обращения обрабатываются каждое на своей задаче,
    // и общее поле означало бы, что одно обращение переименовывает автора у соседнего. Область
    // действия — ровно один блок using, и ставит ее только приемник.

    private sealed record Actor(string Key, string Name);

    private static readonly AsyncLocal<Actor?> _acting = new();

    /// <summary>Сейчас записи пишутся от имени робота, а не человека.</summary>
    public static bool ActingAsRobot => _acting.Value is not null;

    /// <summary>
    /// Писать от имени робота, пока не закончится блок. Ключ и имя попадут и в <c>db_users</c>,
    /// и в журнал изменений — тем же путем, что и у человека, потому что путь один.
    /// </summary>
    public static IDisposable ActAs(string key, string name) => new ActorScope(key, name);

    private sealed class ActorScope : IDisposable
    {
        private readonly Actor? _previous;

        public ActorScope(string key, string name)
        {
            _previous     = _acting.Value;
            _acting.Value = new Actor(key, name);
        }

        public void Dispose() => _acting.Value = _previous;
    }

    // Пустое значение = дефолт → ключ удаляется (в app.db только отличия от стандарта).
    private static string SaveField(string key, string value)
    {
        var trimmed = (value ?? "").Trim();
        if (trimmed.Length == 0) _store?.Remove(key);
        else                     _store?.Set(key, trimmed);
        return trimmed;
    }

    private const string KeyLastName   = "profile.lastName";
    private const string KeyFirstName  = "profile.firstName";
    private const string KeyMiddleName = "profile.middleName";
    private const string KeyDepartment = "profile.department";
    private const string KeyPosition   = "profile.position";
}
