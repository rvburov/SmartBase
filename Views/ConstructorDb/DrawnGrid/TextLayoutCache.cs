using Avalonia.Media;
using Avalonia.Media.TextFormatting;

namespace SmartBase.Views.ConstructorDb.DrawnGrid;

/// <summary>
/// Кеш готовых раскладок текста для рисованной сетки. Раскладка (шейпинг глифов, перенос,
/// обрезка многоточием) — самая дорогая часть рисования ячейки; без кеша каждый кадр
/// перекладывал бы тысячу надписей заново. Ключ — текст, ширина, выравнивание, кисть и предел
/// строк; вытеснение по давности обращения.
///
/// Кисть входит в ключ, потому что цвет текста задается при создании раскладки. Кистей мало:
/// основная, приглушенная, акцентная и редкие пользовательские цвета ячеек.
/// </summary>
internal sealed class TextLayoutCache
{
    private readonly record struct Key(string Text, int Width, TextAlignment Align, string Brush, int MaxLines);

    private sealed class Entry(Key key, TextLayout layout)
    {
        public readonly Key        Key    = key;
        public readonly TextLayout Layout = layout;
    }

    private readonly Dictionary<Key, LinkedListNode<Entry>> _map = new();
    private readonly LinkedList<Entry> _lru = new();
    private readonly int _capacity;

    public TextLayoutCache(Typeface typeface, double fontSize, int capacity = 6000)
    {
        Typeface  = typeface;
        FontSize  = fontSize;
        _capacity = Math.Max(16, capacity);
    }

    public Typeface Typeface { get; }
    public double   FontSize { get; }

    public int Count  => _map.Count;
    public int Hits   { get; private set; }
    public int Misses { get; private set; }

    /// <summary>
    /// Раскладка текста в полосе шириной <paramref name="maxWidth"/>. Одна строка с многоточием
    /// при <paramref name="maxLines"/> = 1, иначе перенос по словам не больше указанного числа
    /// строк (последняя — с многоточием).
    /// </summary>
    public TextLayout Get(string text, double maxWidth, TextAlignment align, IBrush brush, string brushId, int maxLines)
    {
        var width = Math.Max(1, maxWidth);
        var key   = new Key(text, (int)Math.Ceiling(width), align, brushId, maxLines);

        if (_map.TryGetValue(key, out var node))
        {
            Hits++;
            if (!ReferenceEquals(node, _lru.First))
            {
                _lru.Remove(node);
                _lru.AddFirst(node);
            }
            return node.Value.Layout;
        }

        Misses++;
        // Без переноса по словам — как у надписей нынешней таблицы (TextWrapping не задан):
        // несколько строк дают только явные переводы строки (Alt+Enter), каждая режется многоточием.
        var layout = new TextLayout(text, Typeface, FontSize, brush, align, TextWrapping.NoWrap,
                                    TextTrimming.CharacterEllipsis, null, FlowDirection.LeftToRight,
                                    width, double.PositiveInfinity, double.NaN, 0, Math.Max(1, maxLines));
        var added = _lru.AddFirst(new Entry(key, layout));
        _map[key] = added;

        if (_map.Count > _capacity)
        {
            var last = _lru.Last!;
            _lru.RemoveLast();
            _map.Remove(last.Value.Key);
        }
        return layout;
    }

    /// <summary>Сбросить все раскладки — на смену шрифта, темы или зума.</summary>
    public void Clear()
    {
        _map.Clear();
        _lru.Clear();
    }
}
