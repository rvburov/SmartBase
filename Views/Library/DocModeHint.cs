using SmartBase.Core.Documents;

namespace SmartBase.Views.Library;

/// <summary>
/// Подсказка у погашенной кнопки «Как в документе»: почему ЭТОТ документ нельзя показать в его
/// настоящем виде. Кнопка погашена, но видна, и без подсказки не понять, сломана она или так и
/// задумано.
///
/// Раньше подсказку для всякого не-PDF давала ветка PDF — «Страницами пока показывается только
/// PDF», — и у книги XLS или у книги, разбор которой сорвался, она говорила неправду: книга
/// показывается таблицей, а не страницами. А сорвавшийся разбор книги назывался «в книге нет
/// листов с данными». Вдобавок у погашенной кнопки подсказки не показывались вовсе: их включает
/// <c>ToolTip.ShowOnDisabled</c> в разметке.
/// </summary>
public static class DocModeHint
{
    /// <summary>Страницы PDF не открылись: файл битый, занят или пропал.</summary>
    public const string PagesFailed = "Страницы этого документа не открылись";

    /// <summary>Книга не открылась: файл битый, занят или пропал.</summary>
    public const string BookFailed = "Таблица этой книги не открылась";

    /// <summary>В книге нет ни одного листа с данными.</summary>
    public const string BookEmpty = "В книге нет листов с данными";

    /// <summary>Листы с данными в книге есть, но все они скрыты.</summary>
    public const string BookHidden = "Листы с данными в этой книге скрыты, а скрытые листы панель не показывает";

    /// <summary>
    /// Подсказка по формату — до того, как настоящий вид документа открывался. null — формат
    /// показывается в настоящем виде, и подсказку решит итог открытия: страницы PDF и таблица
    /// книги могут и не открыться.
    /// </summary>
    public static string? ForFormat(string path)
    {
        var ext = DocTextExtractor.ExtensionOf(path);
        if (ext is "pdf" or "xlsx" or "xlsm") return null;

        if (DocFormats.FamilyOf(ext) == DocFormatFamily.Text)
            return "У текстового файла другого вида, кроме текста, нет";

        var format = ext.Length > 0 ? $", а {ext.ToUpperInvariant()} — нет" : "";
        // Два предложения — с точкой в конце, как у прочих длинных подсказок программы.
        return $"В режиме «Как в документе» показываются PDF и книги XLSX и XLSM{format}. " +
               "Документ открывается кнопкой «Открыть» на карточке.";
    }

    /// <summary>
    /// Книга тяжелее порога чтения — теми же словами, что у режима «Текст»: порог один, и
    /// снимается он там же, в настройках.
    /// </summary>
    public static string BookTooBig(int maxFileMb) =>
        $"Книга тяжелее порога чтения ({maxFileMb} МБ). Порог меняется в настройках библиотеки.";

    /// <summary>Подсказка по итогу разбора книги; null — листы есть и кнопка работает.</summary>
    public static string? ForBook(DocBookView book) =>
        book.TooBigMb > 0     ? BookTooBig(book.TooBigMb)
      : book.Broken           ? BookFailed
      : book.Sheets.Count > 0 ? null
      : book.HiddenSheets > 0 ? BookHidden
      :                         BookEmpty;
}
