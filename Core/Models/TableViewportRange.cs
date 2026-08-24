using System.Globalization;

namespace SmartBase.Core.Models;

/// <summary>
/// Видимый кусок таблицы для индикатора нижней панели: «10 – 50 из 125 437».
///
/// Таблица конструктора виртуализирована и прокручивается непрерывно — страниц
/// фиксированного размера у нее нет. Поэтому навигация показывает не «страницу N»,
/// а НОМЕРА СТРОК, которые сейчас в окне: сколько строк влезло, столько и в диапазоне
/// (высота строк может быть ручной, число видимых строк меняется от строки к строке).
///
/// Номера — порядковые в ТЕКУЩЕМ срезе (<c>TableRowVm.RowNumber</c>): при фильтре и
/// сортировке отфильтрованный список нумеруется заново с единицы, поэтому «10 – 50 из 120»
/// читается одинаково и с фильтром, и без него.
///
/// Тип чистый (без Avalonia): границы кнопок и текст индикатора считаются здесь и
/// проверяются модульными тестами, а вид знает только про Enabled и строку.
/// </summary>
public readonly record struct TableViewportRange(int First, int Last, int Total)
{
    /// <summary>Ничего не видно: пустая коллекция, список еще не загружен.</summary>
    public static readonly TableViewportRange Empty = new(0, 0, 0);

    /// <summary>
    /// Диапазон с нормализацией: отрицательные значения, «последняя меньше первой» и
    /// номера за пределами общего числа приводятся к допустимым. Источник данных —
    /// живая раскладка DataGrid, где в момент пересборки строк встречается и то, и другое.
    /// </summary>
    public static TableViewportRange Of(int first, int last, int total)
    {
        if (total <= 0) return Empty;
        if (first <= 0 && last <= 0) return new TableViewportRange(0, 0, total);

        var f = Math.Clamp(first <= 0 ? 1 : first, 1, total);
        var l = Math.Clamp(last  <= 0 ? f : last,  f, total);
        return new TableViewportRange(f, l, total);
    }

    /// <summary>
    /// Диапазон из СЛОТОВ, которые показывает DataGrid (<c>DisplayData.FirstScrollingSlot</c>
    /// и <c>LastScrollingSlot</c>) — так его и считает <c>TableView.VisibleRowRange</c>.
    /// Группировки у таблицы нет, поэтому слот = индекс строки, а ее номер = индекс + 1.
    ///
    /// Отрицательный первый слот — «ничего не разложено» (первый проход, пересборка строк):
    /// число записей уже известно, показываем «0 из N». Последним слотом бывает строка «+»
    /// (ее индекс = число записей) — <see cref="Of"/> срежет ее номер по общему числу, и у
    /// самого низа таблицы индикатор честно показывает последнюю ЗАПИСЬ.
    /// </summary>
    public static TableViewportRange FromSlots(int firstSlot, int lastSlot, int total) =>
        total <= 0                              ? Empty
      : firstSlot < 0 || lastSlot < firstSlot   ? Of(0, 0, total)
      :                                           Of(firstSlot + 1, lastSlot + 1, total);

    /// <summary>Есть ли что показывать в индикаторе (и куда листать).</summary>
    public bool HasRows => Total > 0 && First >= 1 && Last >= First;

    /// <summary>Видна первая строка — кнопки «в начало» и «экран назад» гасим.</summary>
    public bool AtStart => !HasRows || First <= 1;

    /// <summary>Видна последняя строка — кнопки «экран вперед» и «в конец» гасим.</summary>
    public bool AtEnd => !HasRows || Last >= Total;

    /// <summary>Текст индикатора нижней панели.</summary>
    public string Label => Total <= 0        ? "Нет записей"
                         : !HasRows          ? $"0 из {Format(Total)}"
                         : First == Last     ? $"{Format(First)} из {Format(Total)}"
                         : $"{Format(First)} – {Format(Last)} из {Format(Total)}";

    // Разряды отбиваются НЕРАЗРЫВНЫМ пробелом, а не пробелом культуры: строка живет в
    // однострочном индикаторе и не должна переноситься между «125» и «437». Формат свой,
    // не культурный, — иначе на разных локалях в одном окне вышли бы разные разделители.
    private static readonly NumberFormatInfo GroupedNumber = new()
    {
        NumberGroupSeparator = " ",
        NumberGroupSizes     = [3],
        NumberDecimalDigits  = 0,
    };

    /// <summary>Число записей для показа: «125 437» (разряды неразрывным пробелом).</summary>
    public static string Format(int value) => value.ToString("#,##0", GroupedNumber);
}

/// <summary>
/// Арифметика вертикальной прокрутки таблицы: куда встает полоса при нажатии кнопок
/// нижней панели. Значения — пиксельные (у вертикальной полосы DataGrid
/// <c>Value</c>/<c>Maximum</c>/<c>ViewportSize</c> именно в пикселях).
/// </summary>
public static class TableViewportScroll
{
    /// <summary>Экран вперед: ровно на высоту окна, не дальше конца таблицы.</summary>
    public static double PageForward(double offset, double viewport, double max) =>
        Clamp(offset + PageStep(viewport), max);

    /// <summary>Экран назад: ровно на высоту окна, не выше начала.</summary>
    public static double PageBack(double offset, double viewport, double max) =>
        Clamp(offset - PageStep(viewport), max);

    /// <summary>В самое начало таблицы.</summary>
    public static double ToStart() => 0;

    /// <summary>В самый конец таблицы.</summary>
    public static double ToEnd(double max) => Math.Max(0, max);

    // Шаг — высота окна. Нулевой/отрицательный вьюпорт (таблица еще не разложена) шага не
    // дает: иначе кнопка «прокручивала» бы на 0 и выглядела сломанной.
    private static double PageStep(double viewport) => viewport > 0 ? viewport : 0;

    private static double Clamp(double value, double max) =>
        Math.Clamp(value, 0, Math.Max(0, max));
}
