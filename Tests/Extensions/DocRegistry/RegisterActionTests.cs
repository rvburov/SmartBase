using SmartBase.Extensions;
using SmartBase.Extensions.DocRegistry;

namespace SmartBase.Tests.Extensions.DocRegistry;

/// <summary>
/// Регистрация ШАГОМ СЦЕНАРИЯ (<see cref="RegisterAction"/>) — та же самая, что в окне, но без
/// человека рядом.
///
/// Сама выдача номеров проверяется в <see cref="DocRegistrarTests"/>; здесь — то, чем действие
/// отличается от окна: причина возвращается словами, а не исключением, спорный случай кончается
/// отказом (соглашаться некому), и коллекция сверяется при каждом запуске.
/// </summary>
public sealed class RegisterActionTests
{
    private static readonly DateTimeOffset Now =
        new(new DateTime(2026, 9, 10, 12, 0, 0, DateTimeKind.Local));

    private static readonly RegisterAction Action = new();

    private static ActionInput Input(params (string Key, string Value)[] values) =>
        new(values.ToDictionary(v => v.Key, v => v.Value, StringComparer.Ordinal));

    private static ActionInput Message(string eskd = "301121", string name = "Втулка",
                                       string execution = "", string number = "") =>
        Input(("name", name), ("org", "АБВГ"), ("eskd", eskd), ("project", "Молоток"),
              ("fio", "Иванов Иван Иванович"), ("execution", execution), ("number", number));

    // ── Обычная регистрация ───────────────────────────────────────────────────

    /// <summary>
    /// Сообщение становится строкой журнала с присвоенным обозначением — и это тот же
    /// <c>DocRegistrar</c>, что работает в окне: логика регистрации не дублируется.
    /// </summary>
    [Fact]
    public void A_message_becomes_a_registered_document()
    {
        using var db = new TempDatabase();
        var collectionId = JournalStand.Create(db.Constructor);

        var result = Action.Run(new ActionContext(db.Constructor, 1, collectionId, Now), Message());

        Assert.True(result.Ok, result.Explanation);
        Assert.Contains("АБВГ.301121.001", result.Explanation, StringComparison.Ordinal);
        Assert.Equal(1, result.Rows);

        var record = Assert.Single(db.Constructor.GetRecords(collectionId));
        Assert.Equal("Втулка", record.Title);
    }

    /// <summary>Второй документ той же серии получает следующий номер — счетчик ведет журнал.</summary>
    [Fact]
    public void The_second_document_of_a_series_gets_the_next_number()
    {
        using var db = new TempDatabase();
        var collectionId = JournalStand.Create(db.Constructor);
        var context      = new ActionContext(db.Constructor, 1, collectionId, Now);

        Action.Run(context, Message());
        var second = Action.Run(context, Message(name: "Кронштейн"));

        Assert.True(second.Ok, second.Explanation);
        Assert.Contains("АБВГ.301121.002", second.Explanation, StringComparison.Ordinal);
    }

    // ── Отказы говорят словами ────────────────────────────────────────────────

    /// <summary>
    /// Незаполненное обязательное поле — ответ отправителю, а не исключение: причина едет в
    /// квитанцию, а у квитанции нет разработчика.
    /// </summary>
    [Fact]
    public void A_missing_field_is_a_named_refusal()
    {
        using var db = new TempDatabase();
        var collectionId = JournalStand.Create(db.Constructor);

        var result = Action.Run(new ActionContext(db.Constructor, 1, collectionId, Now),
                                Input(("org", "АБВГ"), ("eskd", "301121"), ("project", "Молоток")));

        Assert.False(result.Ok);
        Assert.Contains("наименование", result.Explanation, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(db.Constructor.GetRecords(collectionId));
    }

    [Fact]
    public void A_bad_execution_is_a_named_refusal()
    {
        using var db = new TempDatabase();
        var collectionId = JournalStand.Create(db.Constructor);

        var result = Action.Run(new ActionContext(db.Constructor, 1, collectionId, Now),
                                Message(execution: "первое"));

        Assert.False(result.Ok);
        Assert.Contains("не число", result.Explanation, StringComparison.Ordinal);
        Assert.Contains("execution", result.Explanation, StringComparison.Ordinal);
    }

    /// <summary>
    /// Занятое исполнение — отказ, и НИКОГДА молчаливая перезапись: в окне человек соглашается
    /// писать поверх, а у сообщения собеседника нет.
    /// </summary>
    [Fact]
    public void A_taken_execution_is_refused_instead_of_overwritten()
    {
        using var db = new TempDatabase();
        var collectionId = JournalStand.Create(db.Constructor);
        var context      = new ActionContext(db.Constructor, 1, collectionId, Now);

        Assert.True(Action.Run(context, Message(execution: "1")).Ok);

        // Тот же номер и то же исполнение: так выглядит повторная присылка исполнения.
        var again = Action.Run(context, Message(name: "Другой", execution: "1", number: "1"));

        Assert.False(again.Ok);
        Assert.Single(db.Constructor.GetRecords(collectionId));
    }

    // ── Коллекция ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Структура коллекции сверяется при КАЖДОМ запуске: столбец могли переименовать вчера, и
    /// значения легли бы не туда. Сообщение называет, что именно не сошлось.
    /// </summary>
    [Fact]
    public void A_wrong_collection_is_refused_with_the_reason()
    {
        using var db = new TempDatabase();

        var collectionId = JournalStand.Create(db.Constructor, "Не журнал",
            [RegistrySchema.DesignationColumn, RegistrySchema.FioColumn]);

        var result = Action.Run(new ActionContext(db.Constructor, 1, collectionId, Now), Message());

        Assert.False(result.Ok);
        Assert.Contains("не подходит", result.Explanation, StringComparison.Ordinal);
        Assert.Contains("столбцов", result.Explanation, StringComparison.Ordinal);
    }

    [Fact]
    public void A_missing_collection_is_refused()
    {
        using var db = new TempDatabase();

        var result = Action.Run(new ActionContext(db.Constructor, 1, 4242, Now), Message());

        Assert.False(result.Ok);
        Assert.Contains("не найдена", result.Explanation, StringComparison.Ordinal);
    }

    // ── Форма действия ────────────────────────────────────────────────────────

    /// <summary>
    /// Действие объявляет ровно те поля, которые читает: поле, которого нет в списке, оболочке
    /// нечем заполнить, а прочитанное мимо списка молча потерялось бы.
    /// </summary>
    [Fact]
    public void Declared_fields_cover_what_the_action_reads()
    {
        Assert.Equal(ExtensionActionKind.Operation, Action.Kind);

        var keys = Action.Fields.Select(f => f.Key).ToList();

        Assert.Equal(keys.Count, keys.Distinct(StringComparer.Ordinal).Count());
        Assert.Contains("name",    keys);
        Assert.Contains("org",     keys);
        Assert.Contains("eskd",    keys);
        Assert.Contains("project", keys);

        // Обязательными объявлены ровно те, без которых регистрация не идет.
        var required = Action.Fields.Where(f => f.Required).Select(f => f.Key).ToList();
        Assert.Equal(["name", "org", "eskd", "project"], required);
    }
}
