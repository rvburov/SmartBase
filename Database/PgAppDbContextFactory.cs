using Microsoft.EntityFrameworkCore.Design;

namespace SmartBase.Database;

/// <summary>
/// Design-time фабрика для CLI (`dotnet ef migrations add … --context PgAppDbContext`):
/// у PgAppDbContext нет конструктора без параметров, а инструментам нужно построить модель.
/// Строка фиктивная — для скаффолда миграций подключение к серверу не открывается.
/// </summary>
public sealed class PgAppDbContextFactory : IDesignTimeDbContextFactory<PgAppDbContext>
{
    public PgAppDbContext CreateDbContext(string[] args)
        => new("Host=localhost;Port=5432;Database=smartbase_design;Username=postgres;Password=postgres");
}
