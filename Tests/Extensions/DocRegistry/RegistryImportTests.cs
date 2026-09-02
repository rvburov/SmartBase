using SmartBase.Extensions.DocRegistry;

namespace SmartBase.Tests.Extensions.DocRegistry;

/// <summary>
/// Проверка списка номеров КД, загруженного файлом: что считается годной строкой, а что —
/// ошибкой. Чистая логика, без книги и без базы.
///
/// Главное отличие от ручной регистрации: номер тут НЕ ВЫДАЕТСЯ, он уже стоит в файле. Поэтому
/// проверяется не «свободен ли следующий», а «то ли это обозначение и не занято ли оно».
/// </summary>
public sealed class RegistryImportTests
{
    private static readonly string[] Orgs = ["АБВГ", "ГМБВ"];

    private static ImportRow Row(int line = 2,
                                 string name = "Втулка",
                                 string designation = "АБВГ.301121.001",
                                 string fio = "Иванов Иван Иванович",
                                 string project = "Проект",
                                 string accounting = "",
                                 string firstUse = "",
                                 string summary = "") =>
        new(line, name, designation, fio, project, accounting, firstUse, summary);

    private static ImportCheck Check(params ImportRow[] rows) =>
        RegistryImport.Check(rows, [], Orgs);

    private static string Problem(ImportCheck check) => Assert.Single(check.Problems).Text;

    // ── Годная строка ─────────────────────────────────────────────────────────

    [Fact]
    public void Filled_row_passes()
    {
        var check = Check(Row());

        Assert.True(check.Ok);
        Assert.Equal("АБВГ.301121.001", Assert.Single(check.Rows).Designation);
    }

    [Fact]
    public void Execution_is_a_designation_of_its_own()
    {
        var check = Check(Row(designation: "АБВГ.301121.001-01"));

        Assert.True(check.Ok);
        Assert.Equal("АБВГ.301121.001-01", Assert.Single(check.Rows).Designation);
    }

    /// <summary>Крайние пробелы снимаются со всех значений: в файле они не видны, а в журнале
    /// потом мешают и поиску, и сравнению.</summary>
    [Fact]
    public void Spaces_around_values_are_trimmed()
    {
        var check = Check(Row(name: "  Втулка  ", designation: " АБВГ.301121.001 ",
                              fio: " Иванов Иван Иванович ", project: " Проект "));

        var row = Assert.Single(check.Rows);
        Assert.Equal("Втулка",          row.Name);
        Assert.Equal("АБВГ.301121.001", row.Designation);
        Assert.Equal("Проект",          row.Project);
    }

    // ── Обозначение ───────────────────────────────────────────────────────────

    [Theory]
    [InlineData("АБВГ.301121.01")]     // две цифры номера
    [InlineData("АБВГ.301121.0011")]   // четыре
    [InlineData("АБВГ.30112.001")]     // пять цифр кода ЕСКД
    [InlineData("АБВ.301121.001")]     // три буквы кода организации
    [InlineData("АБВГ.301121.001-1")]  // одна цифра исполнения
    [InlineData("АБВГ.301121.001-001")]
    [InlineData("АБВГ.301121.001СБ")]
    [InlineData("АБВГ 301121 001")]
    public void Number_out_of_the_pattern_is_refused(string designation)
    {
        var check = Check(Row(designation: designation));

        Assert.False(check.Ok);
        Assert.Contains(RegistryImport.Sample, Problem(check));
    }

    [Fact]
    public void Zero_number_is_refused()
    {
        Assert.False(Check(Row(designation: "АБВГ.301121.000")).Ok);
    }

    [Fact]
    public void Zero_execution_is_refused()
    {
        Assert.False(Check(Row(designation: "АБВГ.301121.001-00")).Ok);
    }

    [Fact]
    public void Empty_designation_is_refused()
    {
        Assert.Contains(RegistrySchema.DesignationColumn, Problem(Check(Row(designation: ""))));
    }

    // ── Код организации ───────────────────────────────────────────────────────

    [Fact]
    public void Unknown_org_code_is_refused()
    {
        Assert.Contains("не из списка настроек", Problem(Check(Row(designation: "ЖЖЖЖ.301121.001"))));
    }

    /// <summary>Латиница вместо кириллицы: «AБBГ» выглядит как «АБВГ», но это другая серия.
    /// Ловится тем же сличением со списком настроек.</summary>
    [Fact]
    public void Latin_lookalike_is_refused()
    {
        var check = Check(Row(designation: "AБBГ.301121.001"));

        Assert.False(check.Ok);
        Assert.Contains("не из списка настроек", Problem(check));
    }

    /// <summary>Регистр кода организации приводится к написанию из настроек: журнал должен
    /// выглядеть одинаково, откуда бы строка в него ни пришла.</summary>
    [Fact]
    public void Org_code_is_written_as_in_the_settings()
    {
        var check = Check(Row(designation: "абвг.301121.001"));

        Assert.True(check.Ok);
        Assert.Equal("АБВГ.301121.001", Assert.Single(check.Rows).Designation);
    }

    // ── Повторы ───────────────────────────────────────────────────────────────

    [Fact]
    public void Designation_twice_in_the_file_is_refused()
    {
        var check = Check(Row(line: 2), Row(line: 7));

        Assert.Single(check.Rows);
        Assert.Contains("второй раз в файле, первый раз в строке 2", Problem(check));
        Assert.Equal(7, check.Problems[0].Line);
    }

    [Fact]
    public void Designation_already_in_the_journal_is_refused()
    {
        var check = RegistryImport.Check([Row()], ["АБВГ.301121.001"], Orgs);

        Assert.Empty(check.Rows);
        Assert.Contains("уже есть в журнале", Problem(check));
    }

    /// <summary>Исполнение и сам документ — разные обозначения: одно другому не мешает.</summary>
    [Fact]
    public void Execution_does_not_clash_with_its_document()
    {
        var check = RegistryImport.Check([Row(designation: "АБВГ.301121.001-01")],
                                         ["АБВГ.301121.001"], Orgs);

        Assert.True(check.Ok);
    }

    // ── Обязательные поля ─────────────────────────────────────────────────────

    [Fact]
    public void Empty_name_is_refused()
    {
        Assert.Contains(RegistrySchema.TitleColumn, Problem(Check(Row(name: "   "))));
    }

    [Fact]
    public void Empty_project_is_refused()
    {
        Assert.Contains(RegistrySchema.ProjectColumn, Problem(Check(Row(project: ""))));
    }

    [Fact]
    public void Empty_fio_is_refused()
    {
        Assert.Contains(RegistrySchema.FioColumn, Problem(Check(Row(fio: ""))));
    }

    /// <summary>Необязательные столбцы пустыми и остаются — строка от этого годной быть не
    /// перестает.</summary>
    [Fact]
    public void Optional_columns_may_stay_empty()
    {
        Assert.True(Check(Row(accounting: "", firstUse: "", summary: "")).Ok);
    }

    // ── ФИО ───────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("Иванов Иван Иванович")]
    [InlineData("Петрова-Водкина Анна Ивановна")]
    public void Full_name_passes(string fio) => Assert.True(RegistryImport.IsFullName(fio));

    [Theory]
    [InlineData("Иванов И.И.")]
    [InlineData("Иванов И. И.")]
    [InlineData("Иванов Иван")]
    [InlineData("Иванов")]
    [InlineData("Иванов Иван Иванович Иванов")]
    [InlineData("Иванов И Иванович")]
    public void Short_name_is_refused(string fio) => Assert.False(RegistryImport.IsFullName(fio));

    [Fact]
    public void Initials_are_named_as_the_reason()
    {
        Assert.Contains("полностью", Problem(Check(Row(fio: "Иванов И.И."))));
    }

    // ── Разбор целиком ────────────────────────────────────────────────────────

    /// <summary>Ошибки собираются по ВСЕМУ файлу, а не до первой: человек должен исправить все
    /// разом, а не открывать окно заново на каждую строку.</summary>
    [Fact]
    public void Every_bad_row_is_reported()
    {
        var check = Check(
            Row(line: 2),
            Row(line: 3, designation: "АБВГ.301121.02"),
            Row(line: 4, designation: "АБВГ.301121.003", project: ""),
            Row(line: 5, designation: "АБВГ.301121.004"));

        Assert.Equal([2, 5], check.Rows.Select(r => r.Line));
        Assert.Equal(2, check.Problems.Count);
        Assert.Equal([3, 4], check.Problems.Select(p => p.Line));
    }

    /// <summary>Одна ошибка — файл не пишется целиком: строки без ошибок в Rows остаются, но
    /// окно смотрит на Ok, а не на их число.</summary>
    [Fact]
    public void One_problem_makes_the_whole_file_unwritable()
    {
        var check = Check(Row(line: 2), Row(line: 3, designation: "мусор"));

        Assert.False(check.Ok);
        Assert.Single(check.Rows);
    }

    [Fact]
    public void Problem_reads_as_a_line_of_the_report()
    {
        var check = Check(Row(line: 7, project: ""));

        Assert.StartsWith("Строка 7: ", check.Problems[0].ToString());
    }
}
