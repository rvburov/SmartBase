using SmartBase.Core.Models;
using SmartBase.Database.Repositories;
using SmartBase.Extensions.TimeCollector;
using SmartBase.Utils;

namespace SmartBase.Tests.Extensions.TimeCollector;

/// <summary>
/// Выгрузка свода в КОЛЛЕКЦИЮ программы — второй приемник рядом с .xlsm-шаблоном. Проверяется
/// то же, что у Excel-ветки в <c>TimeCollectorRunTests</c>, но на языке таблицы: состав и
/// порядок колонок, значения ячеек в каноне хранения, порядок записей и раскраска.
///
/// Каркас делается на ОБЩЕМ репозитории (UI-поток), записи — на СВОЕМ подключении к тому же
/// файлу: отсюда файловая БД стенда и главная проверка — записи второго подключения видны
/// через основной контекст.
/// </summary>
public sealed class TimeCollectionWriterTests
{
    private static readonly CancellationToken None = CancellationToken.None;

    private static readonly DateTime Day1 = new(2026, 3, 10);
    private static readonly DateTime Day2 = new(2026, 3, 11);

    private static TimeRow Row(string name, string deno, double? norm, DateTime date,
                               string efio = "Иванов", string proj = "П-1",
                               string path = @"C:\расчеты\П-1_2026.03.10_Иванов.xlsm",
                               string normText = "") =>
        new(RNum: 1, Name: name, Deno: deno, Norm: norm, NormText: normText,
            Date: date, Efio: efio, Proj: proj, SourcePath: path);

    private static int Fill(TempDatabase db, TimeShell shell, List<TimeRow> rows)
    {
        var ordered = TimeCollectorEngine.Sort(rows);
        return TimeCollectionWriter.FillRecords(db.Path, shell, ordered,
                                                TimeCollectorEngine.PlanColors(ordered), null, None);
    }

    // ── Каркас коллекции ──────────────────────────────────────────────────────

    /// <summary>Состав и типы колонок — часть договоренности с пользователем: он увидит именно
    /// такую таблицу, и молчаливая смена набора ломает уже созданные коллекции.</summary>
    [Fact]
    public void Shell_repeats_the_columns_of_the_excel_summary()
    {
        using var db = new TempDatabase();

        var shell  = TimeCollectionWriter.CreateShell(db.Constructor, "Трудоемкость");
        var fields = db.Constructor.GetFields(shell.CollectionId);

        Assert.Equal(6, fields.Count);
        Assert.Equal("text",   fields.Single(f => f.Id == shell.DenoFieldId).FieldType);
        Assert.Equal("number", fields.Single(f => f.Id == shell.NormFieldId).FieldType);
        Assert.Equal("date",   fields.Single(f => f.Id == shell.DateFieldId).FieldType);
        Assert.Equal("text",   fields.Single(f => f.Id == shell.EfioFieldId).FieldType);
        Assert.Equal("text",   fields.Single(f => f.Id == shell.ProjFieldId).FieldType);
        Assert.Equal("file",   fields.Single(f => f.Id == shell.FileFieldId).FieldType);

        Assert.Equal(TimeCollectionWriter.DenoFieldName, fields.Single(f => f.Id == shell.DenoFieldId).Name);
        Assert.Equal(TimeCollectionWriter.NormFieldName, fields.Single(f => f.Id == shell.NormFieldId).Name);
        Assert.Equal(TimeCollectionWriter.DateFieldName, fields.Single(f => f.Id == shell.DateFieldId).Name);
        Assert.Equal(TimeCollectionWriter.EfioFieldName, fields.Single(f => f.Id == shell.EfioFieldId).Name);
        Assert.Equal(TimeCollectionWriter.ProjFieldName, fields.Single(f => f.Id == shell.ProjFieldId).Name);
        Assert.Equal(TimeCollectionWriter.FileFieldName, fields.Single(f => f.Id == shell.FileFieldId).Name);
    }

    [Fact]
    public void Title_system_column_is_renamed_to_the_summary_header()
    {
        using var db = new TempDatabase();

        var shell = TimeCollectionWriter.CreateShell(db.Constructor, "Трудоемкость");

        var names = db.Constructor.GetSystemFieldNames(shell.CollectionId);
        Assert.Equal(TimeCollectionWriter.TitleColumnName, names["__title__"]);
    }

    [Fact]
    public void Column_order_matches_the_excel_header()
    {
        using var db = new TempDatabase();

        var shell = TimeCollectionWriter.CreateShell(db.Constructor, "Трудоемкость");
        var order = db.Constructor.GetColumnOrder(shell.CollectionId);

        var expected = new[]
        {
            "__title__", shell.DenoFieldId.ToString(), shell.NormFieldId.ToString(),
            shell.DateFieldId.ToString(), shell.EfioFieldId.ToString(),
            shell.ProjFieldId.ToString(), shell.FileFieldId.ToString(),
        };
        Assert.Equal(expected, expected.OrderBy(key => order[key]).ToArray());
    }

    /// <summary>Поле «файл» заводится БЕЗ правила авто-привязки: расчеты лежат в разных папках,
    /// а правило умеет ровно одну папку и один формат — пути расширение проставляет само.</summary>
    [Fact]
    public void File_field_has_no_auto_link_rule()
    {
        using var db = new TempDatabase();

        var shell = TimeCollectionWriter.CreateShell(db.Constructor, "Трудоемкость");
        var file  = db.Constructor.GetFields(shell.CollectionId).Single(f => f.Id == shell.FileFieldId);

        Assert.Null(FileAutoRule.FromConfig(file.ConfigJson));
    }

    /// <summary>Колонка «Тр-ть» приходит уже настроенной, иначе формат пришлось бы выставлять
    /// руками после КАЖДОГО прогона. Знаков после запятой — «Авто» (-1): трудоемкость приходит
    /// из расчетов с разной точностью, и фиксированная точность врала бы в обе стороны —
    /// дописывала бы «12» до «12,00» и резала бы «12,456». Разряды не разделяются, десятичный
    /// разделитель — запятая.</summary>
    [Fact]
    public void Norm_field_comes_preformatted_with_automatic_decimals()
    {
        using var db = new TempDatabase();

        var shell = TimeCollectionWriter.CreateShell(db.Constructor, "Трудоемкость");
        var norm  = db.Constructor.GetFields(shell.CollectionId).Single(f => f.Id == shell.NormFieldId);

        var fmt = NumberFormatValue.FromConfig(norm.ConfigJson);
        Assert.Equal(NumberFormatKind.Grouped, fmt.Kind);
        Assert.Equal(-1, fmt.Decimals);
        Assert.False(fmt.Thousands);
        Assert.Equal(DecimalMark.Comma, fmt.DecimalSeparator);

        // «Авто» — это «сколько есть», а не «ноль»: показ не дописывает нулей и не режет знаки.
        Assert.Equal("12",     NumberFormatValue.Format("12",     fmt));
        Assert.Equal("12,456", NumberFormatValue.Format("12,456", fmt));
    }

    /// <summary>Дата свода — колонка таблицы, а не событие: тумблер «Отображать в календаре»
    /// у поля выключен. Новое поле-дата заводится выключенным всегда, и каркас на это правило
    /// опирается — иначе календарь заполнился бы датами расчетов.</summary>
    [Fact]
    public void Date_field_stays_out_of_the_calendar()
    {
        using var db = new TempDatabase();

        var shell = TimeCollectionWriter.CreateShell(db.Constructor, "Трудоемкость");
        var date  = db.Constructor.GetFields(shell.CollectionId).Single(f => f.Id == shell.DateFieldId);

        Assert.False(date.IsCalStart);
    }

    // ── Имя коллекции ─────────────────────────────────────────────────────────

    [Fact]
    public void Taken_name_gets_a_numeric_suffix()
    {
        using var db = new TempDatabase();
        TimeCollectionWriter.CreateShell(db.Constructor, "Трудоемкость");

        var second = TimeCollectionWriter.CreateShell(db.Constructor, "Трудоемкость");

        Assert.Equal("Трудоемкость (2)", second.CollectionName);
    }

    [Fact]
    public void Blank_name_falls_back_to_the_default_with_date_and_time()
    {
        using var db = new TempDatabase();

        var shell = TimeCollectionWriter.CreateShell(db.Constructor, "   ");

        Assert.StartsWith("Трудоемкость (", shell.CollectionName);
    }

    // ── Права ─────────────────────────────────────────────────────────────────

    /// <summary>Своего права расширение не заводит — создание каркаса упирается в общее
    /// «Создавать коллекции» (та же точка отказа, что у «Документов в коллекцию»).</summary>
    [Fact]
    public void Shell_creation_needs_the_create_collection_right()
    {
        using var db = new TempDatabase();
        SetRights(db, PermAction.All & ~PermAction.CreateCollection);

        var ex = Assert.Throws<PermissionDeniedException>(
            () => TimeCollectionWriter.CreateShell(db.Constructor, "Трудоемкость"));

        Assert.Equal(PermAction.CreateCollection, ex.Action);
    }

    // ── Записи ────────────────────────────────────────────────────────────────

    [Fact]
    public void Every_summary_row_becomes_a_record()
    {
        using var db = new TempDatabase();
        var shell = TimeCollectionWriter.CreateShell(db.Constructor, "Трудоемкость");

        var created = Fill(db, shell, [Row("Корпус", "АБВ.1", 2, Day1), Row("Вал", "АБВ.2", 3, Day1)]);

        Assert.Equal(2, created);
        Assert.Equal(2, db.Constructor.CountRecords(shell.CollectionId));
    }

    /// <summary>Значения ложатся в каноне ХРАНЕНИЯ: число — с десятичной запятой (канон поля
    /// «число»), дата — ISO-строкой DateValue. Иначе ячейка показывала бы сырой текст, а
    /// фильтр и сортировка работали бы по строке, а не по значению.</summary>
    [Fact]
    public void Values_land_in_their_fields_in_storage_form()
    {
        using var db = new TempDatabase();
        var shell = TimeCollectionWriter.CreateShell(db.Constructor, "Трудоемкость");
        var path  = @"C:\расчеты\П-7_2026.03.10_Петров.xlsm";

        Fill(db, shell, [Row("Корпус", "АБВ.1", 12.5, Day1, "Петров", "П-7", path)]);

        var record = Assert.Single(db.Constructor.GetRecords(shell.CollectionId));
        var values = db.Constructor.GetFieldValues(record.Id!.Value);

        Assert.Equal("Корпус",     record.Title);
        Assert.Equal("АБВ.1",      values[shell.DenoFieldId]);
        Assert.Equal("12,5",       values[shell.NormFieldId]);
        Assert.Equal("2026-03-10", values[shell.DateFieldId]);
        Assert.Equal("Петров",     values[shell.EfioFieldId]);
        Assert.Equal("П-7",        values[shell.ProjFieldId]);
        Assert.Equal(path,         values[shell.FileFieldId]);
    }

    /// <summary>Нечисловая трудоемкость идет в ячейку как есть — ровно как в Excel-своде
    /// (макрос такую строку терял молча, мы ее показываем).</summary>
    [Fact]
    public void Non_numeric_norm_is_written_as_text()
    {
        using var db = new TempDatabase();
        var shell = TimeCollectionWriter.CreateShell(db.Constructor, "Трудоемкость");

        Fill(db, shell, [Row("Корпус", "АБВ.1", null, Day1, normText: "по факту")]);

        var record = Assert.Single(db.Constructor.GetRecords(shell.CollectionId));
        Assert.Equal("по факту", db.Constructor.GetFieldValues(record.Id!.Value)[shell.NormFieldId]);
    }

    /// <summary>Порядок записей — порядок свода (обозначение ↑, дата ↓, наименование ↑), шаг
    /// позиции 1000 оставляет место для вставки строк руками.</summary>
    [Fact]
    public void Records_keep_the_summary_order_with_room_between_them()
    {
        using var db = new TempDatabase();
        var shell = TimeCollectionWriter.CreateShell(db.Constructor, "Трудоемкость");

        Fill(db, shell,
        [
            Row("Вал",    "АБВ.2", 3, Day1),
            Row("Корпус", "АБВ.1", 2, Day1),
            Row("Крышка", "АБВ.1", 2, Day2),
        ]);

        var records = db.Constructor.GetRecords(shell.CollectionId);
        Assert.Equal(["Крышка", "Корпус", "Вал"], records.Select(r => r.Title));   // дата ↓ внутри АБВ.1
        Assert.Equal([1000, 2000, 3000], records.Select(r => r.Position));
    }

    /// <summary>Записи пишутся вторым подключением к тому же файлу базы — основной контекст
    /// обязан их видеть. Это и есть причина, по которой стенд держит БД файлом.</summary>
    [Fact]
    public void Records_written_on_the_own_connection_are_visible_through_the_main_one()
    {
        using var db = new TempDatabase();
        var shell = TimeCollectionWriter.CreateShell(db.Constructor, "Трудоемкость");

        Fill(db, shell, [Row("Корпус", "АБВ.1", 2, Day1)]);

        Assert.Equal("Корпус", Assert.Single(db.Constructor.GetRecords(shell.CollectionId)).Title);
    }

    /// <summary>Отмена бросает и ОСТАВЛЯЕТ каркас: убрать недособранную коллекцию — забота
    /// вызывающего (окно зовет TryDelete). Проверяем именно этот контракт.</summary>
    [Fact]
    public void Cancellation_throws_and_leaves_the_shell_behind()
    {
        using var db = new TempDatabase();
        var shell = TimeCollectionWriter.CreateShell(db.Constructor, "Трудоемкость");
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var rows = TimeCollectorEngine.Sort([Row("Корпус", "АБВ.1", 2, Day1)]);
        Assert.Throws<OperationCanceledException>(
            () => TimeCollectionWriter.FillRecords(db.Path, shell, rows,
                                                   TimeCollectorEngine.PlanColors(rows), null, cts.Token));

        Assert.Contains(db.Constructor.GetAllCollections(), c => c.Id == shell.CollectionId);
    }

    [Fact]
    public void Try_delete_removes_the_collection()
    {
        using var db = new TempDatabase();
        var shell = TimeCollectionWriter.CreateShell(db.Constructor, "Трудоемкость");

        TimeCollectionWriter.TryDelete(db.Constructor, shell.CollectionId);

        Assert.DoesNotContain(db.Constructor.GetAllCollections(), c => c.Id == shell.CollectionId);
    }

    /// <summary>Прогресс постится в UI-поток, поэтому шлется не на каждую запись: на десятках
    /// тысяч строк поток сообщений тормозит диспетчер сильнее самой вставки.</summary>
    [Fact]
    public void Progress_is_not_reported_for_every_record()
    {
        using var db = new TempDatabase();
        var shell = TimeCollectionWriter.CreateShell(db.Constructor, "Трудоемкость");
        var rows  = Enumerable.Range(0, 100)
                              .Select(i => Row($"Деталь {i:000}", $"АБВ.{i:000}", i, Day1))
                              .ToList();
        var reported = new List<CollectProgress>();

        var ordered = TimeCollectorEngine.Sort(rows);
        TimeCollectionWriter.FillRecords(db.Path, shell, ordered, TimeCollectorEngine.PlanColors(ordered),
                                         new SyncProgress<CollectProgress>(reported.Add), None);

        Assert.NotEmpty(reported);
        Assert.True(reported.Count < 20, $"отчетов {reported.Count} на 100 строк — шаг прогресса потерян");
    }

    // ── Раскраска ─────────────────────────────────────────────────────────────

    /// <summary>
    /// План раскраски свода (<c>PlanColors</c>) ложится на оформление таблицы: заливка строки —
    /// на строку записи. Цвета — из палитры приложения, а не из ColorIndex макроса: в таблице
    /// они рисуются так же, как раскраска, поставленная руками. Белые строки не красятся —
    /// «без заливки» и есть дефолт.
    /// </summary>
    [Fact]
    public void Alternating_fill_lands_on_record_rows()
    {
        using var db = new TempDatabase();
        var shell = TimeCollectionWriter.CreateShell(db.Constructor, "Трудоемкость");

        // Две группы обозначения подряд: первая — желтая, вторая — белая (без заливки).
        Fill(db, shell,
        [
            Row("Корпус", "АБВ.1", 2, Day1),
            Row("Вал",    "АБВ.2", 3, Day1),
        ]);

        var records = db.Constructor.GetRecords(shell.CollectionId);
        var styles  = db.Constructor.GetRowStyles(shell.CollectionId);

        Assert.Equal("#FFCC00", styles[records[0].Id!.Value].Bg);
        Assert.DoesNotContain(records[1].Id!.Value, styles.Keys);
    }

    /// <summary>Расхождение трудоемкости у соседей с ОДНИМ обозначением при совпавших проекте
    /// и дате красит обе строки целиком — как красный ColorIndex 3 в Excel-своде.</summary>
    [Fact]
    public void Norm_conflict_within_one_day_paints_both_rows_red()
    {
        using var db = new TempDatabase();
        var shell = TimeCollectionWriter.CreateShell(db.Constructor, "Трудоемкость");

        Fill(db, shell,
        [
            Row("Корпус", "АБВ.1", 2, Day1),
            Row("Крышка", "АБВ.1", 5, Day1),
        ]);

        var styles = db.Constructor.GetRowStyles(shell.CollectionId);
        Assert.Equal(2, styles.Count);
        Assert.All(styles.Values, s => Assert.Equal("#FF3B30", s.Bg));
    }

    /// <summary>Те же соседи, но из разных расчетов (проект или дата отличаются) — красной
    /// становится только ячейка «Тр-ть», строка сохраняет свою заливку группы.</summary>
    [Fact]
    public void Norm_conflict_across_days_paints_only_the_norm_cell()
    {
        using var db = new TempDatabase();
        var shell = TimeCollectionWriter.CreateShell(db.Constructor, "Трудоемкость");

        Fill(db, shell,
        [
            Row("Корпус", "АБВ.1", 2, Day1),
            Row("Корпус", "АБВ.1", 5, Day2),
        ]);

        var ids    = db.Constructor.GetRecords(shell.CollectionId).Select(r => r.Id!.Value).ToList();
        var cells  = db.Constructor.GetCellStylesBatch(shell.CollectionId, ids);
        var styles = db.Constructor.GetRowStyles(shell.CollectionId);

        Assert.All(ids, id => Assert.Equal("#FF3B30", cells[id][shell.NormFieldId.ToString()].Bg));
        Assert.All(styles.Values, s => Assert.Equal("#FFCC00", s.Bg));   // группа осталась желтой
    }

    // ── Колонки по операциям ──────────────────────────────────────────────────

    private static readonly OperationCatalog Catalog = new(["Сборка", "Пайка", "Контроль"]);

    private static TimeRow WithOps(TimeRow row, params (string Name, double Value)[] ops) =>
        row with { Ops = ops.Select(o => new OpEntry(o.Name, o.Value)).ToArray() };

    // Каркас по колонкам операций и заливка — так, как это делает окно.
    private static (TimeShell Shell, OperationLayout Layout) FillWithOps(TempDatabase db, List<TimeRow> rows)
    {
        var ordered = TimeCollectorEngine.Sort(rows);
        var layout  = OperationColumns.Build(ordered, Catalog, fixText: true, []);
        var shell   = TimeCollectionWriter.CreateShell(db.Constructor, "Трудоемкость", layout.Columns);
        TimeCollectionWriter.FillRecords(db.Path, shell, ordered, TimeCollectorEngine.PlanColors(ordered),
                                         null, None, operations: layout);
        return (shell, layout);
    }

    private static int OpField(TimeShell shell, OperationLayout layout, string operation) =>
        shell.OpFieldIds![layout.Columns.ToList().IndexOf(operation)];

    /// <summary>Колонки по операциям стоят после «Файл расчета», как в макросе после «Ссылки»:
    /// сначала «Операции» (текст), потом операции (числа в том же виде, что «Тр-ть» — это та же
    /// трудоемкость, по частям).</summary>
    [Fact]
    public void Operation_columns_follow_the_file_column()
    {
        using var db = new TempDatabase();

        var shell  = TimeCollectionWriter.CreateShell(db.Constructor, "Трудоемкость", ["Сборка", "Пайка"]);
        var fields = db.Constructor.GetFields(shell.CollectionId);
        var order  = db.Constructor.GetColumnOrder(shell.CollectionId);

        Assert.Equal(9, fields.Count);
        var oper = fields.Single(f => f.Id == shell.OperFieldId);
        Assert.Equal((TimeCollectionWriter.OperFieldName, "text"), (oper.Name, oper.FieldType));
        Assert.Equal(["Сборка", "Пайка"], shell.OpFieldIds!.Select(id => fields.Single(f => f.Id == id).Name));
        Assert.All(shell.OpFieldIds!, id =>
        {
            var field = fields.Single(f => f.Id == id);
            Assert.Equal("number", field.FieldType);
            Assert.Equal(-1, NumberFormatValue.FromConfig(field.ConfigJson).Decimals);
        });

        string[] expected =
        [
            shell.FileFieldId.ToString(), shell.OperFieldId.ToString(),
            shell.OpFieldIds![0].ToString(), shell.OpFieldIds[1].ToString(),
        ];
        Assert.Equal(expected, expected.OrderBy(key => order[key]).ToArray());
    }

    /// <summary>Без колонок по операциям каркас прежний: шесть полей, ни «Операций», ни операций.</summary>
    [Fact]
    public void Shell_without_operations_is_unchanged()
    {
        using var db = new TempDatabase();

        var shell = TimeCollectionWriter.CreateShell(db.Constructor, "Трудоемкость");

        Assert.Equal(0, shell.OperFieldId);
        Assert.Null(shell.OpFieldIds);
        Assert.Equal(6, db.Constructor.GetFields(shell.CollectionId).Count);
    }

    /// <summary>Название операции не из списка может совпасть с колонкой свода — поле с таким
    /// именем уже есть, и его создание уронило бы весь прогон. Такая колонка получает приписку.</summary>
    [Fact]
    public void Operation_named_like_a_summary_column_gets_a_suffix()
    {
        using var db = new TempDatabase();

        var shell = TimeCollectionWriter.CreateShell(db.Constructor, "Трудоемкость", ["Дата", "Сборка"]);
        var names = shell.OpFieldIds!.Select(id => db.Constructor.GetFields(shell.CollectionId)
                                                     .Single(f => f.Id == id).Name);

        Assert.Equal(["Дата" + TimeCollectionWriter.OpNameSuffix, "Сборка"], names);
    }

    /// <summary>Время операций ложится в свои колонки в каноне хранения числа; операции,
    /// которой у изделия нет, ячейка не заводится.</summary>
    [Fact]
    public void Operation_times_land_in_their_columns()
    {
        using var db = new TempDatabase();

        var (shell, layout) = FillWithOps(db,
        [
            WithOps(Row("Корпус", "АБВ.1", 12.5, Day1), ("Сборка", 10), ("Контроль", 2.5)),
            WithOps(Row("Вал",    "АБВ.2", 3,    Day1), ("Пайка", 3)),
        ]);

        var records = db.Constructor.GetRecords(shell.CollectionId);
        var body    = db.Constructor.GetFieldValues(records.Single(r => r.Title == "Корпус").Id!.Value);

        Assert.Equal("10",  body[OpField(shell, layout, "Сборка")]);
        Assert.Equal("2,5", body[OpField(shell, layout, "Контроль")]);
        Assert.False(body.ContainsKey(OpField(shell, layout, "Пайка")));
        Assert.False(body.ContainsKey(shell.OperFieldId));                // расхождений нет
    }

    /// <summary>
    /// Одно обозначение, одна трудоемкость, разная разбивка: обе записи «отличается», и
    /// краснеют ячейки разошедшихся операций — в том числе пустая, где операции нет. Совпавшая
    /// операция и ячейка «Тр-ть» не краснеют, заливка строк остается от группы: строку целиком
    /// операции не красят (решение пользователя от 22.09.2026).
    /// </summary>
    [Fact]
    public void Diverging_breakdown_is_marked_and_only_its_cells_turn_red()
    {
        using var db = new TempDatabase();

        var (shell, layout) = FillWithOps(db,
        [
            WithOps(Row("Корпус", "АБВ.1", 12, Day2), ("Сборка", 10), ("Контроль", 2)),
            WithOps(Row("Корпус", "АБВ.1", 12, Day1), ("Сборка", 8),  ("Пайка", 2), ("Контроль", 2)),
        ]);

        var ids   = db.Constructor.GetRecords(shell.CollectionId).Select(r => r.Id!.Value).ToList();
        var cells = db.Constructor.GetCellStylesBatch(shell.CollectionId, ids);

        foreach (var id in ids)
        {
            Assert.Equal(OperationColumns.DiffersText, db.Constructor.GetFieldValues(id)[shell.OperFieldId]);

            var red = cells[id].Where(c => c.Value.Bg == "#FF3B30").Select(c => c.Key).ToHashSet();
            Assert.Equal(new HashSet<string>
            {
                OpField(shell, layout, "Сборка").ToString(),
                OpField(shell, layout, "Пайка").ToString(),
            }, red);
        }

        Assert.All(db.Constructor.GetRowStyles(shell.CollectionId).Values, s => Assert.Equal("#FFCC00", s.Bg));
    }

    // Понижает текущего пользователя до допущенного не-админа с заданной маской на всю базу
    // (тот же прием, что в матрице прав ядра: маски пишет только администратор).
    private static void SetRights(TempDatabase db, PermAction mask)
    {
        var me = db.Context.DbUsers.First(u => u.UserKey == CurrentUser.Key);
        me.IsAdmin = true;
        db.Context.SaveChanges();
        db.Permissions.Reload(notify: false);
        db.Permissions.SetMask(db.Permissions.CurrentUserId, null, (int)mask);

        me.IsAdmin    = false;   // теперь маска действует: администратору можно все
        me.IsApproved = true;
        db.Context.SaveChanges();
        db.Permissions.Reload(notify: false);
    }
}
