using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media.TextFormatting;

namespace SmartBase.Controls.Behaviors;

/// <summary>
/// Вход в правку названия (запись, коллекция, заметка). Название живет двумя контролами:
/// <c>TextBlock</c> показа (обрезанный до первой строки, с многоточием) и <c>TextBox</c> правки
/// (полный текст) — их подменяют по клику.
///
/// Каретка ставится ТУДА, КУДА КЛИКНУЛИ, а не выделяется весь текст: клик по названию не должен
/// менять состояние текста — ровно так ведет себя описание записи, где поле стоит всегда и
/// подменять нечего. Выделить все по-прежнему можно с клавиатуры (Cmd/Ctrl+A).
///
/// Позиция берется попаданием в раскладку НАДПИСИ, а не поля: в момент клика поле еще скрыто и
/// своей раскладки не имеет. Индексы совпадают — показ это начало полного текста (первая строка),
/// поэтому попадание в него всегда указывает на тот же символ.
/// </summary>
public static class TitleEditing
{
    /// <summary>Переключить пару «надпись → поле» и поставить каретку под курсор.</summary>
    public static void Begin(TextBlock display, TextBox box, PointerPressedEventArgs e)
    {
        var caret = CaretAt(display, e.GetPosition(display), box.Text ?? "");

        display.IsVisible = false;
        box.IsVisible     = true;
        box.Focus();
        // Пустое выделение в точке клика: и каретка на месте, и ничего не подсвечено.
        box.SelectionStart = box.SelectionEnd = box.CaretIndex = caret;
    }

    /// <summary>Индекс символа под точкой. Правая половина глифа — каретка за ним, как в любом
    /// текстовом поле. Клик правее конца строки (пустое место шапки) — в конец текста.</summary>
    private static int CaretAt(TextBlock display, Point point, string full)
    {
        if (full.Length == 0) return 0;

        int index;
        try
        {
            var hit = display.TextLayout.HitTestPoint(point);
            // Мимо текста (клик по пустому месту шапки правее названия) — в конец строки.
            if (!hit.IsInside && point.X > 0) return full.Length;
            index = hit.TextPosition + (hit.IsTrailing ? 1 : 0);
        }
        catch { return full.Length; }   // раскладки еще нет — ведем себя как обычный клик в конец

        return Math.Clamp(index, 0, full.Length);
    }
}
