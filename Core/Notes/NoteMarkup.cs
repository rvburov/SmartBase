namespace SmartBase.Core.Notes;

/// <summary>Как выглядит кусок текста в живом просмотре заметки.</summary>
public enum NoteStyle
{
    /// <summary>Служебные символы разметки и все, что читается как подсобное: номера пунктов,
    /// решетка таблицы, горизонтальная линия. Показывается приглушенным.</summary>
    Faint,
    Bold,
    Italic,
    Strike,
    /// <summary>==Подсветка==: маркер поверх текста, начертание не меняется.</summary>
    Highlight,
    /// <summary>Код внутри строки.</summary>
    Code,
    /// <summary>Строка внутри огражденного блока кода — вместе с самими ограждениями.</summary>
    CodeBlock,
    Heading1, Heading2, Heading3, Heading4, Heading5, Heading6,
    Quote,
    Link,
}

/// <summary>
/// Кусок текста с одним оформлением, полуинтервал <c>[Start, Start+Length)</c>.
///
/// Инвариант: спан НИКОГДА не пересекает границу строки. Многострочные блоки (цитата, блок кода)
/// разрезаются при разборе. Благодаря этому выборка спанов строки — двоичный поиск по началу:
/// спан, задевающий строку, обязан в ней и начинаться.
/// </summary>
public readonly record struct NoteSpan(int Start, int Length, NoteStyle Style)
{
    public int End => Start + Length;
}

/// <summary>
/// Служебные символы разметки: <c>**</c>, <c>#</c>, <c>&gt;</c>, ограждения блока кода. Прячутся,
/// пока каретка не на их строке.
/// </summary>
/// <param name="Replacement">
/// Чем нарисовать вместо спрятанного. Обычно null — тогда символы просто исчезают. Маркеру списка
/// подставляется точка: спрятать «-» совсем значило бы сдвинуть строку влево и потерять вид списка.
/// </param>
public readonly record struct NoteMarker(int Start, int Length, string? Replacement = null)
{
    public int End => Start + Length;
}

/// <summary>
/// Разобранная разметка документа. Оба набора отсортированы по началу и лежат массивами:
/// раскраска зовется на каждую видимую строку каждой перерисовки, поэтому доступ здесь — двоичный
/// поиск без LINQ и без выделения памяти.
/// </summary>
public sealed class NoteMarkupLayout
{
    public static readonly NoteMarkupLayout Empty = new([], []);

    private readonly NoteSpan[]   _spans;
    private readonly NoteMarker[] _markers;

    internal NoteMarkupLayout(NoteSpan[] spans, NoteMarker[] markers)
    {
        _spans   = spans;
        _markers = markers;
    }

    /// <summary>Для тестов и отладки.</summary>
    public IReadOnlyList<NoteSpan>   Spans   => _spans;
    public IReadOnlyList<NoteMarker> Markers => _markers;

    /// <summary>Спаны, попадающие в <c>[start, end)</c>, дописываются в готовый список.</summary>
    public void SpansIn(int start, int end, List<NoteSpan> into)
    {
        for (var i = LowerBound(_spans, start); i < _spans.Length && _spans[i].Start < end; i++)
            if (_spans[i].End > start) into.Add(_spans[i]);
    }

    /// <summary>Маркеры, попадающие в <c>[start, end)</c>, дописываются в готовый список.</summary>
    public void MarkersIn(int start, int end, List<NoteMarker> into)
    {
        for (var i = LowerBound(_markers, start); i < _markers.Length && _markers[i].Start < end; i++)
            if (_markers[i].End > start) into.Add(_markers[i]);
    }

    /// <summary>Маркер, начинающийся ровно на этом смещении.</summary>
    public bool TryMarkerAt(int offset, out NoteMarker marker)
    {
        var i = LowerBound(_markers, offset);
        if (i < _markers.Length && _markers[i].Start == offset)
        {
            marker = _markers[i];
            return true;
        }
        marker = default;
        return false;
    }

    /// <summary>Начало ближайшего маркера в <c>[offset, limit)</c>, либо -1.</summary>
    public int FirstMarkerFrom(int offset, int limit)
    {
        var i = LowerBound(_markers, offset);
        return i < _markers.Length && _markers[i].Start < limit ? _markers[i].Start : -1;
    }

    // Первый элемент с Start >= value. Массивы отсортированы при сборке.
    private static int LowerBound(NoteSpan[] a, int value)
    {
        int lo = 0, hi = a.Length;
        while (lo < hi)
        {
            var mid = (lo + hi) >> 1;
            if (a[mid].Start < value) lo = mid + 1; else hi = mid;
        }
        return lo;
    }

    private static int LowerBound(NoteMarker[] a, int value)
    {
        int lo = 0, hi = a.Length;
        while (lo < hi)
        {
            var mid = (lo + hi) >> 1;
            if (a[mid].Start < value) lo = mid + 1; else hi = mid;
        }
        return lo;
    }
}
