using ClosedXML.Excel;
using SmartBase.Extensions.AssemblyCalc;

namespace SmartBase.Tests.Extensions.AssemblyCalc;

/// <summary>
/// Сквозной прогон «Расчёта сборочных единиц»: исходный файл и шаблон собираются на диске,
/// расширение переносит данные, и результат читается обратно.
///
/// Главное, ради чего этот файл существует, — КАРТА СТОЛБЦОВ. Она разрежена (248, 283, 287,
/// 346, 362…) и повторяет выгрузку заказчика: потерянная пара обнаруживается только на боевом
/// файле, где столбец 248 «ВЭ» уезжает в пустоту. Плюс раскраска по уровню и граница очистки
/// шаблона — они тоже не видны ни на одной отдельной функции.
/// </summary>
public sealed class AssemblyCalcRunTests : IDisposable
{
    private const int First = 6;

    private readonly string _root = Path.Combine(Path.GetTempPath(), $"sbext_ac_{Guid.NewGuid():N}");
    private readonly string _source;
    private readonly string _template;
    private readonly string _out;

    public AssemblyCalcRunTests()
    {
        Directory.CreateDirectory(_root);
        _source   = Path.Combine(_root, "исходный.xlsx");
        _template = Path.Combine(_root, "шаблон.xlsm");
        _out      = Path.Combine(_root, "Результат");
        Directory.CreateDirectory(_out);
        ExcelStand.WriteTemplate(_template);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
        catch { /* best-effort */ }
    }

    private AssemblyCalcResult Run(IProgress<int>? progress = null, CancellationToken ct = default) =>
        AssemblyCalcEngine.Run(_source, _template, _out, progress, ct);

    /// <summary>Прогнать расчёт и проверить лист результата (книга закрывается сама).</summary>
    private AssemblyCalcResult RunAndCheck(Action<IXLWorksheet> check)
    {
        var result = Run();
        using var wb = new XLWorkbook(result.OutputPath);
        check(wb.Worksheet(ExcelStand.SheetName));
        return result;
    }

    // ── Карта столбцов ────────────────────────────────────────────────────────

    /// <summary>Все пары «столбец источника → столбец шаблона» разом, включая экзотическую
    /// 248 → 13 («ВЭ»): её потеря видна только на боевом файле.</summary>
    [Fact]
    public void Mapped_columns_land_in_their_target_places()
    {
        ExcelStand.WriteSource(_source, (1, [
            (2,   "уровень-идентификатор"),
            (5,   "Наименование"),
            (11,  "АБВГ.301234.001"),
            (12,  "5"),
            (248, "ВЭ-значение"),
            (283, "вид по СП"),
            (284, "не входит в МК"),
            (287, "АБВГ.ТД.001"),
            (289, "тип ТД"),
            (346, "состояние ОГТ"),
            (362, "состояние по источникам"),
        ]));

        RunAndCheck(ws =>
        {
            Assert.Equal("уровень-идентификатор",   ws.Text(First, 2));
            Assert.Equal("Наименование",            ws.Text(First, 3));
            Assert.Equal("АБВГ.301234.001",         ws.Text(First, 4));
            Assert.Equal("5",                       ws.Text(First, 5));
            Assert.Equal("вид по СП",               ws.Text(First, 7));
            Assert.Equal("АБВГ.ТД.001",             ws.Text(First, 8));
            Assert.Equal("тип ТД",                  ws.Text(First, 9));
            Assert.Equal("состояние ОГТ",           ws.Text(First, 10));
            Assert.Equal("состояние по источникам", ws.Text(First, 11));
            Assert.Equal("не входит в МК",          ws.Text(First, 12));
            Assert.Equal("ВЭ-значение",             ws.Text(First, 13));   // столбец 248
        });
    }

    // ── Границы переноса ──────────────────────────────────────────────────────

    [Fact]
    public void Transfer_starts_at_the_sixth_row()
    {
        ExcelStand.WriteSource(_source, (1, [(5, "Первая строка данных")]));

        var result = RunAndCheck(ws => Assert.Equal("Первая строка данных", ws.Text(First, 3)));

        Assert.Equal(1, result.RowCount);   // шапка источника (1–5) не перенесена
    }

    /// <summary>Пустая ячейка «Уровень» обрывает перенос: ниже в выгрузке идут итоги и мусор,
    /// которым в расчёте не место.</summary>
    [Fact]
    public void Transfer_stops_at_the_first_row_without_a_level()
    {
        ExcelStand.WriteSource(_source,
            (1,    [(5, "Первая")]),
            (null, [(5, "Без уровня")]),
            (2,    [(5, "После разрыва")]));

        var result = RunAndCheck(ws => Assert.Equal("", ws.Text(First + 1, 3)));

        Assert.Equal(1, result.RowCount);
    }

    // ── Уровень и раскраска ───────────────────────────────────────────────────

    [Fact]
    public void Fractional_level_is_truncated()
    {
        ExcelStand.WriteSource(_source, (2.9, [(5, "Плата")]));

        RunAndCheck(ws =>
        {
            Assert.Equal("2", ws.Text(First, 1));
            Assert.Equal("A9D08E", ws.Fill(First, 1));   // цвет уровня 2
        });
    }

    [Fact]
    public void Level_written_as_text_is_understood()
    {
        ExcelStand.WriteSource(_source, ("3", [(5, "Плата")]));

        RunAndCheck(ws =>
        {
            Assert.Equal("3", ws.Text(First, 1));
            Assert.Equal("E2EFDA", ws.Fill(First, 1));   // цвет уровня 3
        });
    }

    // Нечисловой уровень переносится как есть: расширение не выдумывает за пользователя.
    [Fact]
    public void Level_that_is_not_a_number_is_copied_as_is()
    {
        ExcelStand.WriteSource(_source, ("н/д", [(5, "Плата")]));

        RunAndCheck(ws =>
        {
            Assert.Equal("н/д", ws.Text(First, 1));
            Assert.Null(ws.Fill(First, 1));
        });
    }

    [Theory]
    [InlineData(0,  "0070C0")]
    [InlineData(1,  "00B0F0")]
    [InlineData(2,  "A9D08E")]
    [InlineData(3,  "E2EFDA")]
    [InlineData(4,  "8EA9DB")]
    [InlineData(5,  "BDD7EE")]
    [InlineData(6,  "D9E1F2")]
    [InlineData(7,  "A6A6A6")]
    [InlineData(8,  "BFBFBF")]
    [InlineData(9,  "D9D9D9")]
    [InlineData(10, "F2F2F2")]
    public void Every_level_from_zero_to_ten_has_its_own_fill(int level, string expected)
    {
        ExcelStand.WriteSource(_source, (level, [(5, "Плата")]));

        RunAndCheck(ws => Assert.Equal(expected, ws.Fill(First, 3)));
    }

    [Fact]
    public void Level_beyond_the_table_gets_no_fill()
    {
        ExcelStand.WriteSource(_source, (11, [(5, "Плата")]));

        RunAndCheck(ws =>
        {
            Assert.Null(ws.Fill(First, 1));
            Assert.Equal(XLBorderStyleValues.Thin, ws.Cell(First, 1).Style.Border.TopBorder);   // стиль всё равно есть
        });
    }

    // ── Очистка шаблона ───────────────────────────────────────────────────────

    [Fact]
    public void Old_template_rows_are_cleared_before_the_transfer()
    {
        ExcelStand.WriteSource(_source, (1, [(5, "Новая строка")]));

        RunAndCheck(ws =>
        {
            Assert.Equal("Новая строка", ws.Text(First, 3));
            Assert.Equal("", ws.Text(First + 1, 2));    // строки прошлого прогона снесены
            Assert.Equal("", ws.Text(First + 2, 13));
        });
    }

    /// <summary>Чистится ровно до последнего столбца КАРТЫ (13). Столбец 14 шаблона остаётся —
    /// фиксируем как есть: расширение считает, что дальше идут поля пользователя, и правка карты
    /// молча изменила бы это поведение.</summary>
    [Fact]
    public void Cells_to_the_right_of_the_last_mapped_column_survive()
    {
        ExcelStand.WriteSource(_source, (1, [(5, "Новая строка")]));

        RunAndCheck(ws => Assert.Equal("правее области данных", ws.Text(First, 14)));
    }

    [Fact]
    public void Template_content_outside_the_data_sheet_survives()
    {
        ExcelStand.WriteSource(_source, (1, [(5, "Новая строка")]));

        var result = Run();
        using var wb = new XLWorkbook(result.OutputPath);

        Assert.Equal("не трогать",    wb.Worksheet("Обвязка").Cell(1, 1).GetString());
        Assert.Equal("шапка шаблона", wb.Worksheet(ExcelStand.SheetName).Cell(1, 1).GetString());
    }

    // ── Оформление и итоги ────────────────────────────────────────────────────

    [Fact]
    public void Gost_style_and_row_height_are_applied()
    {
        ExcelStand.WriteSource(_source, (1, [(5, "Плата")]));

        RunAndCheck(ws =>
        {
            var style = ws.Cell(First, 3).Style;
            Assert.Equal("GOST type A", style.Font.FontName);
            Assert.Equal(12, style.Font.FontSize);
            Assert.Equal(XLAlignmentHorizontalValues.Center, style.Alignment.Horizontal);
            Assert.Equal(XLBorderStyleValues.Thin, style.Border.RightBorder);
            Assert.Equal("@", style.NumberFormat.Format);
            Assert.Equal(30, ws.Row(First).Height);
        });
    }

    [Fact]
    public void Result_row_count_matches_the_source()
    {
        ExcelStand.WriteSource(_source,
            (1, [(5, "Первая")]),
            (2, [(5, "Вторая")]),
            (3, [(5, "Третья")]));

        var result = Run();

        Assert.Equal(3, result.RowCount);
    }

    [Fact]
    public void Progress_reaches_a_hundred()
    {
        ExcelStand.WriteSource(_source, (1, [(5, "Плата")]));
        var reported = new List<int>();

        Run(new SyncProgress<int>(reported.Add));

        Assert.NotEmpty(reported);
        Assert.Equal(100, reported[^1]);
        Assert.Equal(reported.OrderBy(p => p), reported);
    }

    [Fact]
    public void Result_file_name_carries_the_run_timestamp()
    {
        ExcelStand.WriteSource(_source, (1, [(5, "Плата")]));

        var result = Run();
        var name = Path.GetFileName(result.OutputPath);

        Assert.StartsWith("Расчет сборочных единиц (", name);
        Assert.EndsWith(").xlsm", name);
        Assert.True(File.Exists(result.OutputPath));
    }

    /// <summary>Имя результата НЕ уникализируется — оно с точностью до минуты, и повторный
    /// прогон в ту же минуту затирает прошлый файл. Отличие от «Сводной трудоёмкости», где имя
    /// получает номер. Фиксируем как есть: поведение расширения не меняем, но и молчать о нём
    /// нельзя (см. docs/developer/testing/extension-tests.md).</summary>
    [Fact]
    public void Second_run_in_the_same_minute_overwrites_the_first()
    {
        ExcelStand.WriteSource(_source, (1, [(5, "Первый прогон")]));
        var first = Run();

        ExcelStand.WriteSource(_source, (1, [(5, "Второй прогон")]));
        var second = Run();

        Assert.Equal(first.OutputPath, second.OutputPath);
        Assert.Single(Directory.EnumerateFiles(_out));

        using var wb = new XLWorkbook(second.OutputPath);
        Assert.Equal("Второй прогон", wb.Worksheet(ExcelStand.SheetName).Text(First, 3));
    }

    [Fact]
    public void Cancellation_stops_the_run_and_writes_no_file()
    {
        ExcelStand.WriteSource(_source, (1, [(5, "Плата")]));
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.Throws<OperationCanceledException>(() => Run(ct: cts.Token));

        Assert.Empty(Directory.EnumerateFiles(_out));
    }
}

/// <summary>Синхронный приёмник прогресса: штатный <see cref="Progress{T}"/> публикует колбэк
/// в пул потоков, и к возврату из метода часть отчётов может быть ещё не доставлена.</summary>
internal sealed class SyncProgress<T>(Action<T> onReport) : IProgress<T>
{
    public void Report(T value) => onReport(value);
}
