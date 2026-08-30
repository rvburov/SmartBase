using Avalonia;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Styling;
using Markdown.Avalonia;

namespace SmartBase.Controls.Widgets;

/// <summary>
/// Показ markdown-текста в приложении. ЕДИНСТВЕННЫЙ способ рисовать markdown: голый
/// <see cref="MarkdownScrollViewer"/> в разметке не ставим — он придет со встроенной темой
/// пакета и будет выглядеть иначе, чем остальное приложение (так и разошлись описания
/// расширений и превью записи с инструкцией пользователя).
///
/// Виджет делает ровно две вещи, которые иначе пришлось бы повторять в каждом месте:
///
/// 1. Гасит встроенную тему пакета (<c>MarkdownStyle</c> = пустой набор). Она живет ВНУТРИ
///    контрола и потому сильнее любых наших стилей — пока она на месте, заголовки идут ее
///    размером, а таблицы рисуются черными рамками с «зеброй». Через <c>Application.Styles</c>
///    ее не перебить, только заменить.
/// 2. Вешает на себя наш вид — <c>Assets/Styles/Markdown.axaml</c>. Стили родителя действуют
///    на все поддерево, а рендер строит его сам, поэтому иначе до его элементов не добраться.
///
/// Вид markdown правится ТОЛЬКО в <c>Assets/Styles/Markdown.axaml</c> — один файл на все
/// приложение (см. docs/developer/ui/markdown.md).
/// </summary>
public class AppMarkdown : MarkdownScrollViewer
{
    private static readonly Uri StylesUri = new("avares://SmartBase/Assets/Styles/Markdown.axaml");
    private static readonly Uri BaseUri   = new("avares://SmartBase/");

    /// <summary>Обычный случай: вид виджет берет на себя.</summary>
    public AppMarkdown() : this(inheritStyles: false) { }

    /// <param name="inheritStyles">
    /// true — вид приходит с предка (<see cref="ApplyStyles"/>), свой набор виджет не заводит.
    /// Нужно там, где таких контролов на экране десятки (инструкция пользователя режется на
    /// секции): один набор стилей на общем хосте дешевле, чем копия в каждом контроле.
    /// </param>
    public AppMarkdown(bool inheritStyles)
    {
        // Пустой набор ВМЕСТО темы пакета — см. паспорт класса.
        MarkdownStyle = new Styles();
        if (!inheritStyles) ApplyStyles(this);
    }

    /// <summary>
    /// Повесить вид markdown на общий контейнер — для случая <c>inheritStyles: true</c>.
    /// Отдельный экземпляр <see cref="Styles"/> на каждый вызов не случайность: один набор
    /// может принадлежать ровно одному владельцу («The Styles already has a owner»), раздать
    /// его нескольким контролам нельзя.
    /// </summary>
    public static void ApplyStyles(StyledElement host) =>
        host.Styles.Add(new StyleInclude(BaseUri) { Source = StylesUri });
}
