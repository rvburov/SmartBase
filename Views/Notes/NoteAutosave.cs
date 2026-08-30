using SmartBase.Core.Notes;

namespace SmartBase.Views.Notes;

/// <summary>
/// Чистая часть автосохранения заметки: пауза перед записью, признак «файл изменили снаружи» и имя
/// файла-развязки при конфликте. Без Avalonia — таймер и диалоги живут в
/// <see cref="NotesControllerView"/>, а правила, от которых зависит сохранность чужого текста,
/// покрыты тестами.
/// </summary>
public static class NoteAutosave
{
    /// <summary>
    /// Пауза после последнего нажатия клавиши. Не «по символу», как в панели записи: там правка
    /// уходит в SQLite в той же транзакции, а здесь каждое нажатие — запись на диск, которую
    /// подхватит еще и синхронизатор облака.
    /// </summary>
    public static readonly TimeSpan Debounce = TimeSpan.FromMilliseconds(800);

    /// <summary>Пауза перед пересборкой готового вида в режиме «Просмотр»: рендер markdown идет
    /// синхронно на UI-потоке и без задержки съел бы набор текста.</summary>
    public static readonly TimeSpan PreviewDebounce = TimeSpan.FromMilliseconds(300);

    /// <summary>
    /// Пауза перед пересчетом живой разметки. Короче превью втрое: там человек смотрит на соседнюю
    /// панель, а здесь — ровно на те символы, которые набирает, и задержка читается как подвисание.
    /// Совсем без паузы тоже нельзя: разбор идет по всему тексту заметки.
    /// </summary>
    public static readonly TimeSpan MarkupDebounce = TimeSpan.FromMilliseconds(120);

    /// <summary>
    /// Изменил ли файл кто-то снаружи, пока заметка была открыта. Исчезнувший файл тоже считается
    /// изменением: воссоздавать его молча — значит спорить с тем, кто его удалил.
    /// </summary>
    public static bool ChangedOutside(NoteSnapshot opened, NoteSnapshot? current) =>
        current is not { } now || now != opened;

    /// <summary>
    /// Имя для «сохранить копию» при конфликте: «Отчет (конфликт 2026-08-30 14-22).md». Развязка
    /// обидная, но безопасная — ни одна из двух версий не пропадает. Так же поступает Obsidian.
    /// </summary>
    public static string ConflictName(string fileName, DateTime moment)
    {
        var ext  = VaultNames.ExtensionOf(fileName, isFolder: false);
        var stem = fileName[..^ext.Length];
        return $"{stem} (конфликт {moment:yyyy-MM-dd HH-mm}){ext}";
    }
}
