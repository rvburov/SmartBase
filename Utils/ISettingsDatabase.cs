namespace DataBase.Utils;

/// <summary>
/// Хранилище настроек приложения (ключ-значение). Реализуется поверх системной app.db
/// (см. <c>DatabaseRegistry</c>); <see cref="AppSettings"/> обращается к нему за
/// настройками. Интерфейс живёт в Utils, чтобы не тянуть слой БД в AppSettings.
/// </summary>
public interface ISettingsDatabase
{
    /// <summary>Значение по ключу или null, если ключа нет.</summary>
    string? Get(string key);

    /// <summary>Записать/обновить значение по ключу.</summary>
    void Set(string key, string value);

    /// <summary>Удалить ключ — настройка возвращается к стандарту (дефолту в коде).
    /// Нет ключа — no-op. БД хранит только отличия от стандарта, поэтому AppSettings
    /// удаляет ключ, когда значение равно дефолту.</summary>
    void Remove(string key);
}
