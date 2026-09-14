namespace SmartBase.Core.Models;

/// <summary>Блок раздела «Сочетания клавиш» — группа строк в списке настроек.</summary>
public enum ShortcutBlock { Global, Sidebar, Table, RecordPage, Calendar, Tasks, Notes, NotesTree, Windows }

/// <summary>
/// Зона конфликтов сочетания. Действия конфликтуют, когда их зоны пересекаются: одинаковая
/// зона либо участие <see cref="Global"/>. Разные локальные зоны живут в разных фокусах —
/// Delete уже сегодня значит разное в таблице, календаре и боковой панели, и это нормально.
/// <see cref="Reference"/> — справочные строки, в конфликтах не участвуют.
/// </summary>
public enum ShortcutContext { Global, Table, Sidebar, Calendar, Tasks, Notes, NotesTree, RecordPage, Reference }

/// <summary>
/// Действие с сочетанием клавиш. <paramref name="Defaults"/> — стандартные жесты: первый
/// основной (он показывается в списке и хранится при переназначении), остальные — псевдонимы
/// (Backspace у удалений). Пользовательская привязка — всегда ОДИН жест, заменяющий весь
/// список. У справочных строк (<paramref name="Rebindable"/> = false) вместо жестов может
/// быть <paramref name="FixedLabel"/> — подпись, невыразимая одним жестом («Стрелки»).
/// </summary>
public sealed record ShortcutAction(
    string Id,
    string Title,
    string Description,
    ShortcutBlock Block,
    ShortcutContext Context,
    IReadOnlyList<ShortcutGesture> Defaults,
    bool Rebindable,
    string? FixedLabel = null);

/// <summary>
/// Реестр всех действий программы для раздела «Настройки → Сочетания клавиш» — единственный
/// источник списка, подписей и стандартных жестов. Обработчики клавиш спрашивают
/// <c>ShortcutSettings.Matches(id, e)</c>, поэтому добавить действие — значит дописать строку
/// здесь и один вызов в обработчике. Id — «блок.имя», хранится в app.db как суффикс ключа
/// <c>shortcut.&lt;id&gt;</c>; переименование id осиротит пользовательскую привязку (сироту
/// уберет чистка на старте, привязка тихо сбросится к стандарту).
/// </summary>
public static class ShortcutActions
{
    /// <summary>Жест из канонической строки — только для дефолтов реестра; корректность всех
    /// строк сторожит тест.</summary>
    private static ShortcutGesture G(string canonical) =>
        ShortcutGesture.Parse(canonical) ?? throw new ArgumentException($"Bad default gesture: {canonical}");

    private static ShortcutAction Cmd(
        string id, string title, string desc, ShortcutBlock block, ShortcutContext ctx, params string[] defaults) =>
        new(id, title, desc, block, ctx, defaults.Select(G).ToArray(), Rebindable: true);

    private static ShortcutAction Ref(
        string id, string title, string desc, ShortcutBlock block, string label) =>
        new(id, title, desc, block, ShortcutContext.Reference, [], Rebindable: false, FixedLabel: label);

    public static IReadOnlyList<ShortcutAction> All { get; } =
    [
        // ── Глобальные ──────────────────────────────────────────────────────────
        Cmd("app.quit", "Завершить программу",
            "Работает и из фонового режима. На Mac системное Cmd+Q действует всегда, независимо от этой настройки.",
            ShortcutBlock.Global, ShortcutContext.Global, "Ctrl+Q"),
        Cmd("app.zoomIn", "Увеличить масштаб интерфейса", "",
            ShortcutBlock.Global, ShortcutContext.Global, "Ctrl+Plus"),
        Cmd("app.zoomOut", "Уменьшить масштаб интерфейса", "",
            ShortcutBlock.Global, ShortcutContext.Global, "Ctrl+Minus"),
        Cmd("app.zoomReset", "Стандартный масштаб интерфейса",
            "Ctrl+0 занят заметками — снятием заголовка.",
            ShortcutBlock.Global, ShortcutContext.Global, "Ctrl+Shift+0"),

        // ── Боковая панель ──────────────────────────────────────────────────────
        Cmd("sidebar.createCollection", "Создать коллекцию",
            "В конструкторе базы данных.",
            ShortcutBlock.Sidebar, ShortcutContext.Global, "Ctrl+Shift+N"),
        Cmd("sidebar.duplicateCollection", "Дублировать коллекцию",
            "Выделенные коллекции и группы боковой панели.",
            ShortcutBlock.Sidebar, ShortcutContext.Global, "Ctrl+Shift+D"),
        Cmd("sidebar.createGroup", "Создать группу",
            "В конструкторе базы данных.",
            ShortcutBlock.Sidebar, ShortcutContext.Global, "Ctrl+G"),
        Cmd("sidebar.delete", "Удалить выделенное в боковой панели",
            "Группы и коллекции; действует, когда последний щелчок был в боковой панели.",
            ShortcutBlock.Sidebar, ShortcutContext.Sidebar, "Delete"),

        // ── Таблица ─────────────────────────────────────────────────────────────
        Cmd("table.copy", "Копировать значения ячеек", "",
            ShortcutBlock.Table, ShortcutContext.Table, "Ctrl+C"),
        Cmd("table.cut", "Вырезать значения ячеек", "",
            ShortcutBlock.Table, ShortcutContext.Table, "Ctrl+X"),
        Cmd("table.paste", "Вставить значения", "",
            ShortcutBlock.Table, ShortcutContext.Table, "Ctrl+V"),
        Cmd("table.undo", "Отменить изменение значений",
            "До 20 шагов: вставка, очистка, вырезание.",
            ShortcutBlock.Table, ShortcutContext.Table, "Ctrl+Z"),
        Cmd("table.clearCells", "Очистить значения ячеек", "",
            ShortcutBlock.Table, ShortcutContext.Table, "Delete", "Backspace"),
        Cmd("table.selectAll", "Выделить все ячейки", "",
            ShortcutBlock.Table, ShortcutContext.Table, "Ctrl+A"),
        Cmd("table.deleteRecords", "Удалить выделенные записи", "",
            ShortcutBlock.Table, ShortcutContext.Table, "Delete"),
        Cmd("table.deleteColumns", "Удалить выделенные свойства",
            "Колонки, выделенные по заголовку.",
            ShortcutBlock.Table, ShortcutContext.Table, "Delete", "Backspace"),
        Cmd("table.createRecord", "Создать запись", "",
            ShortcutBlock.Table, ShortcutContext.Table, "Ctrl+N"),
        Cmd("table.duplicateRecord", "Дублировать запись",
            "Выделенные записи.",
            ShortcutBlock.Table, ShortcutContext.Table, "Ctrl+D"),

        Ref("table.ref.move",    "Перейти между ячейками",           "", ShortcutBlock.Table, "Стрелки"),
        Ref("table.ref.extend",  "Растянуть выделение",              "", ShortcutBlock.Table, "Shift+Стрелки"),
        Ref("table.ref.jump",    "К краю таблицы",                   "", ShortcutBlock.Table, "Ctrl+Стрелки"),
        Ref("table.ref.homeEnd", "В начало и конец строки",          "", ShortcutBlock.Table, "Home, End"),
        Ref("table.ref.corners", "В начало и конец таблицы",         "", ShortcutBlock.Table, "Ctrl+Home, Ctrl+End"),
        Ref("table.ref.page",    "Листать страницами",               "", ShortcutBlock.Table, "PageUp, PageDown"),
        Ref("table.ref.tab",     "К соседней ячейке",                "", ShortcutBlock.Table, "Tab, Shift+Tab"),
        Ref("table.ref.edit",    "Начать правку ячейки",             "", ShortcutBlock.Table, "Enter, F2"),
        Ref("table.ref.commit",  "Записать значение",
            "Tab и Shift+Tab записывают и сдвигают рамку.",             ShortcutBlock.Table, "Enter"),
        Ref("table.ref.newline", "Перенос строки в тексте и ссылке", "", ShortcutBlock.Table, "Alt+Enter"),
        Ref("table.ref.escape",  "Снять выделение, отменить правку", "", ShortcutBlock.Table, "Escape"),

        // ── Панель записи ───────────────────────────────────────────────────────
        Cmd("record.deleteProperties", "Удалить выделенные свойства записи", "",
            ShortcutBlock.RecordPage, ShortcutContext.RecordPage, "Delete", "Backspace"),

        // ── Календарь ───────────────────────────────────────────────────────────
        Cmd("calendar.deleteSelection", "Удалить выделенные события",
            "Во всех видах календаря.",
            ShortcutBlock.Calendar, ShortcutContext.Calendar, "Delete", "Backspace"),

        // ── Задачи ──────────────────────────────────────────────────────────────
        Cmd("tasks.deleteSelection", "Удалить выделенные задачи", "",
            ShortcutBlock.Tasks, ShortcutContext.Tasks, "Delete", "Backspace"),
        Ref("tasks.ref.quickAdd", "Создать задачу из строки быстрого добавления", "",
            ShortcutBlock.Tasks, "Enter"),

        // ── Заметки ─────────────────────────────────────────────────────────────
        Cmd("notes.save", "Сохранить заметку сразу",
            "Не дожидаясь автосохранения.",
            ShortcutBlock.Notes, ShortcutContext.Notes, "Ctrl+S"),
        Cmd("notes.bold",       "Жирный",               "", ShortcutBlock.Notes, ShortcutContext.Notes, "Ctrl+B"),
        Cmd("notes.italic",     "Курсив",               "", ShortcutBlock.Notes, ShortcutContext.Notes, "Ctrl+I"),
        Cmd("notes.strike",     "Зачеркнутый",          "", ShortcutBlock.Notes, ShortcutContext.Notes, "Ctrl+Shift+K"),
        Cmd("notes.inlineCode", "Код внутри строки",    "", ShortcutBlock.Notes, ShortcutContext.Notes, "Ctrl+E"),
        Cmd("notes.link",       "Ссылка",               "", ShortcutBlock.Notes, ShortcutContext.Notes, "Ctrl+K"),
        Cmd("notes.bulletList", "Маркированный список", "", ShortcutBlock.Notes, ShortcutContext.Notes, "Ctrl+Shift+L"),
        Cmd("notes.heading1",   "Заголовок 1",          "", ShortcutBlock.Notes, ShortcutContext.Notes, "Ctrl+1"),
        Cmd("notes.heading2",   "Заголовок 2",          "", ShortcutBlock.Notes, ShortcutContext.Notes, "Ctrl+2"),
        Cmd("notes.heading3",   "Заголовок 3",          "", ShortcutBlock.Notes, ShortcutContext.Notes, "Ctrl+3"),
        Cmd("notes.heading4",   "Заголовок 4",          "", ShortcutBlock.Notes, ShortcutContext.Notes, "Ctrl+4"),
        Cmd("notes.heading5",   "Заголовок 5",          "", ShortcutBlock.Notes, ShortcutContext.Notes, "Ctrl+5"),
        Cmd("notes.heading6",   "Заголовок 6",          "", ShortcutBlock.Notes, ShortcutContext.Notes, "Ctrl+6"),
        Cmd("notes.plainText",  "Снять заголовок",      "", ShortcutBlock.Notes, ShortcutContext.Notes, "Ctrl+0"),
        Ref("notes.ref.history", "Отмена и повтор в тексте заметки",
            "Собственная история редактора.", ShortcutBlock.Notes, "Ctrl+Z, Ctrl+Y"),

        // ── Дерево заметок ──────────────────────────────────────────────────────
        Cmd("notesTree.delete", "Переместить в корзину",
            "Выделенные файлы и папки — в корзину системы.",
            ShortcutBlock.NotesTree, ShortcutContext.NotesTree, "Delete"),

        // ── Окна и поля ─────────────────────────────────────────────────────────
        Ref("windows.ref.confirm",       "Подтвердить окно",
            "В окнах с полем названия или пароля.",                  ShortcutBlock.Windows, "Enter"),
        Ref("windows.ref.close",         "Закрыть окно, отменить",   "", ShortcutBlock.Windows, "Escape"),
        Ref("windows.ref.titleEdit",     "Правка заголовка коллекции или записи",
            "Enter записывает, Escape отменяет.",                    ShortcutBlock.Windows, "Enter, Escape"),
        Ref("windows.ref.textClipboard", "Буфер в текстовых полях",  "", ShortcutBlock.Windows, "Ctrl+C, Ctrl+X, Ctrl+V"),
    ];

    private static readonly Dictionary<string, ShortcutAction> ById = All.ToDictionary(a => a.Id);

    /// <summary>Действие по id; null — такого нет (устаревший ключ в app.db).</summary>
    public static ShortcutAction? Find(string id) => ById.GetValueOrDefault(id);

    /// <summary>Подпись блока в разделе настроек и в сообщениях о конфликте.</summary>
    public static string BlockTitle(ShortcutBlock block) => block switch
    {
        ShortcutBlock.Global     => "Глобальные",
        ShortcutBlock.Sidebar    => "Боковая панель",
        ShortcutBlock.Table      => "Таблица",
        ShortcutBlock.RecordPage => "Панель записи",
        ShortcutBlock.Calendar   => "Календарь",
        ShortcutBlock.Tasks      => "Задачи",
        ShortcutBlock.Notes      => "Заметки",
        ShortcutBlock.NotesTree  => "Дерево заметок",
        _                        => "Окна и поля",
    };

    /// <summary>Пересекаются ли зоны конфликтов: одинаковая зона либо участие Global.
    /// Справочная зона не пересекается ни с чем.</summary>
    public static bool ContextsOverlap(ShortcutContext a, ShortcutContext b)
    {
        if (a == ShortcutContext.Reference || b == ShortcutContext.Reference) return false;
        return a == b || a == ShortcutContext.Global || b == ShortcutContext.Global;
    }

    /// <summary>Пары одного контекста, которым ЗАКОННО делить жест: тройка Delete-действий
    /// таблицы разводится состоянием (выделены ячейки / записи / колонки — одно гасит другое),
    /// как и сегодня. Любая другая пара с общим жестом в пересекающихся зонах — конфликт.</summary>
    public static bool AllowSameGesture(string idA, string idB)
    {
        return Trio(idA) && Trio(idB);
        static bool Trio(string id) =>
            id is "table.clearCells" or "table.deleteRecords" or "table.deleteColumns";
    }
}
