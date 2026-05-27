using MyPlanner.Core.Models;
using MyPlanner.Database.Entities;

namespace MyPlanner.Database.Repositories;

public class CategoryRepository(AppDbContext db)
{
    private static readonly (string Name, string Color)[] Defaults =
    [
        ("Работа",       "#007AFF"),
        ("Личное",       "#34C759"),
        ("Важное",       "#FF3B30"),
        ("Документация", "#FF9500"),
        ("Производство", "#AF52DE"),
        ("Совещания",    "#5AC8FA"),
        ("Контроль",     "#FFCC00"),
        ("Праздники",    "#FF2D55"),
    ];

    public void SeedDefaults()
    {
        if (db.Categories.Any()) return;
        foreach (var (name, color) in Defaults)
            Add(name, color);
    }

    // ── CRUD ──────────────────────────────────────────────────────────────────

    public Category? Add(string name, string color)
    {
        if (db.Categories.Any(c => c.Name == name)) return null;

        var entity = new CategoryEntity { Name = name, Color = color };
        db.Categories.Add(entity);
        db.SaveChanges();
        return new Category { Id = entity.Id, Name = entity.Name, Color = entity.Color };
    }

    public bool Update(int id, string name, string color)
    {
        var entity = db.Categories.Find(id);
        if (entity is null) return false;
        if (db.Categories.Any(c => c.Name == name && c.Id != id)) return false;

        entity.Name  = name;
        entity.Color = color;
        db.SaveChanges();
        return true;
    }

    public void Delete(int id)
    {
        var entity = db.Categories.Find(id);
        if (entity is null) return;
        db.Categories.Remove(entity);
        db.SaveChanges();
    }

    public List<Category> GetAll() =>
        db.Categories
          .OrderBy(c => c.Name)
          .Select(c => new Category { Id = c.Id, Name = c.Name, Color = c.Color })
          .ToList();

    public Category? GetById(int id)
    {
        var c = db.Categories.Find(id);
        return c is null ? null : new Category { Id = c.Id, Name = c.Name, Color = c.Color };
    }

    public Category? GetByName(string name)
    {
        var c = db.Categories.FirstOrDefault(x => x.Name == name);
        return c is null ? null : new Category { Id = c.Id, Name = c.Name, Color = c.Color };
    }
}
