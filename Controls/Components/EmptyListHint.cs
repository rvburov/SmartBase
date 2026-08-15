using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Layout;
using Avalonia.Media;
using SmartBase.Utils;

namespace SmartBase.Controls.Components;

/// <summary>
/// Надпись-заглушка для СВОИХ выпадающих списков (чеклист «Мультисписка» и «Связи», панели
/// фильтра колонки таблицы, логов и истории): пустой список иначе выглядит как белая карточка
/// без единого признака, что вариантов нет или поиск ничего не нашёл. У нативных ComboBox то
/// же самое делает копия темы (Assets/Styles/ComboBoxTheme.axaml, надпись «Список пуст»).
///
/// Текст зависит от поиска: пустая строка поиска — «Список пуст» (вариантов нет вообще),
/// непустая — «Ничего не найдено» (не подошли под запрос).
/// </summary>
public static class EmptyListHint
{
    public const string EmptyText    = "Список пуст";
    public const string NotFoundText = "Ничего не найдено";

    /// <summary>Кладёт список и заглушку в общий Panel: заглушка видна, пока в списке нет строк.
    /// content — если список показывается не сам по себе, а в обёртке (шапка «Выделить всё» +
    /// список): в Panel идёт обёртка, а считаются строки всё того же списка.</summary>
    public static Control Wrap(ItemsControl list, TextBox? search, IBrush foreground, double fontSize,
                               Control? content = null)
    {
        var hint = new TextBlock
        {
            Text                = EmptyText,
            Foreground          = foreground,
            FontSize            = fontSize,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment   = VerticalAlignment.Center,
            IsHitTestVisible    = false,
        };
        hint.Bind(Visual.IsVisibleProperty, new Binding(nameof(ItemsControl.ItemCount))
        {
            Source    = list,
            Converter = ItemCountConverters.IsEmpty,
        });

        if (search is not null)
            search.TextChanged += (_, _) =>
                hint.Text = string.IsNullOrWhiteSpace(search.Text) ? EmptyText : NotFoundText;

        var host = new Panel();
        host.Children.Add(content ?? list);
        host.Children.Add(hint);
        return host;
    }
}
