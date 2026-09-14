using Avalonia.Input;
using SmartBase.Core.Models;

namespace SmartBase.Utils;

/// <summary>
/// Живые пользовательские привязки сочетаний клавиш (по образцу <see cref="TaskSettings"/>):
/// кэш поверх AppSettings + событие <see cref="Changed"/> для раздела настроек. Обработчики
/// клавиш спрашивают <see cref="Matches(string, KeyEventArgs)"/> на каждое нажатие — кэш
/// обновляется в Set/Reset, поэтому переназначение действует сразу, без перезапуска.
///
/// Привязка заменяет ВЕСЬ список стандартных жестов действия: назначил свое — стандартные
/// псевдонимы (Backspace у удалений) гаснут. Мусор в хранилище и привязки исчезнувших
/// действий читаются как стандарт.
/// </summary>
public static class ShortcutSettings
{
    private static readonly Dictionary<string, ShortcutGesture> Custom = new(StringComparer.Ordinal);

    /// <summary>Привязки изменились — разделу настроек пора перечитаться.</summary>
    public static event Action? Changed;

    /// <summary>Счетчик изменений — «догоняющая» перерисовка, как у TaskSettings.</summary>
    public static long Stamp { get; private set; }

    /// <summary>Старт: считать сохраненные привязки. Вызывать после AppSettings.AttachStore.</summary>
    public static void Init()
    {
        Custom.Clear();
        foreach (var (actionId, value) in AppSettings.LoadShortcuts())
        {
            if (ShortcutActions.Find(actionId) is not { Rebindable: true }) continue;
            if (ShortcutGesture.Parse(value) is { } gesture) Custom[actionId] = gesture;
        }
    }

    /// <summary>Пользовательский жест действия; null — действует стандарт.</summary>
    public static ShortcutGesture? UserGesture(string actionId) =>
        Custom.TryGetValue(actionId, out var g) ? g : null;

    /// <summary>Отличается ли привязка от стандартной (для кнопки сброса).</summary>
    public static bool IsCustomized(string actionId) => Custom.ContainsKey(actionId);

    /// <summary>Действующие жесты: пользовательский либо весь список стандартных.</summary>
    public static IReadOnlyList<ShortcutGesture> GesturesFor(string actionId)
    {
        if (Custom.TryGetValue(actionId, out var g)) return [g];
        return ShortcutActions.Find(actionId)?.Defaults ?? [];
    }

    /// <summary>Основной действующий жест — для подписей в списке и контекстных меню;
    /// null — у действия нет жеста (справочная строка).</summary>
    public static ShortcutGesture? PrimaryGesture(string actionId)
    {
        var gestures = GesturesFor(actionId);
        return gestures.Count > 0 ? gestures[0] : null;
    }

    /// <summary>Главная проверка обработчиков: нажатие — это действие?</summary>
    public static bool Matches(string actionId, KeyEventArgs e) =>
        Matches(actionId, e.Key, e.KeyModifiers);

    /// <summary>То же без KeyEventArgs — для мест, тестируемых без Avalonia-событий
    /// (NoteEditorBox.CommandFor). Сравнивается целый жест: клавиша и полный набор
    /// модификаторов, Meta считается за Ctrl.</summary>
    public static bool Matches(string actionId, Key key, KeyModifiers mods)
    {
        var gestures = GesturesFor(actionId);
        for (var i = 0; i < gestures.Count; i++)
            if (gestures[i].Matches(key, mods))
                return true;
        return false;
    }

    /// <summary>Назначить жест. Основной стандартный жест не хранится — это сброс:
    /// дельты в базе нет, когда привязка равна стандарту.</summary>
    public static void SetGesture(string actionId, ShortcutGesture gesture)
    {
        if (ShortcutActions.Find(actionId) is not { Rebindable: true } action) return;

        if (action.Defaults.Count > 0 && gesture == action.Defaults[0])
        {
            Reset(actionId);
            return;
        }

        if (UserGesture(actionId) == gesture) return;
        Custom[actionId] = gesture;
        AppSettings.SaveShortcut(actionId, gesture.Canonical);
        Publish();
    }

    /// <summary>Сбросить привязку к стандарту (удалить ключ из app.db).</summary>
    public static void Reset(string actionId)
    {
        AppSettings.SaveShortcut(actionId, null);
        if (!Custom.Remove(actionId)) return;
        Publish();
    }

    /// <summary>
    /// Кто уже занял жест — действие с пересекающейся зоной конфликтов, у которого этот жест
    /// сейчас действует (пользовательский или стандартный). null — жест свободен. Тройка
    /// Delete-действий таблицы, разводимая состоянием, конфликтом не считается
    /// (<see cref="ShortcutActions.AllowSameGesture"/>).
    /// </summary>
    public static ShortcutAction? ConflictOf(ShortcutGesture gesture, string forActionId)
    {
        if (ShortcutActions.Find(forActionId) is not { } self) return null;

        foreach (var other in ShortcutActions.All)
        {
            if (!other.Rebindable || other.Id == forActionId) continue;
            if (!ShortcutActions.ContextsOverlap(self.Context, other.Context)) continue;
            if (ShortcutActions.AllowSameGesture(forActionId, other.Id)) continue;

            foreach (var g in GesturesFor(other.Id))
                if (g == gesture)
                    return other;
        }
        return null;
    }

    private static void Publish()
    {
        Stamp++;
        Changed?.Invoke();
    }
}
