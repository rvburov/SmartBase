using SmartBase.Core.Models;
using SmartBase.Logging;

namespace SmartBase.Tests.Unit;

/// <summary>
/// Подписи журнала изменений и диагностики — общий источник для таблиц на экране
/// и для выгрузки в CSV. Тесты держат главное свойство: в файле оказывается ровно то,
/// что видит пользователь («Изменение», а не «update»), включая склонения и время.
/// </summary>
public sealed class ChangeLogLabelsTests
{
    [Theory]
    [InlineData("create", "Создание")]
    [InlineData("update", "Изменение")]
    [InlineData("duplicate", "Дублирование")]
    [InlineData("user.role", "Смена роли")]
    [InlineData("user.remove", "Удаление пользователя")]
    public void Actions_are_translated(string action, string expected)
    {
        Assert.Equal(expected, ChangeLogLabels.ActionLabel(action));
    }

    /// <summary>Неизвестное действие показывается как есть — новая запись журнала
    /// не должна исчезать из таблицы и выгрузки только потому, что подпись забыли.</summary>
    [Fact]
    public void Unknown_action_falls_back_to_the_raw_value()
    {
        Assert.Equal("что-то.новое", ChangeLogLabels.ActionLabel("что-то.новое"));
        Assert.Equal("плагин", ChangeLogLabels.TypeLabel("плагин"));
    }

    [Theory]
    [InlineData("collection", "Коллекция")]
    [InlineData("record", "Запись")]
    [InlineData("records", "Записи")]
    [InlineData("user", "Пользователь")]
    public void Types_are_translated(string type, string expected)
    {
        Assert.Equal(expected, ChangeLogLabels.TypeLabel(type));
    }

    [Fact]
    public void Object_prefers_the_before_after_detail()
    {
        Assert.Equal("«Клиенты» → «Клиенты 2024»",
            ChangeLogLabels.ObjectText("collection", "Клиенты 2024", null, "«Клиенты» → «Клиенты 2024»"));
    }

    [Fact]
    public void Object_of_a_batch_operation_is_the_count()
    {
        // Разделитель тысяч — из текущей культуры (в русской это неразрывный пробел),
        // поэтому ожидание собирается тем же форматом, а не литералом с пробелом.
        Assert.Equal($"{10_000:N0} записей", ChangeLogLabels.ObjectText("records", "", 10_000, null));
    }

    [Fact]
    public void Object_of_a_collection_adds_the_record_count()
    {
        Assert.Equal("Товары (3 записи)", ChangeLogLabels.ObjectText("collection", "Товары", 3, null));
        Assert.Equal("Товары", ChangeLogLabels.ObjectText("collection", "Товары", 0, null));
    }

    [Fact]
    public void Object_without_a_name_is_a_dash()
    {
        Assert.Equal("—", ChangeLogLabels.ObjectText("record", "", null, null));
    }

    [Theory]
    [InlineData(1, "1 запись")]
    [InlineData(3, "3 записи")]
    [InlineData(11, "11 записей")]
    [InlineData(14, "14 записей")]
    [InlineData(21, "21 запись")]
    public void Records_count_declines_correctly(long n, string expected)
    {
        Assert.Equal(expected, ChangeLogLabels.RecordsCount(n));
    }

    /// <summary>Большие числа разделяются по разрядам — как везде в интерфейсе.</summary>
    [Fact]
    public void Records_count_groups_thousands()
    {
        Assert.Equal($"{1_000_000:N0} записей", ChangeLogLabels.RecordsCount(1_000_000));
    }

    /// <summary>
    /// Журнал хранит UTC — на экране и в файле должно стоять локальное время машины.
    /// Ожидание считается тем же переводом, поэтому тест не зависит от часового пояса.
    /// </summary>
    [Fact]
    public void Timestamp_becomes_local_time()
    {
        var iso   = "2026-08-15T17:45:09.1234567Z";
        var local = DateTime.Parse(iso, null, System.Globalization.DateTimeStyles.RoundtripKind).ToLocalTime();

        Assert.Equal(local.ToString("dd.MM.yyyy HH:mm"), ChangeLogLabels.FormatTs(iso));
        Assert.Equal(local.ToString("dd.MM.yyyy HH:mm:ss"), ChangeLogLabels.FormatTs(iso, withSeconds: true));
    }

    [Fact]
    public void Unparsable_timestamp_is_kept_as_is()
    {
        Assert.Equal("не дата", ChangeLogLabels.FormatTs("не дата"));
    }

    [Theory]
    [InlineData("Information", "Информация")]
    [InlineData("Warning", "Предупреждение")]
    [InlineData("Fatal", "Критическая")]
    [InlineData("Custom", "Custom")]
    public void Log_levels_are_translated(string level, string expected)
    {
        Assert.Equal(expected, LogLabels.LevelLabel(level));
    }

    /// <summary>
    /// Serilog пишет время без суффикса Z — оно все равно UTC. Без AssumeUniversal
    /// строка разобралась бы как местная и в выгрузке поехала бы на часовой пояс.
    /// </summary>
    [Fact]
    public void Log_timestamp_without_zone_is_treated_as_utc()
    {
        var local = new DateTime(2026, 8, 15, 17, 39, 37, DateTimeKind.Utc).ToLocalTime();
        Assert.Equal(local.ToString("dd.MM.yyyy HH:mm:ss"), LogLabels.FormatTs("2026-08-15 17:39:37"));
    }
}
