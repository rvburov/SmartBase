namespace DataBase.Utils;

/// <summary>
/// Хранилище настроек приложения (ключ-значение). Реализуется поверх системной app.db
/// (см. <c>DatabaseRegistry</c>); <see cref="AppSettings"/> обращается к нему вместо
/// прежнего settings.json. Интерфейс живёт в Utils, чтобы не тянуть слой БД в AppSettings.
/// </summary>
public interface IAppSettingsStore
{
    /// <summary>Значение по ключу или null, если ключа нет.</summary>
    string? Get(string key);

    /// <summary>Записать/обновить значение по ключу.</summary>
    void Set(string key, string value);
}
