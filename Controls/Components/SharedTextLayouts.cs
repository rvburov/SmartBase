using Avalonia.Media;
using Avalonia.Media.TextFormatting;

namespace SmartBase.Controls.Components;

/// <summary>
/// Общий кеш раскладок текста рисованных видов календаря: колонки дня, полосы «весь день»,
/// ячеек и полос месяца. После правки (дубль, перенос, удаление) вид строит свои поверхности
/// заново, и без общего кеша каждый видимый блок раскладывал бы свой текст повторно — на неделе
/// с днями-гигантами сотни раскладок, 0,15–0,3 с на UI-потоке после каждой операции. Ключ — все,
/// что задает раскладку (кисть — по цвету: раскладка запекает ее в себя); вытеснение — по
/// давности обращения. Раскладки из кеша общие — владельцы их не освобождают. Только UI-поток.
/// </summary>
internal static class SharedTextLayouts
{
    private readonly record struct Key(string Text, Typeface Face, double Size, Color Color, TextAlignment Align,
                                       TextWrapping Wrap, bool Trim, double MaxWidth, double MaxHeight, int MaxLines);

    private const int Capacity = 6000;
    private static readonly Dictionary<Key, LinkedListNode<(Key Key, TextLayout Layout)>> Map = new();
    private static readonly LinkedList<(Key Key, TextLayout Layout)> Lru = new();

    public static TextLayout Get(string text, Typeface face, double fontSize, IBrush brush, TextAlignment align,
                                 TextWrapping wrap, bool trim, double maxWidth, double maxHeight, int maxLines)
    {
        TextLayout Make() => new(text, face, fontSize, brush, align, wrap,
                                 trim ? TextTrimming.CharacterEllipsis : null, null, FlowDirection.LeftToRight,
                                 maxWidth, maxHeight, double.NaN, 0, maxLines);
        if (brush is not ISolidColorBrush solid) return Make();

        var key = new Key(text, face, fontSize, solid.Color, align, wrap, trim, maxWidth, maxHeight, maxLines);
        if (Map.TryGetValue(key, out var node))
        {
            if (!ReferenceEquals(node, Lru.First))
            {
                Lru.Remove(node);
                Lru.AddFirst(node);
            }
            return node.Value.Layout;
        }
        var layout = Make();
        Map[key] = Lru.AddFirst((key, layout));
        if (Map.Count > Capacity)
        {
            var last = Lru.Last!;
            Lru.RemoveLast();
            Map.Remove(last.Value.Key);
        }
        return layout;
    }
}
