namespace SmartBase.Core.Models;

/// <summary>Запись, которая ссылается на показанную запись.</summary>
public sealed record RelationBacklinkSource(int RecordId, string Title);

/// <summary>
/// Карточка блока «Ссылаются на эту запись»: одно свойство-связь одной базы-источника («Счета ·
/// Клиент»), число записей-источников (точное до <see cref="RelationBacklinkText.Cap"/>, дальше —
/// <see cref="More"/>) и первые названия — сначала новые записи.
/// </summary>
/// <param name="SourceDbId">База-источник: 0 — база показанной записи, иначе ее постоянный номер.</param>
/// <param name="Targets">Номера базы цели в ребрах этого свойства: у своей базы — 0 и свой номер
/// (старые ребра), у другой — номер базы показанной записи.</param>
public sealed record RelationBacklinkGroup(int SourceDbId, int FieldId, string Field, int CollectionId, string Collection,
    int Sources, bool More, IReadOnlyList<RelationBacklinkSource> First, IReadOnlyList<int> Targets)
{
    /// <summary>Та же карточка с тем же показом; списки сравниваются по составу.</summary>
    public bool SameAs(RelationBacklinkGroup other) =>
        SourceDbId == other.SourceDbId && FieldId == other.FieldId && Field == other.Field && CollectionId == other.CollectionId
        && Collection == other.Collection && Sources == other.Sources && More == other.More
        && First.SequenceEqual(other.First) && Targets.SequenceEqual(other.Targets);
}

/// <summary>Раздел блока: свои ссылки или ссылки из одной другой подключенной базы.</summary>
/// <param name="BaseName">Имя другой базы; null — база показанной записи.</param>
/// <param name="Failed">Базу не удалось прочитать (занята, недоступна).</param>
public sealed record RelationBacklinkSection(int SourceDbId, string? BaseName, IReadOnlyList<RelationBacklinkGroup> Groups,
    bool Failed = false)
{
    /// <summary>Два ответа показывают одно и то же — страница записи не перестраивает карточки.</summary>
    public static bool Same(IReadOnlyList<RelationBacklinkSection> a, IReadOnlyList<RelationBacklinkSection> b) =>
        a.Count == b.Count && a.Zip(b).All(p => p.First.SourceDbId == p.Second.SourceDbId && p.First.BaseName == p.Second.BaseName
            && p.First.Failed == p.Second.Failed && p.First.Groups.Count == p.Second.Groups.Count
            && p.First.Groups.Zip(p.Second.Groups).All(g => g.First.SameAs(g.Second)));
}

/// <summary>Страница списка источников: записи и ключ следующей страницы (номер последней
/// просмотренной записи; null — больше нет).</summary>
public sealed record RelationBacklinkPage(IReadOnlyList<RelationBacklinkSource> Items, int? Next);

/// <summary>Тексты блока обратных ссылок — в одном месте для карточек, списка и тестов.</summary>
public static class RelationBacklinkText
{
    /// <summary>До скольких источников карточка считает точно.</summary>
    public const int Cap = 1000;
    /// <summary>Сколько названий показывает карточка.</summary>
    public const int FirstCount = 3;

    /// <summary>Значение карточки без ссылок.</summary>
    public const string None = "ссылок нет";
    /// <summary>Карточка базы, которую не удалось прочитать.</summary>
    public const string FailedName = "Ссылки не прочитаны";
    public const string FailedValue = "база занята или недоступна";

    /// <summary>Название карточки: «Счета · Клиент» — коллекция-источник и свойство, как в окне удаления.</summary>
    public static string Name(RelationBacklinkGroup group) => $"{group.Collection} · {group.Field}";

    /// <summary>Подпись раздела: «Ссылаются на эту запись» или «… из базы «Учет»».</summary>
    public static string Header(string? baseName) =>
        baseName is null ? "Ссылаются на эту запись" : $"Ссылаются на эту запись из базы «{baseName}»";

    /// <summary>Значение карточки: «ссылок нет», одно название, «3 записи: Счет №41, Счет №40, Счет
    /// №39» или «более 1 000 записей: …».</summary>
    public static string Value(RelationBacklinkGroup group)
    {
        if (group.Sources == 0) return None;
        var names = string.Join(", ", group.First.Select(s => Title(s.Title)));
        if (group.Sources == 1 && !group.More) return names;
        var count = group.More ? $"более {Cap:N0} записей" : $"{group.Sources} {RecordsWord(group.Sources)}";
        return $"{count}: {names}" + (group.Sources > group.First.Count || group.More ? "…" : "");
    }

    /// <summary>Название источника одной строкой; пустое — «Без названия».</summary>
    public static string Title(string? title) => TitleText.IsPlaceholder(title) ? TitleText.Placeholder : TitleText.FirstLine(title!);

    /// <summary>Подсказка под списком источников, показанных не все: точное число, пока оно
    /// считается, — «из более чем 1 000» у большой карточки, иначе пусто.</summary>
    public static string PageHint(int shown, int? total, bool more) => total is int all
        ? $"Показаны {shown:N0} из {all:N0}"
        : more ? $"Показаны {shown:N0} из более чем {Cap:N0}" : "";

    /// <summary>Подсказка под списком найденного, когда найдено больше страницы.</summary>
    public static string SearchHint(int shown) => $"Показаны первые {shown:N0} — прокрутите или уточните поиск";

    public static string RecordsWord(int n) => (n % 100, n % 10) switch
    {
        ( >= 11 and <= 14, _) => "записей",
        (_, 1) => "запись",
        (_, >= 2 and <= 4) => "записи",
        _ => "записей",
    };
}
