namespace SmartBase.Utils;

/// <summary>
/// Утилиты для работы с Markdown.
/// Хранение — plain markdown в DbFieldValue.Value; показ — виджет AppMarkdown;
/// для отображения в календаре/списках используется StripMarkdown.
/// </summary>
public static class MarkdownUtils
{
    /// <summary>
    /// Снимает разметку Markdown, оставляя только plain-текст.
    /// Используется в превью (List/Day/Month/Year), где разметка не нужна.
    /// </summary>
    public static string StripMarkdown(string? md)
    {
        if (string.IsNullOrWhiteSpace(md)) return "";
        try
        {
            return Markdig.Markdown.ToPlainText(md).Trim();
        }
        catch
        {
            return md;
        }
    }
}
