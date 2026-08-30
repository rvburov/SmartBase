using Avalonia;
using Avalonia.Media;
using Avalonia.Styling;

namespace SmartBase.Controls.Markup;

/// <summary>
/// Вид размеченного текста: ВСЕ числа, цвета и размеры показа markdown — здесь и только здесь.
///
/// Раньше вид жил в <c>Assets/Styles/Markdown.axaml</c> селекторами по классам, которые вешал
/// чужой рендер (Markdown.Avalonia). Рендер теперь свой, и селекторы перестали работать по двум
/// причинам сразу: строчная разметка в Avalonia — это <c>Inline</c>, а не контрол, и стилями до
/// нее не добраться; а блоки мы строим сами и можем задать им вид напрямую, без промежуточного
/// слоя классов. Поэтому один файл стилей заменен одним файлом параметров — обещание
/// «вид правится в одном месте» осталось (см. docs/developer/ui/markdown.md).
///
/// Значения читаются из токенов приложения (<c>Typography.axaml</c>, <c>Colors.axaml</c>,
/// <c>Controls.axaml</c>) в момент сборки документа. Живой «Размер шрифта» работает не через
/// DynamicResource, а пересборкой: <see cref="MarkupView"/> подписан на
/// <c>FontScale.Changed</c> — так же, как таблица и дерево боковой панели, которые тоже строят
/// текст в C#.
///
/// Числа не выдуманы: это принятая спецификация оформления (docs/developer/ui/markdown.md,
/// раздел «Что решено по виду разметки»). Менять их можно, но парами — почти каждое
/// повторено в живом редакторе заметок (<c>Views/Notes/Live/NoteMarkupColorizer.cs</c>),
/// иначе «Просмотр» и «Динамический просмотр» разойдутся.
/// </summary>
internal static class MarkupTheme
{
    // ── Вертикальный ритм ───────────────────────────────────────────────────────

    /// <summary>
    /// Высота строки текста. Число не наше: это <c>FontSize * 1.35</c> — ровно та формула, по
    /// которой высоту строки считает AvaloniaEdit
    /// (<c>VisualLineTextParagraphProperties.LineHeight</c>). Там она жестко зафиксирована на
    /// все строки, изменить ее нечем, поэтому «Просмотр» подтягивается к редактору, а не
    /// наоборот: иначе одна и та же заметка в двух режимах идет разной высотой строк.
    ///
    /// Задавать просвет через <c>LineSpacing</c> нельзя: при измерении он в высоту входит, а в
    /// раскладку отрисовки — нет, и на настоящем шрифте последняя строка абзаца переставала
    /// рисоваться (проверено на стенде Tests/MarkupProbe).
    /// </summary>
    public static double LineHeight => SizeBase * 1.35;

    /// <summary>
    /// Наименьший просвет вокруг заголовка — по строке с каждой стороны. Пустых строк исходника
    /// тут мало: заголовок отделяет часть текста от части, и без воздуха вокруг он читается как
    /// обычная жирная строка.
    ///
    /// Сверху и снизу поровну не случайно: ровно столько дает одна пустая строка, а вокруг
    /// заголовка ее ставят почти всегда. Значит на нормально набранном тексте «Просмотр» и
    /// «Динамический просмотр» совпадают до пикселя — воздух добавляет не рендер, а сам файл.
    /// </summary>
    public static double HeadingSpace => LineHeight;

    // ── Текст ───────────────────────────────────────────────────────────────────

    public static FontFamily FontBase => Family("FontFamilyBase", FontFamily.Default);
    public static FontFamily FontMono => Family("FontFamilyMono", FontFamily.Default);

    public static double SizeBase   => Size("FontSizeBase",   12);
    public static double SizeSmall  => Size("FontSizeSmall",  10);
    public static double SizeMono   => Size("FontSizeMono",   11);
    public static double SizeH3     => Size("FontSizeH3",     14);
    public static double SizeMedium => Size("FontSizeMedium", 16);
    public static double SizeLarge  => Size("FontSizeLarge",  20);

    public static FontWeight WeightBold => Resource("FontWeightBold") is FontWeight w ? w : FontWeight.SemiBold;

    public static IBrush TextPrimary   => Brush("BrushPrimaryText",   Brushes.Black);
    public static IBrush TextSecondary => Brush("BrushSecondaryText", Brushes.Gray);
    public static IBrush Accent        => Brush("BrushAccent",        Brushes.RoyalBlue);
    public static IBrush Selection     => Brush("BrushTextSelection", Brushes.LightGray);

    /// <summary>
    /// Размер заголовка по уровню. H1 — заголовок документа, H2 — раздел, H3 — подраздел,
    /// H4 и ниже идут базовым кеглем: там иерархию держит цвет (<see cref="HeadingBrush"/>).
    /// </summary>
    public static double HeadingSize(int level) => level switch
    {
        1 => SizeLarge,
        2 => SizeMedium,
        3 => SizeH3,
        _ => SizeBase,
    };

    /// <summary>
    /// Цвет заголовка. Ниже четвертого уровня — приглушенный: разводить H5 и H6 регистром
    /// нельзя (в редакторе текст — содержимое файла, регистр там не изменить), поэтому между
    /// собой они одинаковы намеренно.
    /// </summary>
    public static IBrush HeadingBrush(int level) => level >= 5 ? TextSecondary : TextPrimary;

    // ── Код ─────────────────────────────────────────────────────────────────────

    /// <summary>Свой цвет, а не вторичный серый: серым набраны служебные знаки разметки, и код
    /// с ними слился бы.</summary>
    public static IBrush CodeText => Brush("BrushCode", Brushes.Teal);

    public static IBrush CardBg => Brush("BrushCardBg", Brushes.WhiteSmoke);

    /// <summary>Коробка кода внутри строки. Воздух по бокам больше, чем сверху и снизу:
    /// иначе подложка распирает строку и абзац начинает «дышать».</summary>
    public static readonly Thickness InlineCodePadding = new(5, 2);

    /// <summary>Блок кода: воздух по бокам, сверху и снизу — как в спецификации.</summary>
    public static readonly Thickness CodeBlockPadding = new(12, 10);

    // ── Подсветка ───────────────────────────────────────────────────────────────

    /// <summary>Заливка <c>==подсветки==</c>: та же светлая подложка, что у чипов и выделенных
    /// строк, — маркер, а не второй акцент.</summary>
    public static IBrush HighlightBg => Brush("BrushAccentLight", Brushes.LightYellow);

    // ── Цитата ──────────────────────────────────────────────────────────────────

    public static readonly Thickness QuotePadding = new(10);

    /// <summary>Полоса слева. Толще линии-разделителя: разделитель тут читался бы царапиной.</summary>
    public const double QuoteBarWidth = 3;

    public static IBrush QuoteBar => Brush("BrushLineStrong", Brushes.Silver);

    // ── Выносные блоки (callout) ────────────────────────────────────────────────

    /// <summary>Насыщенность подложки выносного блока: цвет вида, разбавленный до фона.
    /// Держим цветом с прозрачностью, а не отдельным светлым токеном, — светлых оттенков
    /// понадобилось бы четыре, и все ради одного элемента.</summary>
    public const double CalloutTint = 0.10;

    public static readonly Thickness CalloutPadding = new(12, 8);

    public const double CalloutIconSize = 16;

    // ── Списки ──────────────────────────────────────────────────────────────────

    /// <summary>Отступ вложенного уровня.</summary>
    public const double ListIndent = 16;

    /// <summary>
    /// Ширина колонки маркера: отступ уровня плюс просвет до текста. Она же — «висячий»
    /// отступ: вторая строка пункта встает под первую, а не под маркер.
    /// </summary>
    public const double ListMarkerColumn = ListIndent + 8;

    /// <summary>Кружок первого уровня и колечко второго — одного диаметра: уровни разводит
    /// заливка, а не размер, иначе список выглядит как список разного.</summary>
    public const double BulletSize = 5;

    /// <summary>Третий уровень — черточка: круглых форм на три уровня не хватает.</summary>
    public const double DashWidth  = 7;
    public const double DashHeight = 1.5;

    public static IBrush ListMarker => TextSecondary;

    // ── Список задач ────────────────────────────────────────────────────────────

    public const double CheckBoxSize   = 16;
    public const double CheckBoxRadius = 2;

    /// <summary>Толщина галочки. Не из токенов иконок: галочка тут своя, вписанная в
    /// шестнадцать пикселей, — общие толщины рассчитаны на другой размер.</summary>
    public const double CheckTickWidth = 2;

    public static IBrush CheckBoxLine => Brush("BrushLineStrong", Brushes.Silver);

    /// <summary>Сделанный пункт — приглушенным, но БЕЗ зачеркивания: зачеркнутый текст
    /// нечитаем, а список задач перечитывают.</summary>
    public static IBrush TaskDoneText => TextSecondary;

    // ── Таблица ─────────────────────────────────────────────────────────────────

    /// <summary>Воздух в ячейке: по горизонтали больше, чем по вертикали, — колонки должны
    /// отделяться друг от друга без вертикальных линий.</summary>
    public static readonly Thickness TableCellPadding = new(10, 6);

    public static IBrush TableHeaderBg => Brush("BrushSecondaryBg", Brushes.WhiteSmoke);

    public static IBrush Separator => Brush("BrushSeparator", Brushes.LightGray);

    // ── Скругления ──────────────────────────────────────────────────────────────

    public static CornerRadius RadiusXS     => Radius("RadiusXS",     4);
    public static CornerRadius RadiusBase   => Radius("RadiusBase",   6);
    public static CornerRadius RadiusMedium => Radius("RadiusMedium", 8);

    // ── Чтение токенов ──────────────────────────────────────────────────────────
    //
    // Через Application.Current, а не через контрол: словари приложения одни на все окна, а
    // строить блок приходится до того, как он попал в дерево. Fallback у каждого свой —
    // документ обязан показаться даже если тему не подняли (headless-тесты).

    private static object? Resource(string key)
    {
        var app = Application.Current;
        return app is not null && app.TryGetResource(key, ThemeVariant.Default, out var v) ? v : null;
    }

    private static double Size(string key, double fallback) =>
        Resource(key) is double d && d > 0 ? d : fallback;

    private static IBrush Brush(string key, IBrush fallback) =>
        Resource(key) is IBrush b ? b : fallback;

    private static FontFamily Family(string key, FontFamily fallback) =>
        Resource(key) is FontFamily f ? f : fallback;

    private static CornerRadius Radius(string key, double fallback) =>
        Resource(key) is CornerRadius c ? c : new CornerRadius(fallback);
}
