using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using SmartBase.Core.Models;
using SmartBase.Utils;

namespace SmartBase.Controls.Dialogs;

/// <summary>
/// Захват нового сочетания для действия («Настройки → Сочетания клавиш», кнопка «+»).
/// Показывает нажатое сочетание и сохраняет его по кнопке; результат — жест или null
/// (отмена). Сам в настройки ничего не пишет — это делает вызвавшая панель.
///
/// Escape тут всегда отмена, поэтому назначить его нельзя (его и нет в реестре жестов).
/// Клавиши-одиночки без модификатора отбиваются: голая буква печатала бы символ и запускала
/// действие разом. Занятое сочетание не перехватывается: сначала освободи его у прежнего
/// владельца — так у одного жеста всегда один хозяин, и отменить это одним кликом нельзя.
/// </summary>
public partial class ShortcutCaptureDialog : Window
{
    private readonly ShortcutAction _action;
    private ShortcutGesture? _captured;

    public ShortcutCaptureDialog() : this(ShortcutActions.All[0]) { }

    public ShortcutCaptureDialog(ShortcutAction action)
    {
        InitializeComponent();
        _action = action;

        ActionText.Text  = ShortcutActions.FullTitle(action);
        GestureText.Text = "Нажмите сочетание клавиш";

        CancelBtn.Click  += (_, _) => Close(null);
        ConfirmBtn.Click += (_, _) => { if (_captured is { } g) Close(g); };

        // Туннелем и поверх уже обработанных: иначе Tab ушел бы на перевод фокуса, а стрелки —
        // кнопкам, и до захвата эти клавиши не дошли бы.
        AddHandler(KeyDownEvent, OnCaptureKeyDown, RoutingStrategies.Tunnel, handledEventsToo: true);

        // Фокус — в зону захвата: нажатия идут к сфокусированному элементу, и без него окно
        // клавиш не увидит вовсе. Кнопкам фокус не отдаем — на них Enter сработал бы нажатием.
        Opened += (_, _) => { if (!CaptureBox.Focus()) Focus(); };
    }

    private void OnCaptureKeyDown(object? sender, KeyEventArgs e)
    {
        e.Handled = true;

        if (e.Key == Key.Escape) { Close(null); return; }

        // Нажаты одни модификаторы — показываем незавершенное сочетание и ждем клавишу.
        if (e.Key is Key.LeftCtrl or Key.RightCtrl or Key.LeftShift or Key.RightShift
                  or Key.LeftAlt or Key.RightAlt or Key.LWin or Key.RWin)
        {
            Pending(e.KeyModifiers);
            return;
        }

        if (ShortcutGesture.TryCreate(e.Key, e.KeyModifiers) is not { } gesture)
        {
            // Escape, стрелки и прочая навигация вшиты в поведение разделов и не назначаются.
            Reject("Эта клавиша занята навигацией и не назначается.");
            return;
        }

        GestureText.Text = gesture.Display();

        if (gesture.Modifiers == KeyModifiers.None && ShortcutGesture.RequiresModifier(gesture.Key))
        {
            Reject("Добавьте модификатор: Ctrl, Alt или Shift.");
            return;
        }

        if (ShortcutSettings.ConflictOf(gesture, _action.Id) is { } taken)
        {
            Reject($"Уже используется: {taken.Title} ({ShortcutActions.BlockTitle(taken.Block)}).");
            return;
        }

        _captured = gesture;
        NoticeBanner.Message = null;
        ConfirmBtn.IsEnabled = true;
    }

    // Зажаты модификаторы без клавиши: «Ctrl+Shift+…».
    private void Pending(KeyModifiers mods)
    {
        _captured = null;
        ConfirmBtn.IsEnabled = false;

        var parts = new List<string>(3);
        if (mods.HasFlag(KeyModifiers.Control) || mods.HasFlag(KeyModifiers.Meta))
            parts.Add(OperatingSystem.IsMacOS() ? "Cmd" : "Ctrl");
        if (mods.HasFlag(KeyModifiers.Alt))   parts.Add("Alt");
        if (mods.HasFlag(KeyModifiers.Shift)) parts.Add("Shift");

        GestureText.Text = parts.Count > 0
            ? string.Join("+", parts) + "+…"
            : "Нажмите сочетание клавиш";
    }

    private void Reject(string reason)
    {
        _captured = null;
        ConfirmBtn.IsEnabled = false;
        NoticeBanner.Message = reason;
    }
}
