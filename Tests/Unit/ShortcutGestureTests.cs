using Avalonia.Input;
using SmartBase.Core.Models;

namespace SmartBase.Tests.Unit;

/// <summary>
/// Сочетание клавиш как значение: разбор строки, каноническая запись и сравнение с нажатием.
/// Главный инвариант — канон один на обе платформы: «Ctrl» значит командный модификатор,
/// Cmd (Meta) читается и сравнивается как Ctrl, а показывается «Cmd» только на Mac.
/// Второй инвариант — сравнивается ЦЕЛЫЙ жест: лишний модификатор значит «сочетание не наше».
/// </summary>
public sealed class ShortcutGestureTests
{
    private static ShortcutGesture Parse(string text) =>
        ShortcutGesture.Parse(text) ?? throw new Xunit.Sdk.XunitException($"не разобрано: {text}");

    // ── Разбор и канон ────────────────────────────────────────────────────────

    [Theory]
    [InlineData("Ctrl+C")]
    [InlineData("Ctrl+Shift+K")]
    [InlineData("Ctrl+Alt+Shift+F5")]
    [InlineData("Delete")]
    [InlineData("Ctrl+0")]
    [InlineData("Ctrl+Plus")]
    public void Canonical_text_survives_a_round_trip(string canonical) =>
        Assert.Equal(canonical, Parse(canonical).Canonical);

    [Fact]
    public void Modifier_order_and_case_are_free_on_input()
    {
        var canonical = Parse("Ctrl+Alt+Shift+K").Canonical;
        Assert.Equal(canonical, Parse("shift+ALT+ctrl+k").Canonical);
        Assert.Equal(canonical, Parse("Alt+Shift+Ctrl+K").Canonical);
    }

    [Fact]
    public void Cmd_and_meta_are_read_as_the_canonical_ctrl()
    {
        Assert.Equal("Ctrl+C", Parse("Cmd+C").Canonical);
        Assert.Equal("Ctrl+C", Parse("Meta+C").Canonical);
        Assert.Equal(Parse("Ctrl+C"), Parse("Command+C"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Ctrl+")]
    [InlineData("+C")]
    [InlineData("Ctrl")]           // одни модификаторы — не жест
    [InlineData("Ctrl+Escape")]    // Escape всегда отменяет, назначать нечего
    [InlineData("Ctrl+Up")]        // навигация не переназначается
    [InlineData("Hyper+C")]
    public void Garbage_does_not_parse(string text) =>
        Assert.Null(ShortcutGesture.Parse(text));

    // ── Клавиатура: нормализация ──────────────────────────────────────────────

    [Fact]
    public void Numpad_digits_and_signs_are_the_same_keys_as_the_main_row()
    {
        var five = Parse("Ctrl+5");
        Assert.True(five.Matches(Key.D5, KeyModifiers.Control));
        Assert.True(five.Matches(Key.NumPad5, KeyModifiers.Control));

        var plus = Parse("Ctrl+Plus");
        Assert.True(plus.Matches(Key.OemPlus, KeyModifiers.Control));
        Assert.True(plus.Matches(Key.Add, KeyModifiers.Control));
    }

    [Fact]
    public void Meta_counts_as_control_when_matching()
    {
        var copy = Parse("Ctrl+C");
        Assert.True(copy.Matches(Key.C, KeyModifiers.Control));
        Assert.True(copy.Matches(Key.C, KeyModifiers.Meta));
    }

    // ── Сравнение целого жеста ────────────────────────────────────────────────

    [Fact]
    public void An_extra_modifier_means_the_gesture_is_not_ours()
    {
        var copy = Parse("Ctrl+C");
        Assert.False(copy.Matches(Key.C, KeyModifiers.Control | KeyModifiers.Shift));
        Assert.False(copy.Matches(Key.C, KeyModifiers.Control | KeyModifiers.Alt));
        Assert.False(copy.Matches(Key.C, KeyModifiers.None));
        Assert.False(copy.Matches(Key.V, KeyModifiers.Control));
    }

    [Fact]
    public void Delete_without_modifiers_does_not_match_delete_with_them()
    {
        var clear = Parse("Delete");
        Assert.True(clear.Matches(Key.Delete, KeyModifiers.None));
        Assert.False(clear.Matches(Key.Delete, KeyModifiers.Alt));
    }

    // ── Создание из нажатия ───────────────────────────────────────────────────

    [Fact]
    public void Only_keys_from_the_table_become_gestures()
    {
        Assert.NotNull(ShortcutGesture.TryCreate(Key.K, KeyModifiers.Control));
        Assert.NotNull(ShortcutGesture.TryCreate(Key.F7, KeyModifiers.None));
        Assert.Null(ShortcutGesture.TryCreate(Key.Escape, KeyModifiers.Control));
        Assert.Null(ShortcutGesture.TryCreate(Key.Left, KeyModifiers.Control));
        Assert.Null(ShortcutGesture.TryCreate(Key.LeftCtrl, KeyModifiers.Control));
    }

    [Fact]
    public void A_bare_letter_or_digit_needs_a_modifier_but_a_command_key_does_not()
    {
        Assert.True(ShortcutGesture.RequiresModifier(Key.K));
        Assert.True(ShortcutGesture.RequiresModifier(Key.D1));
        Assert.True(ShortcutGesture.RequiresModifier(Key.Enter));
        Assert.False(ShortcutGesture.RequiresModifier(Key.Delete));
        Assert.False(ShortcutGesture.RequiresModifier(Key.Back));
        Assert.False(ShortcutGesture.RequiresModifier(Key.F2));
    }

    // ── Подписи ───────────────────────────────────────────────────────────────

    [Fact]
    public void On_mac_the_command_modifier_is_shown_as_cmd()
    {
        var gesture = Parse("Ctrl+Shift+K");
        Assert.Equal("Ctrl+Shift+K", gesture.Display(mac: false));
        Assert.Equal("Cmd+Shift+K",  gesture.Display(mac: true));
        Assert.Equal(["Ctrl", "Shift", "K"], gesture.DisplayParts(mac: false));
        Assert.Equal(["Cmd", "Shift", "K"],  gesture.DisplayParts(mac: true));
    }

    [Fact]
    public void Signs_are_shown_as_signs_and_stored_as_words()
    {
        Assert.Equal("Ctrl++", Parse("Ctrl+Plus").Display(mac: false));
        Assert.Equal("Ctrl+-", Parse("Ctrl+Minus").Display(mac: false));
        Assert.Equal("Ctrl+Plus", Parse("Ctrl+Plus").Canonical);
    }

    // ── Жест для подписи пункта меню ──────────────────────────────────────────

    [Fact]
    public void Menu_gesture_uses_the_platform_command_modifier()
    {
        var mac = Parse("Ctrl+Shift+C").ToKeyGesture(KeyModifiers.Meta);
        Assert.Equal(Key.C, mac.Key);
        Assert.Equal(KeyModifiers.Meta | KeyModifiers.Shift, mac.KeyModifiers);

        var win = Parse("Ctrl+C").ToKeyGesture(KeyModifiers.Control);
        Assert.Equal(KeyModifiers.Control, win.KeyModifiers);

        // Без командного модификатора подмены нет.
        Assert.Equal(KeyModifiers.None, Parse("Delete").ToKeyGesture(KeyModifiers.Meta).KeyModifiers);
    }
}
