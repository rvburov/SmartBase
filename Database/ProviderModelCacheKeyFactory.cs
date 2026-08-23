using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;

namespace SmartBase.Database;

/// <summary>
/// Ключ кеша EF-модели с учетом провайдера. Стандартный ключ — только тип контекста,
/// а AppDbContext обслуживает и SQLite (PascalCase-колонки), и PostgreSQL (lowercase,
/// см. AppDbContext.ApplyLowercaseNames). Без этого ключа первый открытый провайдер
/// «застолбил» бы модель и для второго — SQL со вторыми именами колонок молча ломался бы.
/// </summary>
public sealed class ProviderModelCacheKeyFactory : IModelCacheKeyFactory
{
    public object Create(DbContext context, bool designTime)
        => (context.GetType(), (context as AppDbContext)?.IsPostgres ?? false, designTime);
}
