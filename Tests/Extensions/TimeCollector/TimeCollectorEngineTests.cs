using SmartBase.Extensions.TimeCollector;

namespace SmartBase.Tests.Extensions.TimeCollector;

/// <summary>
/// Чистая логика расширения «Сводная трудоёмкость»: разбор имени файла расчёта, отбор файлов
/// в папке, порядок строк свода, план раскраски и скрытие пустых колонок. Всё это — порт
/// макроса <c>TimeCollector</c>, поэтому тесты заодно фиксируют, где поведение намеренно
/// отличается от исходника (см. docs/developer/features/extensions.md).
/// </summary>
public sealed class TimeCollectorEngineTests
{
    // Колонки свода: 1 — служебный номер строки источника, дальше как в шапке листа «Таблица».
    private const int ColRNum = 1, ColName = 2, ColDeno = 3, ColNorm = 4, ColEfio = 6, ColProj = 7;

    private static TimeRow Row(string deno, double? norm, string date, string proj = "П",
                               string name = "изделие", string efio = "Иванов") =>
        new(RNum: 0, Name: name, Deno: deno, Norm: norm, NormText: norm is null ? "н/д" : "",
            Date: DateTime.Parse(date), Efio: efio, Proj: proj, SourcePath: @"C:\р.xlsm");

    // ── Разбор имени файла ────────────────────────────────────────────────────

    [Fact]
    public void FileName_Is_Split_Into_Project_Date_And_Person()
    {
        var parsed = TimeCollectorEngine.ParseFileName("Изделие-А_2025.03.14_Иванов И.И..xlsm");

        Assert.NotNull(parsed);
        Assert.Equal("Изделие-А", parsed.Proj);
        Assert.Equal(new DateTime(2025, 3, 14), parsed.Date);
        Assert.Equal("Иванов И.И.", parsed.Efio);
    }

    // ФИО берётся после ПОСЛЕДНЕГО подчёркивания, проект — до первого: ровно так делит имя
    // макрос, поэтому лишние подчёркивания в середине попадают в дату и имя не разбирается.
    [Fact]
    public void Person_Is_Taken_After_The_Last_Underscore()
    {
        var parsed = TimeCollectorEngine.ParseFileName("П_2025.01.02_Петров_Сидоров.xlsm");

        Assert.NotNull(parsed);
        Assert.Equal("П", parsed.Proj);
        Assert.Equal("Сидоров", parsed.Efio);
    }

    [Theory]
    [InlineData("отчёт.xlsm")]                       // нет подчёркиваний
    [InlineData("Проект_2025.03.14.xlsm")]           // нет второго подчёркивания
    [InlineData("Проект_март_Иванов.xlsm")]          // дата не разбирается
    [InlineData("_2025.03.14_Иванов.xlsm")]          // пустой проект
    [InlineData("Проект_2025.03.14_.xlsm")]          // пустое ФИО
    [InlineData("")]
    public void Bad_File_Names_Are_Rejected(string fileName) =>
        Assert.Null(TimeCollectorEngine.ParseFileName(fileName));

    // Основной вид даты — «ГГГГ.ММ.ДД» (его собирает CalculationLink); «ДД.ММ.ГГГГ» принимается
    // на случай ручного переименования — по длине первой части видно, что это год.
    [Fact]
    public void Both_Date_Layouts_Are_Understood()
    {
        Assert.Equal(new DateTime(2025, 3, 14),
                     TimeCollectorEngine.ParseFileName("П_2025.03.14_И.xlsm")!.Date);
        Assert.Equal(new DateTime(2025, 3, 14),
                     TimeCollectorEngine.ParseFileName("П_14.03.2025_И.xlsm")!.Date);
    }

    // ── Отбор файлов в папке ──────────────────────────────────────────────────

    [Fact]
    public void Only_Xlsm_Without_Dollar_Sign_Is_Taken()
    {
        using var dir = new TempDir();
        foreach (var name in new[]
                 {
                     "П_2025.01.01_Иванов.xlsm",     // берём
                     "П_2025.01.02_Петров.XLSM",     // берём: расширение без учёта регистра
                     "~$П_2025.01.01_Иванов.xlsm",   // служебный файл открытой книги — мимо
                     "П_2025.01.03_Сидоров.xlsx",    // не .xlsm — мимо
                     "заметки.txt",
                 })
            File.WriteAllText(Path.Combine(dir.Path, name), "");

        var found = TimeCollectorEngine.FindCalcFiles(dir.Path, recursive: false)
                                       .Select(Path.GetFileName)
                                       .ToList();

        Assert.Equal(2, found.Count);
        Assert.Contains("П_2025.01.01_Иванов.xlsm", found);
        Assert.Contains("П_2025.01.02_Петров.XLSM", found);
    }

    [Fact]
    public void Subfolders_Are_Visited_Only_When_Recursive()
    {
        using var dir = new TempDir();
        var nested = Directory.CreateDirectory(Path.Combine(dir.Path, "2025"));
        File.WriteAllText(Path.Combine(dir.Path, "П_2025.01.01_Иванов.xlsm"), "");
        File.WriteAllText(Path.Combine(nested.FullName, "П_2025.02.01_Петров.xlsm"), "");

        Assert.Single(TimeCollectorEngine.FindCalcFiles(dir.Path, recursive: false));
        Assert.Equal(2, TimeCollectorEngine.FindCalcFiles(dir.Path, recursive: true).Count);
    }

    // ── Порядок строк ─────────────────────────────────────────────────────────

    // Три ключа макроса: обозначение ↑, дата ↓, наименование ↑. В отличие от исходника
    // (там Header:=xlYes оставлял первую строку данных на месте) сортируются ВСЕ строки.
    [Fact]
    public void Rows_Are_Ordered_By_Designation_Then_Newest_Date_Then_Name()
    {
        var sorted = TimeCollectorEngine.Sort(
        [
            Row("BBB", 1, "2025-01-01", name: "первая"),
            Row("AAA", 1, "2025-01-01", name: "старая"),
            Row("AAA", 1, "2025-05-01", name: "новая"),
            Row("AAA", 1, "2025-05-01", name: "а-новая"),
        ]);

        Assert.Equal(["а-новая", "новая", "старая", "первая"], sorted.Select(r => r.Name));
    }

    // ── Раскраска ─────────────────────────────────────────────────────────────

    [Fact]
    public void Fill_Alternates_On_Every_Change_Of_Designation()
    {
        var plan = TimeCollectorEngine.PlanColors(
        [
            Row("AAA", 1, "2025-01-01"),
            Row("AAA", 1, "2025-01-01"),
            Row("BBB", 1, "2025-01-01"),
            Row("CCC", 1, "2025-01-01"),
        ]);

        Assert.Equal(TimeCollectorEngine.RowFill.Yellow, plan[0].Fill);
        Assert.Equal(TimeCollectorEngine.RowFill.Yellow, plan[1].Fill);
        Assert.Equal(TimeCollectorEngine.RowFill.White,  plan[2].Fill);
        Assert.Equal(TimeCollectorEngine.RowFill.Yellow, plan[3].Fill);
    }

    [Fact]
    public void Rows_Without_Designation_Stay_White()
    {
        var plan = TimeCollectorEngine.PlanColors(
        [
            Row("", 1, "2025-01-01"),
            Row("", 1, "2025-01-01"),
        ]);

        Assert.All(plan, p => Assert.Equal(TimeCollectorEngine.RowFill.White, p.Fill));
        Assert.All(plan, p => Assert.False(p.NormRed));
    }

    // Одно обозначение, разная трудоёмкость, совпали проект и дата — красными обе строки
    // целиком: расчёт одного и того же изделия в одном проекте разошёлся сам с собой.
    [Fact]
    public void Same_Project_And_Date_With_Different_Norm_Paints_Both_Rows_Red()
    {
        var plan = TimeCollectorEngine.PlanColors(
        [
            Row("AAA", 10, "2025-01-01", proj: "П"),
            Row("AAA", 20, "2025-01-01", proj: "П"),
        ]);

        Assert.Equal(TimeCollectorEngine.RowFill.Red, plan[0].Fill);
        Assert.Equal(TimeCollectorEngine.RowFill.Red, plan[1].Fill);
        Assert.All(plan, p => Assert.False(p.NormRed));
    }

    // Проект или дата разные — красной становится только ячейка «Тр-ть», сама строка
    // сохраняет цвет своей группы.
    [Fact]
    public void Different_Project_With_Different_Norm_Paints_Only_The_Norm_Cell()
    {
        var plan = TimeCollectorEngine.PlanColors(
        [
            Row("AAA", 10, "2025-01-01", proj: "П-1"),
            Row("AAA", 20, "2025-01-01", proj: "П-2"),
        ]);

        Assert.All(plan, p => Assert.Equal(TimeCollectorEngine.RowFill.Yellow, p.Fill));
        Assert.All(plan, p => Assert.True(p.NormRed));
    }

    [Fact]
    public void Equal_Norms_Are_Not_Marked()
    {
        var plan = TimeCollectorEngine.PlanColors(
        [
            Row("AAA", 10, "2025-01-01"),
            Row("AAA", 10, "2025-01-01"),
        ]);

        Assert.All(plan, p => Assert.Equal(TimeCollectorEngine.RowFill.Yellow, p.Fill));
        Assert.All(plan, p => Assert.False(p.NormRed));
    }

    [Fact]
    public void Empty_Plan_For_Empty_Table() =>
        Assert.Empty(TimeCollectorEngine.PlanColors([]));

    // ── Пустые колонки ────────────────────────────────────────────────────────

    // Служебная колонка со номером строки источника прячется всегда — её содержимое нужно
    // макросу шаблона, а не человеку.
    [Fact]
    public void Service_Column_Is_Always_Hidden()
    {
        var hidden = TimeCollectorEngine.HiddenColumns([Row("AAA", 1, "2025-01-01")]);

        Assert.Contains(ColRNum, hidden);
        Assert.DoesNotContain(ColName, hidden);
        Assert.DoesNotContain(ColDeno, hidden);
        Assert.DoesNotContain(ColNorm, hidden);
    }

    [Fact]
    public void Columns_Empty_In_Every_Row_Are_Hidden()
    {
        var hidden = TimeCollectorEngine.HiddenColumns(
        [
            Row("AAA", 1, "2025-01-01", proj: "", efio: ""),
            Row("BBB", 1, "2025-01-01", proj: "", efio: ""),
        ]);

        Assert.Contains(ColProj, hidden);
        Assert.Contains(ColEfio, hidden);
        Assert.DoesNotContain(ColDeno, hidden);
    }

    [Fact]
    public void One_Filled_Value_Keeps_The_Column_Visible()
    {
        var hidden = TimeCollectorEngine.HiddenColumns(
        [
            Row("AAA", 1, "2025-01-01", proj: ""),
            Row("BBB", 1, "2025-01-01", proj: "П"),
        ]);

        Assert.DoesNotContain(ColProj, hidden);
    }
}
