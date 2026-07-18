using Microsoft.EntityFrameworkCore.Design;
using SmartBase.Utils;

namespace SmartBase.Database;

public class AppDbContextFactory : IDesignTimeDbContextFactory<AppDbContext>
{
    public AppDbContext CreateDbContext(string[] args)
    {
        AppSettings.EnsureCreated();
        return new AppDbContext(AppSettings.DbPath);
    }
}
