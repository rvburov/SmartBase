using Microsoft.EntityFrameworkCore;
using SmartBase.Database.Entities;

namespace SmartBase.Database.Repositories;

public partial class ConstructorRepository
{
    /// <summary>Постоянное состояние расширения в этой базе. Читается свежий снимок,
    /// включая записи соседних подключений; конкретные ключи знает только расширение.</summary>
    public string? ReadExtensionState(string extensionId, string key)
    {
        var name = ExtensionStateKey(extensionId, key);
        return db.DbMeta.AsNoTracking().Where(row => row.Key == name)
            .Select(row => row.Value).FirstOrDefault();
    }

    /// <summary>Сохранить состояние на текущем подключении. Для атомарности с записями
    /// вызывается внутри той же транзакции и блокировки коллекции, что и операция расширения.</summary>
    public void WriteExtensionState(string extensionId, string key, string value)
    {
        var name = ExtensionStateKey(extensionId, key);
        var row = db.DbMeta.Find(name);
        if (row is not null)
        {
            db.Entry(row).Reload();
            if (db.Entry(row).State == EntityState.Detached) row = null;
        }
        if (row is null) db.DbMeta.Add(new DbMetaEntity { Key = name, Value = value });
        else row.Value = value;
        db.SaveChanges();
    }

    private static string ExtensionStateKey(string extensionId, string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(extensionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        if (extensionId.Length > 128 || extensionId.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('-' or '_' or '.')))
            throw new ArgumentException("Неверный идентификатор расширения", nameof(extensionId));
        if (key.Length > 256) throw new ArgumentException("Слишком длинный ключ состояния", nameof(key));
        return $"extension-state:{extensionId}:{key}";
    }
}
