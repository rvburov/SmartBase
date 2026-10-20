using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace SmartBase.Controls.Components;

// Иконки типов свойств (lucide, сетка 24×24) — единый источник для диалога
// создания/изменения свойства и заголовков колонок таблицы.
// Геометрия берется из общего словаря Assets/Styles/Icons.axaml.
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
        "status"   => Icon("IconFieldStatus"),
        "multiselect" => Icon("IconFieldMultiSelect"),
        "checkbox" => Icon("IconFieldCheckbox"),
        "url"      => Icon("IconFieldUrl"),
        "file"     => Icon("IconFieldFile"),
        "relation" => Icon("IconFieldRelation"),
        "lookup"   => Icon("IconFieldRelation"),
        "rollup"   => Icon("IconFieldNumber"),
        "user"     => Icon("IconUser"),
        // Вычисляемые типы: значение авто из метаданных записи. Иконку переиспользуем
        // (дата — календарь, пользователь — человек), различаются подписью.
        "created_at" or "updated_at" => Icon("IconCalendarDate"),
        "created_by" or "updated_by" => Icon("IconUser"),
        _          => null,
    };
}
