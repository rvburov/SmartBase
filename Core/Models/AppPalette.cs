namespace SmartBase.Core.Models;

/// Палитра приложения: девять именованных цветов, из которых пользователь помечает
/// коллекции, записи, строки и ячейки. Единый источник — раньше список жил приватным
/// массивом внутри сайдбара и был не виден никому другому.
///
/// Цвет хранится и передаётся hex-строкой «#RRGGBB» (так же, как db_collections.Color
/// и categories.Color). Отдельного «id цвета» в проекте нет и заводить его не нужно:
/// палитра — это подсказка в меню, а не замкнутый список допустимых значений.
///
/// «Без цвета» — см. [EventColors]: у коллекции это дефолтный синий, у записи и
/// оформления таблицы — null (наследовать/дефолт).
public static class AppPalette
{
    public readonly record struct Swatch(string Hex, string Name);

    public static readonly IReadOnlyList<Swatch> Colors =
    [
        new("#34C759", "Зеленый"),    new("#FF3B30", "Красный"),
        new("#FF9500", "Оранжевый"),  new("#FF2D55", "Розовый"),
        new("#AF52DE", "Фиолетовый"), new("#FFCC00", "Желтый"),
        new("#5E5CE6", "Индиго"),     new("#64D2FF", "Голубой"),
        new("#FF6B6B", "Коралловый"),
    ];

    /// Имя цвета для показа; для незнакомого hex — сам hex (палитра не ограничивает значения).
    public static string NameOf(string? hex)
    {
        if (string.IsNullOrWhiteSpace(hex)) return "Без цвета";
        foreach (var s in Colors)
            if (string.Equals(s.Hex, hex, StringComparison.OrdinalIgnoreCase)) return s.Name;
        return hex;
    }

    /// Похоже ли значение на «#RRGGBB». Пустое и мусорное отсекаем до Color.Parse,
    /// чтобы битая строка в БД не роняла отрисовку строки таблицы.
    public static bool IsHex(string? value)
    {
        if (value is not { Length: 7 } v || v[0] != '#') return false;
        for (var i = 1; i < 7; i++)
            if (!Uri.IsHexDigit(v[i])) return false;
        return true;
    }
}
