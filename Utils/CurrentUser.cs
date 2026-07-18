namespace SmartBase.Utils;

/// <summary>
/// Текущий пользователь приложения. Идентичность определяется автоматически, без входа:
/// ключ = «учётка@компьютер» (Environment.UserName + MachineName, нижний регистр) —
/// различает и разных людей на одном компьютере (разные учётки ОС), и одинаковые учётки
/// на разных машинах. ФИО заполняется вручную в «Профиль → Общие данные» и хранится
/// в app.db (<see cref="ISettingsDatabase"/>, ключи profile.*) — как настройки, только
/// отличия от пустого. Ключ и ФИО попадают в db_users каждой открытой базы конструктора
/// (снапшот для прав) и в журнал изменений.
///
/// Значения ФИО кешируются в полях: чтение <see cref="DisplayName"/> потокобезопасно
/// (журнал пишут и фоновые потоки массовых операций), app.db трогают только запись
/// из UI-потока и загрузка при старте.
/// </summary>
public static class CurrentUser
{
    private static ISettingsDatabase? _store;
    private static string _lastName = "", _firstName = "", _middleName = "";

    /// <summary>Подключить хранилище (app.db) и загрузить ФИО. Вызывается на старте.</summary>
    public static void AttachStore(ISettingsDatabase store)
    {
        _store      = store;
        _lastName   = store.Get(KeyLastName)   ?? "";
        _firstName  = store.Get(KeyFirstName)  ?? "";
        _middleName = store.Get(KeyMiddleName) ?? "";
    }

    /// <summary>Стабильный ключ пользователя: «учётка@компьютер» в нижнем регистре.</summary>
    public static string Key { get; } =
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

    /// <summary>«Фамилия Имя Отчество» (заполненные части) или ключ, если ФИО не задано.</summary>
    public static string DisplayName
    {
        get
        {
            var name = string.Join(" ",
                new[] { _lastName, _firstName, _middleName }.Where(p => p.Length > 0));
            return name.Length > 0 ? name : Key;
        }
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
}
