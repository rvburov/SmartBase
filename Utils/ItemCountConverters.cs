using Avalonia.Data.Converters;

namespace SmartBase.Utils;

/// <summary>
/// Конвертеры количества элементов для разметки: показ заглушки «Список пуст» в выпадашке
/// (копия темы ComboBox, Assets/Styles/ComboBoxTheme.axaml). Отдельный класс, потому что
/// биндинг в шаблоне сравнивает int с нулем, а встроенного конвертера под это нет.
/// </summary>
public static class ItemCountConverters
{
    /// <summary>true, когда элементов нет (ItemCount = 0).</summary>
    public static readonly IValueConverter IsEmpty =
        new FuncValueConverter<int, bool>(count => count == 0);
}
