namespace SmartBase.Tests.Integration;

/// <summary>
/// Проверяет саму тестовую обвязку: временная БД реально поднимается через
/// <see cref="TempDatabase"/> (миграции + сиды проходят) и корректно чистится.
/// </summary>
public sealed class InfrastructureSmokeTests
{
    [Fact]
    public void TempDatabase_opens_seeds_and_cleans_up()
    {
        string path;
        using (var db = new TempDatabase())
        {
            path = db.Path;
            Assert.True(File.Exists(path), "файл БД должен быть создан миграцией");

            // Сиды категорий выполняются в composition root — база не пустая.
            Assert.NotEmpty(db.Categories.GetAll());

            // Текущий пользователь ОС зарегистрирован первым → администратор с полным доступом.
            Assert.True(db.Permissions.IsAdmin);
            Assert.True(db.Permissions.AccessGranted);
        }

        Assert.False(File.Exists(path), "файл БД должен быть удалён при Dispose");
    }
}
