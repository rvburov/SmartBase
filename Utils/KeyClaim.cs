using Avalonia.Input;

namespace SmartBase.Utils;

/// <summary>
/// Пометка «эту клавишу уже забрал другой раздел».
///
/// Разделы, у которых выделение живет вне фокуса (календарь, задачи), слушают клавиши на окне
/// с <c>handledEventsToo: true</c> — иначе нажатие, обработанное внутренним списком, до них не
/// доходит. Обратная сторона: такой обработчик видит и ЧУЖИЕ обработанные события. Без пометки
/// один Delete сработал бы дважды — боковая панель удалила бы коллекцию, а календарь заодно
/// выделенные события.
///
/// Хранится ссылка на само событие: она уникальна для нажатия, поэтому «забрано» не переживает
/// клавишу и не требует уборки.
/// </summary>
public static class KeyClaim
{
    private static KeyEventArgs? _claimed;

    /// <summary>Забрать клавишу себе: пометить событие и объявить его обработанным.</summary>
    public static void Claim(KeyEventArgs e)
    {
        _claimed = e;
        e.Handled = true;
    }

    /// <summary>Забрал ли эту клавишу кто-то другой (тогда свое действие делать не нужно).</summary>
    public static bool Claimed(KeyEventArgs e) => ReferenceEquals(_claimed, e);
}
