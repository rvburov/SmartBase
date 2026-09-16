namespace SmartBase.Integrations;

/// <summary>
/// Куда ложится одна колонка ответа.
/// </summary>
/// <param name="Column">Имя колонки в ответе — так, как его назвала схема источника.</param>
/// <param name="FieldId">Свойство коллекции; <c>null</c> — колонка становится названием
/// записи.</param>
public sealed record BindingColumn(string Column, int? FieldId);

/// <summary>
/// Привязка — «как ответ ложится в коллекцию»: какая колонка куда, по чему искать уже
/// заведенную запись и что делать с найденной и ненайденной.
///
/// Модель без ввода-вывода: применяет ее <c>ConstructorRepository.ApplyRows</c>.
///
/// Сопоставление задается ОДНОЙ колонкой, а не отдельной парой «колонка + поле»: искать
/// разрешено только по тому, что и так переносится. Иначе получалась бы связь, которой нет на
/// экране: запись обновляется по полю, которого в привязке не видно.
///
/// База данных названа явно, отдельным ключом. Соединения и запросы лежат в системной app.db и
/// общие для всех баз, а ключи коллекций у каждой базы свои: без ключа базы сохраненная привязка
/// после переключения баз указывала бы на коллекцию с тем же номером в СОСЕДНЕЙ базе — и обмен
/// молча уложил бы чужие записи не туда.
/// </summary>
/// <param name="DbId">База данных, в которой живет коллекция.</param>
/// <param name="CollectionId">Коллекция, в которую кладем.</param>
/// <param name="Columns">Колонки ответа и их место в коллекции. Колонка, которой здесь нет,
/// не переносится — это нормально: сторонняя программа отдает лишнее почти всегда.</param>
/// <param name="MatchColumn">Колонка, по значению которой ищется уже заведенная запись. Пусто —
/// не искать вовсе: каждая строка ответа станет новой записью.</param>
/// <param name="UpdateExisting">Найденную запись обновлять.</param>
/// <param name="CreateMissing">Ненайденную заводить.</param>
public sealed record CollectionBinding(
    int                          DbId,
    int                          CollectionId,
    IReadOnlyList<BindingColumn> Columns,
    string                       MatchColumn   = "",
    bool                         UpdateExisting = true,
    bool                         CreateMissing  = true)
{
    /// <summary>Сопоставление задано — записи будут искаться, а не заводиться подряд.</summary>
    public bool Matches => !string.IsNullOrWhiteSpace(MatchColumn);

    /// <summary>Есть ли колонка сопоставления среди переносимых.</summary>
    public bool MatchColumnBound => Columns.Any(c => Same(c.Column, MatchColumn));

    /// <summary>
    /// Куда ложится колонка сопоставления; <c>null</c> — в название либо такой колонки нет
    /// вовсе. Различать эти два случая нужно через <see cref="MatchColumnBound"/>.
    /// </summary>
    public int? MatchFieldId =>
        Columns.FirstOrDefault(c => Same(c.Column, MatchColumn))?.FieldId;

    /// <summary>
    /// Что не так с привязкой; пустой список — можно применять. Проверяется форма: существуют
    /// ли колонки в ответе и свойства в коллекции — вопрос применения, там же и объясняется.
    /// </summary>
    public IReadOnlyList<string> Problems()
    {
        var problems = new List<string>();

        if (DbId <= 0)
            problems.Add("Не выбрана база данных");

        if (CollectionId <= 0)
            problems.Add("Не выбрана коллекция");

        if (Columns.Count == 0)
            problems.Add("Не выбрано ни одной колонки для переноса");

        if (Columns.Count(c => c.FieldId is null) > 1)
            problems.Add("Названием записи может стать только одна колонка");

        foreach (var group in Columns.Where(c => c.FieldId is not null).GroupBy(c => c.FieldId))
            if (group.Count() > 1)
                problems.Add($"В одно свойство сложены сразу несколько колонок: "
                           + string.Join(", ", group.Select(c => $"«{c.Column}»")));

        foreach (var group in Columns.GroupBy(c => c.Column, StringComparer.OrdinalIgnoreCase))
            if (group.Count() > 1)
                problems.Add($"Колонка «{group.Key}» указана несколько раз");

        if (Matches && !MatchColumnBound)
            problems.Add($"Колонка сопоставления «{MatchColumn}» не переносится — "
                       + "искать по ней нечего");

        // Ни обновлять, ни заводить — привязка, которая гарантированно ничего не делает.
        if (Matches && !UpdateExisting && !CreateMissing)
            problems.Add("Ни обновление найденных, ни создание новых не выбрано — переносить нечего");

        return problems;
    }

    private static bool Same(string left, string right) =>
        string.Equals(left?.Trim(), right?.Trim(), StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Чем кончилось применение привязки. Считается все: молчаливый пропуск половины записей —
/// именно то, что потом ищут неделю.
/// </summary>
/// <param name="Created">Заведено новых записей.</param>
/// <param name="Updated">Обновлено найденных.</param>
/// <param name="Skipped">Пропущено: нечего обновлять или запрещено заводить.</param>
/// <param name="Ambiguous">Пропущено из-за неоднозначности: в коллекции несколько записей с
/// таким же значением ключа, и какую из них править — неизвестно.</param>
/// <param name="Empty">Пропущено из-за пустого ключа: сопоставлять не по чему.</param>
public sealed record BindingResult(int Created, int Updated, int Skipped, int Ambiguous, int Empty)
{
    public static readonly BindingResult Nothing = new(0, 0, 0, 0, 0);

    /// <summary>Сколько строк ответа обработано всего.</summary>
    public int Total => Created + Updated + Skipped + Ambiguous + Empty;

    /// <summary>Итог одной строкой — для отчета и журнала.</summary>
    public string Summary()
    {
        var parts = new List<string>();

        if (Created   > 0) parts.Add($"создано {Created}");
        if (Updated   > 0) parts.Add($"обновлено {Updated}");
        if (Skipped   > 0) parts.Add($"пропущено {Skipped}");
        if (Ambiguous > 0) parts.Add($"неоднозначных {Ambiguous}");
        if (Empty     > 0) parts.Add($"без ключа {Empty}");

        return parts.Count == 0 ? "изменений нет" : string.Join(", ", parts);
    }
}
