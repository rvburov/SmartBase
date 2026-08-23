using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;

namespace SmartBase.Controls.Behaviors;

/// <summary>
/// Выравнивание текста в поле, которое РАСТЕТ по содержимому (поля «Название» в окнах записи
/// и задачи: <c>MinHeight</c> на одну строку, <c>MaxHeight</c> на три, дальше прокрутка).
///
/// Одна строка — текст по центру плашки, несколько — от верхнего края. Постоянный
/// <c>VerticalContentAlignment</c> не годится ни один: <c>Top</c> прижимает одинокую строку
/// кверху (в поле остается пустой запас до <c>MinHeight</c>), а <c>Center</c> ломает показ,
/// когда строк больше, чем влезает.
///
/// Признак «строка одна» берем не из текста, а из ГЕОМЕТРИИ: поле стоит на своей минимальной
/// высоте — значит содержимое короче нее и запас есть, его и делим поровну. Как только текст
/// перестает помещаться, поле начинает расти, отступы задает уже сам шаблон — выравниваем
/// поверху. Так правило не зависит ни от размера шрифта, ни от масштаба интерфейса, ни от
/// того, перенос это по <c>\n</c> или по ширине поля.
///
/// Вешается стилем или прямо в разметке:
/// <code>&lt;TextBox b:GrowingFieldAlign.CenterWhenSingleLine="True" … /&gt;</code>
/// </summary>
public static class GrowingFieldAlign
{
    public static readonly AttachedProperty<bool> CenterWhenSingleLineProperty =
        AvaloniaProperty.RegisterAttached<TextBox, bool>(
            "CenterWhenSingleLine", typeof(GrowingFieldAlign));

    public static void SetCenterWhenSingleLine(TextBox control, bool value) =>
        control.SetValue(CenterWhenSingleLineProperty, value);

    public static bool GetCenterWhenSingleLine(TextBox control) =>
        control.GetValue(CenterWhenSingleLineProperty);

    static GrowingFieldAlign()
    {
        CenterWhenSingleLineProperty.Changed.AddClassHandler<TextBox>((control, e) =>
        {
            control.PropertyChanged -= OnControlPropertyChanged;
            if (e.NewValue is not true) return;

            control.PropertyChanged += OnControlPropertyChanged;
            Apply(control);
        });
    }

    private static void OnControlPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == Visual.BoundsProperty && sender is TextBox control) Apply(control);
    }

    private static void Apply(TextBox control)
    {
        // Дребезга нет: выравнивание не влияет на высоту поля (ее задают содержимое и
        // Min/MaxHeight), поэтому признак «на минимальной высоте» от правки не меняется.
        var atMinHeight = control.Bounds.Height <= control.MinHeight + 0.5;
        control.VerticalContentAlignment = atMinHeight ? VerticalAlignment.Center : VerticalAlignment.Top;
    }
}
