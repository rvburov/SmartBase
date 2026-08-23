using ClosedXML.Excel;
using SmartBase.Extensions.ProductCompare;

namespace SmartBase.Tests.Extensions.ProductCompare;

/// <summary>
/// Сквозной прогон «Сравнения составов»: на диске собираются настоящие книги, расширение
/// пишет результат в шаблон, и результат читается обратно. Именно здесь проверяется то, чего
/// не видно на отдельных функциях — раскладка «старое слева / новое справа», сопоставление по
/// иерархическому номеру, числовой порядок строк и заливка уникальных обозначений.
///
/// Приватный тип Hierarchy напрямую не тестируется (он и не публичен) — его поведение видно
/// по порядку и по совпадению строк результата, то есть ровно так, как его видит пользователь.
/// </summary>
public sealed class ProductCompareRunTests : IDisposable
{
    private const int First = 6;          // первая строка данных и в источнике, и в результате

    private readonly string _root = Path.Combine(Path.GetTempPath(), $"sbext_pc_{Guid.NewGuid():N}");
    private readonly string _template;
    private readonly string _old;
    private readonly string _new;
    private readonly string _out;

    public ProductCompareRunTests()
    {
        Directory.CreateDirectory(_root);
        _template = Path.Combine(_root, "шаблон.xlsm");
        _old      = Path.Combine(_root, "старый.xlsx");
        _new      = Path.Combine(_root, "новый.xlsx");
        _out      = Path.Combine(_root, "Результат");
        Directory.CreateDirectory(_out);
        ExcelStand.WriteTemplate(_template);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
        catch { /* best-effort */ }
    }

    private CompareResult Run(IProgress<int>? progress = null, CancellationToken ct = default) =>
        ProductCompareEngine.Run(_template, _old, _new, _out, progress, ct);

    /// <summary>Прогнать сравнение и проверить лист результата (книга закрывается сама).</summary>
    private CompareResult RunAndCheck(Action<IXLWorksheet> check)
    {
        var result = Run();
        using var wb = new XLWorkbook(result.OutputPath);
        check(wb.Worksheets.First());
        return result;
    }

    private static CompareRow Row(string hierarchy, string name, string designation, string qty = "1") =>
        new(Pos: "п", Hierarchy: hierarchy, Name: name, Designation: designation, Qty: qty);

    // ── Раскладка ─────────────────────────────────────────────────────────────

    [Fact]
    public void Old_data_goes_left_and_new_data_goes_right()
    {
        ExcelStand.WriteSource(_old, Row("1.", "Плата старая", "АБВГ.001"));
        ExcelStand.WriteSource(_new, Row("1.", "Плата новая",  "АБВГ.001"));

        RunAndCheck(ws =>
        {
            Assert.Equal("п",            ws.Text(First, 1));
            Assert.Equal("1.",           ws.Text(First, 2));
            Assert.Equal("Плата старая", ws.Text(First, 3));
            Assert.Equal("АБВГ.001",     ws.Text(First, 4));
            Assert.Equal("1",            ws.Text(First, 5));

            Assert.Equal("Плата новая",  ws.Text(First, 9));
            Assert.Equal("АБВГ.001",     ws.Text(First, 10));
        });
    }

    [Fact]
    public void Rows_are_matched_by_the_hierarchy_number()
    {
        ExcelStand.WriteSource(_old, Row("1.", "Плата", "АБВГ.001"));
        ExcelStand.WriteSource(_new, Row("1.", "Плата", "АБВГ.001"));

        var result = Run();

        Assert.Equal(1, result.TotalRows);   // одна строка результата, а не две
        Assert.Equal(1, result.OldRows);
        Assert.Equal(1, result.NewRows);
    }

    /// <summary>Иерархия сравнивается по УРОВНЯМ, а не как строка: «1.2.» обязана идти раньше
    /// «1.10.». Строковое сравнение поставило бы их наоборот, и состав изделия читался бы
    /// вперемешку.</summary>
    [Fact]
    public void Hierarchy_is_ordered_numerically_not_alphabetically()
    {
        ExcelStand.WriteSource(_old,
            Row("1.10.", "Десятый", "АБВГ.010"),
            Row("1.2.",  "Второй",  "АБВГ.002"));
        ExcelStand.WriteSource(_new);

        RunAndCheck(ws =>
        {
            Assert.Equal("Второй",  ws.Text(First,     3));
            Assert.Equal("Десятый", ws.Text(First + 1, 3));
        });
    }

    [Fact]
    public void Shorter_prefix_comes_first()
    {
        ExcelStand.WriteSource(_old,
            Row("1.1.", "Вложенный", "АБВГ.011"),
            Row("1.",   "Корневой",  "АБВГ.001"));
        ExcelStand.WriteSource(_new);

        RunAndCheck(ws =>
        {
            Assert.Equal("Корневой",  ws.Text(First,     3));
            Assert.Equal("Вложенный", ws.Text(First + 1, 3));
        });
    }

    /// <summary>Строки только из нового файла оставляют левую сторону ПУСТОЙ, но
    /// отформатированной: иначе в таблице зияла бы дыра без границ.</summary>
    [Fact]
    public void Row_present_only_in_the_new_file_leaves_the_left_side_blank()
    {
        ExcelStand.WriteSource(_old);
        ExcelStand.WriteSource(_new, Row("1.", "Только в новом", "АБВГ.001"));

        RunAndCheck(ws =>
        {
            Assert.Equal("", ws.Text(First, 3));
            Assert.Equal("Только в новом", ws.Text(First, 9));
            Assert.Equal(XLBorderStyleValues.Thin, ws.Cell(First, 3).Style.Border.TopBorder);
        });
    }

    // ── Заливка уникальных обозначений ────────────────────────────────────────

    [Fact]
    public void Designation_unique_to_the_old_file_is_red()
    {
        ExcelStand.WriteSource(_old, Row("1.", "Убрали", "АБВГ.ушло"));
        ExcelStand.WriteSource(_new, Row("2.", "Общая",  "АБВГ.общее"));

        RunAndCheck(ws => Assert.Equal("FF0000", ws.Fill(First, 1)));
    }

    [Fact]
    public void Designation_unique_to_the_new_file_is_green()
    {
        ExcelStand.WriteSource(_old, Row("1.", "Общая",    "АБВГ.общее"));
        ExcelStand.WriteSource(_new, Row("2.", "Добавили", "АБВГ.новое"));

        RunAndCheck(ws => Assert.Equal("00FF00", ws.Fill(First + 1, 7)));
    }

    [Fact]
    public void Designation_present_in_both_files_is_not_painted()
    {
        ExcelStand.WriteSource(_old, Row("1.", "Плата", "АБВГ.общее"));
        ExcelStand.WriteSource(_new, Row("1.", "Плата", "АБВГ.общее"));

        RunAndCheck(ws =>
        {
            Assert.Null(ws.Fill(First, 1));
            Assert.Null(ws.Fill(First, 7));
        });
    }

    // ── Границы чтения ────────────────────────────────────────────────────────

    // Шапка источника (строки 1–5) в результат попадать не должна — там подписи, а не состав.
    [Fact]
    public void Rows_above_the_sixth_are_not_read()
    {
        ExcelStand.WriteSource(_old, Row("1.", "Единственная", "АБВГ.001"));
        ExcelStand.WriteSource(_new);

        var result = Run();

        Assert.Equal(1, result.OldRows);
        Assert.Equal(1, result.TotalRows);
    }

    /// <summary>Нечисловые части иерархии отбрасываются: «1.а.2.» превращается в «1.2.» и
    /// сливается с настоящей «1.2.». Особенность разбора, унаследованная от исходной утилиты, —
    /// фиксируем как есть, чтобы правка не прошла молча.</summary>
    [Fact]
    public void Non_numeric_parts_of_the_hierarchy_are_dropped()
    {
        ExcelStand.WriteSource(_old,
            Row("1.2.",   "Обычная",  "АБВГ.001"),
            Row("1.а.2.", "С буквой", "АБВГ.002"));
        ExcelStand.WriteSource(_new);

        var result = Run();

        Assert.Equal(2, result.OldRows);    // прочитаны обе строки
        Assert.Equal(1, result.TotalRows);  // но ключ у них один — в результате одна
    }

    /// <summary>Пустая и нетекстовая иерархия дают один и тот же пустой ключ, и выживает
    /// ПОСЛЕДНЯЯ такая строка файла. Тоже поведение исходной утилиты — фиксируем.</summary>
    [Fact]
    public void Rows_without_a_hierarchy_collapse_into_one_key()
    {
        ExcelStand.WriteSource(_old,
            Row("", "Первая без номера", "АБВГ.001"),
            Row("", "Вторая без номера", "АБВГ.002"));
        ExcelStand.WriteSource(_new);

        var result = RunAndCheck(ws => Assert.Equal("Вторая без номера", ws.Text(First, 3)));

        Assert.Equal(2, result.OldRows);
        Assert.Equal(1, result.TotalRows);
    }

    // ── Оформление и итоги ────────────────────────────────────────────────────

    [Fact]
    public void Gost_style_is_applied_to_every_written_cell()
    {
        ExcelStand.WriteSource(_old, Row("1.", "Плата", "АБВГ.001"));
        ExcelStand.WriteSource(_new);

        RunAndCheck(ws =>
        {
            var style = ws.Cell(First, 3).Style;
            Assert.Equal("GOST type A", style.Font.FontName);
            Assert.Equal(12, style.Font.FontSize);
            Assert.Equal(XLAlignmentHorizontalValues.Center, style.Alignment.Horizontal);
            Assert.True(style.Alignment.WrapText);
            Assert.Equal(XLBorderStyleValues.Thin, style.Border.LeftBorder);
        });
    }

    [Fact]
    public void Row_height_is_thirty()
    {
        ExcelStand.WriteSource(_old, Row("1.", "Плата", "АБВГ.001"));
        ExcelStand.WriteSource(_new);

        RunAndCheck(ws => Assert.Equal(30, ws.Row(First).Height));
    }

    [Fact]
    public void Result_counters_match_the_sources()
    {
        ExcelStand.WriteSource(_old,
            Row("1.", "Общая", "АБВГ.общее"),
            Row("2.", "Ушла",  "АБВГ.ушло"));
        ExcelStand.WriteSource(_new,
            Row("1.", "Общая",  "АБВГ.общее"),
            Row("3.", "Пришла", "АБВГ.новое"));

        var result = Run();

        Assert.Equal(2, result.OldRows);
        Assert.Equal(2, result.NewRows);
        Assert.Equal(3, result.TotalRows);   // объединение ключей 1., 2., 3.
    }

    [Fact]
    public void Progress_reaches_a_hundred()
    {
        ExcelStand.WriteSource(_old, Row("1.", "Плата", "АБВГ.001"));
        ExcelStand.WriteSource(_new, Row("1.", "Плата", "АБВГ.001"));
        var reported = new List<int>();

        Run(new SyncProgress<int>(reported.Add));

        Assert.NotEmpty(reported);
        Assert.Equal(100, reported[^1]);
        Assert.Equal(reported.OrderBy(p => p), reported);   // проценты не скачут назад
    }

    /// <summary>Обвязка шаблона (второй лист, макросы, настройки) обязана пережить прогон —
    /// расширение пишет только в первый лист.</summary>
    [Fact]
    public void Template_content_outside_the_data_area_survives()
    {
        ExcelStand.WriteSource(_old, Row("1.", "Плата", "АБВГ.001"));
        ExcelStand.WriteSource(_new);

        var result = Run();
        using var wb = new XLWorkbook(result.OutputPath);

        Assert.Equal("не трогать", wb.Worksheet("Обвязка").Cell(1, 1).GetString());
        Assert.Equal("шапка шаблона", wb.Worksheets.First().Cell(1, 1).GetString());
    }

    [Fact]
    public void Result_file_name_carries_the_run_timestamp()
    {
        ExcelStand.WriteSource(_old, Row("1.", "Плата", "АБВГ.001"));
        ExcelStand.WriteSource(_new);

        var result = Run();
        var name = Path.GetFileName(result.OutputPath);

        Assert.StartsWith("Результат сравнения ", name);
        Assert.EndsWith(".xlsm", name);
        Assert.Equal(_out, Path.GetDirectoryName(result.OutputPath));
        Assert.True(File.Exists(result.OutputPath));
    }

    [Fact]
    public void Cancellation_stops_the_run_and_writes_no_file()
    {
        ExcelStand.WriteSource(_old, Row("1.", "Плата", "АБВГ.001"));
        ExcelStand.WriteSource(_new, Row("1.", "Плата", "АБВГ.001"));
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.Throws<OperationCanceledException>(() => Run(ct: cts.Token));

        Assert.Empty(Directory.EnumerateFiles(_out));
    }
}

/// <summary>Синхронный приемник прогресса: штатный <see cref="Progress{T}"/> публикует колбэк
/// в пул потоков, и к возврату из метода часть отчетов может быть еще не доставлена.</summary>
internal sealed class SyncProgress<T>(Action<T> onReport) : IProgress<T>
{
    public void Report(T value) => onReport(value);
}
