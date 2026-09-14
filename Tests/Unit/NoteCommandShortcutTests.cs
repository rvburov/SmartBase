using Avalonia.Input;
using SmartBase.Controls.Components;
using SmartBase.Controls.Widgets;
using SmartBase.Core.Models;
using SmartBase.Utils;

namespace SmartBase.Tests.Unit;

/// <summary>
/// Разметка заметки по клавишам: таблица «клавиша → команда» переехала на реестр сочетаний,
/// и здесь сторожится, что стандартные сочетания остались прежними, а переназначение
/// действительно доходит до редактора. Редактор для этого не нужен — <c>CommandFor</c>
/// чистая функция.
/// </summary>
public sealed class NoteCommandShortcutTests
{
    private sealed class FakeStore : ISettingsDatabase
    {
        private readonly Dictionary<string, string> _d = new();
        public bool Has(string key) => _d.ContainsKey(key);
        public string? Get(string key) => _d.TryGetValue(key, out var v) ? v : null;
        public void Set(string key, string value) => _d[key] = value;
        public void Remove(string key) => _d.Remove(key);
        public IReadOnlyList<string> KeysWithPrefix(string prefix) =>
            [.. _d.Keys.Where(k => k.StartsWith(prefix, StringComparison.Ordinal))];
    }

    private static void Attach()
    {
        AppSettings.AttachStore(new FakeStore());
        ShortcutSettings.Init();
    }

    private const KeyModifiers Ctrl = KeyModifiers.Control;
    private const KeyModifiers CtrlShift = KeyModifiers.Control | KeyModifiers.Shift;

    [Theory]
    [InlineData(Key.B, Ctrl, NoteCommand.Bold)]
    [InlineData(Key.I, Ctrl, NoteCommand.Italic)]
    [InlineData(Key.E, Ctrl, NoteCommand.InlineCode)]
    [InlineData(Key.K, Ctrl, NoteCommand.Link)]
    [InlineData(Key.K, CtrlShift, NoteCommand.Strike)]
    [InlineData(Key.L, CtrlShift, NoteCommand.BulletList)]
    [InlineData(Key.D0, Ctrl, NoteCommand.PlainText)]
    [InlineData(Key.D1, Ctrl, NoteCommand.Heading1)]
    [InlineData(Key.D6, Ctrl, NoteCommand.Heading6)]
    public void Standard_shortcuts_map_to_the_same_commands_as_before(Key key, KeyModifiers mods, NoteCommand expected)
    {
        Attach();
        Assert.Equal(expected, NoteEditorBox.CommandFor(key, mods));
    }

    [Fact]
    public void On_mac_the_command_modifier_works_too()
    {
        Attach();
        Assert.Equal(NoteCommand.Bold, NoteEditorBox.CommandFor(Key.B, KeyModifiers.Meta));
    }

    [Fact]
    public void A_gesture_that_is_not_ours_gives_no_command()
    {
        Attach();
        Assert.Null(NoteEditorBox.CommandFor(Key.B, KeyModifiers.None));
        Assert.Null(NoteEditorBox.CommandFor(Key.B, CtrlShift));   // Ctrl+Shift+B никому не назначен
        Assert.Null(NoteEditorBox.CommandFor(Key.Q, Ctrl));
    }

    [Fact]
    public void A_reassigned_shortcut_reaches_the_editor_and_the_old_one_stops()
    {
        Attach();
        ShortcutSettings.SetGesture("notes.bold", ShortcutGesture.Parse("Ctrl+Alt+B")!.Value);

        Assert.Equal(NoteCommand.Bold, NoteEditorBox.CommandFor(Key.B, KeyModifiers.Control | KeyModifiers.Alt));
        Assert.Null(NoteEditorBox.CommandFor(Key.B, Ctrl));
        // Соседние команды не задеты.
        Assert.Equal(NoteCommand.Italic, NoteEditorBox.CommandFor(Key.I, Ctrl));
    }
}
