using SmartBase.Database.Repositories;

namespace SmartBase.Extensions.PdfWatermark;

/// <summary>Раскладка журнала выдачи: id полей, в которые расширение пишет. Существование
/// объекта означает, что структура коллекции сошлась.</summary>
public sealed record JournalShape(
    int CollectionId,
    string CollectionName,
    int CopyFieldId,
    int HolderFieldId,
    int DateFieldId);

/// <summary>Итог проверки: либо раскладка, либо причина отказа. Никогда не оба сразу.</summary>
public sealed record JournalCheck(JournalShape? Shape, string? Problem)
{
    public bool Ok => Shape is not null;
}

/// <summary>
/// Эталон коллекции-журнала выдачи и проверка, что выбранная коллекция ему отвечает.
///
/// Расширение пишет в ЧУЖУЮ готовую коллекцию — журнал заводит человек в конструкторе, — поэтому
/// перед первой же записью надо убедиться, что значения лягут туда, куда задумано. Проверяются
/// имена, ПОРЯДОК и типы четырех столбцов: переставленные столбцы прошли бы проверку по одним
/// именам, а номер экземпляра уехал бы в графу «кому выдан».
///
/// Тот же прием, что у «Регистрации КД» (<c>RegistrySchema</c>), с одной своей проверкой —
/// выключенным тумблером календаря у столбца даты (см. <see cref="Check"/>).
/// </summary>
public static class JournalSchema
{
    /// <summary>Ключ системной колонки записи (та же строка, что в ConstructorRepository).</summary>
    public const string TitleKey = "__title__";

    public const string TitleColumn  = "Документ";
    public const string CopyColumn   = "Экземпляр";
    public const string HolderColumn = "Кому выдан";
    public const string DateColumn   = "Дата выдачи";

    /// <summary>Эталон: столбцы слева направо. Первый — системная колонка записи.</summary>
    public static IReadOnlyList<string> Columns => [TitleColumn, CopyColumn, HolderColumn, DateColumn];

    /// <summary>Одна колонка коллекции в порядке показа. <paramref name="FieldId"/> = <c>null</c>
    /// у системной колонки записи.</summary>
    public sealed record Column(string Name, int? FieldId, string FieldType, bool InCalendar);

    /// <summary>
    /// Проверить коллекцию и, если структура сошлась, отдать раскладку ее полей.
    /// </summary>
    public static JournalCheck Check(ConstructorRepository repo, int collectionId, string collectionName)
    {
        var columns = Ordered(repo, collectionId);
        var wanted  = Columns;

        if (columns.Count != wanted.Count)
            return new JournalCheck(null,
                $"столбцов {columns.Count}, а должно быть {wanted.Count}: {string.Join(", ", wanted)}");

        for (var i = 0; i < wanted.Count; i++)
            if (!SameName(columns[i].Name, wanted[i]))
                return new JournalCheck(null,
                    $"{i + 1}-й столбец — «{columns[i].Name}», а должен быть «{wanted[i]}»");

        // Первый столбец обязан быть системной колонкой записи: имя документа — это название
        // записи, а не значение поля. Одноименное обычное поле на первом месте прошло бы
        // проверку по именам, но подбор номера искал бы документы не там.
        //
        // Этой одной проверки хватает на обе беды сразу. Системная колонка у коллекции ровно
        // одна, а столбцов к этому месту уже ровно четыре, — значит если она НЕ первая, то
        // первым стоит поле, и мы здесь же и остановимся. Отдельной проверки «системная колонка
        // уехала вглубь ряда» не нужно: до нее нельзя дойти.
        if (columns[0].FieldId is not null)
            return new JournalCheck(null,
                $"«{TitleColumn}» должен быть системным столбцом записи, а не обычным полем");

        // Типы, в отличие от «Регистрации КД», проверяются строго. Причина у каждого своя:
        // «Экземпляр» — число, потому что по нему идет подбор первого свободного; «Дата
        // выдачи» — дата, потому что иначе журнал сортируется по дате как по строке.
        if (Problem(columns[1], "number", "Число")  is { } copy)   return new JournalCheck(null, copy);
        if (Problem(columns[2], "text",   "Текст")  is { } holder) return new JournalCheck(null, holder);
        if (Problem(columns[3], "date",   "Дата")   is { } date)   return new JournalCheck(null, date);

        // Тумблер календаря у столбца даты обязан быть ВЫКЛЮЧЕН, и это не придирка. Репозиторий
        // включает его первому полю-дате коллекции сам (AddField, autoCalendar), то есть человек
        // получает его включенным по умолчанию, — а дальше каждая выдача становится событием
        // календаря, и раздел заполняется сотнями «выдач» вперемешку с настоящими сроками.
        if (columns[3].InCalendar)
            return new JournalCheck(null,
                $"у столбца «{DateColumn}» включен показ в календаре — выключите тумблер в свойствах столбца");

        return new JournalCheck(new JournalShape(
            collectionId, collectionName,
            CopyFieldId:   columns[1].FieldId!.Value,
            HolderFieldId: columns[2].FieldId!.Value,
            DateFieldId:   columns[3].FieldId!.Value), null);
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
            (new Column(names.GetValueOrDefault(TitleKey, "Название"), null, "title", false),
             Position(TitleKey, 0)),
        };

        foreach (var field in fields)
        {
            var id  = field.Id!.Value;
            var key = id.ToString();
            columns.Add((new Column(field.Name, id, field.FieldType, field.IsCalStart),
                         Position(key, field.Position)));
        }

        // Сортировка устойчивая: у колонок с равными позициями сохраняется порядок добавления.
        return columns.OrderBy(c => c.Position).Select(c => c.Column).ToList();
    }

    /// <summary>Имена столбцов равны с точностью до регистра, крайних пробелов и написания
    /// буквы «е».</summary>
    public static bool SameName(string? actual, string? expected) =>
        Fold(actual) == Fold(expected);

    /// <summary>
    /// Сворачивание перед сравнением. Второе написание буквы «е» здесь не текст, а СИМВОЛ:
    /// эталон написан одним написанием, а в чужом журнале столбец может быть заведен другим,
    /// и такая коллекция обязана подходить без переименования столбца. Уберешь эту строку —
    /// перестанет.
    ///
    /// Тем же способом сравниваются имена документов при подборе номера
    /// (<see cref="IssueJournal"/>): «Отчет» и «отчет » — один документ, и второй экземпляр
    /// обязан получить номер 2, а не начать нумерацию заново.
    /// </summary>
    public static string Fold(string? text) =>
        (text ?? "").Trim().ToLowerInvariant().Replace('ё', 'е');

    /// <summary>Отказ по типу столбца или <c>null</c>, если тип тот.</summary>
    private static string? Problem(Column column, string wantedType, string wantedName) =>
        column.FieldType == wantedType
            ? null
            : $"столбец «{column.Name}» должен быть типа «{wantedName}», а он «{TypeName(column.FieldType)}»";

    /// <summary>Русское имя типа для сообщения об отказе: человек видит в конструкторе его, а не
    /// внутренний код.</summary>
    private static string TypeName(string fieldType) => fieldType switch
    {
        "text"        => "Текст",
        "number"      => "Число",
        "date"        => "Дата",
        "select"      => "Один из списка",
        "multiselect" => "Несколько из списка",
        "status"      => "Статус",
        "checkbox"    => "Флажок",
        "url"         => "Ссылка",
        "user"        => "Пользователь",
        "file"        => "Файл",
        "markdown"    => "Текстовый блок",
        _             => fieldType,
    };
}
