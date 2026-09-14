using SmartBase.Core.Models;

namespace SmartBase.Tests.Unit;

/// <summary>
/// Планировщик вставки: куда лягут значения из буфера и что будет пропущено. Правила — привычные
/// по табличным редакторам (выделенная ячейка = левый верхний угол блока, блок разливается вниз и
/// вправо), но с двумя
/// собственными ограничениями: строки при вставке НЕ добавляются, и значение приводится к формату
/// хранения, а не к показанному на экране.
/// </summary>
public sealed class TablePastePlannerTests
{
    private static readonly PasteColumn Title  = new(TitleKey, "text", IsTitle: true);
    private static readonly PasteColumn User   = new("19", "user", Options: ["Иванов И.", "Петрова А."]);
    private static readonly PasteColumn Text   = new("10", "text");
    private static readonly PasteColumn Number = new("11", "number");
    private static readonly PasteColumn Date   = new("12", "date");
    private static readonly PasteColumn Check  = new("13", "checkbox");
    private static readonly PasteColumn Status = new("14", "status", Options: ["Запланировано", "В работе", "Выполнено"]);
    private static readonly PasteColumn Multi  = new("15", "multiselect", Options: ["Красный", "Синий", "Зеленый"]);
    private static readonly PasteColumn Created = new("16", "created_at");
    private static readonly PasteColumn Link   = new("17", "relation");
    private static readonly PasteColumn File   = new("18", "file");

    private const string TitleKey = "__title__";

    private static IReadOnlyList<IReadOnlyList<string>> Values(params string[][] rows) =>
        rows.Select(r => (IReadOnlyList<string>)r).ToList();

    // ── Разлив блока ──────────────────────────────────────────────────────────

    [Fact]
    public void Block_spreads_down_and_right_from_the_start_cell()
    {
        var plan = TablePastePlanner.Plan(
            Values(["a", "b"], ["c", "d"]),
            startRow: 1, startColumn: 0,
            columns: [Title, Text], rowCount: 5);

        Assert.Equal(4, plan.Cells.Count);
        Assert.Equal(0, plan.SkippedCount);
        Assert.Equal(new PasteCell(1, 0, TitleKey, "a"), plan.Cells[0]);
        Assert.Equal(new PasteCell(2, 1, "10", "d"), plan.Cells[3]);
    }

    /// <summary>Строки при вставке не заводятся: иначе один Ctrl+V молча создал бы сотни записей.
    /// Все, что не влезло, отбрасывается и попадает в итог.</summary>
    [Fact]
    public void Everything_below_the_last_row_is_dropped()
    {
        var plan = TablePastePlanner.Plan(
            Values(["a"], ["b"], ["c"]),
            startRow: 1, startColumn: 0,
            columns: [Text], rowCount: 2);

        Assert.Single(plan.Cells);
        Assert.Equal(2, plan.Skipped[PasteSkipReason.OutsideTable]);
    }

    [Fact]
    public void Everything_right_of_the_last_column_is_dropped()
    {
        var plan = TablePastePlanner.Plan(
            Values(["a", "b", "c"]),
            startRow: 0, startColumn: 0,
            columns: [Title, Text], rowCount: 1);

        Assert.Equal(2, plan.Cells.Count);
        Assert.Equal(1, plan.Skipped[PasteSkipReason.OutsideTable]);
    }

    /// <summary>Пустая ячейка буфера очищает целевую — как в табличном редакторе.</summary>
    [Fact]
    public void Empty_value_clears_the_target_cell()
    {
        var plan = TablePastePlanner.Plan(Values([""]), 0, 0, [Text], rowCount: 1);

        Assert.Equal("", Assert.Single(plan.Cells).StorageValue);
    }

    /// <summary>«Название» пустым не вставляется — как и очистка его не стирает: запись без
    /// названия теряется в списках и связях. Остальные значения того же блока ложатся.</summary>
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Empty_value_does_not_erase_the_title(string text)
    {
        var plan = TablePastePlanner.Plan(Values([text, "хвост"]), 0, 0, [Title, Text], rowCount: 1);

        var cell = Assert.Single(plan.Cells);
        Assert.Equal("10", cell.ColumnKey);
        Assert.Equal(1, plan.Skipped[PasteSkipReason.EmptyTitle]);
    }

    /// <summary>Пробелы по краям текста — часть значения: срезать их молча значит портить данные.</summary>
    [Fact]
    public void Text_is_pasted_verbatim()
    {
        var plan = TablePastePlanner.Plan(Values(["  отступ  "]), 0, 0, [Text], rowCount: 1);

        Assert.Equal("  отступ  ", Assert.Single(plan.Cells).StorageValue);
    }

    /// <summary>«Пользователь» — закрытый список: чужое имя в ячейке выглядело бы исполнителем,
    /// но не нашлось бы ни в фильтре, ни в выборе.</summary>
    [Fact]
    public void User_must_be_one_of_the_approved_users()
    {
        var known   = TablePastePlanner.Plan(Values(["иванов и."]), 0, 0, [User], rowCount: 1);
        var unknown = TablePastePlanner.Plan(Values(["Сидоров"]), 0, 0, [User], rowCount: 1);

        Assert.Equal("Иванов И.", Assert.Single(known.Cells).StorageValue);
        Assert.Empty(unknown.Cells);
        Assert.Equal(1, unknown.Skipped[PasteSkipReason.NotInOptions]);
    }

    // ── Приведение к формату хранения ─────────────────────────────────────────

    [Theory]
    [InlineData("42.5", DecimalMark.Comma, "42,5")]
    [InlineData("42,5", DecimalMark.Dot,   "42.5")]
    [InlineData("-7",   DecimalMark.Comma, "-7")]
    public void Numbers_take_the_separator_of_their_column(string text, DecimalMark mark, string stored)
    {
        var column = Number with { DecimalSeparator = mark };

        var plan = TablePastePlanner.Plan(Values([text]), 0, 0, [column], rowCount: 1);

        Assert.Equal(stored, Assert.Single(plan.Cells).StorageValue);
    }

    [Fact]
    public void Non_numeric_text_does_not_go_into_a_number_column()
    {
        var plan = TablePastePlanner.Plan(Values(["абв"]), 0, 0, [Number], rowCount: 1);

        Assert.Empty(plan.Cells);
        Assert.Equal(1, plan.Skipped[PasteSkipReason.WrongFormat]);
    }

    /// <summary>Дата уходит в базу каноном, а не тем, как ее показали: иначе сортировка и фильтр
    /// увидели бы два разных представления одного дня.</summary>
    [Fact]
    public void Date_is_stored_in_canonical_form()
    {
        var plan = TablePastePlanner.Plan(Values(["01.02.2026"]), 0, 0, [Date], rowCount: 1);

        DateValue.TryParse("01.02.2026", out var expected);
        Assert.Equal(expected.ToStorage(), Assert.Single(plan.Cells).StorageValue);
    }

    [Fact]
    public void Unparseable_date_is_skipped()
    {
        var plan = TablePastePlanner.Plan(Values(["вчера"]), 0, 0, [Date], rowCount: 1);

        Assert.Empty(plan.Cells);
        Assert.Equal(1, plan.Skipped[PasteSkipReason.WrongFormat]);
    }

    /// <summary>Чекбокс понимает и галочку из своей же таблицы, и привычные «да»/«нет».</summary>
    [Theory]
    [InlineData("✓", "1")]
    [InlineData("1", "1")]
    [InlineData("да", "1")]
    [InlineData("ИСТИНА", "1")]
    [InlineData("✔", "1")]
    [InlineData("0", "0")]
    [InlineData("нет", "0")]
    [InlineData("ЛОЖЬ", "0")]
    [InlineData("может быть", null)]
    [InlineData("", "")]
    public void Checkbox_understands_common_spellings(string text, string? stored)
    {
        var plan = TablePastePlanner.Plan(Values([text]), 0, 0, [Check], rowCount: 1);

        if (stored is null) Assert.Empty(plan.Cells);
        else Assert.Equal(stored, Assert.Single(plan.Cells).StorageValue);
    }

    /// <summary>Значение записывается ТОЧНО как в списке опций: «в работе» из чужой таблицы становится
    /// «В работе», иначе в колонке завелись бы два похожих значения.</summary>
    [Fact]
    public void Option_value_is_matched_case_insensitively_but_stored_exactly()
    {
        var plan = TablePastePlanner.Plan(Values(["в работе"]), 0, 0, [Status], rowCount: 1);

        Assert.Equal("В работе", Assert.Single(plan.Cells).StorageValue);
    }

    [Fact]
    public void Value_outside_the_option_list_is_skipped()
    {
        var plan = TablePastePlanner.Plan(Values(["Отменено"]), 0, 0, [Status], rowCount: 1);

        Assert.Empty(plan.Cells);
        Assert.Equal(1, plan.Skipped[PasteSkipReason.NotInOptions]);
    }

    [Fact]
    public void Multiselect_takes_known_values_in_their_exact_spelling()
    {
        var plan = TablePastePlanner.Plan(Values(["красный, Синий; Красный"]), 0, 0, [Multi], rowCount: 1);

        var stored = Assert.Single(plan.Cells).StorageValue;
        Assert.Equal(["Красный", "Синий"], MultiSelectValue.Parse(stored));
    }

    /// <summary>Незнакомый кусок — вся ячейка пропускается: молча оставить половину
    /// скопированного значило бы потерять остальное без следа.</summary>
    [Fact]
    public void Multiselect_with_an_unknown_value_is_skipped_whole()
    {
        var plan = TablePastePlanner.Plan(Values(["Красный, Отменено"]), 0, 0, [Multi], rowCount: 1);

        Assert.Empty(plan.Cells);
        Assert.Equal(1, plan.Skipped[PasteSkipReason.NotInOptions]);
    }

    /// <summary>Опция с запятой внутри не разваливается на куски, а разделитель столбца (здесь —
    /// точка с запятой) понимается так же, как запятая.</summary>
    [Fact]
    public void Multiselect_options_may_contain_separators()
    {
        var column = new PasteColumn("15", "multiselect", Options: ["Ул. Ленина, 5", "Склад"], Separator: "; ");

        var plan = TablePastePlanner.Plan(Values(["ул. ленина, 5; Склад"]), 0, 0, [column], rowCount: 1);

        Assert.Equal(["Ул. Ленина, 5", "Склад"], MultiSelectValue.Parse(Assert.Single(plan.Cells).StorageValue));
    }

    // ── Размножение по выделению ──────────────────────────────────────────────

    /// <summary>Одно значение на выделенный столбец — заполняет его целиком.</summary>
    [Fact]
    public void One_value_fills_the_whole_selection()
    {
        var plan = TablePastePlanner.Plan(Values(["Заказ"]), 0, 0, [Title], rowCount: 4,
                                          selectionRows: 4, selectionColumns: 1);

        Assert.Equal(4, plan.Cells.Count);
        Assert.All(plan.Cells, c => Assert.Equal("Заказ", c.StorageValue));
        Assert.Equal([0, 1, 2, 3], plan.Cells.Select(c => c.Row));
    }

    /// <summary>Два значения ложатся парами по кругу: два, снова два, снова два.</summary>
    [Fact]
    public void Block_repeats_down_the_selection()
    {
        var plan = TablePastePlanner.Plan(Values(["А"], ["Б"]), 0, 0, [Title], rowCount: 6,
                                          selectionRows: 6, selectionColumns: 1);

        Assert.Equal(["А", "Б", "А", "Б", "А", "Б"], plan.Cells.Select(c => c.StorageValue));
    }

    /// <summary>Выделение не кратно блоку — размножаем сколько влезло и обрываем по краю
    /// выделения: отказывать человеку в работе из-за арифметики незачем.</summary>
    [Fact]
    public void Repeat_is_cut_at_the_edge_of_the_selection()
    {
        var plan = TablePastePlanner.Plan(Values(["А"], ["Б"]), 0, 0, [Title], rowCount: 5,
                                          selectionRows: 5, selectionColumns: 1);

        Assert.Equal(["А", "Б", "А", "Б", "А"], plan.Cells.Select(c => c.StorageValue));
    }

    /// <summary>Размножение работает и вширь: одно значение — на весь выделенный прямоугольник.</summary>
    [Fact]
    public void One_value_fills_a_rectangle()
    {
        var plan = TablePastePlanner.Plan(Values(["зд"]), 0, 0, [Title, Text], rowCount: 3,
                                          selectionRows: 3, selectionColumns: 2);

        Assert.Equal(6, plan.Cells.Count);
        Assert.All(plan.Cells, c => Assert.Equal("зд", c.StorageValue));
    }

    /// <summary>Выделена одна ячейка — выделение ни при чем: блок разливается от нее как есть.</summary>
    [Fact]
    public void Single_selected_cell_pastes_the_block_as_is()
    {
        var plan = TablePastePlanner.Plan(Values(["А"], ["Б"]), 0, 0, [Title], rowCount: 6,
                                          selectionRows: 1, selectionColumns: 1);

        Assert.Equal(["А", "Б"], plan.Cells.Select(c => c.StorageValue));
    }

    /// <summary>Выделение МЕНЬШЕ блока — тоже не в счет: вставляем блок целиком, не обрезая его
    /// по выделению: лишнее просто выходит за его границы.</summary>
    [Fact]
    public void Selection_smaller_than_the_block_does_not_cut_it()
    {
        var plan = TablePastePlanner.Plan(Values(["А"], ["Б"], ["В"]), 0, 0, [Title], rowCount: 6,
                                          selectionRows: 2, selectionColumns: 1);

        Assert.Equal(["А", "Б", "В"], plan.Cells.Select(c => c.StorageValue));
    }

    /// <summary>Размножение не выходит за последнюю строку таблицы: строки вставка не добавляет.</summary>
    [Fact]
    public void Repeat_stops_at_the_last_row_of_the_table()
    {
        var plan = TablePastePlanner.Plan(Values(["А"]), 0, 0, [Title], rowCount: 2,
                                          selectionRows: 5, selectionColumns: 1);

        Assert.Equal(2, plan.Cells.Count);
        Assert.Equal(3, plan.Skipped[PasteSkipReason.OutsideTable]);
    }

    // ── Куда вставить нельзя ──────────────────────────────────────────────────

    [Fact]
    public void Computed_columns_are_skipped()
    {
        var plan = TablePastePlanner.Plan(Values(["01.02.2026"]), 0, 0, [Created], rowCount: 1);

        Assert.Empty(plan.Cells);
        Assert.Equal(1, plan.Skipped[PasteSkipReason.ComputedColumn]);
    }

    /// <summary>Файл текстом не задается, связь с другой базой — тоже: ее цель в другом файле, и
    /// найти записи по названию отсюда нельзя.</summary>
    [Fact]
    public void File_and_cross_database_relation_columns_are_skipped()
    {
        var plan = TablePastePlanner.Plan(Values(["что-нибудь", "Проект А"]), 0, 0,
                                          [File, Link with { IsCrossDatabaseRelation = true }], rowCount: 1);

        Assert.Empty(plan.Cells);
        Assert.Equal(2, plan.Skipped[PasteSkipReason.NotTextColumn]);
    }

    /// <summary>Связь внутри базы вставляется названиями: планировщик только чистит текст, записи
    /// по названиям находит база.</summary>
    [Fact]
    public void Relation_is_passed_on_as_titles()
    {
        var plan = TablePastePlanner.Plan(Values(["  Проект А, Проект Б "]), 0, 0, [Link], rowCount: 1);

        Assert.Equal("Проект А, Проект Б", Assert.Single(plan.Cells).StorageValue);
    }

    /// <summary>Read-only столбец пропускается, НО позицию в блоке занимает: соседи справа не
    /// съезжают на колонку влево. Скрытые в «Отображении» колонки сюда не попадают вовсе —
    /// таблица передает только видимые, как их и копирует.</summary>
    [Fact]
    public void Read_only_column_is_skipped_but_keeps_its_place()
    {
        var plan = TablePastePlanner.Plan(
            Values(["Заказ", "нельзя", "хвост"]), 0, 0,
            [Title, Text with { IsReadOnly = true }, Text], rowCount: 1);

        Assert.Equal(2, plan.Cells.Count);
        Assert.Equal(1, plan.Skipped[PasteSkipReason.ComputedColumn]);
        // «хвост» лег в третий столбец, а не подтянулся во второй.
        Assert.Equal(2, plan.Cells[1].Column);
        Assert.Equal("хвост", plan.Cells[1].StorageValue);
    }

    /// <summary>Галочка правится кликом, а не текстовым редактором, и таблица держит такую
    /// колонку read-only на уровне сетки. Для вставки это НЕ запрет: значение из буфера в нее
    /// ложится штатно (таблица снимает признак, см. PasteColumnOf).</summary>
    [Fact]
    public void Checkbox_column_is_writable()
    {
        Assert.True(TablePastePlanner.IsWritable(Check));

        var plan = TablePastePlanner.Plan(Values(["да"]), 0, 0, [Check], rowCount: 1);

        Assert.Equal("1", Assert.Single(plan.Cells).StorageValue);
    }

    [Fact]
    public void Writable_check_matches_the_skipping_rules()
    {
        Assert.True(TablePastePlanner.IsWritable(Text));
        Assert.True(TablePastePlanner.IsWritable(Title));
        Assert.False(TablePastePlanner.IsWritable(Created));
        Assert.True(TablePastePlanner.IsWritable(Link));
        Assert.False(TablePastePlanner.IsWritable(Link with { IsCrossDatabaseRelation = true }));
        Assert.False(TablePastePlanner.IsWritable(File));
        Assert.False(TablePastePlanner.IsWritable(Text with { IsReadOnly = true }));
    }

    /// <summary>Смешанный блок: часть значений ложится, часть нет — и то, что легло, не зависит
    /// от того, что было пропущено рядом.</summary>
    [Fact]
    public void Mixed_block_reports_every_reason()
    {
        var plan = TablePastePlanner.Plan(
            Values(["Заказ", "абв", "01.02.2026", "нечто"]),
            startRow: 0, startColumn: 0,
            columns: [Title, Number, Date, Created], rowCount: 1);

        Assert.Equal(2, plan.Cells.Count);
        Assert.Equal(2, plan.SkippedCount);
        Assert.Equal(1, plan.Skipped[PasteSkipReason.WrongFormat]);
        Assert.Equal(1, plan.Skipped[PasteSkipReason.ComputedColumn]);
    }
}
