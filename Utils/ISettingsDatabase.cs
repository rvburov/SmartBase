namespace SmartBase.Utils;

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

    /// <summary>
    /// Все ключи, начинающиеся с приставки. Нужен там, где набор ключей заранее неизвестен
    /// и группу приходится обходить целиком: настройки расширений лежат под <c>ext.</c>, и
    /// при старте среди них ищутся осиротевшие — от расширений, которых в сборке больше нет
    /// (см. <see cref="AppSettings.RemoveOrphanExtensionSettings"/>).
    /// </summary>
    IReadOnlyList<string> KeysWithPrefix(string prefix);
}
