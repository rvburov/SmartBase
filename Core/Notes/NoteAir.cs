namespace SmartBase.Core.Notes;

/// <summary>
/// Где у заметки стоит воздух — отступ над первой и под последней строкой.
///
/// В редакторе он живет не в отступах контрола, а в высоте крайних строк документа (иначе не
/// прокручивался бы вместе с текстом, см. <c>Views/Notes/Live/NoteAirGenerator</c>). Значит все
/// сводится к двум смещениям — началу и концу текста, — и краевой случай тут ровно один: у пустой
/// заметки это одно и то же место, а воздух нужен с обеих сторон.
/// </summary>
public static class NoteAir
{
    /// <summary>
    /// Ближайшее интересное смещение начиная с <paramref name="startOffset"/>: начало текста, его
    /// конец либо −1, если позади осталось и то и другое.
    /// </summary>
    public static int InterestFrom(int startOffset, int textLength)
    {
        if (startOffset <= 0) return 0;
        return startOffset <= textLength ? textLength : -1;
    }

    /// <summary>
    /// Сколько воздуха добавить строке в этом месте: сверху и снизу.
    ///
    /// <paramref name="trailingBlank"/> — высота пустой строки, которой кончается файл, либо 0,
    /// если ее нет. Такая строка есть почти всегда (перевод строки в конце — норма для текстовых
    /// файлов), и снизу она сама по себе дает воздух. Сверху такой строки нет, поэтому ее высота
    /// добавляется к верхнему отступу: иначе снизу просвет получается на строку больше, чем
    /// сверху, — что и видно глазом.
    /// </summary>
    public static (double Top, double Bottom) At(int offset, int textLength, double air, double trailingBlank) =>
        (offset == 0 ? air + trailingBlank : 0, offset == textLength ? air : 0);
}
