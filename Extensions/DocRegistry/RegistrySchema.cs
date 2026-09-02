using SmartBase.Database.Repositories;

namespace SmartBase.Extensions.DocRegistry;

/// <summary>
/// Раскладка коллекции регистрации: id полей, в которые расширение пишет значения.
/// Строится только проверкой <see cref="RegistrySchema.Check"/> — существование объекта
/// означает, что структура коллекции сошлась с эталоном.
/// </summary>
/// <param name="DateFieldType">Тип колонки «Дата регистрации» в коллекции: от него зависит
/// канон записи даты (см. <see cref="DocRegistrar"/>).</param>
public sealed record RegistryShape(
    int CollectionId, string CollectionName,
    int DesignationFieldId, int FioFieldId, int DateFieldId, string DateFieldType,
    int ProjectFieldId, int AccountingFieldId, int FirstUseFieldId, int SummaryFieldId);

/// <summary>Итог проверки коллекции: либо раскладка, либо причина отказа.</summary>
/// <param name="Shape">Раскладка полей; <c>null</c> — коллекция не подходит.</param>
/// <param name="Problem">Что именно не сошлось (для второй строки сообщения); <c>null</c> — все сошлось.</param>
public sealed record SchemaCheck(RegistryShape? Shape, string? Problem)
{
    public bool Ok => Shape is not null;
}

/// <summary>
/// Эталон структуры коллекции регистрации и проверка выбранной коллекции на соответствие.
///
/// Столбцов ровно восемь и идут они в заданном порядке — это и есть признак «той самой»
/// коллекции. Расширение пишет в чужую готовую коллекцию (в отличие от «Документов в
/// коллекцию», которое создает свою), поэтому промахнуться легко: подтвердить структуру
/// заранее дешевле, чем разбирать потом записи, разложенные не по тем столбцам.
///
/// Первый столбец — СИСТЕМНАЯ колонка записи (Title), переименованная в «Наименование»;
/// остальные семь — обычные поля коллекции. Сравнение имен прощает регистр, крайние пробелы и
/// разнобой в написании буквы «е» (см. <see cref="SameName"/>), но не прощает порядок:
/// перепутанные местами столбцы означают, что значения лягут не туда.
/// </summary>
public static class RegistrySchema
{
    /// <summary>Ключ системной колонки записи (та же строка, что в ConstructorRepository/TableRowVm).</summary>
    public const string TitleKey = "__title__";

    public const string TitleColumn       = "Наименование";
    public const string DesignationColumn = "Обозначение";
    public const string FioColumn         = "ФИО регистрирующего";
    public const string DateColumn        = "Дата регистрации";
    public const string ProjectColumn     = "Проект";
    public const string AccountingColumn  = "Отметка учета";
    public const string FirstUseColumn    = "Примечание (первичное применение)";
    public const string SummaryColumn     = "Примечание (краткая характеристика)";

    /// <summary>Эталон: столбцы слева направо. Первый — системная колонка записи.</summary>
    public static IReadOnlyList<string> Columns =>
    [
        TitleColumn, DesignationColumn, FioColumn, DateColumn,
        ProjectColumn, AccountingColumn, FirstUseColumn, SummaryColumn,
    ];

    /// <summary>Одна колонка коллекции в порядке показа. <paramref name="FieldId"/> = <c>null</c>
    /// у системной колонки записи.</summary>
    public sealed record Column(string Name, int? FieldId, string FieldType);

    /// <summary>
    /// Проверить коллекцию и, если структура сошлась, отдать раскладку ее полей.
    /// </summary>
    public static SchemaCheck Check(ConstructorRepository repo, int collectionId, string collectionName)
    {
        var columns = Ordered(repo, collectionId);
        var wanted  = Columns;

        if (columns.Count != wanted.Count)
            return new SchemaCheck(null,
                $"столбцов {columns.Count}, а должно быть {wanted.Count}: {string.Join(", ", wanted)}");

        for (var i = 0; i < wanted.Count; i++)
            if (!SameName(columns[i].Name, wanted[i]))
                return new SchemaCheck(null,
                    $"{i + 1}-й столбец — «{columns[i].Name}», а должен быть «{wanted[i]}»");

        // Первый столбец обязан быть системной колонкой записи: значение «Наименования» — это
        // название записи, а не значение поля. Одноименное обычное поле на первом месте прошло
        // бы проверку по именам, но писать в него как в название было бы ошибкой.
        if (columns[0].FieldId is not null)
            return new SchemaCheck(null,
                $"«{TitleColumn}» должен быть системным столбцом записи, а не обычным полем");

        // Обратный случай: системную колонку переименовали и увели вглубь ряда. Тогда у одного
        // из семи полей нет id, и писать в него нечем.
        for (var i = 1; i < columns.Count; i++)
            if (columns[i].FieldId is null)
                return new SchemaCheck(null,
                    $"«{columns[i].Name}» — системный столбец записи, а должен быть обычным полем");

        return new SchemaCheck(new RegistryShape(
            collectionId, collectionName,
            DesignationFieldId: columns[1].FieldId!.Value,
            FioFieldId:         columns[2].FieldId!.Value,
            DateFieldId:        columns[3].FieldId!.Value,
            DateFieldType:      columns[3].FieldType,
            ProjectFieldId:     columns[4].FieldId!.Value,
            AccountingFieldId:  columns[5].FieldId!.Value,
            FirstUseFieldId:    columns[6].FieldId!.Value,
            SummaryFieldId:     columns[7].FieldId!.Value), null);
    }

    /// <summary>
    /// Колонки коллекции слева направо — как их показывает таблица: системная колонка записи,
    /// затем поля; сохраненный порядок (<c>column_meta</c>) перекрывает порядок полей.
    ///
    /// Расчет повторяет <c>ConstructorRepository.GetExportColumns</c>, но БЕЗ его отсева полей
    /// типа «markdown»: экспорту они не нужны, а проверке структуры нужны — иначе колонка,
    /// вклинившаяся в середину ряда, осталась бы невидимой, и проверка сказала бы «сошлось»
    /// о коллекции с другим порядком столбцов.
    /// </summary>
    public static List<Column> Ordered(ConstructorRepository repo, int collectionId)
    {
        var fields = repo.GetFields(collectionId);
        var names  = repo.GetSystemFieldNames(collectionId);
        var order  = repo.GetColumnOrder(collectionId);

        // Порядок не сохраняли (колонки ни разу не двигали) — дефолт: название, затем поля
        // по своей позиции. Смещение 3000 держит поля правее названия, как в таблице.
        int Position(string key, int fieldPosition) =>
            order.TryGetValue(key, out var saved) ? saved
            : key == TitleKey                     ? 1000
            :                                       3000 + fieldPosition;

        var columns = new List<(Column Column, int Position)>(fields.Count + 1)
        {
            (new Column(names.GetValueOrDefault(TitleKey, "Название"), null, "title"), Position(TitleKey, 0)),
        };

        foreach (var field in fields)
        {
            var id  = field.Id!.Value;
            var key = id.ToString();
            columns.Add((new Column(field.Name, id, field.FieldType), Position(key, field.Position)));
        }

        // Сортировка устойчивая: у колонок с равными позициями сохраняется порядок добавления.
        return columns.OrderBy(c => c.Position).Select(c => c.Column).ToList();
    }

    /// <summary>Имена столбцов равны с точностью до регистра, крайних пробелов и написания
    /// буквы «е».</summary>
    public static bool SameName(string? actual, string? expected) =>
        Fold(actual) == Fold(expected);

    // Сворачивание перед сравнением. Второе написание буквы «е» здесь не текст, а СИМВОЛ:
    // эталон написан одним написанием, а в чужом журнале столбец может быть заведен другим,
    // и такая коллекция обязана подходить без переименования столбца. Уберешь эту строку —
    // перестанет.
    private static string Fold(string? name) =>
        (name ?? "").Trim().ToLowerInvariant().Replace('ё', 'е');
}
