using SmartBase.Extensions.DocRegistry;

namespace SmartBase.Tests.Extensions.DocRegistry;

/// <summary>
/// Разбор списка номеров КД, загруженного файлом: какой шаблон обозначения считается годным и
/// какой номер по нему выдается. Чистая логика, без книги и без базы.
///
/// Главное: обозначение в файле — ШАБЛОН, а не готовый номер. Человек пишет код организации и
/// код ЕСКД, на месте номера ставит ХХХ (и ХХ на месте исполнения), а номер выдает расширение —
/// тот же первый свободный в серии, что и при ручной регистрации.
/// </summary>
public sealed class RegistryImportTests
{
    private static readonly string[] Orgs = ["АБВГ", "ГМБВ"];

    private static ImportRow Row(int line = 2,
                                 string name = "Втулка",
                                 string designation = "АБВГ.301121.ХХХ",
                                 string fio = "Иванов Иван Иванович",
                                 string project = "Проект",
                                 string accounting = "",
                                 string firstUse = "",
                                 string summary = "") =>
        new(line, name, designation, fio, project, accounting, firstUse, summary);

    private static ImportCheck Check(params ImportRow[] rows) =>
        RegistryImport.Check(rows, [], Orgs);

    private static ImportCheck Against(string[] journal, params ImportRow[] rows) =>
        RegistryImport.Check(rows, journal, Orgs);

    private static string Problem(ImportCheck check) => Assert.Single(check.Problems).Text;

    private static string Given(ImportCheck check)
    {
        Assert.True(check.Ok, check.Problems.Count > 0 ? check.Problems[0].ToString() : "");
        return Assert.Single(check.Rows).Designation;
    }

    // ── Выдача номера ─────────────────────────────────────────────────────────

    [Fact]
    public void Empty_series_starts_at_the_first_number()
    {
        Assert.Equal("АБВГ.301121.001", Given(Check(Row())));
    }

    [Fact]
    public void Number_is_the_first_free_in_the_journal()
    {
        var journal = new[] { "АБВГ.301121.001", "АБВГ.301121.003" };

        Assert.Equal("АБВГ.301121.002", Given(Against(journal, Row())));
    }

    /// <summary>Своя нумерация у каждой серии: чужой код ЕСКД занятости не создает.</summary>
    [Fact]
    public void Other_series_does_not_take_the_number()
    {
        Assert.Equal("АБВГ.301121.001", Given(Against(["АБВГ.723611.001"], Row())));
    }

    /// <summary>Новому номеру исполнение не назначают: номер только выдается, и приписывать
    /// исполнение не к чему. В сообщении обе правильные строки.</summary>
    [Fact]
    public void New_number_with_an_execution_mark_is_refused()
    {
        var problem = Problem(Check(Row(designation: "АБВГ.301121.ХХХ-ХХ")));

        Assert.Contains("новому номеру исполнение не назначают", problem);
        Assert.Contains("«АБВГ.301121.ХХХ»",                     problem);
        Assert.Contains("«АБВГ.301121.012-ХХ»",                  problem);
    }

    /// <summary>Тот же отказ и с готовым исполнением при метке номера.</summary>
    [Fact]
    public void New_number_with_a_ready_execution_is_refused()
    {
        Assert.Contains("новому номеру исполнение не назначают",
                        Problem(Check(Row(designation: "АБВГ.301121.ХХХ-01"))));
    }

    // ── Названный номер: исполнение существующего документа ───────────────────

    /// <summary>Номер называет человек — так исполнение приписывают к УЖЕ ВЫДАННОМУ документу.
    /// Исполнение все равно выдается: первое свободное у этого номера.</summary>
    [Fact]
    public void Named_number_takes_the_first_execution()
    {
        Assert.Equal("АБВГ.301121.014-01",
                     Given(Against(["АБВГ.301121.014"], Row(designation: "АБВГ.301121.014-ХХ"))));
    }

    /// <summary>Исполнения одного документа идут подряд по ходу файла.</summary>
    [Fact]
    public void Executions_of_one_document_go_one_after_another()
    {
        var check = Against(["АБВГ.301121.014"],
                            Row(line: 2, designation: "АБВГ.301121.014-ХХ"),
                            Row(line: 3, designation: "АБВГ.301121.014-ХХ"),
                            Row(line: 4, designation: "АБВГ.301121.014-ХХ"));

        Assert.Equal(["АБВГ.301121.014-01", "АБВГ.301121.014-02", "АБВГ.301121.014-03"],
                     check.Rows.Select(r => r.Designation));
    }

    /// <summary>Исполнения, уже выданные в журнале, продолжаются со следующего свободного —
    /// дырки заполняются так же, как у номеров.</summary>
    [Fact]
    public void Journal_executions_are_continued()
    {
        var journal = new[] { "АБВГ.301121.014", "АБВГ.301121.014-01", "АБВГ.301121.014-03" };

        Assert.Equal("АБВГ.301121.014-02",
                     Given(Against(journal, Row(designation: "АБВГ.301121.014-ХХ"))));
    }

    /// <summary>Документа с таким номером в журнале может не быть вовсе — старые документы туда
    /// часто не заносились, и запрет означал бы, что их исполнения внести нельзя.</summary>
    [Fact]
    public void Number_missing_from_the_journal_still_gets_the_first_execution()
    {
        Assert.Equal("АБВГ.301121.014-01", Given(Check(Row(designation: "АБВГ.301121.014-ХХ"))));
    }

    /// <summary>Названный номер считается занятым: строке с меткой он уже не достанется, иначе
    /// два документа получили бы один номер.</summary>
    [Fact]
    public void Named_number_is_taken_from_the_marks()
    {
        var check = Check(Row(line: 2, designation: "АБВГ.301121.001-ХХ"),
                          Row(line: 3, designation: "АБВГ.301121.ХХХ"));

        Assert.Equal(["АБВГ.301121.001-01", "АБВГ.301121.002"],
                     check.Rows.Select(r => r.Designation));
    }

    [Fact]
    public void Named_number_with_all_executions_taken_is_refused()
    {
        var journal = Enumerable.Range(1, 99).Select(e => $"АБВГ.301121.014-{e:D2}").ToArray();

        Assert.Contains("заняты все исполнения 01–99",
                        Problem(Against(journal, Row(designation: "АБВГ.301121.014-ХХ"))));
    }

    [Fact]
    public void Zero_number_is_refused()
    {
        Assert.Contains("001 первый номер серии",
                        Problem(Check(Row(designation: "АБВГ.301121.000-ХХ"))));
    }

    /// <summary>Номера раздаются подряд ПО ХОДУ ФАЙЛА: журнал между строками не перечитывается,
    /// иначе два документа одной серии получили бы один номер.</summary>
    [Fact]
    public void Numbers_are_given_one_after_another()
    {
        var check = Check(Row(line: 2), Row(line: 3), Row(line: 4));

        Assert.Equal(["АБВГ.301121.001", "АБВГ.301121.002", "АБВГ.301121.003"],
                     check.Rows.Select(r => r.Designation));
    }

    /// <summary>Каждая строка — свой документ, даже если строки одинаковы.</summary>
    [Fact]
    public void Every_row_is_a_document_of_its_own()
    {
        var check = Check(Row(line: 2, designation: "АБВГ.301121.ХХХ"),
                          Row(line: 3, designation: "АБВГ.301121.ХХХ"));

        Assert.Equal(["АБВГ.301121.001", "АБВГ.301121.002"],
                     check.Rows.Select(r => r.Designation));
    }

    /// <summary>Разные серии в одном файле считаются порознь.</summary>
    [Fact]
    public void Series_are_counted_apart()
    {
        var check = Check(Row(line: 2, designation: "АБВГ.301121.ХХХ"),
                          Row(line: 3, designation: "АБВГ.723611.ХХХ"),
                          Row(line: 4, designation: "ГМБВ.301121.ХХХ"),
                          Row(line: 5, designation: "АБВГ.301121.ХХХ"));

        Assert.Equal(["АБВГ.301121.001", "АБВГ.723611.001", "ГМБВ.301121.001", "АБВГ.301121.002"],
                     check.Rows.Select(r => r.Designation));
    }

    [Fact]
    public void Exhausted_series_is_refused()
    {
        var journal = Enumerable.Range(1, 999).Select(n => $"АБВГ.301121.{n:D3}").ToArray();

        Assert.Contains("заняты все номера 001–999", Problem(Against(journal, Row())));
    }

    // ── Шаблон ────────────────────────────────────────────────────────────────

    /// <summary>Икс принимается и латинский, и кириллический, в любом регистре: на клавиатуре они
    /// на одной клавише, и человек наберет тот, который у него включен.</summary>
    [Theory]
    [InlineData("АБВГ.301121.ХХХ")]
    [InlineData("АБВГ.301121.ххх")]
    [InlineData("АБВГ.301121.XXX")]
    [InlineData("АБВГ.301121.xxx")]
    [InlineData("АБВГ.301121.Xхx")]
    public void Mark_is_taken_in_any_alphabet_and_case(string designation)
    {
        Assert.Equal("АБВГ.301121.001", Given(Check(Row(designation: designation))));
    }

    [Fact]
    public void Spaces_around_the_template_are_trimmed()
    {
        Assert.Equal("АБВГ.301121.001", Given(Check(Row(designation: "  АБВГ.301121.ХХХ  "))));
    }

    /// <summary>Номер сам по себе — ошибка: номера выдает журнал. В сообщении сразу обе
    /// правильные строки — и на новый документ, и на исполнение этого номера.</summary>
    [Fact]
    public void Number_without_an_execution_mark_is_refused()
    {
        var problem = Problem(Check(Row(designation: "АБВГ.301121.012")));

        Assert.Contains("номер выдает расширение", problem);
        Assert.Contains("«АБВГ.301121.ХХХ»",       problem);
        Assert.Contains("«АБВГ.301121.012-ХХ»",    problem);
    }

    /// <summary>Написанное исполнение — тоже ошибка, но сказано об этом отдельно: номер документа
    /// в подсказке сохраняется, менять человек хотел не его.</summary>
    [Fact]
    public void Ready_execution_is_refused_with_a_fixture()
    {
        var problem = Problem(Check(Row(designation: "АБВГ.301121.012-01")));

        Assert.Contains("исполнение выдает расширение", problem);
        Assert.Contains("«АБВГ.301121.012-ХХ»",         problem);
    }

    [Theory]
    [InlineData("АБВГ.301121.ХХ")]      // две метки вместо трех
    [InlineData("АБВГ.301121.ХХХХ")]    // четыре
    [InlineData("АБВГ.301121.ХХХ-Х")]   // одна метка исполнения
    [InlineData("АБВГ.301121.ХХХ-ХХХ")]
    [InlineData("АБВГ.30112.ХХХ")]      // пять цифр кода ЕСКД
    [InlineData("АБВ.301121.ХХХ")]      // три буквы кода организации
    [InlineData("АБВГ.301121.ХХХСБ")]
    [InlineData("АБВГ.301121-ХХХ")]
    [InlineData("АБВГ 301121 ХХХ")]
    [InlineData("АБВГ.301121.")]
    public void Anything_but_the_template_is_refused(string designation)
    {
        Assert.Contains(RegistryImport.Sample, Problem(Check(Row(designation: designation))));
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
        Assert.Contains("не из списка настроек", Problem(Check(Row(designation: "ЖЖЖЖ.301121.ХХХ"))));
    }

    /// <summary>Латиница вместо кириллицы: «AБBГ» выглядит как «АБВГ», но это другая серия.
    /// Ловится тем же сличением со списком настроек.</summary>
    [Fact]
    public void Latin_lookalike_is_refused()
    {
        Assert.Contains("не из списка настроек", Problem(Check(Row(designation: "AБBГ.301121.ХХХ"))));
    }

    /// <summary>Регистр кода организации приводится к написанию из настроек: журнал должен
    /// выглядеть одинаково, откуда бы строка в него ни пришла.</summary>
    [Fact]
    public void Org_code_is_written_as_in_the_settings()
    {
        Assert.Equal("АБВГ.301121.001", Given(Check(Row(designation: "абвг.301121.ХХХ"))));
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

    /// <summary>Крайние пробелы снимаются со всех значений: в файле они не видны, а в журнале
    /// потом мешают и поиску, и сравнению.</summary>
    [Fact]
    public void Spaces_around_values_are_trimmed()
    {
        var check = Check(Row(name: "  Втулка  ", fio: " Иванов Иван Иванович ", project: " Проект "));
        var row   = Assert.Single(check.Rows);

        Assert.Equal("Втулка", row.Name);
        Assert.Equal("Проект", row.Project);
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
            Row(line: 3, designation: "АБВГ.301121.012-01"),
            Row(line: 4, project: ""),
            Row(line: 5));

        Assert.Equal([2, 5], check.Rows.Select(r => r.Line));
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
        Assert.StartsWith("Строка 7: ", Check(Row(line: 7, project: "")).Problems[0].ToString());
    }
}
