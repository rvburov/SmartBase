namespace SmartBase.Views.Library;

/// <summary>Размер карточки документа в ленте библиотеки.</summary>
public enum DocCardSize
{
    Small,
    Medium,
    Large,
}

/// <summary>
/// Геометрия карточки на каждый размер: окошко страницы, лента формата, высота карточки и
/// сколько строк найденного текста в нее помещается.
///
/// Числа лежат здесь, а не в сборщике карточки, по двум причинам. Во-первых, они связаны
/// арифметикой, и связь надо было где-то записать: высота карточки обязана вмещать название,
/// подпись, сниппет и строку действия — иначе соседние карточки получаются разной высоты и
/// лента дергается при каждой подъехавшей странице. Во-вторых, это единственное место, где
/// размеры можно проверить тестом.
/// </summary>
public readonly record struct DocTileShape(
    double BoxWidth,
    double BoxHeight,
    double TapeHeight,
    int    SnippetLines,
    int    SnippetWords,
    double CardHeight,
    string LabelFontKey,
    double LabelFontFallback)
{
    /// <summary>Высота плитки целиком: окошко плюс лента.</summary>
    public double TileHeight => BoxHeight + TapeHeight;
}

public static class DocTiles
{
    /// <summary>
    /// Пропорции окошка одинаковы у всех размеров (7:5) — картинку в кеше режет
    /// <c>DocThumbnail</c> один раз и на все размеры. Разные пропорции потребовали бы трех
    /// кешей и трех рендеров одного документа ради одной и той же шапки страницы.
    ///
    /// Высота карточки не равна высоте плитки: она посчитана от ТЕКСТА (название, подпись,
    /// строки сниппета, «Прочитать документ»), потому что именно текст в маленьком размере
    /// оказывается выше плитки. Плитка в этом случае стоит по центру.
    ///
    /// Надпись формата растет вместе с лентой — и растет ТОКЕНАМИ типографики, а не сырыми
    /// числами: размер текста в приложении задается только ими, иначе настройка «Размер шрифта»
    /// обходит плитку стороной.
    /// </summary>
    public static DocTileShape Of(DocCardSize size) => size switch
    {
        DocCardSize.Small => new(56,  40, 32, SnippetLines: 1, SnippetWords: 10, CardHeight: 84,
                                 LabelFontKey: "FontSizeSmall",  LabelFontFallback: 10),
        DocCardSize.Large => new(126, 90, 62, SnippetLines: 6, SnippetWords: 48, CardHeight: 164,
                                 LabelFontKey: "FontSizeLarge",  LabelFontFallback: 20),
        _                 => new(84,  60, 46, SnippetLines: 3, SnippetWords: 24, CardHeight: 118,
                                 LabelFontKey: "FontSizeMedium", LabelFontFallback: 16),
    };

    /// <summary>Ключ размера в хранилище настроек.</summary>
    public static string KeyOf(DocCardSize size) => size switch
    {
        DocCardSize.Small => "small",
        DocCardSize.Large => "large",
        _                 => "medium",
    };

    /// <summary>Разбор ключа; мусор и неизвестное значение читаются как стандарт.</summary>
    /// <summary>
    /// Размер по ключу настройки. Незнакомое значение — это стандарт: в хранилище может лежать
    /// что угодно (правка руками, откат версии), и падать из-за него нельзя.
    ///
    /// Запасной вариант обязан СОВПАДАТЬ с <c>AppSettings.LibraryCardSizeDefault</c>: разойдись
    /// они — пустое хранилище и мусор в нем дали бы разный размер карточки, причем молча.
    /// Сторож — <c>LibraryCardSizeTests</c>.
    /// </summary>
    public static DocCardSize Parse(string? key) => key switch
    {
        "medium" => DocCardSize.Medium,
        "large"  => DocCardSize.Large,
        _        => DocCardSize.Small,
    };
}
