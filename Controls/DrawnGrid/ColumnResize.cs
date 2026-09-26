namespace SmartBase.Controls.DrawnGrid;

/// <summary>
/// Арифметика жеста ручной ширины колонки — та же, что была у нынешней таблицы
/// (<c>DragColResize</c>), только без контролов. Правила: колонки СЛЕВА не трогаются никогда
/// (иначе граница уехала бы из-под курсора); рост колонки уводит соседей вправо без изменения их
/// ширины — таблица становится шире окна и появляется горизонтальная прокрутка; сужение раздает
/// освободившееся место колонкам справа пропорционально их ширине, пустоты справа не остается;
/// у самой правой движимой колонки пол — точка «таблица ровно заполнена», справа некому принять
/// место. Ниже <see cref="ColumnLayout.MinManualWidth"/> колонка не опускается.
/// Тесты — <c>Tests/Unit/ColumnResizeTests</c>.
/// </summary>
public static class ColumnResize
{
    /// <summary>
    /// Новые ширины после сдвига правой границы колонки <paramref name="key"/>.
    /// </summary>
    /// <param name="keys">Движимые показанные колонки слева направо.</param>
    /// <param name="baseWidths">Снимок ширин на старте жеста (ключ → DIP).</param>
    /// <param name="key">Колонка, чью границу тянут.</param>
    /// <param name="raw">Желаемая ширина колонки: стартовая плюс сдвиг курсора и прокрутки.</param>
    /// <param name="fill">Ширина, при которой таблица ровно заполняет окно (без служебных колонок).</param>
    public static Dictionary<string, double> Apply(IReadOnlyList<string> keys, IReadOnlyDictionary<string, double> baseWidths,
                                                   string key, double raw, double fill)
    {
        var result = new Dictionary<string, double>(baseWidths);
        var idx = -1;
        for (var i = 0; i < keys.Count; i++) if (keys[i] == key) { idx = i; break; }
        if (idx < 0) return result;

        double Base(string k) => baseWidths.TryGetValue(k, out var w) && w > 0 ? w : ColumnLayout.MinManualWidth;

        var others = 0.0;
        for (var i = 0; i < keys.Count; i++) if (i != idx) others += Base(keys[i]);

        var hasRight = idx < keys.Count - 1;
        var floor    = hasRight ? ColumnLayout.MinManualWidth : Math.Max(ColumnLayout.MinManualWidth, fill - others);
        var target   = Math.Max(floor, raw);
        var surplus  = fill - (others + target);

        result[key] = target;

        var rightSum = 0.0;
        for (var i = idx + 1; i < keys.Count; i++) rightSum += Base(keys[i]);
        for (var i = idx + 1; i < keys.Count; i++)
        {
            var k = keys[i];
            result[k] = surplus > 0 && rightSum > 0 ? Base(k) + surplus * Base(k) / rightSum : Base(k);
        }
        return result;
    }
}
