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
/// Восемь столбцов эталона, найденные ПО ИМЕНАМ, — признак «той самой» коллекции. Расширение
/// пишет в чужую готовую коллекцию (в отличие от «Документов в коллекцию», которое создает
/// свою), поэтому промахнуться легко: подтвердить структуру заранее дешевле, чем разбирать
/// потом записи, разложенные не по тем столбцам.
///
/// Порядок не важен, а лишние столбцы разрешены где угодно: в журнал дописывают свои колонки —
/// например, для письма, — и расширение их не трогает (решение пользователя 21.10.2026).
/// Значения ложатся по найденным id полей, поэтому переставленный столбец получит свое
/// значение, а не соседнее. Нельзя только двух столбцов с одним именем эталона: в какой из
/// них писать, не понять.
///
/// «Наименование» — СИСТЕМНАЯ колонка записи (Title), переименованная из «Названия»; остальные
/// семь — обычные поля коллекции. Сравнение имен прощает регистр, крайние пробелы и разнобой в
/// написании буквы «е» (см. <see cref="SameName"/>).
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
        var (found, problem) = Find(Ordered(repo, collectionId), c => c.Name);
        if (found is null) return new SchemaCheck(null, problem);

        // «Наименование» обязано быть системной колонкой записи: его значение — это название
        // записи, а не значение поля. Одноименное обычное поле прошло бы проверку по именам, но
        // писать в него как в название было бы ошибкой.
        if (found[0].FieldId is not null)
            return new SchemaCheck(null,
                $"«{TitleColumn}» должен быть системным столбцом записи, а не обычным полем");

        // Обратный случай: системную колонку переименовали в имя одного из семи полей. Тогда у
        // этого поля нет id, и писать в него нечем.
        for (var i = 1; i < found.Count; i++)
            if (found[i].FieldId is null)
                return new SchemaCheck(null,
                    $"«{found[i].Name}» — системный столбец записи, а должен быть обычным полем");

        return new SchemaCheck(new RegistryShape(
            collectionId, collectionName,
            DesignationFieldId: found[1].FieldId!.Value,
            FioFieldId:         found[2].FieldId!.Value,
            DateFieldId:        found[3].FieldId!.Value,
            DateFieldType:      found[3].FieldType,
            ProjectFieldId:     found[4].FieldId!.Value,
            AccountingFieldId:  found[5].FieldId!.Value,
            FirstUseFieldId:    found[6].FieldId!.Value,
            SummaryFieldId:     found[7].FieldId!.Value), null);
    }

    /// <summary>
    /// Найти столбцы эталона среди <paramref name="columns"/> по именам — в порядке эталона.
    /// Лишние пропускаются, порядок не важен. <c>null</c> — какого-то столбца нет или он
    /// встречается дважды; причина — во втором значении. Тот же разбор у шапки файла
    /// (<see cref="RegistryFile"/>): человек видит одни и те же слова и там и тут.
    /// </summary>
    public static (IReadOnlyList<T>? Found, string Problem) Find<T>(IReadOnlyList<T> columns,
                                                                    Func<T, string> name)
    {
        var found   = new List<T>(Columns.Count);
        var missing = new List<string>();

        foreach (var wanted in Columns)
        {
            var same = columns.Where(c => SameName(name(c), wanted)).ToList();

            if (same.Count > 1)
                return (null, $"столбец «{wanted}» встречается несколько раз — непонятно, в какой писать");

            if (same.Count == 0) missing.Add(wanted);
            else                 found.Add(same[0]);
        }

        if (missing.Count == 1)
            return (null, $"нет столбца «{missing[0]}»");

        if (missing.Count > 1)
            return (null, $"нет столбцов: {string.Join(", ", missing.Select(m => $"«{m}»"))}");

        return (found, "");
    }

    /// <summary>
    /// Колонки коллекции слева направо — как их показывает таблица: системная колонка записи,
    /// затем поля; сохраненный порядок (<c>column_meta</c>) перекрывает порядок полей.
    ///
    /// Расчет повторяет <c>ConstructorRepository.GetExportColumns</c>, но БЕЗ его отсева полей
    /// типа «markdown»: экспорту они не нужны, а проверке структуры нужны — иначе такое поле с
    /// именем из эталона осталось бы невидимым, и проверка не заметила бы двух одноименных
    /// столбцов.
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
