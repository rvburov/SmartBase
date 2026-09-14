namespace SmartBase.Core.Models;

/// <summary>Почему значение не легло в ячейку. Показывается пользователю итогом, поэтому
/// причины крупные: дробить их мельче человеку не нужно.</summary>
public enum PasteSkipReason
{
    /// <summary>Столбец заполняется сам (дата создания, автор) или закрыт для правки.</summary>
    ComputedColumn,
    /// <summary>Связь и файл: значение живет не текстом, его выбирают в своем окне.</summary>
    NotTextColumn,
    /// <summary>Текст не подходит типу столбца: «абв» в число, «вчера» в дату.</summary>
    WrongFormat,
    /// <summary>Значения нет в списке допустимых (столбцы «Список», «Статус», «Пользователь»).</summary>
    NotInOptions,
    /// <summary>Блок вышел за последнюю строку или последний столбец таблицы.</summary>
    OutsideTable,
    /// <summary>Пустое значение пришлось на «Название»: запись без названия теряется в списках и
    /// связях, поэтому стереть его вставкой нельзя — как и очисткой.</summary>
    EmptyTitle,
}

/// <summary>Столбец глазами вставки: все, что нужно решить про одну ячейку, без ViewModels.
/// <paramref name="DecimalSeparator"/> — канон хранения числа у ЭТОГО столбца: значение
/// приводится к нему сразу, иначе в колонке смешались бы «42.5» и «42,5».
/// <paramref name="IsTitle"/> — системный столбец «Название»: у него свое правило пустого значения.
/// <paramref name="Options"/> — допустимые значения закрытого списка; для «Пользователя» это
/// допущенные пользователи базы (без списка «Пользователь» принимается как текст).</summary>
public sealed record PasteColumn(
    string Key,
    string FieldType,
    bool IsReadOnly = false,
    IReadOnlyList<string>? Options = null,
    DecimalMark DecimalSeparator = DecimalMark.Comma,
    bool IsTitle = false);

/// <summary>Одно значение, готовое лечь в базу: координаты, столбец и значение В ФОРМАТЕ ХРАНЕНИЯ.</summary>
public sealed record PasteCell(int Row, int Column, string ColumnKey, string StorageValue);

/// <summary>Что получится из вставки: что записать и сколько чего пропущено.</summary>
public sealed record TablePastePlan(
    IReadOnlyList<PasteCell> Cells,
    IReadOnlyDictionary<PasteSkipReason, int> Skipped)
{
    public int SkippedCount => Skipped.Values.Sum();
}

/// <summary>
/// Планировщик вставки: по матрице из буфера, точке вставки и описанию столбцов решает, что
/// именно записать. Ничего не пишет сам — только считает, поэтому проверяется тестами целиком.
///
/// Правило разлива — привычное по табличным редакторам и зависит от того, ЧТО выделено:
///
/// <list type="bullet">
/// <item>выделена одна ячейка — она левый верхний угол, блок из буфера идет от нее вниз и
///       вправо, каков бы ни был его размер;</item>
/// <item>выделено больше ячеек, чем лежит в буфере, — блок ПОВТОРЯЕТСЯ по выделению: одно
///       значение заполняет его целиком, два значения ложатся парами (два, снова два, снова
///       два), и так для любого блока;</item>
/// <item>выделение меньше блока — выделение не в счет, работает первое правило.</item>
/// </list>
///
/// Столбцы передаются ТОЛЬКО видимые, в порядке показа: скрытый в «Отображении» столбец
/// копирование пропускает, и если бы вставка отводила ему место, скопированный блок ложился бы
/// обратно со сдвигом на чужие столбцы.
///
/// Все, что вышло за последнюю строку или последний столбец таблицы, отбрасывается: строки при
/// вставке не добавляются — иначе один Ctrl+V молча заводил бы сотни записей.
///
/// Значения приводятся к формату ХРАНЕНИЯ, а не к показанному на экране: дата уходит в базу
/// каноном DateValue, число — с разделителем своего столбца, чекбокс — «1»/«0». Иначе сортировка
/// и фильтр начали бы видеть два разных представления одного значения.
/// </summary>
public static class TablePastePlanner
{
    /// <param name="columns">Видимые столбцы таблицы в порядке показа.</param>
    /// <param name="selectionRows">Высота выделения в ячейках (1 — выделена одна ячейка).</param>
    /// <param name="selectionColumns">Ширина выделения в видимых столбцах.</param>
    public static TablePastePlan Plan(
        IReadOnlyList<IReadOnlyList<string>> values,
        int startRow,
        int startColumn,
        IReadOnlyList<PasteColumn> columns,
        int rowCount,
        int selectionRows = 1,
        int selectionColumns = 1)
    {
        var cells   = new List<PasteCell>();
        var skipped = new Dictionary<PasteSkipReason, int>();

        void Skip(PasteSkipReason reason) =>
            skipped[reason] = skipped.TryGetValue(reason, out var n) ? n + 1 : 1;

        var blockRows = values.Count;
        var blockCols = 0;
        foreach (var row in values) blockCols = Math.Max(blockCols, row.Count);
        if (blockRows == 0 || blockCols == 0) return new TablePastePlan(cells, skipped);

        // Размножаем блок по выделению, только если выделение БОЛЬШЕ блока и вмещает его целиком.
        // Иначе выделение не в счет: блок разливается от левого верхнего угла как есть.
        var tile = selectionRows >= blockRows && selectionColumns >= blockCols
                && (selectionRows > blockRows || selectionColumns > blockCols);

        var height = tile ? selectionRows    : blockRows;
        var width  = tile ? selectionColumns : blockCols;

        for (var r = 0; r < height; r++)
        {
            var targetRow = startRow + r;
            var source    = values[r % blockRows];

            for (var c = 0; c < width; c++)
            {
                var targetCol = startColumn + c;

                // В блоке этой ячейки нет (строки из буфера бывают разной длины) — цель не трогаем.
                var sourceCol = c % blockCols;
                if (sourceCol >= source.Count) continue;

                if (targetRow >= rowCount || targetCol >= columns.Count || targetRow < 0 || targetCol < 0)
                {
                    Skip(PasteSkipReason.OutsideTable);
                    continue;
                }

                var column = columns[targetCol];
                if (Convert(source[sourceCol], column) is { } storage)
                    cells.Add(new PasteCell(targetRow, targetCol, column.Key, storage));
                else
                    Skip(SkipReasonFor(column, source[sourceCol]));
            }
        }

        return new TablePastePlan(cells, skipped);
    }

    /// <summary>Можно ли вообще писать текст в такой столбец. Отдельно от <see cref="Convert"/>,
    /// потому что этим же вопросом задается очистка: очищать вычисляемое и связи нельзя.</summary>
    public static bool IsWritable(PasteColumn column) =>
        !column.IsReadOnly
        && !FieldTypeInfo.IsComputed(column.FieldType)
        && !FieldTypeInfo.IsRelation(column.FieldType)
        && column.FieldType != "file";

    /// <summary>Текст → значение в формате хранения; null — в эту ячейку такой текст не ложится.</summary>
    public static string? Convert(string? text, PasteColumn column)
    {
        if (!IsWritable(column)) return null;

        var source = text ?? "";

        // «Название» пустым не вставляется — по той же причине, по какой его не стирает очистка:
        // запись без названия превращается в «Без названия» и теряется в списках и связях.
        // Непустое название пишется как есть: пробелы в тексте — тоже данные.
        if (column.IsTitle)
            return string.IsNullOrWhiteSpace(source) ? null : source;

        // Текст, ссылка и все неизвестное — как есть, без обрезки: пробелы по краям в тексте могут
        // быть частью значения, и молча их срезать значит портить данные. Пустая ячейка буфера
        // очищает целевую.
        if (!IsTyped(column.FieldType)) return source;

        // Типизированным значениям краевые пробелы ничего не добавляют, зато мешают разбору.
        var raw = source.Trim();
        if (raw.Length == 0) return "";

        switch (column.FieldType)
        {
            case "number":
                // Формат числа в буфере может быть каким угодно («42.5», «42,5»), а в базе у
                // столбца свой разделитель — приводим сразу, как это делает обычная правка ячейки.
                return Exporters.TableWriters.WriterHelpers.TryNumber(raw, out _)
                    ? NumberFormatValue.NormalizeStorage(raw, column.DecimalSeparator)
                    : null;

            case "date":
                return DateValue.TryParse(raw, out var date) ? date.ToStorage() : null;

            case "checkbox":
                return ParseCheckbox(raw);

            case "select":
            case "status":
                // Список закрыт: чужое значение показалось бы в ячейке, но не нашлось бы ни в
                // фильтре, ни в выпадающем списке — то есть выглядело бы как порча данных.
                return HasOption(column, raw, out var exact) ? exact : null;

            case "user":
                // «Пользователь» — тоже закрытый список: имя, которого нет среди допущенных, в
                // ячейке выглядело бы исполнителем, но не нашлось бы ни в фильтре, ни в выборе.
                if (column.Options is null) return raw;
                return HasOption(column, raw, out var user) ? user : null;

            case "multiselect":
                return ParseMultiSelect(raw, column);

            default:
                return raw;
        }
    }

    // Типы, у которых значение разбирается, а не хранится текстом как есть.
    private static bool IsTyped(string fieldType) =>
        fieldType is "number" or "date" or "checkbox" or "select" or "status" or "multiselect" or "user";

    private static PasteSkipReason SkipReasonFor(PasteColumn column, string text)
    {
        if (FieldTypeInfo.IsComputed(column.FieldType)) return PasteSkipReason.ComputedColumn;
        if (FieldTypeInfo.IsRelation(column.FieldType) || column.FieldType == "file")
            return PasteSkipReason.NotTextColumn;
        if (column.IsReadOnly) return PasteSkipReason.ComputedColumn;
        if (column.IsTitle) return PasteSkipReason.EmptyTitle;
        if (column.FieldType is "select" or "status" or "multiselect" or "user") return PasteSkipReason.NotInOptions;
        return PasteSkipReason.WrongFormat;
    }

    // Чекбокс: понимаем и то, что показано в ячейке («✓»), и то, что лежит в базе, и привычные
    // слова по-русски и по-английски. Иначе копирование из соседней колонки своей же таблицы не
    // вставлялось бы обратно.
    private static string? ParseCheckbox(string raw) => raw.ToLowerInvariant() switch
    {
        "1" or "true" or "да" or "yes" or "✓" or "x" or "х" => "1",
        "0" or "false" or "нет" or "no" or "-"              => "0",
        _                                                   => null,
    };

    private static bool HasOption(PasteColumn column, string value, out string exact)
    {
        // Сравниваем без учета регистра, а пишем ТОЧНОЕ значение опции: «в работе» из чужой
        // таблицы должно стать «В работе», иначе в колонке заведутся два похожих значения.
        foreach (var option in column.Options ?? [])
            if (string.Equals(option, value, StringComparison.OrdinalIgnoreCase))
            {
                exact = option;
                return true;
            }
        exact = value;
        return false;
    }

    // Мультисписок: в буфере он лежит перечислением через запятую (так же, как показан в
    // ячейке), в базе — JSON-массивом. Ни одно значение не опознали — вставлять нечего.
    private static string? ParseMultiSelect(string raw, PasteColumn column)
    {
        var parts = raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var kept  = new List<string>();
        foreach (var part in parts)
            if (HasOption(column, part, out var exact) && !kept.Contains(exact))
                kept.Add(exact);
        return kept.Count > 0 ? MultiSelectValue.Serialize(kept) : null;
    }
}
