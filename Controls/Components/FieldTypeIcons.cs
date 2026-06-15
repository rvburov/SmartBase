using Avalonia.Media;

namespace DataBase.Controls.Components;

// Иконки типов свойств (lucide, сетка 24×24) — единый источник для диалога
// создания/изменения свойства и заголовков колонок таблицы.
public static class FieldTypeIcons
{
    // case-sensitive
    private static readonly Geometry Text = Geometry.Parse(
        "M2 16 6.039 6.31a.5.5 0 0 1 .923 0L11 16 M22 9v7 M3.304 13h6.392 M22 12.5a3.5 3.5 0 1 1-7 0 3.5 3.5 0 0 1 7 0");
    // hash
    private static readonly Geometry Number = Geometry.Parse("M4 9h16 M4 15h16 M10 3 8 21 M16 3 14 21");
    // calendar
    private static readonly Geometry Date = Geometry.Parse(
        "M11 14h1v4 M16 2v4 M3 10h18 M8 2v4 M5 4h14a2 2 0 0 1 2 2v14a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2V6a2 2 0 0 1 2-2z");
    // list
    private static readonly Geometry Select = Geometry.Parse("M3 5h.01 M3 12h.01 M3 19h.01 M8 5h13 M8 12h13 M8 19h13");
    // square-check
    private static readonly Geometry Checkbox = Geometry.Parse(
        "M5 3h14a2 2 0 0 1 2 2v14a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2V5a2 2 0 0 1 2-2z M9 12l2 2 4-4");
    // link
    private static readonly Geometry Url = Geometry.Parse(
        "M10 13a5 5 0 0 0 7.54.54l3-3a5 5 0 0 0-7.07-7.07l-1.72 1.71 M14 11a5 5 0 0 0-7.54-.54l-3 3a5 5 0 0 0 7.07 7.07l1.71-1.71");

    // Геометрия по коду типа поля. Неизвестный/системный тип → null (иконку не рисуем).
    public static Geometry? ForType(string? fieldType) => fieldType switch
    {
        "text"     => Text,
        "number"   => Number,
        "date"     => Date,
        "select"   => Select,
        "checkbox" => Checkbox,
        "url"      => Url,
        _          => null,
    };
}
