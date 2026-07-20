
namespace SmartBase.Tests.Integration;

/// <summary>
/// Репозиторий категорий: идемпотентные сиды, CRUD, запрет дублей имён, поиск по имени.
/// </summary>
public sealed class CategoryRepositoryTests
{
    [Fact]
    public void SeedDefaults_is_idempotent()
    {
        using var db = new TempDatabase();
        // Сиды уже выполнены в composition root; повторный вызов ничего не добавляет.
        var before = db.Categories.GetAll().Count;
        db.Categories.SeedDefaults();
        Assert.Equal(before, db.Categories.GetAll().Count);
        Assert.True(before > 0);
    }

    [Fact]
    public void Add_update_delete_and_get_by_name()
    {
        using var db = new TempDatabase();
        var repo = db.Categories;

        var added = repo.Add("Тест", "#123456");
        Assert.NotNull(added);
        var id = added!.Id!.Value;
        Assert.Equal("Тест", repo.GetByName("Тест")!.Name);

        Assert.Null(repo.Add("Тест", "#000000")); // дубль имени не создаётся

        Assert.True(repo.Update(id, "Тест2", "#654321"));
        Assert.Null(repo.GetByName("Тест"));
        Assert.Equal("#654321", repo.GetByName("Тест2")!.Color);

        repo.Delete(id);
        Assert.Null(repo.GetById(id));
    }
}
