using Avalonia.Input;

namespace SmartBase.Core.Models;

/// <summary>
/// Сочетание клавиш в каноническом виде: модификаторы всегда Ctrl, Alt, Shift (в этом порядке),
/// клавиша — токен из фиксированной таблицы. Канон один на обе платформы: «Ctrl» означает
/// командный модификатор, на macOS это Cmd — <see cref="Normalize"/> сводит Meta к Control на
/// входе, а <see cref="Display()"/> показывает «Cmd» на Mac. Цифровой ряд и NumPad — одна
/// клавиша («5»), «+»/«-» основного ряда и NumPad — тоже.
///
/// Экземпляры создаются только через <see cref="Parse"/> и <see cref="TryCreate"/> — оба
/// нормализуют вход, поэтому сравнение record-структур и есть сравнение сочетаний.
/// </summary>
public readonly record struct ShortcutGesture(Key Key, KeyModifiers Modifiers)
{
    // ── Таблица токенов ─────────────────────────────────────────────────────────

    /// <summary>Канонические клавиши и их имена в строке хранения. Чего нет в таблице, то не
    /// назначается и не парсится: Escape всегда отменяет, стрелки и прочая навигация вшиты
    /// в поведение и переназначению не подлежат.</summary>
    private static readonly Dictionary<Key, string> Tokens = BuildTokens();

    private static readonly Dictionary<string, Key> TokensBack =
        Tokens.ToDictionary(p => p.Value.ToLowerInvariant(), p => p.Key);

    private static Dictionary<Key, string> BuildTokens()
    {
        var map = new Dictionary<Key, string>();
        for (var k = Key.A; k <= Key.Z; k++) map[k] = k.ToString();
        for (var i = 0; i <= 9; i++)         map[Key.D0 + i] = i.ToString();
        for (var i = 1; i <= 12; i++)        map[Key.F1 + (i - 1)] = "F" + i;
        map[Key.Delete]   = "Delete";
        map[Key.Back]     = "Backspace";
        map[Key.Insert]   = "Insert";
        map[Key.OemPlus]  = "Plus";
        map[Key.OemMinus] = "Minus";
        map[Key.Enter]    = "Enter";
        map[Key.Tab]      = "Tab";
        map[Key.Space]    = "Space";
        map[Key.Home]     = "Home";
        map[Key.End]      = "End";
        map[Key.PageUp]   = "PageUp";
        map[Key.PageDown] = "PageDown";
        map[Key.Escape]   = "Escape";
        // Стрелки назначаются только с модификатором (см. RequiresModifier): голыми они двигают
        // выделение в таблице и курсор в тексте, и отнимать их у навигации нельзя.
        map[Key.Up]       = "Up";
        map[Key.Down]     = "Down";
        map[Key.Left]     = "Left";
        map[Key.Right]    = "Right";
        return map;
    }

    // ── Нормализация ────────────────────────────────────────────────────────────

    /// <summary>Meta (Cmd на Mac) — это канонический Ctrl. Вызывается на любом входе.</summary>
    public static KeyModifiers Normalize(KeyModifiers mods)
    {
        if (mods.HasFlag(KeyModifiers.Meta))
            mods = (mods & ~KeyModifiers.Meta) | KeyModifiers.Control;
        return mods;
    }

    /// <summary>NumPad-цифры и NumPad «+»/«-» сводятся к основному ряду: физических клавиш две,
    /// сочетание одно.</summary>
    public static Key NormalizeKey(Key key) => key switch
    {
        >= Key.NumPad0 and <= Key.NumPad9 => Key.D0 + (key - Key.NumPad0),
        Key.Add                           => Key.OemPlus,
        Key.Subtract                      => Key.OemMinus,
        _                                 => key,
    };

    // ── Создание ────────────────────────────────────────────────────────────────

    /// <summary>Жест из нажатия. null — клавиша не из таблицы (Escape, стрелки, голый
    /// модификатор и т.п.); такое не назначается.</summary>
    public static ShortcutGesture? TryCreate(Key key, KeyModifiers mods)
    {
        key = NormalizeKey(key);
        return Tokens.ContainsKey(key) ? new ShortcutGesture(key, Normalize(mods)) : null;
    }

    /// <summary>«Ctrl+Shift+K» → жест. Регистр и порядок модификаторов свободные, «Cmd»/«Meta»
    /// читаются как Ctrl. Мусор (пусто, неизвестный токен, одни модификаторы) → null.</summary>
    public static ShortcutGesture? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;

        var parts = text.Split('+', StringSplitOptions.TrimEntries);
        if (parts.Length == 0 || parts.Any(p => p.Length == 0)) return null;

        var mods = KeyModifiers.None;
        for (var i = 0; i < parts.Length - 1; i++)
        {
            switch (parts[i].ToLowerInvariant())
            {
                case "ctrl" or "control" or "cmd" or "command" or "meta": mods |= KeyModifiers.Control; break;
                case "alt" or "option":                                   mods |= KeyModifiers.Alt;     break;
                case "shift":                                             mods |= KeyModifiers.Shift;   break;
                default: return null;
            }
        }

        return TokensBack.TryGetValue(parts[^1].ToLowerInvariant(), out var key)
            ? new ShortcutGesture(key, mods)
            : null;
    }

    // ── Сравнение с нажатием ────────────────────────────────────────────────────

    /// <summary>Совпадение с нажатием: клавиша и ПОЛНЫЙ набор модификаторов. Ctrl+Shift+C не
    /// совпадает с Ctrl+C — лишний модификатор значит «сочетание не наше».</summary>
    public bool Matches(Key key, KeyModifiers mods) =>
        NormalizeKey(key) == Key && Normalize(mods) == Modifiers;

    /// <summary>Голая буква, цифра, Enter или стрелка в сочетании недопустимы — они печатают
    /// символ, подтверждают окна или двигают выделение. Без модификатора живут только
    /// клавиши-команды.</summary>
    public static bool RequiresModifier(Key key) => NormalizeKey(key) switch
    {
        >= Key.F1 and <= Key.F12             => false,
        Key.Delete or Key.Back or Key.Insert => false,
        _                                    => true,
    };

    // ── Строки ──────────────────────────────────────────────────────────────────

    /// <summary>Строка для хранения и сравнения: «Ctrl+Alt+Shift+Клавиша».</summary>
    public string Canonical
    {
        get
        {
            var sb = new System.Text.StringBuilder();
            if (Modifiers.HasFlag(KeyModifiers.Control)) sb.Append("Ctrl+");
            if (Modifiers.HasFlag(KeyModifiers.Alt))     sb.Append("Alt+");
            if (Modifiers.HasFlag(KeyModifiers.Shift))   sb.Append("Shift+");
            sb.Append(Tokens.TryGetValue(Key, out var token) ? token : Key.ToString());
            return sb.ToString();
        }
    }

    /// <summary>Подпись для человека на текущей платформе.</summary>
    public string Display() => Display(OperatingSystem.IsMacOS());

    /// <summary>Подпись для человека: на Mac «Ctrl» показывается как «Cmd»; «Plus»/«Minus» — как
    /// знаки. Параметр — для тестов обеих платформ.</summary>
    public string Display(bool mac)
    {
        var sb = new System.Text.StringBuilder();
        if (Modifiers.HasFlag(KeyModifiers.Control)) sb.Append(mac ? "Cmd+" : "Ctrl+");
        if (Modifiers.HasFlag(KeyModifiers.Alt))     sb.Append("Alt+");
        if (Modifiers.HasFlag(KeyModifiers.Shift))   sb.Append("Shift+");
        sb.Append(Key switch
        {
            Key.OemPlus  => "+",
            Key.OemMinus => "-",
            _            => Tokens.TryGetValue(Key, out var token) ? token : Key.ToString(),
        });
        return sb.ToString();
    }

    /// <summary>Части подписи по отдельности («Ctrl», «Shift», «K») — для плашек-клавиш в
    /// настройках.</summary>
    public IReadOnlyList<string> DisplayParts(bool mac)
    {
        var parts = new List<string>(4);
        if (Modifiers.HasFlag(KeyModifiers.Control)) parts.Add(mac ? "Cmd" : "Ctrl");
        if (Modifiers.HasFlag(KeyModifiers.Alt))     parts.Add("Alt");
        if (Modifiers.HasFlag(KeyModifiers.Shift))   parts.Add("Shift");
        parts.Add(Key switch
        {
            Key.OemPlus  => "+",
            Key.OemMinus => "-",
            _            => Tokens.TryGetValue(Key, out var token) ? token : Key.ToString(),
        });
        return parts;
    }

    /// <summary>Жест для <c>InputGesture</c> пункта меню: канонический Ctrl подменяется командным
    /// модификатором платформы (его дает <c>PlatformSettings.HotkeyConfiguration</c>).</summary>
    public KeyGesture ToKeyGesture(KeyModifiers commandModifiers)
    {
        var mods = Modifiers;
        if (mods.HasFlag(KeyModifiers.Control))
            mods = (mods & ~KeyModifiers.Control) | commandModifiers;
        return new KeyGesture(Key, mods);
    }

    public override string ToString() => Canonical;
}
