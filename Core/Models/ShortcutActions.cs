namespace SmartBase.Core.Models;

/// <summary>Блок раздела «Сочетания клавиш» — приставка в названии строки: «Таблица: Копировать
/// значения ячеек».</summary>
public enum ShortcutBlock { Global, Sidebar, Table, RecordPage, Calendar, Tasks, Notes, NotesTree, Windows }

/// <summary>
/// Зона конфликтов сочетания. Действия конфликтуют, когда их зоны пересекаются: одинаковая
/// зона либо участие <see cref="Global"/>. Разные локальные зоны живут в разных фокусах —
/// Delete уже сегодня значит разное в таблице, календаре и боковой панели, и это нормально.
/// </summary>
public enum ShortcutContext { Global, Table, Sidebar, Calendar, Tasks, Notes, NotesTree, RecordPage, Windows }

/// <summary>
/// Действие с сочетанием клавиш. <paramref name="Defaults"/> — стандартные жесты: первый
/// основной (он показывается в списке и хранится при переназначении), остальные — псевдонимы
/// (Backspace у удалений). Пользовательская привязка — всегда ОДИН жест, заменяющий весь
/// список.
///
/// <paramref name="Rebindable"/> = false — справочная строка: клавиша вшита в поведение
/// таблицы, редакторов и окон, ее только показывают. Жесты у таких строк все равно заполнены:
/// так они ЗАНИМАЮТ свое сочетание, и окно захвата не даст отнять Enter у правки ячейки.
/// <paramref name="FixedLabel"/> — подпись для жестов, которые одной клавишей не выразить
/// («Стрелки» — это сразу четыре).
/// </summary>
public sealed record ShortcutAction(
    string Id,
    string Title,
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
///
/// В строке ровно одно сочетание: «В начало таблицы» и «В конец таблицы» — разные строки.
/// Список из нескольких клавиш в одной строке нечем назначать и неудобно читать.
/// </summary>
public static class ShortcutActions
{
    /// <summary>Жест из канонической строки — только для дефолтов реестра; корректность всех
    /// строк сторожит тест.</summary>
    private static ShortcutGesture G(string canonical) =>
        ShortcutGesture.Parse(canonical) ?? throw new ArgumentException($"Bad default gesture: {canonical}");

    private static ShortcutAction Cmd(
        string id, string title, ShortcutBlock block, ShortcutContext ctx, params string[] defaults) =>
        new(id, title, block, ctx, defaults.Select(G).ToArray(), Rebindable: true);

    /// <summary>Справочная строка: клавиша показана, но не назначается. Жест все равно
    /// занимает свое сочетание — отнять его у навигации нельзя.</summary>
    private static ShortcutAction Ref(
        string id, string title, ShortcutBlock block, ShortcutContext ctx, params string[] gestures) =>
        new(id, title, block, ctx, gestures.Select(G).ToArray(), Rebindable: false);

    /// <summary>Справочная строка с подписью вместо жеста — когда клавиш сразу несколько
    /// («Стрелки»). Жесты перечислены, чтобы их нельзя было занять чем-то другим.</summary>
    private static ShortcutAction RefLabel(
        string id, string title, ShortcutBlock block, ShortcutContext ctx, string label, params string[] gestures) =>
        new(id, title, block, ctx, gestures.Select(G).ToArray(), Rebindable: false, FixedLabel: label);

    /// <summary>Буквы и цифры — жесты справочной строки «Начать правку набором текста»: печатать в
    /// выделенную ячейку можно любой из них, и все они заняты набором.</summary>
    private static string[] LettersAndDigits() =>
        [.. Enumerable.Range('A', 26).Select(c => ((char)c).ToString()), .. Enumerable.Range(0, 10).Select(d => d.ToString())];

    public static IReadOnlyList<ShortcutAction> All { get; } =
    [
        // ── Глобальные ──────────────────────────────────────────────────────────
        Cmd("app.quit",      "Завершить программу",            ShortcutBlock.Global, ShortcutContext.Global, "Ctrl+Q"),
        Cmd("app.zoomIn",    "Увеличить масштаб интерфейса",   ShortcutBlock.Global, ShortcutContext.Global, "Ctrl+Plus"),
        Cmd("app.zoomOut",   "Уменьшить масштаб интерфейса",   ShortcutBlock.Global, ShortcutContext.Global, "Ctrl+Minus"),
        Cmd("app.zoomReset", "Стандартный масштаб интерфейса", ShortcutBlock.Global, ShortcutContext.Global, "Ctrl+Shift+0"),
        Cmd("app.contextMenu", "Открыть контекстное меню",     ShortcutBlock.Global, ShortcutContext.Global, "Shift+F10"),

        // ── Боковая панель ──────────────────────────────────────────────────────
        Cmd("sidebar.createCollection",    "Создать коллекцию",     ShortcutBlock.Sidebar, ShortcutContext.Sidebar, "Ctrl+Shift+N"),
        Cmd("sidebar.duplicateCollection", "Дублировать коллекцию", ShortcutBlock.Sidebar, ShortcutContext.Sidebar, "Ctrl+Shift+D"),
        Cmd("sidebar.createGroup",         "Создать группу",        ShortcutBlock.Sidebar, ShortcutContext.Sidebar, "Ctrl+G"),
        Cmd("sidebar.rename",              "Переименовать",         ShortcutBlock.Sidebar, ShortcutContext.Sidebar, "F2"),
        Cmd("sidebar.relocate",            "Расположение",          ShortcutBlock.Sidebar, ShortcutContext.Sidebar, "Ctrl+M"),
        Cmd("sidebar.delete",              "Удалить выделенное",    ShortcutBlock.Sidebar, ShortcutContext.Sidebar, "Delete"),

        // ── Таблица: значения ───────────────────────────────────────────────────
        Cmd("table.copy",        "Копировать значения ячеек",   ShortcutBlock.Table, ShortcutContext.Table, "Ctrl+C"),
        Cmd("table.cut",         "Вырезать значения ячеек",     ShortcutBlock.Table, ShortcutContext.Table, "Ctrl+X"),
        Cmd("table.paste",       "Вставить значения",           ShortcutBlock.Table, ShortcutContext.Table, "Ctrl+V"),
        Cmd("table.undo",        "Отменить изменение значений", ShortcutBlock.Table, ShortcutContext.Table, "Ctrl+Z"),
        Cmd("table.clearCells",  "Очистить значения ячеек",     ShortcutBlock.Table, ShortcutContext.Table, "Delete", "Backspace"),
        Cmd("table.selectAll",   "Выделить все ячейки",         ShortcutBlock.Table, ShortcutContext.Table, "Ctrl+A"),

        // ── Таблица: записи ─────────────────────────────────────────────────────
        Cmd("table.createRecord",    "Создать запись",             ShortcutBlock.Table, ShortcutContext.Table, "Ctrl+N"),
        Cmd("table.openRecord",      "Открыть запись",             ShortcutBlock.Table, ShortcutContext.Table, "Ctrl+Enter"),
        Cmd("table.duplicateRecord", "Дублировать запись",         ShortcutBlock.Table, ShortcutContext.Table, "Ctrl+D"),
        Cmd("table.relocateRecords", "Расположение записей",       ShortcutBlock.Table, ShortcutContext.Table, "Ctrl+M"),
        Cmd("table.recordDisplay",   "Отображение записей",        ShortcutBlock.Table, ShortcutContext.Table, "Ctrl+Shift+R"),
        Cmd("table.moveRecordUp",    "Сдвинуть запись вверх",      ShortcutBlock.Table, ShortcutContext.Table, "Alt+Up"),
        Cmd("table.moveRecordDown",  "Сдвинуть запись вниз",       ShortcutBlock.Table, ShortcutContext.Table, "Alt+Down"),
        Cmd("table.rowHeightMode",   "Ручная высота строк",        ShortcutBlock.Table, ShortcutContext.Table, "Ctrl+Shift+H"),
        // Delete остается вторым жестом: он удаляет то, что выделено, и так работал всегда.
        // Основной — свой, чтобы в меню «Удалить» и «Очистить значения» не стояла одна клавиша.
        Cmd("table.deleteRecords",   "Удалить выделенные записи",  ShortcutBlock.Table, ShortcutContext.Table, "Ctrl+Delete", "Delete"),

        // ── Таблица: свойства (столбцы) ─────────────────────────────────────────
        Cmd("table.createProperty",    "Создать свойство",            ShortcutBlock.Table, ShortcutContext.Table, "Alt+N"),
        Cmd("table.editProperty",      "Изменить свойство",           ShortcutBlock.Table, ShortcutContext.Table, "Alt+E"),
        Cmd("table.duplicateProperty", "Дублировать свойство",        ShortcutBlock.Table, ShortcutContext.Table, "Alt+D"),
        Cmd("table.columnDisplay",     "Отображение столбцов",        ShortcutBlock.Table, ShortcutContext.Table, "Alt+O"),
        Cmd("table.moveColumnRight",   "Сдвинуть столбец вправо",     ShortcutBlock.Table, ShortcutContext.Table, "Alt+Right"),
        Cmd("table.moveColumnLeft",    "Сдвинуть столбец влево",      ShortcutBlock.Table, ShortcutContext.Table, "Alt+Left"),
        Cmd("table.columnWidthMode",   "Ручная ширина столбцов",      ShortcutBlock.Table, ShortcutContext.Table, "Alt+W"),
        Cmd("table.deleteColumns",     "Удалить выделенные свойства", ShortcutBlock.Table, ShortcutContext.Table, "Alt+Delete", "Delete", "Backspace"),

        // ── Таблица: справочно ──────────────────────────────────────────────────
        RefLabel("table.ref.move",   "Перейти между ячейками", ShortcutBlock.Table, ShortcutContext.Table,
                 "Стрелки",       "Up", "Down", "Left", "Right"),
        RefLabel("table.ref.extend", "Растянуть выделение",    ShortcutBlock.Table, ShortcutContext.Table,
                 "Shift+Стрелки", "Shift+Up", "Shift+Down", "Shift+Left", "Shift+Right"),
        RefLabel("table.ref.jump",   "К краю таблицы",         ShortcutBlock.Table, ShortcutContext.Table,
                 "Ctrl+Стрелки",  "Ctrl+Up", "Ctrl+Down", "Ctrl+Left", "Ctrl+Right"),
        Ref("table.ref.rowStart",   "В начало строки",                     ShortcutBlock.Table, ShortcutContext.Table, "Home"),
        Ref("table.ref.rowEnd",     "В конец строки",                      ShortcutBlock.Table, ShortcutContext.Table, "End"),
        Ref("table.ref.tableStart", "В начало таблицы",                    ShortcutBlock.Table, ShortcutContext.Table, "Ctrl+Home"),
        Ref("table.ref.tableEnd",   "В конец таблицы",                     ShortcutBlock.Table, ShortcutContext.Table, "Ctrl+End"),
        Ref("table.ref.pageUp",     "На экран вверх",                      ShortcutBlock.Table, ShortcutContext.Table, "PageUp"),
        Ref("table.ref.pageDown",   "На экран вниз",                       ShortcutBlock.Table, ShortcutContext.Table, "PageDown"),
        Ref("table.ref.tabNext",    "К ячейке правее",                     ShortcutBlock.Table, ShortcutContext.Table, "Tab"),
        Ref("table.ref.tabPrev",    "К ячейке левее",                      ShortcutBlock.Table, ShortcutContext.Table, "Shift+Tab"),
        Ref("table.ref.edit",       "Начать правку ячейки",                ShortcutBlock.Table, ShortcutContext.Table, "Enter"),
        Ref("table.ref.editF2",     "Начать правку ячейки второй клавишей", ShortcutBlock.Table, ShortcutContext.Table, "F2"),
        RefLabel("table.ref.type",  "Начать правку набором текста",        ShortcutBlock.Table, ShortcutContext.Table,
                 "Буква или цифра", LettersAndDigits()),
        Ref("table.ref.editMode",   "Переключить стрелки в правке между текстом и ячейками",
            ShortcutBlock.Table, ShortcutContext.Table, "F2"),
        Ref("table.ref.newline",    "Перенос строки в тексте и ссылке",    ShortcutBlock.Table, ShortcutContext.Table, "Alt+Enter"),
        Ref("table.ref.escape",     "Снять выделение, отменить правку",    ShortcutBlock.Table, ShortcutContext.Table, "Escape"),

        // ── Панель записи ───────────────────────────────────────────────────────
        Cmd("record.editProperty",      "Изменить свойство",           ShortcutBlock.RecordPage, ShortcutContext.RecordPage, "Alt+E"),
        Cmd("record.duplicateProperty", "Дублировать свойство",        ShortcutBlock.RecordPage, ShortcutContext.RecordPage, "Alt+D"),
        Cmd("record.createProperty",    "Создать свойство",            ShortcutBlock.RecordPage, ShortcutContext.RecordPage, "Alt+N"),
        Cmd("record.movePropertyUp",    "Сдвинуть свойство вверх",     ShortcutBlock.RecordPage, ShortcutContext.RecordPage, "Alt+Up"),
        Cmd("record.movePropertyDown",  "Сдвинуть свойство вниз",      ShortcutBlock.RecordPage, ShortcutContext.RecordPage, "Alt+Down"),
        Cmd("record.deleteProperties",  "Удалить выделенные свойства", ShortcutBlock.RecordPage, ShortcutContext.RecordPage, "Delete", "Backspace"),

        // ── Календарь ───────────────────────────────────────────────────────────
        Cmd("calendar.openEvent", "Открыть событие",
            ShortcutBlock.Calendar, ShortcutContext.Calendar, "Ctrl+Enter"),
        Cmd("calendar.duplicateSelection", "Дублировать выделенные события",
            ShortcutBlock.Calendar, ShortcutContext.Calendar, "Ctrl+D"),
        Cmd("calendar.deleteSelection", "Удалить выделенные события",
            ShortcutBlock.Calendar, ShortcutContext.Calendar, "Delete", "Backspace"),

        // ── Задачи ──────────────────────────────────────────────────────────────
        Cmd("tasks.openTask", "Открыть задачу",
            ShortcutBlock.Tasks, ShortcutContext.Tasks, "Ctrl+Enter"),
        Cmd("tasks.duplicateSelection", "Дублировать выделенные задачи",
            ShortcutBlock.Tasks, ShortcutContext.Tasks, "Ctrl+D"),
        Cmd("tasks.deleteSelection", "Удалить выделенные задачи",
            ShortcutBlock.Tasks, ShortcutContext.Tasks, "Delete", "Backspace"),
        Ref("tasks.ref.quickAdd", "Создать задачу из строки быстрого добавления",
            ShortcutBlock.Tasks, ShortcutContext.Tasks, "Enter"),

        // ── Заметки ─────────────────────────────────────────────────────────────
        Cmd("notes.save",        "Сохранить заметку сразу", ShortcutBlock.Notes, ShortcutContext.Notes, "Ctrl+S"),
        Cmd("notes.bold",        "Жирный",                  ShortcutBlock.Notes, ShortcutContext.Notes, "Ctrl+B"),
        Cmd("notes.italic",      "Курсив",                  ShortcutBlock.Notes, ShortcutContext.Notes, "Ctrl+I"),
        Cmd("notes.strike",      "Зачеркнутый",             ShortcutBlock.Notes, ShortcutContext.Notes, "Ctrl+Shift+K"),
        Cmd("notes.inlineCode",  "Код внутри строки",       ShortcutBlock.Notes, ShortcutContext.Notes, "Ctrl+E"),
        Cmd("notes.link",        "Ссылка",                  ShortcutBlock.Notes, ShortcutContext.Notes, "Ctrl+K"),
        Cmd("notes.bulletList",  "Маркированный список",    ShortcutBlock.Notes, ShortcutContext.Notes, "Ctrl+Shift+L"),
        Cmd("notes.orderedList", "Нумерованный список",     ShortcutBlock.Notes, ShortcutContext.Notes, "Ctrl+Shift+O"),
        Cmd("notes.quote",       "Цитата",                  ShortcutBlock.Notes, ShortcutContext.Notes, "Ctrl+Shift+Q"),
        Cmd("notes.codeBlock",   "Блок кода",               ShortcutBlock.Notes, ShortcutContext.Notes, "Ctrl+Shift+C"),
        Cmd("notes.table",       "Таблица",                 ShortcutBlock.Notes, ShortcutContext.Notes, "Ctrl+Shift+T"),
        Cmd("notes.rule",        "Разделитель",             ShortcutBlock.Notes, ShortcutContext.Notes, "Ctrl+Shift+M"),
        Cmd("notes.clearFormat", "Снять оформление",        ShortcutBlock.Notes, ShortcutContext.Notes, "Ctrl+Shift+Z"),
        Cmd("notes.heading1",    "Заголовок 1",             ShortcutBlock.Notes, ShortcutContext.Notes, "Ctrl+1"),
        Cmd("notes.heading2",    "Заголовок 2",             ShortcutBlock.Notes, ShortcutContext.Notes, "Ctrl+2"),
        Cmd("notes.heading3",    "Заголовок 3",             ShortcutBlock.Notes, ShortcutContext.Notes, "Ctrl+3"),
        Cmd("notes.heading4",    "Заголовок 4",             ShortcutBlock.Notes, ShortcutContext.Notes, "Ctrl+4"),
        Cmd("notes.heading5",    "Заголовок 5",             ShortcutBlock.Notes, ShortcutContext.Notes, "Ctrl+5"),
        Cmd("notes.heading6",    "Заголовок 6",             ShortcutBlock.Notes, ShortcutContext.Notes, "Ctrl+6"),
        Cmd("notes.plainText",   "Снять заголовок",         ShortcutBlock.Notes, ShortcutContext.Notes, "Ctrl+0"),
        Ref("notes.ref.undo", "Отменить правку текста",           ShortcutBlock.Notes, ShortcutContext.Notes, "Ctrl+Z"),
        Ref("notes.ref.redo", "Вернуть отмененную правку текста", ShortcutBlock.Notes, ShortcutContext.Notes, "Ctrl+Y"),

        // ── Дерево заметок ──────────────────────────────────────────────────────
        Cmd("notesTree.createNote",   "Создать заметку",       ShortcutBlock.NotesTree, ShortcutContext.NotesTree, "Ctrl+N"),
        Cmd("notesTree.createFolder", "Создать папку",         ShortcutBlock.NotesTree, ShortcutContext.NotesTree, "Ctrl+Shift+N"),
        Cmd("notesTree.rename",       "Переименовать",         ShortcutBlock.NotesTree, ShortcutContext.NotesTree, "F2"),
        Cmd("notesTree.duplicate",    "Дублировать",           ShortcutBlock.NotesTree, ShortcutContext.NotesTree, "Ctrl+D"),
        Cmd("notesTree.relocate",     "Расположение",          ShortcutBlock.NotesTree, ShortcutContext.NotesTree, "Ctrl+M"),
        Cmd("notesTree.delete",       "Переместить в корзину", ShortcutBlock.NotesTree, ShortcutContext.NotesTree, "Delete"),

        // ── Окна и поля ─────────────────────────────────────────────────────────
        Ref("windows.ref.confirm",     "Подтвердить окно",                        ShortcutBlock.Windows, ShortcutContext.Windows, "Enter"),
        Ref("windows.ref.close",       "Закрыть окно, отменить",                  ShortcutBlock.Windows, ShortcutContext.Windows, "Escape"),
        Ref("windows.ref.titleSave",   "Записать заголовок коллекции или записи", ShortcutBlock.Windows, ShortcutContext.Windows, "Enter"),
        Ref("windows.ref.textSelectAll", "Выделить весь текст в поле",            ShortcutBlock.Windows, ShortcutContext.Windows, "Ctrl+A"),
        Ref("windows.ref.textCopy",    "Копировать текст в поле",                 ShortcutBlock.Windows, ShortcutContext.Windows, "Ctrl+C"),
        Ref("windows.ref.textCut",     "Вырезать текст в поле",                   ShortcutBlock.Windows, ShortcutContext.Windows, "Ctrl+X"),
        Ref("windows.ref.textPaste",   "Вставить текст в поле",                   ShortcutBlock.Windows, ShortcutContext.Windows, "Ctrl+V"),
    ];

    private static readonly Dictionary<string, ShortcutAction> ById = All.ToDictionary(a => a.Id);

    /// <summary>Действие по id; null — такого нет (устаревший ключ в app.db).</summary>
    public static ShortcutAction? Find(string id) => ById.GetValueOrDefault(id);

    /// <summary>Приставка блока — начало названия строки и часть сообщения о конфликте.</summary>
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

    /// <summary>Название строки целиком: «Таблица: Копировать значения ячеек».</summary>
    public static string FullTitle(ShortcutAction action) =>
        $"{BlockTitle(action.Block)}: {action.Title}";

    /// <summary>Пересекаются ли зоны конфликтов: одинаковая зона либо участие Global.</summary>
    public static bool ContextsOverlap(ShortcutContext a, ShortcutContext b) =>
        a == b || a == ShortcutContext.Global || b == ShortcutContext.Global;

    /// <summary>
    /// Пары одной зоны, которым ЗАКОННО делить жест, — там, где выбор делает состояние, а не
    /// клавиша: тройка Delete таблицы (выделены ячейки / записи / колонки — одно гасит другое)
    /// и справочные строки, где та же клавиша значит разное в правке и вне ее (Enter, Tab,
    /// Escape). Любая другая пара с общим жестом в пересекающихся зонах — конфликт.
    /// </summary>
    public static bool AllowSameGesture(string idA, string idB)
    {
        if (Trio(idA) && Trio(idB)) return true;
        // Справочные строки друг другу не мешают: их клавиши разводит состояние таблицы.
        return Find(idA) is { Rebindable: false } && Find(idB) is { Rebindable: false };

        static bool Trio(string id) =>
            id is "table.clearCells" or "table.deleteRecords" or "table.deleteColumns";
    }
}
