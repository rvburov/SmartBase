using SmartBase.Views.Settings;

namespace SmartBase.Tests.Unit;

/// <summary>
/// Метка строки списка «Управление базой данных». Проверяется текст, а не раскладка: он
/// собирается из двух признаков, и перепутать их местами легко, а видно это только глазами на
/// живом экране.
///
/// Раньше движок писался только у PostgreSQL, и строка SQLite стояла без пометки вовсе — рядом с
/// помеченной строкой это читалось как недоделка, а не как «SQLite».
/// </summary>
public sealed class DatabaseRowTests
{
    private static string Badge(bool postgres, bool @default) =>
        new DatabaseRow { IsPostgres = postgres, IsDefault = @default }.EngineBadge;

    [Theory]
    [InlineData(false, false, "SQLite")]
    [InlineData(true,  false, "PostgreSQL")]
    [InlineData(false, true,  "SQLite (По умолчанию)")]
    [InlineData(true,  true,  "PostgreSQL (По умолчанию)")]
    public void The_badge_names_the_engine_and_marks_the_default(bool postgres, bool isDefault,
                                                                 string expected) =>
        Assert.Equal(expected, Badge(postgres, isDefault));

    /// <summary>
    /// Метка есть у КАЖДОЙ строки: пустая метка вернула бы прежнюю картину, где часть строк
    /// помечена, а часть нет.
    /// </summary>
    [Fact]
    public void Every_row_gets_a_badge()
    {
        foreach (var postgres in new[] { false, true })
            foreach (var isDefault in new[] { false, true })
                Assert.NotEmpty(Badge(postgres, isDefault));
    }

    /// <summary>Корзина не предлагается у базы по умолчанию: удалить ее нельзя.</summary>
    [Theory]
    [InlineData(true,  false)]
    [InlineData(false, true)]
    public void The_default_database_cannot_be_deleted(bool isDefault, bool shown) =>
        Assert.Equal(shown, new DatabaseRow { IsDefault = isDefault }.ShowDeleteButton);
}
