namespace SmartBase.Core.Models;

/// Пара «фон + цвет текста» одного уровня оформления таблицы: ячейки, строки или
/// колонки. Аспект `null` = «не задан»: строка в БД живёт, пока задан хоть один
/// (тот же принцип, что у column_meta — см. RemoveColumnMetaIfEmpty).
///
/// Значения — hex «#RRGGBB» из [AppPalette]; НАСЫЩЕННЫЕ. Приглушение фона и
/// затемнение текста — забота отрисовки (Utils/ColorTools), а не хранения: так один
/// и тот же цвет одинаково выглядит в таблице, календаре и задачах.
public readonly record struct RowStyle(string? Bg, string? Fg)
{
    public static readonly RowStyle None = default;

    public bool IsEmpty => Bg is null && Fg is null;

    /// Отбрасывает мусор из БД (пустые строки, битый hex) — иначе одно кривое значение
    /// роняло бы отрисовку всей строки таблицы.
    public static RowStyle Clean(string? bg, string? fg) =>
        new(AppPalette.IsHex(bg) ? bg : null, AppPalette.IsHex(fg) ? fg : null);

    /// ЕДИНСТВЕННОЕ место, где описан приоритет цвета. Точечное перебивает общее:
    /// ячейка → строка → колонка.
    ///
    /// Фон строки целиком — это и есть цвет записи: отдельной «локальной» заливки строки
    /// нет специально. Два фона на одну строку выглядели одинаково и различались лишь тем,
    /// видит ли их календарь, — пользователю такая разница не читается, поэтому уровень
    /// один. У ЦВЕТА ТЕКСТА уровень строки остался: он не дублирует ничего.
    ///
    /// Сюда же позже встанет слой правил («Статус = Готово → зелёный») — между
    /// ручными уровнями и дефолтом; ни схема, ни вызывающий код не изменятся.
    public static RowStyle Resolve(RowStyle cell, RowStyle row, string? recordColor, RowStyle column)
    {
        var record = AppPalette.IsHex(recordColor) ? recordColor : null;
        return new RowStyle(
            Bg: cell.Bg ?? record  ?? column.Bg,
            Fg: cell.Fg ?? row.Fg  ?? column.Fg);
    }
}

/// Какой аспект оформления правит вызов: заливка или цвет текста. Один и тот же набор
/// уровней (строка, ячейка, колонка) красится обоими, поэтому аспект — параметр, а не
/// пара близнецов-методов на каждый уровень.
public enum StyleAspect
{
    Background,
    Text,
}
