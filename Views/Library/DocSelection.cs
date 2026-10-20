using SmartBase.Core.Models;

namespace SmartBase.Views.Library;

/// <summary>
/// Ключ документа: база, запись, файловое свойство и путь. Одной записи мало — запись с двумя
/// включенными файловыми столбцами или несколькими путями дает несколько карточек, каждую нужно выделять
/// по отдельности. Путь сохраняет выделение, когда перескан добавил файл перед выбранным.
/// </summary>
public readonly record struct DocKey(int DbId, int RecordId, int FileFieldId, string Path = "");

/// <summary>
/// Выделение в ленте документов — чистая логика без Avalonia, ради тестов
/// (<c>DocSelectionTests</c>). Хранит ключи, а не карточки: карточка под виртуализацией живет
/// недолго, ключ переживает и перезагрузку ленты, и вытеснение страницы из кеша.
/// </summary>
public sealed class DocSelection
{
    private readonly HashSet<DocKey> _keys = [];

    /// <summary>Ключ документа; null — у записи нет id (такой в ленте не бывает, но проверка дешевая).</summary>
    public static DocKey? KeyOf(DocRecord d) =>
        d.Id is int id ? new DocKey(d.DbId, id, d.FileFieldId, d.Path) : null;

    public int Count => _keys.Count;

    public IReadOnlyCollection<DocKey> Keys => _keys;

    public bool Contains(DocRecord d) => KeyOf(d) is { } k && _keys.Contains(k);

    public void Clear() => _keys.Clear();

    /// <summary>Выделить один документ, сбросив прежнее выделение.</summary>
    public void Select(DocRecord d)
    {
        _keys.Clear();
        if (KeyOf(d) is { } k) _keys.Add(k);
    }

    /// <summary>Добавить или убрать документ (Shift-клик).</summary>
    public void Toggle(DocRecord d)
    {
        if (KeyOf(d) is not { } k) return;
        if (!_keys.Add(k)) _keys.Remove(k);
    }

    /// <summary>
    /// Выделить диапазон между двумя документами в порядке показа (Ctrl+Shift). Документы, до
    /// которых лента еще не догрузилась, в <paramref name="ordered"/> не попадают — и в
    /// выделение тоже: обещать выделение того, чего на экране нет, нельзя.
    /// </summary>
    public void SelectRange(IReadOnlyList<DocRecord> ordered, DocRecord from, DocRecord to)
    {
        var a = IndexOf(ordered, from);
        var b = IndexOf(ordered, to);
        if (a < 0 || b < 0) { Select(to); return; }

        if (a > b) (a, b) = (b, a);
        for (var i = a; i <= b; i++)
            if (KeyOf(ordered[i]) is { } k) _keys.Add(k);
    }

    private static int IndexOf(IReadOnlyList<DocRecord> ordered, DocRecord d)
    {
        if (KeyOf(d) is not { } key) return -1;
        for (var i = 0; i < ordered.Count; i++)
            if (KeyOf(ordered[i]) is { } k && k == key) return i;
        return -1;
    }
}
