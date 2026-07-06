using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace DataBase.Controls.Components;

// Иконки типов свойств (lucide, сетка 24×24) — единый источник для диалога
// создания/изменения свойства и заголовков колонок таблицы.
// Геометрия берётся из общего словаря Assets/Styles/Icons.axaml.
public static class FieldTypeIcons
{
    private static Geometry? Icon(string key) =>
        Application.Current!.TryFindResource(key, out var res) && res is Geometry g ? g : null;

    // Геометрия по коду типа поля (case-sensitive).
    // Неизвестный/системный тип → null (иконку не рисуем).
    public static Geometry? ForType(string? fieldType) => fieldType switch
    {
        "text"     => Icon("IconFieldText"),
        "number"   => Icon("IconFieldNumber"),
        "date"     => Icon("IconCalendarDate"),
        "select"   => Icon("IconFieldSelect"),
        "checkbox" => Icon("IconFieldCheckbox"),
        "url"      => Icon("IconFieldUrl"),
        "user"     => Icon("IconUser"),
        _          => null,
    };
}
