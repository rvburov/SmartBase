using Microsoft.EntityFrameworkCore.Design;
using MyPlanner.Utils;

namespace MyPlanner.Database;

public class AppDbContextFactory : IDesignTimeDbContextFactory<AppDbContext>
{
    public AppDbContext CreateDbContext(string[] args)
    {
        AppSettings.EnsureCreated();
        return new AppDbContext(AppSettings.DbPath);
    }
}
