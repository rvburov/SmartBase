using Avalonia.Controls;

namespace SmartBase.Extensions.ProductCompare;

/// <summary>
/// Расширение «Сравнение составов изделий»: сравнивает два Excel-состава (старый/новый) и
/// формирует итог в .xlsm-шаблоне. Запускается отдельным окном (<see cref="ProductCompareWindow"/>)
/// в основном стиле приложения. Логика вынесена в <see cref="ProductCompareEngine"/>; расширение
/// самостоятельно и зависит только от <see cref="IExtension"/> + контролов/стилей приложения.
/// </summary>
public sealed class ProductCompareExtension : IExtension
{
    /// <summary>Идентификатор строкой-константой — на него ссылается окно, когда читает
    /// свои настройки у оболочки.</summary>
    public const string ExtensionId = "product-compare";

    /// <summary>Имя настройки «файл-шаблон» (полный ключ хранения собирает оболочка).</summary>
    public const string TemplateKey = "template";

    public string Id   => ExtensionId;
    public string Name => "Сравнение составов изделий";

    /// <summary>Путь к шаблону в коде не зашит: файл лежит на файловом сервере, буква
    /// смонтированного диска у каждой машины своя.</summary>
    public IReadOnlyList<ExtensionSetting> Settings =>
    [
        new(TemplateKey, "Файл-шаблон", @"\\server\share\Шаблон сравнения файлов изделий.xlsm"),
    ];

    public string MarkdownDescription =>
        """
        Сравнивает составы изделий из двух Excel-файлов (старый и новый) и формирует
        итог в .xlsm-шаблоне: что удалено, что добавлено, что осталось.

        ## Что делает

        - Сопоставляет строки по иерархическому номеру (столбец 2), уникальность — по
          обозначению (столбец 11).
        - В шаблон пишет старые данные слева (столбцы 1–5), новые — справа (столбцы 7–11),
          начиная с 6-й строки.
        - Уникальное в старом подсвечивает красным, уникальное в новом — зеленым.
        - Применяет стиль ГОСТ (шрифт «GOST type A», границы, выравнивание); макросы
          шаблона сохраняются.

        ## Как пользоваться

        1. Укажите файл-шаблон (.xlsm).
        2. Укажите старый и новый файлы (.xlsx/.xlsm).
        3. Выберите папку для результата и нажмите «Сравнить файлы».

        > Результат сохраняется как «Результат сравнения ДД.ММ.ГГГГ_ЧЧ-ММ-СС.xlsm».
        """;

    /// <summary>Запускаемое расширение — кнопка «Запустить» активна.</summary>
    public bool CanRun => true;

    public Window? CreateWindow() => new ProductCompareWindow();
}
