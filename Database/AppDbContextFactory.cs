using Microsoft.EntityFrameworkCore.Design;
using DataBase.Utils;

namespace DataBase.Database;

public class AppDbContextFactory : IDesignTimeDbContextFactory<AppDbContext>
{
    public AppDbContext CreateDbContext(string[] args)
    {
        AppSettings.EnsureCreated();
        return new AppDbContext(AppSettings.DbPath);
    }
}
