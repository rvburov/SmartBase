using SmartBase.Core.Models;
using SmartBase.Extensions.DocRegistry;

namespace SmartBase.Tests.Extensions.DocRegistry;

/// <summary>
/// Сама регистрация: что попадает в коллекцию и какой номер получает документ. Прогон идет по
/// настоящей базе — от подбора номера по столбцу «Обозначение» до разложенных значений.
/// </summary>
public sealed class DocRegistrarTests
{
    private static readonly DateTime Now = new(2026, 8, 27, 18, 23, 0);

    private static RegistrationInput Input(string eskd = "301121", string name = "Втулка") =>
        new(Name: name, Org: "АБВГ", Eskd: eskd, Fio: "Иванов Иван Иванович",
            Project: "Проект", Accounting: "Учтено", FirstUse: "Изделие 1", Summary: "Кратко");

    // ── Что попадает в коллекцию ──────────────────────────────────────────────

    [Fact]
    public void All_eight_columns_are_filled()
    {
        using var db = new TempDatabase();
        var id    = JournalStand.Create(db.Constructor);
        var shape = JournalStand.Shape(db.Constructor, id);

        var done   = DocRegistrar.Register(db.Constructor, shape, Input(), Now);
        var record = db.Constructor.GetRecord(done.RecordId);
        var values = db.Constructor.GetFieldValues(done.RecordId);

        Assert.NotNull(record);
        Assert.Equal("Втулка",          record.Title);                       // 1. Наименование
        Assert.Equal("АБВГ.301121.001", values[shape.DesignationFieldId]);   // 2. Обозначение
        Assert.Equal("Иванов Иван Иванович", values[shape.FioFieldId]);      // 3. ФИО
        Assert.True(values.ContainsKey(shape.DateFieldId));                  // 4. Дата регистрации
        Assert.Equal("Проект",          values[shape.ProjectFieldId]);       // 5. Проект
        Assert.Equal("Учтено",          values[shape.AccountingFieldId]);    // 6. Отметка учета
        Assert.Equal("Изделие 1",       values[shape.FirstUseFieldId]);      // 7. Первичное применение
        Assert.Equal("Кратко",          values[shape.SummaryFieldId]);       // 8. Краткая характеристика
    }

    /// <summary>Дата регистрации — момент нажатия кнопки, а не «весь день»: важно, когда именно
    /// выдан номер. Пишется в каноне колонки «дата», иначе в ячейке была бы ISO-строка.</summary>
    [Fact]
    public void Registration_date_keeps_the_time()
    {
        using var db = new TempDatabase();
        var id    = JournalStand.Create(db.Constructor);
        var shape = JournalStand.Shape(db.Constructor, id);

        var done   = DocRegistrar.Register(db.Constructor, shape, Input(), Now);
        var stored = db.Constructor.GetFieldValues(done.RecordId)[shape.DateFieldId];

        Assert.True(DateValue.TryParseStorage(stored, out var value), $"Не канон даты: «{stored}»");
        Assert.False(value.AllDay);
        Assert.Equal(Now, value.Start);
    }

    /// <summary>Текстовая колонка вместо «даты» — редкость, но тогда в ячейке должно оказаться
    /// то, что человек и прочитает, а не ISO-канон.</summary>
    [Fact]
    public void Text_date_column_gets_a_readable_stamp() =>
        Assert.Equal("27.08.2026 18:23", DocRegistrar.StampFor("text", Now));

    /// <summary>Незаполненные примечания не заводят пустых строк значений: пустое значение и
    /// отсутствие строки для таблицы одно и то же.</summary>
    [Fact]
    public void Empty_optional_fields_are_not_written()
    {
        using var db = new TempDatabase();
        var id    = JournalStand.Create(db.Constructor);
        var shape = JournalStand.Shape(db.Constructor, id);

        var input = new RegistrationInput("Втулка", "АБВГ", "301121", "Иванов И. И.", "Проект");
        var done  = DocRegistrar.Register(db.Constructor, shape, input, Now);

        var values = db.Constructor.GetFieldValues(done.RecordId);

        // Проект в этот перечень не входит: он обязателен и пустым сюда не доходит.
        Assert.Equal("Проект", values[shape.ProjectFieldId]);

        Assert.False(values.ContainsKey(shape.AccountingFieldId));
        Assert.False(values.ContainsKey(shape.FirstUseFieldId));
        Assert.False(values.ContainsKey(shape.SummaryFieldId));
    }

    // ── Нумерация ─────────────────────────────────────────────────────────────

    [Fact]
    public void First_document_of_a_series_gets_001()
    {
        using var db = new TempDatabase();
        var id    = JournalStand.Create(db.Constructor);
        var shape = JournalStand.Shape(db.Constructor, id);

        var done = DocRegistrar.Register(db.Constructor, shape, Input(), Now);

        Assert.Equal(1, done.Number);
        Assert.Equal("АБВГ.301121.001", done.Designation);
    }

    /// <summary>Пример из задания: в коллекции заняты 001 и 003 — документ получает 002.</summary>
    [Fact]
    public void Free_number_in_the_middle_is_taken()
    {
        using var db = new TempDatabase();
        var id    = JournalStand.Create(db.Constructor);
        var shape = JournalStand.Shape(db.Constructor, id);

        JournalStand.AddRow(db.Constructor, shape, "Было", "АБВГ.301121.001");
        JournalStand.AddRow(db.Constructor, shape, "Было", "АБВГ.301121.003");

        var done = DocRegistrar.Register(db.Constructor, shape, Input(), Now);

        Assert.Equal("АБВГ.301121.002", done.Designation);
    }

    [Fact]
    public void Numbers_run_on_within_one_series()
    {
        using var db = new TempDatabase();
        var id    = JournalStand.Create(db.Constructor);
        var shape = JournalStand.Shape(db.Constructor, id);

        var first  = DocRegistrar.Register(db.Constructor, shape, Input(), Now);
        var second = DocRegistrar.Register(db.Constructor, shape, Input(), Now);

        Assert.Equal("АБВГ.301121.001", first.Designation);
        Assert.Equal("АБВГ.301121.002", second.Designation);
    }

    /// <summary>У каждого кода ЕСКД нумерация своя — соседняя серия счет не сдвигает.</summary>
    [Fact]
    public void Another_eskd_code_starts_from_001_again()
    {
        using var db = new TempDatabase();
        var id    = JournalStand.Create(db.Constructor);
        var shape = JournalStand.Shape(db.Constructor, id);

        DocRegistrar.Register(db.Constructor, shape, Input("301121"), Now);
        DocRegistrar.Register(db.Constructor, shape, Input("301121"), Now);
        var other = DocRegistrar.Register(db.Constructor, shape, Input("723611"), Now);

        Assert.Equal("АБВГ.723611.001", other.Designation);
    }

    [Fact]
    public void Exhausted_series_is_refused()
    {
        using var db = new TempDatabase();
        var id    = JournalStand.Create(db.Constructor);
        var shape = JournalStand.Shape(db.Constructor, id);

        for (var n = Designation.MinNumber; n <= Designation.MaxNumber; n++)
            JournalStand.AddRow(db.Constructor, shape, "Было", Designation.Format("АБВГ", "301121", n));

        var error = Assert.Throws<DocRegistryException>(
            () => DocRegistrar.Register(db.Constructor, shape, Input(), Now));

        Assert.Contains("АБВГ.301121.", error.Message);
    }

    // ── Исполнения ────────────────────────────────────────────────────────────

    /// <summary>Исполнение — часть обозначения: строка та же, к номеру приписывается «-NN».</summary>
    [Fact]
    public void Execution_is_appended_to_the_number()
    {
        using var db = new TempDatabase();
        var id    = JournalStand.Create(db.Constructor);
        var shape = JournalStand.Shape(db.Constructor, id);

        var done = DocRegistrar.Register(db.Constructor, shape, Input() with { Execution = 7 }, Now);

        Assert.Equal("АБВГ.301121.001-07", done.Designation);
        Assert.Equal(1, done.Number);
        Assert.Equal(7, done.Execution);
    }

    /// <summary>
    /// Названный номер расширение берет как есть — не подбирает первый свободный. На этом стоит
    /// работа окна: исполнения одного документа заводят подряд, и после «001-01» номер 001 занят,
    /// а «001-02» должно уйти к нему же, а не к следующему номеру.
    /// </summary>
    [Fact]
    public void Named_number_is_taken_as_is()
    {
        using var db = new TempDatabase();
        var id    = JournalStand.Create(db.Constructor);
        var shape = JournalStand.Shape(db.Constructor, id);

        var first  = DocRegistrar.Register(db.Constructor, shape, Input() with { Execution = 1 }, Now);
        var second = DocRegistrar.Register(
            db.Constructor, shape, Input() with { Execution = 2, Number = first.Number }, Now);

        Assert.Equal("АБВГ.301121.001-01", first.Designation);
        Assert.Equal("АБВГ.301121.001-02", second.Designation);
    }

    /// <summary>Занятое исполнение — отказ: две строки с одним обозначением заводятся только
    /// осознанно, а спрашивает об этом окно.</summary>
    [Fact]
    public void Taken_execution_is_refused_without_confirmation()
    {
        using var db = new TempDatabase();
        var id    = JournalStand.Create(db.Constructor);
        var shape = JournalStand.Shape(db.Constructor, id);

        JournalStand.AddRow(db.Constructor, shape, "Было", "АБВГ.301121.002-05");

        var error = Assert.Throws<DocRegistryException>(() => DocRegistrar.Register(
            db.Constructor, shape, Input() with { Execution = 5, Number = 2 }, Now));

        Assert.Contains("АБВГ.301121.002-05", error.Message);
        Assert.Single(db.Constructor.GetRecords(id));   // следов отказ не оставил
    }

    [Fact]
    public void Confirmed_duplicate_execution_is_written()
    {
        using var db = new TempDatabase();
        var id    = JournalStand.Create(db.Constructor);
        var shape = JournalStand.Shape(db.Constructor, id);

        JournalStand.AddRow(db.Constructor, shape, "Было", "АБВГ.301121.002-05");

        var done = DocRegistrar.Register(
            db.Constructor, shape,
            Input() with { Execution = 5, Number = 2, ConfirmedDuplicate = true }, Now);

        Assert.Equal("АБВГ.301121.002-05", done.Designation);
    }

    /// <summary>Свободное исполнение того же номера отказа не вызывает: занят номер с ДРУГИМ
    /// исполнением, а это другое обозначение.</summary>
    [Fact]
    public void Free_execution_of_a_taken_number_is_written()
    {
        using var db = new TempDatabase();
        var id    = JournalStand.Create(db.Constructor);
        var shape = JournalStand.Shape(db.Constructor, id);

        JournalStand.AddRow(db.Constructor, shape, "Было", "АБВГ.301121.002-05");

        var done = DocRegistrar.Register(
            db.Constructor, shape, Input() with { Execution = 6, Number = 2 }, Now);

        Assert.Equal("АБВГ.301121.002-06", done.Designation);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(100)]
    public void Impossible_execution_is_refused(int execution)
    {
        using var db = new TempDatabase();
        var id    = JournalStand.Create(db.Constructor);
        var shape = JournalStand.Shape(db.Constructor, id);

        Assert.Throws<DocRegistryException>(() => DocRegistrar.Register(
            db.Constructor, shape, Input() with { Execution = execution }, Now));

        Assert.Empty(db.Constructor.GetRecords(id));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1000)]
    public void Impossible_named_number_is_refused(int number)
    {
        using var db = new TempDatabase();
        var id    = JournalStand.Create(db.Constructor);
        var shape = JournalStand.Shape(db.Constructor, id);

        Assert.Throws<DocRegistryException>(() => DocRegistrar.Register(
            db.Constructor, shape, Input() with { Execution = 1, Number = number }, Now));

        Assert.Empty(db.Constructor.GetRecords(id));
    }

    /// <summary>Строка исполнения заполняется теми же столбцами, что и любая другая.</summary>
    [Fact]
    public void Execution_row_is_filled_like_any_other()
    {
        using var db = new TempDatabase();
        var id    = JournalStand.Create(db.Constructor);
        var shape = JournalStand.Shape(db.Constructor, id);

        var done   = DocRegistrar.Register(db.Constructor, shape, Input() with { Execution = 1 }, Now);
        var record = db.Constructor.GetRecord(done.RecordId);
        var values = db.Constructor.GetFieldValues(done.RecordId);

        Assert.Equal("Втулка",             record!.Title);
        Assert.Equal("АБВГ.301121.001-01", values[shape.DesignationFieldId]);
        Assert.Equal("Проект",             values[shape.ProjectFieldId]);
        Assert.True(values.ContainsKey(shape.DateFieldId));
    }

    // ── Порядок записей ───────────────────────────────────────────────────────

    /// <summary>Журнал ведется по порядку выдачи номеров, поэтому запись дописывается В КОНЕЦ.
    /// Тест держит это поведение: расширение просит конец через <c>AddRecordAfter(…, 0)</c>.</summary>
    [Fact]
    public void Records_are_appended_to_the_end()
    {
        using var db = new TempDatabase();
        var id    = JournalStand.Create(db.Constructor);
        var shape = JournalStand.Shape(db.Constructor, id);

        JournalStand.AddRow(db.Constructor, shape, "Первая", "АБВГ.723611.001");
        DocRegistrar.Register(db.Constructor, shape, Input(name: "Вторая"), Now);
        DocRegistrar.Register(db.Constructor, shape, Input(name: "Третья"), Now);

        var titles = db.Constructor.GetRecordTitlesOrdered(id).Select(r => r.Title).ToList();

        Assert.Equal(["Первая", "Вторая", "Третья"], titles);
    }

    // ── Проверка ввода ────────────────────────────────────────────────────────

    [Fact]
    public void Empty_name_is_refused()
    {
        using var db = new TempDatabase();
        var id    = JournalStand.Create(db.Constructor);
        var shape = JournalStand.Shape(db.Constructor, id);

        Assert.Throws<DocRegistryException>(() => DocRegistrar.Register(
            db.Constructor, shape, Input() with { Name = "   " }, Now));
    }

    [Fact]
    public void Empty_organisation_is_refused()
    {
        using var db = new TempDatabase();
        var id    = JournalStand.Create(db.Constructor);
        var shape = JournalStand.Shape(db.Constructor, id);

        Assert.Throws<DocRegistryException>(() => DocRegistrar.Register(
            db.Constructor, shape, Input() with { Org = "" }, Now));
    }

    /// <summary>Проект — обязательное поле окна, и запись держит это условие сама: окно не
    /// единственный вход в регистрацию.</summary>
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Empty_project_is_refused(string project)
    {
        using var db = new TempDatabase();
        var id    = JournalStand.Create(db.Constructor);
        var shape = JournalStand.Shape(db.Constructor, id);

        Assert.Throws<DocRegistryException>(() => DocRegistrar.Register(
            db.Constructor, shape, Input() with { Project = project }, Now));

        Assert.Empty(db.Constructor.GetRecords(id));
    }

    [Theory]
    [InlineData("30112")]     // пять цифр
    [InlineData("3011211")]   // семь цифр
    [InlineData("30112a")]
    [InlineData("")]
    public void Incomplete_eskd_code_is_refused(string eskd)
    {
        using var db = new TempDatabase();
        var id    = JournalStand.Create(db.Constructor);
        var shape = JournalStand.Shape(db.Constructor, id);

        Assert.Throws<DocRegistryException>(
            () => DocRegistrar.Register(db.Constructor, shape, Input(eskd), Now));
    }

    /// <summary>Отказ не оставляет следов: полузаполненная строка в журнале хуже, чем никакой —
    /// по ней выдали бы номер повторно.</summary>
    [Fact]
    public void Refused_registration_leaves_no_record()
    {
        using var db = new TempDatabase();
        var id    = JournalStand.Create(db.Constructor);
        var shape = JournalStand.Shape(db.Constructor, id);

        Assert.Throws<DocRegistryException>(
            () => DocRegistrar.Register(db.Constructor, shape, Input("30112"), Now));

        Assert.Empty(db.Constructor.GetRecords(id));
    }

    // ── Предпросмотр номера ───────────────────────────────────────────────────

    [Fact]
    public void Preview_shows_the_number_that_will_be_given()
    {
        using var db = new TempDatabase();
        var id    = JournalStand.Create(db.Constructor);
        var shape = JournalStand.Shape(db.Constructor, id);

        JournalStand.AddRow(db.Constructor, shape, "Было", "АБВГ.301121.001");

        var designations = DocRegistrar.Designations(db.Constructor, shape);
        var preview      = DocRegistrar.NextNumber(designations, "АБВГ", "301121");
        var done         = DocRegistrar.Register(db.Constructor, shape, Input(), Now);

        Assert.Equal(preview, done.Number);
    }
}
