namespace DataBase.Extensions.Sample;

/// <summary>
/// Расширение-заглушка: показывает имя и Markdown-описание, но ничего не запускает
/// (<see cref="CanRun"/> = false). Образец структуры самостоятельного расширения —
/// живёт в собственной папке <c>Extensions/Sample/</c> и зависит только от
/// <see cref="IExtension"/>.
/// </summary>
public sealed class SampleExtension : IExtension
{
    public string Id   => "sample";
    public string Name => "Пример расширения";

    public string MarkdownDescription =>
        """
        # Пример расширения

        Это **заглушка** — образец самостоятельного расширения.

        Каждое расширение живёт в своей папке `Extensions/<Имя>/` и не зависит
        от остального приложения, кроме интерфейса `IExtension`.

        ## Что делает оболочка
        - Показывает **имя** в навбаре блока и в строке сайдбара
        - Рендерит это **описание** в Markdown
        - Даёт кнопку запуска справа в навбаре

        > Запуск пока не реализован — расширение служит каркасом.
        """;

    /// <summary>Заглушка не запускается — кнопка в навбаре будет неактивна.</summary>
    public bool CanRun => false;

    /// <summary>No-op: запускать пока нечего.</summary>
    public void Run() { }
}
