using Avalonia.Input;
using SmartBase.Utils;

namespace SmartBase.Tests.Unit;

/// <summary>
/// Пометка «клавишу уже забрали». Нужна там, где обработчик слушает окно с
/// <c>handledEventsToo</c> и потому видит даже обработанные события: без пометки один Delete
/// удалил бы и коллекцию в боковой панели, и выделенные события календаря.
/// </summary>
public sealed class KeyClaimTests
{
    private static KeyEventArgs Key(Key key) => new() { Key = key, RoutedEvent = InputElement.KeyDownEvent };

    [Fact]
    public void A_claimed_key_is_handled_and_recognized()
    {
        var e = Key(Avalonia.Input.Key.Delete);
        Assert.False(KeyClaim.Claimed(e));

        KeyClaim.Claim(e);

        Assert.True(e.Handled);
        Assert.True(KeyClaim.Claimed(e));
    }

    [Fact]
    public void The_claim_does_not_outlive_its_key_press()
    {
        KeyClaim.Claim(Key(Avalonia.Input.Key.Delete));

        // Следующее нажатие — другое событие, и оно свободно.
        Assert.False(KeyClaim.Claimed(Key(Avalonia.Input.Key.Delete)));
    }
}
