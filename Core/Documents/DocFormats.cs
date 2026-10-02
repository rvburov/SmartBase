namespace SmartBase.Core.Documents;

/// <summary>Семейство формата документа — им подписана и покрашена лента на плитке карточки.</summary>
public enum DocFormatFamily
{
    Pdf,
    Word,
    Excel,
    Text,
    Other,
}

/// <summary>
/// Опознание формата документа по расширению: подпись на ленте плитки и ключи кистей.
///
/// Зачем отдельный класс, а не свойство <see cref="Models.DocRecord.Format"/>: <c>Format</c>
/// отдает сырое расширение («DOCX»), а карточке нужно СЕМЕЙСТВО — «WORD» одинаково для doc и
/// docx, «ТЕКСТ» для txt, md и csv. Семейство же выбирает цвет, поэтому правило сведено в одно
/// место: иначе подпись и цвет разъехались бы при первом добавлении расширения.
///
/// «ТЕКСТ» на плитке стоит ровно у тех файлов, внутри которых поиск умеет читать: список
/// текстовых расширений один на двоих с разбором (<see cref="DocTextExtractor.PlainTextExtensions"/>).
/// Раньше списков было два, и у INI, JSON, XML, YML и YAML лента «ТЕКСТ» обещала то, чего поиск не
/// делал. У «WORD» и «EXCEL» иначе: лента говорит правду про формат, а не про чтение, — старые
/// <c>.doc</c> и <c>.xls</c> не читаются, и об этом говорит чип «текст не прочитан».
/// </summary>
public static class DocFormats
{
    /// <summary>Подпись на ленте, когда расширения у файла нет вовсе.</summary>
    public const string NoExtensionLabel = "ФАЙЛ";

    private static readonly string[] WordExtensions  = ["doc", "docx", "dot", "dotx", "docm"];
    private static readonly string[] ExcelExtensions = ["xls", "xlsx", "xlsm", "xlsb", "xlt", "xltx"];

    /// <summary>Семейство по расширению без точки (регистр не важен).</summary>
    public static DocFormatFamily FamilyOf(string extension)
    {
        var ext = extension.Trim().TrimStart('.').ToLowerInvariant();
        if (ext.Length == 0)                                    return DocFormatFamily.Other;
        if (ext == "pdf")                                       return DocFormatFamily.Pdf;
        if (Array.IndexOf(WordExtensions, ext)  >= 0)           return DocFormatFamily.Word;
        if (Array.IndexOf(ExcelExtensions, ext) >= 0)           return DocFormatFamily.Excel;
        if (Array.IndexOf(DocTextExtractor.PlainTextExtensions, ext) >= 0) return DocFormatFamily.Text;
        return DocFormatFamily.Other;
    }

    /// <summary>
    /// Подпись на ленте. У известных семейств — имя семейства, у прочих — само расширение
    /// заглавными: «DWG» полезнее безликого «ФАЙЛ», а держать список всех расширений мира
    /// незачем.
    /// </summary>
    public static string LabelOf(string extension)
    {
        var ext = extension.Trim().TrimStart('.');
        if (ext.Length == 0) return NoExtensionLabel;

        return FamilyOf(ext) switch
        {
            DocFormatFamily.Pdf   => "PDF",
            DocFormatFamily.Word  => "WORD",
            DocFormatFamily.Excel => "EXCEL",
            DocFormatFamily.Text  => "ТЕКСТ",
            _                     => ext.ToUpperInvariant(),
        };
    }

    /// <summary>Ключ кисти обводки документа и надписи на ленте в
    /// <c>Assets/Styles/Colors.axaml</c>.</summary>
    public static string StrokeBrushKey(DocFormatFamily family) => family switch
    {
        DocFormatFamily.Pdf   => "BrushFormatPdf",
        DocFormatFamily.Word  => "BrushFormatWord",
        DocFormatFamily.Excel => "BrushFormatExcel",
        DocFormatFamily.Text  => "BrushFormatText",
        _                     => "BrushFormatOther",
    };

    /// <summary>Ключ кисти светлой заливки ленты.</summary>
    public static string FillBrushKey(DocFormatFamily family) => family switch
    {
        DocFormatFamily.Pdf   => "BrushFormatPdfLight",
        DocFormatFamily.Word  => "BrushFormatWordLight",
        DocFormatFamily.Excel => "BrushFormatExcelLight",
        DocFormatFamily.Text  => "BrushFormatTextLight",
        _                     => "BrushFormatOtherLight",
    };
}
