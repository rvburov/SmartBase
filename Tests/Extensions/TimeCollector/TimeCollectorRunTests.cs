using System.IO.Compression;
using System.Xml;
using ClosedXML.Excel;
using SmartBase.Extensions.TimeCollector;

namespace SmartBase.Tests.Extensions.TimeCollector;

/// <summary>
/// Сквозной прогон расширения «Сводная трудоемкость»: на диске создаются настоящие .xlsm —
/// шаблон с листом «Таблица» и несколько расчетов с листом «Расшифровка», — после чего
/// <see cref="TimeCollectorEngine.Run"/> собирает свод и результат читается обратно.
///
/// Тест держит вместе все, чего не видно на чистой логике: чтение через ExcelDataReader,
/// запись через ClosedXML (очистка листа, объединения шапки, заливка диапазонами, скрытые
/// колонки, автофильтр, имя ALL_DATA, формулы HYPERLINK) и разбор проблемных файлов —
/// битого и с отсутствующим листом.
/// </summary>
public sealed class TimeCollectorRunTests : IDisposable
{
    private readonly string _root     = Path.Combine(Path.GetTempPath(), $"sbtc_{Guid.NewGuid():N}");
    private readonly string _calc;
    private readonly string _out;
    private readonly string _template;

    public TimeCollectorRunTests()
    {
        _calc     = Path.Combine(_root, "Расчеты");
        _out      = Path.Combine(_root, "Свод");
        _template = Path.Combine(_root, "шаблон.xlsm");

        Directory.CreateDirectory(_calc);
        Directory.CreateDirectory(_out);
        CreateTemplate(_template);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
        catch { /* best-effort */ }
    }

    // ── Стенд ─────────────────────────────────────────────────────────────────

    // Шаблон: лист «Таблица» с чужими данными и заливкой прошлого прогона — их обязана
    // снести очистка, иначе старые строки останутся ниже нового свода.
    private static void CreateTemplate(string path)
    {
        using var wb = new XLWorkbook();
        var ws = wb.AddWorksheet(TimeCollectorEngine.TargetSheet);
        ws.Cell(1, 2).Value = "старая шапка";
        ws.Range(1, 2, 2, 2).Merge();
        ws.Cell(4, 2).Value = "строка прошлого прогона";
        ws.Cell(4, 2).Style.Fill.BackgroundColor = XLColor.FromArgb(0, 128, 0);
        ws.Cell(99, 2).Value = "далекий хвост";
        ws.Column(2).Width = 40;                                   // ширина шаблона обязана уцелеть
        wb.AddWorksheet("Настройки").Cell(1, 2).Value = @"\\старый\путь";
        wb.SaveAs(path);
    }

    // Расчет: лист «Расшифровка», где колонки листа A…K = F1…F11 запроса макроса.
    // В свод попадают только строки с заполненными иерархией (B) и трудоемкостью (G).
    private void CreateCalc(string fileName, params (string Hierarchy, string Name, string Deno, object? Norm)[] rows)
    {
        using var wb = new XLWorkbook();
        var ws = wb.AddWorksheet("Расшифровка");
        for (var i = 0; i < rows.Length; i++)
        {
            var (hierarchy, name, deno, norm) = rows[i];
            var row = i + 1;
            ws.Cell(row, 2).Value = hierarchy;
            ws.Cell(row, 3).Value = name;
            ws.Cell(row, 4).Value = deno;
            switch (norm)
            {
                case null: break;
                case double d: ws.Cell(row, 7).Value = d; break;
                case string s: ws.Cell(row, 7).Value = s; break;
            }
        }
        wb.SaveAs(Path.Combine(_calc, fileName));
    }

    private CollectResult Run(bool recursive = false, bool fixText = true) =>
        TimeCollectorEngine.Run(_calc, recursive, _template, _out,
                                progress: null, CancellationToken.None, fixText);

    // ── Сквозной прогон ───────────────────────────────────────────────────────

    [Fact]
    public void Rows_From_All_Calculations_Land_In_The_Template_Sheet()
    {
        CreateCalc("Изделие_2025.03.14_Иванов.xlsm",
            ("Изделие", "Блок питания", "АБВГ.111 111.001", 12.5),
            ("",        "Пайка",        "",                 3.0),      // нет иерархии — мимо
            ("1.",      "Плата",        "АБВГ.222 222.002", 4.25));
        CreateCalc("Изделие_2025.01.09_Петров.xlsm",
            ("Изделие", "Блок питания", "АБВГ.111111.001",  9.0));     // то же обозначение, старее

        var result = Run();

        Assert.Equal(3, result.RowCount);
        Assert.Equal(2, result.ProcessedFiles);
        Assert.Empty(result.Issues);
        Assert.True(File.Exists(result.OutputPath));

        using var wb = new XLWorkbook(result.OutputPath);
        var ws = wb.Worksheet(TimeCollectorEngine.TargetSheet);

        // Шапка — на своих местах, подписи объединены с ячейкой под ними.
        Assert.Equal("Наименование", ws.Cell(1, 2).GetString());
        Assert.Equal("Ссылка",       ws.Cell(1, 8).GetString());
        Assert.True(ws.Cell(1, 2).IsMerged());

        // Пробелы в обозначении убраны, поэтому оба «Блока питания» — одна группа, и свежая
        // дата идет первой: обозначение ↑, дата ↓, наименование ↑.
        Assert.Equal("АБВГ.111111.001", ws.Cell(4, 3).GetString());
        Assert.Equal(12.5,              ws.Cell(4, 4).GetDouble());
        Assert.Equal(new DateTime(2025, 3, 14), ws.Cell(4, 5).GetDateTime());
        Assert.Equal("Иванов",          ws.Cell(4, 6).GetString());
        Assert.Equal("Изделие",         ws.Cell(4, 7).GetString());
        Assert.Equal(9.0,               ws.Cell(5, 4).GetDouble());
        Assert.Equal("АБВГ.222222.002", ws.Cell(6, 3).GetString());

        // Старые данные шаблона снесены — ниже свода пусто.
        Assert.True(ws.Cell(99, 2).IsEmpty());

        // Служебная колонка скрыта, но номер строки источника в ней есть: по нему макрос
        // шаблона открывает расчет (строка 1 листа «Расшифровка» → RNUM 0).
        Assert.True(ws.Column(1).IsHidden);
        Assert.Equal(0, ws.Cell(4, 1).GetDouble());
        Assert.Equal(2, ws.Cell(6, 1).GetDouble());

        // Ссылка: и метка для макроса, и формула перехода без макросов. Адрес БЕЗ апострофов:
        // для HYPERLINK это не ссылка, а текст, и апостроф уходит в имя файла — Excel перестает
        // узнавать «[путь]Лист» и достраивает путь относительно книги свода.
        Assert.Contains("HYPERLINK(\"[", ws.Cell(4, 8).FormulaA1);
        Assert.Contains("]Расшифровка!A1", ws.Cell(4, 8).FormulaA1);
        Assert.Contains(">>>", ws.Cell(4, 8).FormulaA1);
        Assert.Equal(">>>", ws.Cell(4, 8).CachedValue.ToString());

        // Автофильтр и именованный диапазон — на строке-шапке фильтра и всех данных.
        Assert.True(ws.AutoFilter.IsEnabled);
        Assert.True(wb.DefinedNames.TryGetValue("ALL_DATA", out var named));
        Assert.Contains("$A$3", named.RefersTo);

        // Лист «Настройки» — не дело расширения: путь к папке живет в настройках приложения,
        // а содержимое шаблона остается нетронутым.
        Assert.Equal(@"\\старый\путь", wb.Worksheet("Настройки").Cell(1, 2).GetString());
    }

    // ── Точность трудоемкости ─────────────────────────────────────────────────

    // Трудоемкость в расчете посчитана формулой вроде «=СУММ(G3:G11)+H12+H96+…», и ее кэш
    // оседает в файле с двоичной погрешностью: 6,1000000000000005 вместо 6,1 — ДРУГОЕ число,
    // у него другие биты. .NET показывает все 17 цифр, и ломалось это в трех местах: показ
    // в коллекции, значение в листе Excel и — главное — сравнение для красной подсветки,
    // которое считается один раз для обоих приемников.
    //
    // Значения ниже — с настоящего свода. Погрешность у них разной глубины: у первого она
    // на 17-й значащей цифре (такую прячет и сам Excel), у второго — на 15-й, за пределом
    // Excel: сложение двух десятков слагаемых накапливает ошибку глубже, чем одна операция.
    [Theory]
    [InlineData("12.999999999999998", 13.0)]
    [InlineData("0.850000000000001",  0.85)]
    [InlineData("237.07499999999996", 237.075)]
    [InlineData("1092.7799999999997", 1092.78)]
    public void Binary_Noise_Of_Excel_Formulas_Does_Not_Reach_The_Table(string planted, double expected)
    {
        const string file = "П_2025.03.14_Иванов.xlsm";
        CreateCalc(file,
            ("Изделие", "Стенка в сборе", "АБВГ.100200.300", 1.5),
            ("1.",      "Стенка в сборе", "АБВГ.100200.300", expected));
        PlantBinaryNoise(file, was: "1.5", becomes: planted);

        // Страховка стенда: погрешность обязана доехать до файла, иначе тест ничего не проверяет.
        Assert.Contains(planted, SheetXml(Path.Combine(_calc, file)));

        var table = TimeCollectorEngine.Collect(
            _calc, recursive: false, progress: null, CancellationToken.None);

        Assert.All(table.Rows, row => Assert.Equal(expected, row.Norm));

        // Одинаковые значения больше не расходятся — красить нечего.
        Assert.All(table.Paints, paint => Assert.False(paint.NormRed));
        Assert.DoesNotContain(table.Paints, paint => paint.Fill == TimeCollectorEngine.RowFill.Red);
    }

    // Положить погрешность в расчет через ClosedXML нельзя: на записи он округляет число так же,
    // как Excel (15 значащих цифр), и 6,1000000000000005 превращается в 6,1. Поэтому значение
    // подменяется прямо в XML готового файла — ровно так оно и выглядит в настоящем расчете,
    // где ячейку считала формула и Excel закэшировал результат.
    private void PlantBinaryNoise(string fileName, string was, string becomes)
    {
        var path = Path.Combine(_calc, fileName);
        using var zip = ZipFile.Open(path, ZipArchiveMode.Update);
        var entry = zip.GetEntry(SheetPart) ?? throw new InvalidOperationException("нет " + SheetPart);

        string xml;
        using (var r = new StreamReader(entry.Open())) xml = r.ReadToEnd();

        var planted = xml.Replace($">{was}<", $">{becomes}<");
        Assert.NotEqual(xml, planted);                     // стенд обязан сработать

        entry.Delete();
        using var w = new StreamWriter(zip.CreateEntry(SheetPart).Open());
        w.Write(planted);
    }

    // ── Один алфавит ──────────────────────────────────────────────────────────

    // Расчеты набивают в двух раскладках вперемешку, и в русском слове оказывается латинская
    // буква-двойник. На глаз не видно, но для программы строки разные. Разбор идет ПО СЛОВУ,
    // и здесь собраны все ветки этого решения разом.
    [Fact]
    public void Mixed_Layout_Words_Are_Brought_To_One_Alphabet()
    {
        CreateCalc("П_2025.03.14_Иванов.xlsm",
            // Русское слово с латинской «c»: «п» двойника не имеет и решает за все слово.
            ("Изделие", "Корпуc",  "К1", 1.0),
            // Английское слово с кириллической «о»: «R», «t» решают в обратную сторону.
            ("1.",      "Rоtor",   "К2", 2.0),
            // Однозначные буквы ОБОИХ алфавитов («Г» и «d») — не трогаем.
            ("2.",      "ГОСТd",   "К3", 3.0),
            // Слово целиком из двойников: три кириллических против одной латинской «P».
            ("3.",      "СТОP",    "К4", 4.0),
            // Тоже целиком из двойников, но поровну — решать нечем.
            ("4.",      "ОP",      "К5", 5.0));

        var result = Run();
        using var wb = new XLWorkbook(result.OutputPath);
        var ws = wb.Worksheet(TimeCollectorEngine.TargetSheet);

        var byDeno = Enumerable.Range(4, 5).ToDictionary(r => ws.Cell(r, 3).GetString(), r => r);
        string Name(string deno) => ws.Cell(byDeno[deno], 2).GetString();

        Assert.Equal("Корпус", Name("К1"));                 // c → с
        Assert.Equal("Rotor",  Name("К2"));                 // о → o
        Assert.Equal("ГОСТd",  Name("К3"));                 // не тронуто
        Assert.Equal("СТОР",   Name("К4"));                 // P → Р по большинству
        Assert.Equal("ОP",     Name("К5"));                 // поровну — не тронуто

        Assert.Equal(3, result.Text.Values);                // исправлено три значения
        Assert.Equal(2, result.Text.Mixed);                 // два слова оставлены как есть
    }

    // Ради этого все и делается: обозначение — ключ сортировки и раскраски, и сравнивается оно
    // побайтно (StringComparison.Ordinal в PlanColors). Одна буква из чужой раскладки разносила
    // одно и то же обозначение по двум группам с разной заливкой.
    [Fact]
    public void One_Designation_Typed_Two_Ways_Becomes_One_Group()
    {
        // Второе обозначение отличается ровно одной буквой: латинская «A» вместо кириллической.
        CreateCalc("П_2025.03.14_Иванов.xlsm", ("Изделие", "Первая",  "АБВГ.100200.300", 7.0));
        CreateCalc("П_2025.03.15_Иванов.xlsm", ("Изделие", "Вторая",  "AБВГ.100200.300", 7.0));

        var fixedRun = Run();
        using (var wb = new XLWorkbook(fixedRun.OutputPath))
        {
            var ws = wb.Worksheet(TimeCollectorEngine.TargetSheet);
            Assert.Equal(ws.Cell(4, 3).GetString(), ws.Cell(5, 3).GetString());
            Assert.Equal(ws.Cell(4, 2).Style.Fill.BackgroundColor,
                         ws.Cell(5, 2).Style.Fill.BackgroundColor);

            // Цифры не тронуты: «0»/«О» — тоже двойники, но подмена уничтожила бы номер.
            Assert.EndsWith(".100200.300", ws.Cell(4, 3).GetString());
        }

        // Замена в обозначении названа поименно — ее должно быть видно в журнале.
        Assert.Single(fixedRun.Text.Designations);
        Assert.Contains("АБВГ.100200.300", fixedRun.Text.Designations[0]);

        // Со снятой галочкой — две разные строки и, значит, две группы.
        var asIs = Run(fixText: false);
        using (var wb = new XLWorkbook(asIs.OutputPath))
        {
            var ws = wb.Worksheet(TimeCollectorEngine.TargetSheet);
            Assert.NotEqual(ws.Cell(4, 3).GetString(), ws.Cell(5, 3).GetString());
            Assert.NotEqual(ws.Cell(4, 2).Style.Fill.BackgroundColor,
                            ws.Cell(5, 2).Style.Fill.BackgroundColor);
        }
        Assert.Equal(0, asIs.Text.Values);
        Assert.Empty(asIs.Text.Designations);
    }

    // Выгрузка в коллекцию идет через тот же Collect, что и выгрузка в Excel: правка текста
    // общая для обоих приемников и по умолчанию включена.
    [Fact]
    public void Collect_Cleans_Text_By_Default_For_Both_Destinations()
    {
        CreateCalc("П_2025.03.14_Иванов.xlsm", ("Изделие", "Корпуc", "К1", 1.0));

        var table = TimeCollectorEngine.Collect(
            _calc, recursive: false, progress: null, CancellationToken.None);

        Assert.Equal("Корпус", table.Rows[0].Name);
        Assert.Equal(1, table.Text.Values);
    }

    // «ФИО» и «Проект» — части имени файла расчета, и макрос шаблона собирает имя обратно
    // из этих колонок. Исправленная буква сделала бы файл ненаходимым, поэтому раскладку
    // в них не трогают, как бы она ни выглядела.
    [Fact]
    public void Project_And_Author_Keep_The_Spelling_Of_The_File_Name()
    {
        // «Cтанок» с латинской «C» и «Ивaнов» с латинской «a».
        CreateCalc("Cтанок_2025.03.14_Ивaнов.xlsm", ("Изделие", "Деталь", "К1", 1.0));

        var result = Run();
        using var wb = new XLWorkbook(result.OutputPath);
        var ws = wb.Worksheet(TimeCollectorEngine.TargetSheet);

        Assert.Equal("Ивaнов", ws.Cell(4, 6).GetString());
        Assert.Equal("Cтанок", ws.Cell(4, 7).GetString());

        // И в счетчик правок они не попадают: чужая раскладка здесь не считается ошибкой.
        Assert.Equal(0, result.Text.Values);
    }

    // Рамка у Excel принадлежит ЯЧЕЙКЕ, а не диапазону: у объединенной пары B1:B2 нижний край
    // рисует именно B2. Строки 2 и 3 писались вовсе без ячеек — шапка выходила с оборванными
    // рамками, хотя макрос обводил весь A1:H3.
    [Fact]
    public void Header_Is_Bordered_In_All_Three_Rows()
    {
        CreateCalc("П_2025.03.14_Иванов.xlsm", ("Изделие", "А", "АА", 1.0));

        using var wb = new XLWorkbook(Run().OutputPath);
        var ws = wb.Worksheet(TimeCollectorEngine.TargetSheet);

        // Шапка занимает строки 1-3, свод начинается с 4-й.
        for (var row = 1; row <= 3; row++)
            for (var col = 1; col <= 8; col++)
                Assert.NotEqual(XLBorderStyleValues.None,
                                ws.Cell(row, col).Style.Border.BottomBorder);
    }

    [Fact]
    public void Designation_Groups_Alternate_And_Diverging_Norms_Turn_Red()
    {
        // Один проект, одна дата, одно обозначение, разная трудоемкость → обе строки красные.
        CreateCalc("П_2025.03.14_Иванов.xlsm",
            ("Изделие", "А", "АА", 10.0),
            ("1.",      "Б", "АА", 20.0),
            ("2.",      "В", "ББ", 1.0));

        var result = Run();
        Assert.Equal(3, result.RowCount);

        using var wb = new XLWorkbook(result.OutputPath);
        var ws = wb.Worksheet(TimeCollectorEngine.TargetSheet);

        var red = XLColor.FromArgb(255, 0, 0);
        Assert.Equal(red, ws.Cell(4, 2).Style.Fill.BackgroundColor);
        Assert.Equal(red, ws.Cell(5, 2).Style.Fill.BackgroundColor);

        // Следующая группа — своя заливка, не красная.
        Assert.NotEqual(red, ws.Cell(6, 2).Style.Fill.BackgroundColor);
    }

    // Готовый свод — сам законный шаблон для следующего прогона: в нем уже есть автофильтр,
    // имя ALL_DATA, скрытая колонка, объединенная шапка и заливка. Очистка обязана снять все
    // это, иначе второй прогон упадет на повторном ALL_DATA или оставит чужие строки.
    [Fact]
    public void Previous_Result_Can_Serve_As_The_Template_For_The_Next_Run()
    {
        CreateCalc("П_2025.03.14_Иванов.xlsm",
            ("Изделие", "А", "АА", 1.0),
            ("1.",      "Б", "ББ", 2.0),
            ("2.",      "В", "ВВ", 3.0));
        var first = Run();
        Assert.Equal(3, first.RowCount);

        // Второй прогон по одному файлу, но уже поверх первого результата.
        File.Delete(Path.Combine(_calc, "П_2025.03.14_Иванов.xlsm"));
        CreateCalc("П_2025.04.01_Петров.xlsm", ("Изделие", "Г", "ГГ", 4.0));

        var second = TimeCollectorEngine.Run(_calc, recursive: false, first.OutputPath, _out,
                                             progress: null, CancellationToken.None);

        Assert.Equal(1, second.RowCount);
        Assert.NotEqual(first.OutputPath, second.OutputPath);

        using var wb = new XLWorkbook(second.OutputPath);
        var ws = wb.Worksheet(TimeCollectorEngine.TargetSheet);

        Assert.Equal("ГГ", ws.Cell(4, 3).GetString());
        Assert.True(ws.Cell(5, 3).IsEmpty());                      // строки прошлого свода снесены
        Assert.True(ws.Cell(5, 2).Style.Fill.BackgroundColor.ColorType == XLColorType.Theme ||
                    ws.Cell(5, 2).Style.Fill.PatternType == XLFillPatternValues.None);
        Assert.True(ws.AutoFilter.IsEnabled);
        Assert.Single(wb.DefinedNames, n => n.Name == "ALL_DATA");
    }

    // Имя результата содержит время с точностью до минуты, а прогон идет минутами: второй
    // запуск в ту же минуту обязан получить свое имя, а не затереть первый свод.
    [Fact]
    public void Second_Run_In_The_Same_Minute_Does_Not_Overwrite_The_First()
    {
        CreateCalc("П_2025.03.14_Иванов.xlsm", ("Изделие", "А", "АА", 1.0));

        var first  = Run();
        var second = Run();

        Assert.NotEqual(first.OutputPath, second.OutputPath);
        Assert.True(File.Exists(first.OutputPath));
        Assert.True(File.Exists(second.OutputPath));
        Assert.EndsWith(" (2).xlsm", second.OutputPath);
    }

    // ── Проблемные файлы ──────────────────────────────────────────────────────

    [Fact]
    public void Broken_And_Unsuitable_Files_Are_Reported_But_Do_Not_Stop_The_Run()
    {
        CreateCalc("Хороший_2025.03.14_Иванов.xlsm", ("Изделие", "А", "АА", 1.0));

        // Битый файл: расширение то, содержимое — не Excel.
        File.WriteAllText(Path.Combine(_calc, "Битый_2025.03.14_Иванов.xlsm"), "не excel");

        // Есть книга, но нет нужного листа.
        using (var wb = new XLWorkbook())
        {
            wb.AddWorksheet("Другое").Cell(1, 1).Value = "х";
            wb.SaveAs(Path.Combine(_calc, "Безлиста_2025.03.14_Иванов.xlsm"));
        }

        // Имя не по образцу — разобрать проект, дату и ФИО неоткуда.
        CreateCalc("отчет.xlsm", ("Изделие", "А", "АА", 1.0));

        var result = Run();

        Assert.Equal(1, result.ProcessedFiles);
        Assert.Equal(4, result.TotalFiles);
        Assert.Equal(1, result.RowCount);

        Assert.Equal(FileStatus.Corrupted,
                     result.Issues.Single(i => i.FileName.StartsWith("Битый")).Status);
        Assert.Equal(FileStatus.NoSheet,
                     result.Issues.Single(i => i.FileName.StartsWith("Безлиста")).Status);
        Assert.Equal(FileStatus.BadName,
                     result.Issues.Single(i => i.FileName.StartsWith("отчет")).Status);
    }

    // Книга, открытая другим пользователем, читается с диска: терять ее строки хуже, чем
    // показать данные на момент последнего сохранения. В журнал она попадает пометкой.
    // «Открыта» — это подтвержденный факт: рядом лежит «~$»-файл И книгу кто-то держит
    // (здесь — открытый поток теста, как это делает Excel).
    [Fact]
    public void File_Opened_By_Someone_Else_Is_Read_And_Flagged()
    {
        var path = Path.Combine(_calc, "Занятый_2025.03.14_Иванов.xlsm");
        CreateCalc("Занятый_2025.03.14_Иванов.xlsm", ("Изделие", "А", "АА", 7.0));
        File.WriteAllBytes(Path.Combine(_calc, "~$Занятый_2025.03.14_Иванов.xlsm"),
                           [7, .. "SIDOROV"u8]);

        using var held = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        var result = Run();

        Assert.Equal(1, result.RowCount);
        var issue = Assert.Single(result.Issues);
        Assert.Equal(FileStatus.OpenedByOther, issue.Status);
        Assert.Contains("SIDOROV", issue.Message);
        Assert.True(issue.IsProblem());
    }

    // Тот же «~$»-файл, но книгу никто не держит: маркер остался от аварийно закрытого Excel
    // или приехал вместе с копией папки. Замечанием это не считается — данные полны и свежи,
    // в журнале такие файлы идут отдельной строкой со счетчиком.
    [Fact]
    public void Stale_Owner_Marker_Is_Noted_But_Not_Counted_As_A_Problem()
    {
        CreateCalc("Свободный_2025.03.14_Иванов.xlsm", ("Изделие", "А", "АА", 7.0));
        File.WriteAllBytes(Path.Combine(_calc, "~$Свободный_2025.03.14_Иванов.xlsm"),
                           [7, .. "SIDOROV"u8]);

        var result = Run();

        Assert.Equal(1, result.RowCount);
        var issue = Assert.Single(result.Issues);
        Assert.Equal(FileStatus.StaleLock, issue.Status);
        Assert.Contains("SIDOROV", issue.Message);
        Assert.False(issue.IsProblem());
    }

    // Управляющий символ в ячейке расчета (остатки форматирования из выгрузок CAD) — не повод
    // потерять прогон: XmlWriter на таком знаке бросает ArgumentException, и свод не писался
    // вовсе — за минуты до конца, потеряв все прочитанное. Знак меняется на пробел, остальное
    // доезжает до листа как есть.
    [Fact]
    public void Control_Character_In_A_Cell_Does_Not_Break_The_Run()
    {
        CreateCalc("Грязный_2025.03.14_Иванов.xlsm",
                   ("Изделие", "Корпус" + (char)0x01 + "01", "АБВГ.333333.003", 3.0));

        var result = Run();

        Assert.Equal(1, result.RowCount);
        using var wb = new XLWorkbook(result.OutputPath);
        var ws = wb.Worksheet(TimeCollectorEngine.TargetSheet);
        Assert.Equal("Корпус 01", ws.Cell(4, 2).GetString());          // первая строка свода, «Наименование»
    }

    // Данные расчетов бывают «злыми»: переносы строк и спецсимволы XML в наименовании, эмодзи
    // (пара суррогатов), переполнение в трудоемкости. Проверяем не глазами, а как это делает
    // Excel: лист обязан читаться XML-парсером, а книга — открываться целиком. Переполнение
    // особенно коварно: «Infinity» в <v> оставляет XML синтаксически целым, и Excel молча
    // объявляет книгу нечитаемой, выбрасывая лист при «восстановлении».
    [Fact]
    public void Nasty_Cell_Values_Keep_The_Workbook_Readable()
    {
        CreateCalc("Злой_2025.03.14_Иванов.xlsm",
            ("Изделие", "Корпус\nвторая строка", "АБВГ.111111.001", 1.0),
            ("Изделие", "A & B < C > D \" \' ]]>", "АБВГ.222222.002", 2.0),
            ("Изделие", "Корпус \U0001F600 узел", "АБВГ.333333.003", 3.0),
            ("Изделие", "Переполнение",            "АБВГ.444444.004", "1e400"));

        var result = Run();
        Assert.Equal(4, result.RowCount);

        using (var zip = ZipFile.OpenRead(result.OutputPath))
        {
            var sheet = zip.Entries.First(e => e.FullName.StartsWith("xl/worksheets/sheet"));
            using var reader = XmlReader.Create(sheet.Open());
            while (reader.Read()) { }                       // битый лист бросит XmlException
        }

        using var wb = new XLWorkbook(result.OutputPath);   // а нечитаемую книгу не откроет ClosedXML
        var ws = wb.Worksheet(TimeCollectorEngine.TargetSheet);

        // Порядок свода — по обозначению, значит переполнение легло последней строкой.
        Assert.Equal("#ЧИСЛО!", ws.Cell(7, 4).GetString());
    }

    // Цепочка вычислений перечисляет ячейки с формулами поименно, а свод меняет их набор
    // целиком. Оставленная от шаблона, она не совпадает с листом — и Excel объявляет книгу
    // нечитаемой. В результат она не попадает вместе со своими объявлениями: часть, заявленная
    // в [Content_Types].xml и rels, но отсутствующая, ломает книгу ничуть не меньше.
    [Fact]
    public void Calc_Chain_Of_The_Template_Does_Not_Reach_The_Result()
    {
        AddCalcChain(_template);
        CreateCalc("Изделие_2025.03.14_Иванов.xlsm", ("Изделие", "Блок", "АБВГ.777777.007", 1.0));

        var result = Run();

        using var zip = ZipFile.OpenRead(result.OutputPath);
        Assert.DoesNotContain(zip.Entries, e => e.FullName == "xl/calcChain.xml");
        Assert.DoesNotContain("calcChain", ReadEntry(zip, "[Content_Types].xml"));
        Assert.DoesNotContain("calcChain", ReadEntry(zip, "xl/_rels/workbook.xml.rels"));
    }

    // Дописать в готовый .xlsm цепочку вычислений и оба объявления о ней — так выглядит
    // книга, которую сохранил Excel.
    private static void AddCalcChain(string path)
    {
        using var zip = ZipFile.Open(path, ZipArchiveMode.Update);

        using (var w = new StreamWriter(zip.CreateEntry("xl/calcChain.xml").Open()))
            w.Write("""<?xml version="1.0" encoding="UTF-8" standalone="yes"?><calcChain xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main"><c r="H4" i="1"/></calcChain>""");

        Patch(zip, "[Content_Types].xml", "</Types>",
              """<Override PartName="/xl/calcChain.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.calcChain+xml"/></Types>""");
        Patch(zip, "xl/_rels/workbook.xml.rels", "</Relationships>",
              """<Relationship Id="rId777" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/calcChain" Target="calcChain.xml"/></Relationships>""");
    }

    private static void Patch(ZipArchive zip, string part, string anchor, string replacement)
    {
        var entry = zip.GetEntry(part)!;
        string xml;
        using (var r = new StreamReader(entry.Open())) xml = r.ReadToEnd();

        using var s = entry.Open();
        s.SetLength(0);
        using var w = new StreamWriter(s);
        w.Write(xml.Replace(anchor, replacement));
    }

    private static string ReadEntry(ZipArchive zip, string part)
    {
        using var r = new StreamReader(zip.GetEntry(part)!.Open());
        return r.ReadToEnd();
    }

    [Fact]
    public void Nothing_To_Collect_Is_A_Friendly_Error()
    {
        CreateCalc("Пустой_2025.03.14_Иванов.xlsm", ("", "А", "АА", 1.0));

        var ex = Assert.Throws<TimeCollectException>(() => Run());
        Assert.Contains("подходящих строк", ex.Message);
        Assert.Empty(Directory.GetFiles(_out));
    }

    [Fact]
    public void Empty_Folder_Is_A_Friendly_Error() =>
        Assert.Contains("*.xlsm", Assert.Throws<TimeCollectException>(() => Run()).Message);

    [Fact]
    public void Subfolders_Are_Collected_Only_When_Recursive()
    {
        CreateCalc("Верх_2025.03.14_Иванов.xlsm", ("Изделие", "А", "АА", 1.0));

        var nested = Directory.CreateDirectory(Path.Combine(_calc, "2024"));
        using (var wb = new XLWorkbook())
        {
            var ws = wb.AddWorksheet("Расшифровка");
            ws.Cell(1, 2).Value = "Изделие";
            ws.Cell(1, 3).Value = "Б";
            ws.Cell(1, 4).Value = "ББ";
            ws.Cell(1, 7).Value = 2.0;
            wb.SaveAs(Path.Combine(nested.FullName, "Низ_2024.12.01_Петров.xlsm"));
        }

        Assert.Equal(1, Run().RowCount);
        Assert.Equal(2, Run(recursive: true).RowCount);
    }

    // ── Проблемный шаблон ─────────────────────────────────────────────────────

    // В шаблоне остается мусорное определенное имя со ссылкой на другую книгу (префикс «[2]»
    // плюс имя листа с пробелами без апострофов). Excel такую книгу открывает, ClosedXML —
    // нет, и раньше на этом падал весь прогон. Заводятся такие имена сами, от копирования
    // листа между книгами, а чинить их некому: часть скрыта и не видна в Диспетчере имен.
    [Fact]
    public void Template_With_An_Unparsable_Defined_Name_Is_Repaired_And_The_Run_Succeeds()
    {
        const string junk = "Порядок видов [2]работ!$A$1:$A$11";

        // Второе имя — с пустой ссылкой: оно ломается иначе, чем первое (не разбором, а
        // отказом принять пустую строку), и раньше срывало бы ремонт всей книги.
        AddDefinedNames(_template, ("МУСОР", junk), ("ПУСТОЕ", ""));

        // Страховка стенда: без ремонта здесь падает само открытие книги.
        Assert.ThrowsAny<Exception>(() => new XLWorkbook(_template));

        CreateCalc("П_2025.03.14_Иванов.xlsm", ("Изделие", "А", "АА", 1.0));

        var result = Run();

        Assert.Equal(1, result.RowCount);
        Assert.True(File.Exists(result.OutputPath));

        // Оригинал шаблона не тронут — ремонт шел на временной копии.
        Assert.Contains(junk, WorkbookXml(_template));

        // В своде мусорного имени нет, а рабочее имя диапазона на месте.
        using var wb = new XLWorkbook(result.OutputPath);
        Assert.DoesNotContain(wb.DefinedNames, n => n.Name == "МУСОР");
        Assert.DoesNotContain(wb.DefinedNames, n => n.Name == "ПУСТОЕ");
        Assert.Single(wb.DefinedNames, n => n.Name == "ALL_DATA");
    }

    // Ремонтом лечится только непарсящееся имя. Все остальное обязано дойти до пользователя
    // понятным текстом с именем файла, а не превратиться в загадочный сбой.
    [Fact]
    public void Broken_Template_Is_A_Friendly_Error()
    {
        File.WriteAllText(_template, "не excel");
        CreateCalc("П_2025.03.14_Иванов.xlsm", ("Изделие", "А", "АА", 1.0));

        var ex = Assert.Throws<TimeCollectException>(() => Run());

        Assert.Contains("Не удалось открыть файл-шаблон", ex.Message);
        Assert.Contains(Path.GetFileName(_template), ex.Message);
        Assert.Empty(Directory.GetFiles(_out));
    }

    // ── Целость шаблона ───────────────────────────────────────────────────────

    // Свод пишется потоково: результат собирается НОВЫМ zip-архивом, куда части шаблона
    // копируются побайтно (см. TimeSheetWriter). Проверяем ровно то, что при этом могло бы
    // потеряться и чего не видно по значениям ячеек: макросы, привязка кода к листу, чужие
    // листы и настройки колонок.
    [Fact]
    public void Template_Parts_Survive_The_Streaming_Write()
    {
        // vbaProject.bin своими байтами: настоящий макрос собрать нечем, а нам важно, что
        // часть переезжает в результат НЕ ТРОНУТОЙ — по ней Excel и находит код книги.
        var macro = new byte[] { 0xD0, 0xCF, 0x11, 0xE0, 1, 2, 3, 4, 5 };
        AddPart(_template, "xl/vbaProject.bin", macro);
        SetSheetCodeName(_template, "ЛистСвода");
        Assert.Contains("ЛистСвода", SheetXml(_template));         // страховка самого стенда

        CreateCalc("П_2025.03.14_Иванов.xlsm", ("Изделие", "А", "АА", 1.0));
        var result = Run();

        Assert.Equal(macro, ReadPart(result.OutputPath, "xl/vbaProject.bin"));
        Assert.Contains("ЛистСвода", SheetXml(result.OutputPath));

        using var wb = new XLWorkbook(result.OutputPath);

        // Чужой лист шаблона и его содержимое на месте.
        Assert.Equal(@"\\старый\путь", wb.Worksheet("Настройки").Cell(1, 2).GetString());

        // Ширины колонок — из шаблона: их настраивал тот, кто шаблон делал.
        var ws = wb.Worksheet(TimeCollectorEngine.TargetSheet);
        Assert.Equal(40, ws.Column(2).Width, precision: 0);
    }

    // Адрес ссылки — это полный путь к расчету, а строковая константа формулы у Excel куда
    // короче ячейки (см. ExcelMaxFormulaText). Формулу с константой длиннее Excel при открытии
    // книги выбрасывает («Удаленные записи: Формула»), поэтому такая строка получает простой
    // текст «>>>» — ровно то, что писал макрос, и переход по ней он же и делает.
    [Fact]
    public void Too_Long_Path_Gets_The_Plain_Mark_Instead_Of_A_Formula()
    {
        // Путь подгоняется под предел, но остается в MAX_PATH: длиннее файл просто не создать.
        var filler = 245 - _calc.Length - 1 - "_2025.03.14_Иванов.xlsm".Length;
        Assert.True(filler > 0, "временная папка стенда слишком длинная для этой проверки");

        CreateCalc(new string('П', filler) + "_2025.03.14_Иванов.xlsm",
                   ("Изделие", "Длинный", "ДД", 1.0));
        CreateCalc("К_2025.03.14_Петров.xlsm", ("Изделие", "Короткий", "КК", 2.0));

        var result = Run();

        // Строк две, а формула ровно одна — у той, чей путь влез.
        Assert.Equal(1, SheetXml(result.OutputPath).Split("HYPERLINK").Length - 1);

        // И об этом сказано в журнале: ячейка выглядит так же, а работает иначе.
        Assert.Equal(1, result.Sheet.PlainLinks);

        using var wb = new XLWorkbook(result.OutputPath);
        var ws = wb.Worksheet(TimeCollectorEngine.TargetSheet);

        var byName = Enumerable.Range(4, 2).ToDictionary(r => ws.Cell(r, 2).GetString(), r => r);
        Assert.False(ws.Cell(byName["Длинный"], 8).HasFormula);
        Assert.Equal(">>>", ws.Cell(byName["Длинный"], 8).GetString());
        Assert.True(ws.Cell(byName["Короткий"], 8).HasFormula);
    }

    // Скобки в имени файла рвут адрес книги внутри формулы («[путь]Лист»), и такая строка
    // получает текст макроса — формула с испорченным адресом хуже, чем ее отсутствие.
    [Fact]
    public void Brackets_In_The_File_Name_Get_The_Plain_Mark_Too()
    {
        CreateCalc("П[1]_2025.03.14_Иванов.xlsm", ("Изделие", "А", "АА", 1.0));

        using var wb = new XLWorkbook(Run().OutputPath);
        var ws = wb.Worksheet(TimeCollectorEngine.TargetSheet);

        Assert.False(ws.Cell(4, 8).HasFormula);
        Assert.Equal(">>>", ws.Cell(4, 8).GetString());
    }

    // Обвязка листа переезжает из шаблона в результат, и переезжать она обязана ЦЕЛИКОМ:
    // вместе с объявлениями неймспейсов с корня листа и списком mc:Ignorable. Excel помечает
    // чужими неймспейсами свои поздние добавки (xr:uid на гиперссылках, фильтрах, примечаниях)
    // и разрешает их не замечать ровно этим списком — элемент, принесенный без него, делает
    // лист нечитаемым, хотя XML безупречен. Заодно проверяется схемный порядок: legacyDrawing
    // (им держатся примечания и кнопки на листе) обязан идти РАНЬШЕ tableParts, а элементу,
    // которого у листа не бывает, в результате не место вовсе.
    [Fact]
    public void Sheet_Namespaces_And_Late_Elements_Survive_In_Schema_Order()
    {
        DecorateSheet(_template);

        CreateCalc("П_2025.03.14_Иванов.xlsm", ("Изделие", "А", "АА", 1.0));
        var result = Run();
        var xml    = SheetXml(result.OutputPath);

        Assert.Contains("mc:Ignorable=\"xr\"", xml);
        Assert.Contains("xr:uid", xml);

        var legacy = xml.IndexOf("legacyDrawing", StringComparison.Ordinal);
        var tables = xml.IndexOf("tableParts", StringComparison.Ordinal);
        Assert.True(legacy > 0 && tables > 0, "обвязка шаблона до результата не доехала");
        Assert.True(legacy < tables, "legacyDrawing уехал за tableParts — Excel забракует лист");

        // Выброшенное не должно пропадать втихую — журнал называет его поименно.
        Assert.DoesNotContain("mysteryPart", xml);
        Assert.Contains("mysteryPart", result.Sheet.Dropped);
    }

    // Дописать в лист шаблона то, чего ClosedXML не пишет: объявления с корня, гиперссылку
    // с «поздним» атрибутом и хвостовые элементы. r:id у legacyDrawing нет намеренно —
    // стенд смотрит только на МЕСТО элемента, а связь пришлось бы заводить настоящую.
    private static void DecorateSheet(string path)
    {
        const string mc = "http://schemas.openxmlformats.org/markup-compatibility/2006";
        const string xr = "http://schemas.microsoft.com/office/spreadsheetml/2014/revision";

        using var zip = ZipFile.Open(path, ZipArchiveMode.Update);
        var entry = zip.GetEntry(SheetPart) ?? throw new InvalidOperationException("нет " + SheetPart);

        string xml;
        using (var r = new StreamReader(entry.Open())) xml = r.ReadToEnd();

        var p = xml.Contains("<x:worksheet", StringComparison.Ordinal) ? "x:" : "";

        // ClosedXML часть объявлений пишет сам — дописываем только недостающие, иначе
        // получится дубликат атрибута и стенд развалится раньше проверки.
        var add = "";
        if (!xml.Contains("xmlns:mc=", StringComparison.Ordinal)) add += $" xmlns:mc=\"{mc}\"";
        if (!xml.Contains("xmlns:xr=", StringComparison.Ordinal)) add += $" xmlns:xr=\"{xr}\"";
        xml = xml.Replace($"<{p}worksheet", $"<{p}worksheet{add}");

        xml = xml.Contains("mc:Ignorable=", StringComparison.Ordinal)
            ? System.Text.RegularExpressions.Regex.Replace(xml, "mc:Ignorable=\"[^\"]*\"", "mc:Ignorable=\"xr\"")
            : xml.Replace($"<{p}worksheet", $"<{p}worksheet mc:Ignorable=\"xr\"");

        var anchor = $"</{p}sheetData>";
        xml = xml.Replace(anchor, anchor +
            $"<{p}hyperlinks><{p}hyperlink ref=\"H4\" location=\"Таблица!A1\" " +
            $"xr:uid=\"{{00000000-0001-0000-0000-000000000000}}\" /></{p}hyperlinks>" +
            $"<{p}legacyDrawing /><{p}tableParts count=\"0\" /><{p}mysteryPart />");

        entry.Delete();
        using var w = new StreamWriter(zip.CreateEntry(SheetPart).Open());
        w.Write(xml);
    }

    // Лист свода не обязан быть в книге первым: писатель ищет его часть через связи книги
    // (имя листа → r:id → цель в workbook.xml.rels), а не по имени файла части.
    [Fact]
    public void Target_Sheet_Is_Found_Even_When_It_Is_Not_First_In_The_Workbook()
    {
        using (var wb = new XLWorkbook())
        {
            wb.AddWorksheet("Настройки").Cell(1, 1).Value = "первый лист — чужой";
            wb.AddWorksheet("Ещё один").Cell(1, 1).Value = "и второй тоже";
            wb.AddWorksheet(TimeCollectorEngine.TargetSheet).Cell(1, 2).Value = "старая шапка";
            wb.SaveAs(_template);
        }

        CreateCalc("П_2025.03.14_Иванов.xlsm", ("Изделие", "А", "АА", 5.0));
        var result = Run();

        Assert.Equal(1, result.RowCount);
        using var book = new XLWorkbook(result.OutputPath);
        Assert.Equal("АА", book.Worksheet(TimeCollectorEngine.TargetSheet).Cell(4, 3).GetString());
        Assert.Equal("первый лист — чужой", book.Worksheet("Настройки").Cell(1, 1).GetString());
    }

    // Лист «Таблица» добавляется в стенд первым, поэтому ClosedXML кладет его в sheet1.xml.
    // Берем часть ПО ИМЕНИ, а не первую подходящую: после пересоздания часть уезжает в конец
    // архива, и «первая по порядку» оказывается уже другим листом.
    private const string SheetPart = "xl/worksheets/sheet1.xml";

    private static void AddPart(string path, string entryName, byte[] content)
    {
        using var zip = ZipFile.Open(path, ZipArchiveMode.Update);
        zip.GetEntry(entryName)?.Delete();
        using var s = zip.CreateEntry(entryName).Open();
        s.Write(content);
    }

    private static byte[] ReadPart(string path, string entryName)
    {
        using var zip = ZipFile.OpenRead(path);
        var entry = zip.GetEntry(entryName) ?? throw new InvalidOperationException($"нет части {entryName}");
        using var s  = entry.Open();
        using var ms = new MemoryStream();
        s.CopyTo(ms);
        return ms.ToArray();
    }

    // codeName связывает лист с его кодом в макросах: потеряется — отвалятся обработчики
    // событий листа, а именно ими шаблон открывает расчет по «>>>».
    private static void SetSheetCodeName(string path, string codeName)
    {
        using var zip = ZipFile.Open(path, ZipArchiveMode.Update);
        var entry = zip.GetEntry(SheetPart) ?? throw new InvalidOperationException("нет " + SheetPart);

        string xml;
        using (var r = new StreamReader(entry.Open())) xml = r.ReadToEnd();
        var open = xml.IndexOf("<x:worksheet", StringComparison.Ordinal) >= 0 ? "x:" : "";
        xml = xml.Replace($"<{open}sheetData",
                          $"<{open}sheetPr codeName=\"{codeName}\" /><{open}sheetData");

        entry.Delete();
        using var w = new StreamWriter(zip.CreateEntry(entry.FullName).Open());
        w.Write(xml);
    }

    private static string SheetXml(string path)
    {
        using var zip = ZipFile.OpenRead(path);
        var entry = zip.GetEntry(SheetPart) ?? throw new InvalidOperationException("нет " + SheetPart);
        using var r = new StreamReader(entry.Open());
        return r.ReadToEnd();
    }

    // Дописать в книгу определенные имена мимо ClosedXML: .xlsm — это zip, имена лежат в
    // xl/workbook.xml. Через API такие имена не завести — ClosedXML их же и не разбирает.
    private static void AddDefinedNames(string path, params (string Name, string RefersTo)[] names)
    {
        using var zip = ZipFile.Open(path, ZipArchiveMode.Update);
        var entry = zip.GetEntry("xl/workbook.xml")
                    ?? throw new InvalidOperationException("в книге нет xl/workbook.xml");

        string xml;
        using (var reader = new StreamReader(entry.Open())) xml = reader.ReadToEnd();

        // ClosedXML пишет части с префиксом «x:», Excel — без него; стенд не должен зависеть
        // от того, кто именно собрал книгу.
        var (open, close) = xml.Contains("<x:sheets>") ? ("<x:", "</x:") : ("<", "</");
        var block = $"{open}definedNames>" +
                    string.Concat(names.Select(n =>
                        $"{open}definedName name=\"{n.Name}\">{n.RefersTo}{close}definedName>")) +
                    $"{close}definedNames>";

        xml = xml.Contains($"{open}definedNames />")
            ? xml.Replace($"{open}definedNames />", block)
            : xml.Replace($"{close}sheets>", $"{close}sheets>{block}");

        entry.Delete();
        using var writer = new StreamWriter(zip.CreateEntry("xl/workbook.xml").Open());
        writer.Write(xml);
    }

    private static string WorkbookXml(string path)
    {
        using var zip = ZipFile.OpenRead(path);
        using var reader = new StreamReader(
            zip.GetEntry("xl/workbook.xml")!.Open());
        return reader.ReadToEnd();
    }
}
