using SmartBase.Database;
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

    // База по умолчанию всегда SQLite: «Основную» заводит сама программа файлом
    // (DatabaseRegistry.EnsureDefault), а все добавленные записи, включая PostgreSQL, приходят
    // с IsDefault = false, и перенести флаг нечем. Сочетания «PostgreSQL по умолчанию» здесь нет
    // намеренно: тест на состояние, которого не бывает, обещает поведение, которого никто не
    // проверял.
    [Theory]
    [InlineData(false, false, "SQLite")]
    [InlineData(true,  false, "PostgreSQL")]
    [InlineData(false, true,  "SQLite (По умолчанию)")]
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

    /// <summary>
    /// Движок называется одним методом на три места: строка списка, заголовок окна «Настройки
    /// базы данных» и список «Резервного копирования». Разъедутся написания — разъедется и
    /// ощущение, что это одна и та же база.
    /// </summary>
    [Theory]
    [InlineData(false, "SQLite")]
    [InlineData(true,  "PostgreSQL")]
    public void The_engine_is_named_the_same_way_everywhere(bool postgres, string expected)
    {
        Assert.Equal(expected, DatabaseRow.EngineName(postgres));
        Assert.StartsWith(DatabaseRow.EngineName(postgres), Badge(postgres, false));
    }

    /// <summary>
    /// Название в списке и пароль подключения правит администратор базы. Исключение одно:
    /// неподключенная база прав не показывает вовсе, и поля открыты — иначе база с устаревшим
    /// паролем не чинится никогда (войти нельзя, а без входа не видно и прав).
    /// </summary>
    [Theory]
    [InlineData(true,  true,  true)]    // подключена, администратор — правит
    [InlineData(true,  false, false)]   // подключена, обычный пользователь — поля серые
    [InlineData(false, false, true)]    // не подключена — прав не видно, поля открыты
    [InlineData(false, true,  true)]
    public void Only_the_database_admin_edits_the_name_and_the_password(
        bool mounted, bool isAdmin, bool allowed) =>
        Assert.Equal(allowed, DatabaseRow.CanManage(mounted, isAdmin));

    /// <summary>
    /// Строка размера пишется двумя способами, и различие не косметическое. В списке баз и в
    /// вопросе перед сжатием рядом нет заголовка — величину надо назвать словами. В окне
    /// настроек базы над строкой уже стоит заголовок «Размер файла», и повтор читался бы как
    /// заикание.
    /// </summary>
    [Fact]
    public void The_size_names_itself_in_the_list_and_stays_bare_under_the_heading()
    {
        var stats = new DatabaseManager.StorageStats(
            FileBytes: 268 * 1024, LiveBytes: 212 * 1024, FreeBytes: 56 * 1024,
            FreeRatio: 0.21, WalBytes: 0);

        Assert.StartsWith("размер файла ", DatabaseRow.FormatStorage(stats));
        Assert.DoesNotContain("размер файла", DatabaseRow.StorageValue(stats));
        Assert.EndsWith(DatabaseRow.StorageValue(stats), DatabaseRow.FormatStorage(stats));
        Assert.Contains("свободно", DatabaseRow.StorageValue(stats));
    }

    /// <summary>Корзина не предлагается у базы по умолчанию: удалить ее нельзя.</summary>
    [Theory]
    [InlineData(true,  false)]
    [InlineData(false, true)]
    public void The_default_database_cannot_be_deleted(bool isDefault, bool shown) =>
        Assert.Equal(shown, new DatabaseRow { IsDefault = isDefault }.ShowDeleteButton);
}
