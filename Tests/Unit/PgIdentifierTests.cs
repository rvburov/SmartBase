using SmartBase.Database;

namespace SmartBase.Tests.Unit;

/// <summary>
/// Валидация имен базы/роли для окна «Создать базу данных PostgreSQL»
/// (<see cref="PgDatabaseCreator.ValidateName"/>): правило нарочно строже сервера —
/// только строчные латинские, цифры и подчеркивание, чтобы созданное из приложения имя
/// не требовало кавычек при ручном администрировании сервера.
/// </summary>
public class PgIdentifierTests
{
    [Theory]
    [InlineData("projects")]
    [InlineData("crm_2026")]
    [InlineData("_internal")]
    [InlineData("a")]
    public void Valid_names_pass(string name) =>
        Assert.Null(PgDatabaseCreator.ValidateName(name));

    [Theory]
    [InlineData("")]                    // пусто
    [InlineData("   ")]                 // пробелы
    [InlineData("Projects")]            // заглавные — сервер сложил бы в строчные
    [InlineData("2026crm")]             // с цифры
    [InlineData("моябаза")]             // кириллица
    [InlineData("my-db")]               // дефис
    [InlineData("my db")]               // пробел
    [InlineData("db;drop")]             // разделитель — защита от кривых имен в DDL
    public void Invalid_names_rejected(string name) =>
        Assert.NotNull(PgDatabaseCreator.ValidateName(name));

    [Fact]
    public void Name_longer_than_63_rejected()
    {
        Assert.Null(PgDatabaseCreator.ValidateName(new string('a', 63)));
        Assert.NotNull(PgDatabaseCreator.ValidateName(new string('a', 64)));
    }
}
