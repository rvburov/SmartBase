using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;
using SmartBase.Controls.Components;
using SmartBase.Core.Models;
using SmartBase.Utils;

namespace SmartBase.Controls.DrawnGrid;

/// <summary>Как рисованная надпись встает «по центру» — так же, как контрол, который ее заменит.</summary>
internal enum TextCentering
{
    /// <summary>Блок надписи по центру области, левый край — на целом пикселе кадра (номер строки,
    /// «№» и «+» шапки — как TextBlock с HorizontalAlignment=Center на краю окна).</summary>
    Block,

    /// <summary>Блок надписи по центру области, на целом пикселе — смещение от края области
    /// (значение ячейки, которая правится списком: TextBlock внутри полосы значения).</summary>
    BlockInArea,

    /// <summary>Каждая строка по центру полосы шириной, округленной вверх до DIP (значение ячейки,
    /// которая правится полем ввода: TextAlignment=Center у TextBox).</summary>
    Lines,
}

/// <summary>
/// Токены вида и примитивы рисования, общие для всех рисованных поверхностей (таблица коллекции и
/// служебные таблицы — <see cref="GridSurface"/>, список свойств страницы записи —
/// <see cref="PropertySurface"/>): кисти и перья темы, шрифт и целая высота строки текста, иконки
/// реестра, кеш раскладок текста; надпись, иконка, галочка, залитая фигура. Один отрисовщик на все
/// поверхности — иначе сведенные с эталонами пиксели (стрелка списка, галочка, обрезка надписи)
/// разошлись бы между таблицей и панелью записи (план drawn-grid.md, решение 33).
///
/// Читается заново на смену зума, DPI, шрифта, высоты строк и темы (<see cref="Load"/>); до первого
/// чтения поверхности работают на запасных значениях (<see cref="Fallback"/>).
/// </summary>
internal sealed class SurfaceStyle
{
    public const double IconSize   = 16;   // IconSizeSmall
    public const double CheckSize  = 16;   // CheckBoxSize
    public const double CheckGlyph = 12;   // CheckBoxGlyphSize

    /// <summary>Стрелка списка — фигура из шаблона ComboBox темы Fluent (локальное значение шаблона,
    /// стили приложения ее не перебивают), вписывается в квадрат 12×12.</summary>
    public static readonly Geometry ChoiceGlyph =
        Geometry.Parse("M1939 486L2029 576L1024 1581L19 576L109 486L1024 1401L1939 486Z");

    public static readonly TextDecorationCollection Underline =
        [new TextDecoration { Location = TextDecorationLocation.Underline }];

    public IBrush Bg          { get; private set; } = Brushes.White;
    public IBrush InactiveBg  { get; private set; } = Brushes.WhiteSmoke;
    public IBrush HoverBg     { get; private set; } = Brushes.WhiteSmoke;   // BrushSecondaryBg — наведение на ячейку
    public IBrush HoverStrong { get; private set; } = Brushes.LightGray;    // BrushHover — наведение на заголовок и кнопку
    public IBrush Primary     { get; private set; } = Brushes.Black;
    public IBrush Secondary   { get; private set; } = Brushes.Gray;
    public IBrush Accent      { get; private set; } = Brushes.RoyalBlue;
    public IBrush AccentLight { get; private set; } = Brushes.LightBlue;    // BrushAccentLight — выделенная запись и колонка
    public IBrush SelectTint  { get; private set; } = new SolidColorBrush(Colors.RoyalBlue, 0.08);   // тонировка диапазона
    public IBrush Red         { get; private set; } = Brushes.Red;
    public IBrush Green       { get; private set; } = Brushes.Green;
    public IBrush SeparatorBrush { get; private set; } = Brushes.LightGray;

    // Ячейка-список служебных таблиц: закрытый ComboBox приложения и его недоступное состояние из темы.
    public IBrush ChoiceBg             { get; private set; } = Brushes.WhiteSmoke;
    public IBrush ChoiceBorder         { get; private set; } = Brushes.LightGray;
    public IBrush ChoiceBgDisabled     { get; private set; } = Brushes.LightGray;
    public IBrush ChoiceBorderDisabled { get; private set; } = Brushes.Gray;
    public IBrush ChoiceGlyphBrush     { get; private set; } = Brushes.Gray;
    public IBrush ChoiceGlyphDisabled  { get; private set; } = Brushes.Gray;
    public double ChoiceRadius         { get; private set; } = 8;

    // Недоступная галочка: CheckBox:disabled — Opacity 0,5, и Avalonia умножает на нее каждый штрих
    // отдельно: рамка поверх заливки того же цвета выходит темнее заливки. Повторяем кистями с той
    // же прозрачностью, а не общим слоем.
    public IBrush AccentHalf      { get; private set; } = Brushes.LightBlue;
    public IBrush WhiteHalf       { get; private set; } = Brushes.White;
    public Pen    CheckBorderHalf { get; private set; } = new(Brushes.Gray, 1);
    public Pen    Separator       { get; private set; } = new(Brushes.LightGray, 1);
    public Pen    CheckBorder     { get; private set; } = new(Brushes.Black, 1);
    public double LineWidth       { get; private set; } = 1;
    public double RowHeight       { get; private set; } = 36;
    public double LineHeight      { get; private set; } = 16;     // LineHeightBase — целая высота строки текста ячеек

    /// <summary>Отступ строки текста от края в строке обычной высоты — «сверху» и «снизу» в высокой
    /// строке держат именно его (<see cref="CellAlign.TextTop"/>).</summary>
    public double TextEdge => Math.Max(0, (RowHeight - LineHeight) / 2);
    public Typeface Typeface      { get; private set; } = Typeface.Default;
    public double FontSize        { get; private set; } = 12;
    public double IconStroke      { get; private set; } = 1.8;
    public double RadiusMedium    { get; private set; } = 8;
    public Geometry? Chevron      { get; private set; }
    public Geometry? Funnel       { get; private set; }
    public Geometry? Check        { get; private set; }
    public Geometry? Paperclip    { get; private set; }

    public TextLayoutCache Text { get; private set; } = new(Typeface.Default, 12);

    private readonly Dictionary<string, Geometry?> _typeIcons = new();
    private readonly Dictionary<IBrush, Pen>       _iconPens  = new(ReferenceEqualityComparer.Instance);
    private Func<string, object?> _res = _ => null;

    /// <summary>Запасные значения — пока поверхность не в дереве и ресурсы темы недоступны.</summary>
    public static SurfaceStyle Fallback() => new();

    /// <summary>Прочитать токены темы у контрола (ресурсы контрола, затем приложения); высота строки —
    /// по ключу ресурса (плотность конструктора или RowHeightStandard).</summary>
    public static SurfaceStyle Load(Control host, string rowHeightKey)
    {
        var s = new SurfaceStyle();
        s._res = key => host.TryFindResource(key, out var v) ? v
                      : Application.Current is { } app && app.TryFindResource(key, out v) ? v
                      : null;

        s.Bg          = s.Brush("BrushBg",            Brushes.White);
        s.InactiveBg  = s.Brush("BrushInactiveBg",    Brushes.WhiteSmoke);
        s.HoverBg     = s.Brush("BrushSecondaryBg",   Brushes.WhiteSmoke);
        s.HoverStrong = s.Brush("BrushHover",         Brushes.LightGray);
        s.Primary     = s.Brush("BrushPrimaryText",   Brushes.Black);
        s.Secondary   = s.Brush("BrushSecondaryText", Brushes.Gray);
        s.Accent      = s.Brush("BrushAccent",        Brushes.RoyalBlue);
        s.AccentLight = s.Brush("BrushAccentLight",   Brushes.LightBlue);
        // Тонировка диапазона — акцент с прозрачностью 0,08, как Background рамки прежнего слоя.
        s.SelectTint  = new SolidColorBrush(s.Accent is ISolidColorBrush sc ? sc.Color : Colors.RoyalBlue, 0.08);
        s.Red         = s.Brush("BrushRed",           Brushes.Red);
        s.Green       = s.Brush("BrushGreen",         Brushes.Green);
        s.SeparatorBrush       = s.Brush("BrushSeparator", Brushes.LightGray);
        s.ChoiceBg             = s.Brush("BrushSecondaryBg", Brushes.WhiteSmoke);
        s.ChoiceBorder         = s.Brush("BrushSeparator",   Brushes.LightGray);
        s.ChoiceBgDisabled     = s.Brush("ComboBoxBackgroundDisabled",              Brushes.LightGray);
        s.ChoiceBorderDisabled = s.Brush("ComboBoxBorderBrushDisabled",             Brushes.Gray);
        s.ChoiceGlyphBrush     = s.Brush("ComboBoxDropDownGlyphForeground", s.Secondary);
        s.ChoiceGlyphDisabled  = s.Brush("ComboBoxDropDownGlyphForegroundDisabled", s.ChoiceGlyphBrush);
        s.RadiusMedium         = s._res("RadiusMedium") is CornerRadius radius ? radius.TopLeft : 8;
        s.ChoiceRadius         = s.RadiusMedium;
        s.LineWidth   = s.Dbl("SeparatorThickness", 1);
        s.AccentHalf      = new SolidColorBrush(s.Accent is ISolidColorBrush ac ? ac.Color : Colors.RoyalBlue, 0.5);
        s.WhiteHalf       = new SolidColorBrush(Colors.White, 0.5);
        s.CheckBorderHalf = new Pen(new SolidColorBrush(s.Primary is ISolidColorBrush pc ? pc.Color : Colors.Black, 0.5), s.LineWidth);
        s.Separator   = new Pen(s.SeparatorBrush, s.LineWidth);
        s.CheckBorder = new Pen(s.Primary, s.LineWidth);
        s.RowHeight   = s.Dbl(rowHeightKey, ConstructorSettings.RowHeight);
        s.FontSize    = s.Dbl("FontSizeBase", 12);
        s.Typeface    = s._res("FontFamilyBase") is FontFamily family ? new Typeface(family) : Typeface.Default;
        s.IconStroke  = s.Dbl("IconStrokeSmall", 1.8);
        s.Chevron     = s.Geo("IconChevronDown");
        s.Funnel      = s.Geo("IconFilter");
        s.Check       = s.Geo("CheckBoxGlyphGeometry");
        s.Paperclip   = s.Geo("IconFieldFile");

        // Целая высота строки текста — тот же токен, что у надписей и редакторов ячеек нынешней
        // таблицы (LineHeightBase пишет FontScale): боксы показа и правки одной высоты на любой сетке.
        var probe   = new TextLayout("Ag", s.Typeface, s.FontSize, s.Primary);
        var natural = probe.TextLines.Count > 0 && probe.TextLines[0].Height > 0 ? probe.TextLines[0].Height : s.FontSize * 1.35;
        s.LineHeight = s.Dbl("LineHeightBase", Math.Ceiling(natural));

        s.Text = new TextLayoutCache(s.Typeface, s.FontSize);
        return s;
    }

    private IBrush    Brush(string key, IBrush fallback) => _res(key) is IBrush b ? b : fallback;
    private double    Dbl(string key, double fallback)   => _res(key) is double d ? d : fallback;
    private Geometry? Geo(string key)                    => _res(key) as Geometry;

    /// <summary>Геометрия реестра Icons.axaml по ключу (кешируется до следующего чтения токенов).</summary>
    public Geometry? CachedIcon(string key)
    {
        var cacheKey = ":" + key;
        if (!_typeIcons.TryGetValue(cacheKey, out var geo))
        {
            geo = Geo(key);
            _typeIcons[cacheKey] = geo;
        }
        return geo;
    }

    /// <summary>Иконка типа поля (реестр <see cref="FieldTypeIcons"/>).</summary>
    public Geometry? TypeIcon(string? fieldType)
    {
        var key = fieldType ?? "";
        if (!_typeIcons.TryGetValue(key, out var geo))
        {
            geo = FieldTypeIcons.ForType(fieldType);
            _typeIcons[key] = geo;
        }
        return geo;
    }

    /// <summary>Координата на физическом пикселе кадра.</summary>
    public static double Snap(double v, double scale) => Math.Round(v * scale) / scale;

    /// <summary>Размер, округленный вверх до физического пикселя — как DesiredSize при округлении раскладки.</summary>
    public static double CeilToPixel(double v, double scale) => Math.Ceiling(v * scale - 1e-6) / scale;

    /// <summary>
    /// Что и как пишет ячейка «Файла» (как <c>FileEditor</c> в плоском режиме): имя ссылкой (акцент,
    /// подчеркнуто), когда файл на месте; «имя — файл не найден» серым, когда его нет; пусто — серое
    /// «Выбрать файл…»; у авто-привязки — «папка недоступна» / имя / «файл не найден». Есть ли файл — из
    /// кеша <see cref="FileProbe"/>, диск с UI-потока не спрашивается. <paramref name="fg"/> — цвет
    /// текста ячейки, если задан оформлением (тогда ссылка им и красится).
    /// </summary>
    public (string Text, IBrush Brush, string Id, bool Underline) FileLook(string path, string autoFolder, IBrush? fg = null, string fgId = "")
    {
        var paths = FileValue.ReadPaths(path);
        if (paths.Count == 1) path = paths[0];
        else if (paths.Count == 0) path = "";
        var name = FileValue.DisplayName(path);
        var auto = autoFolder.Length > 0;
        if (auto && FileProbe.FolderExists(autoFolder) == false) return ("папка недоступна", Secondary, "s", false);
        if (path.Length == 0) return (auto ? "файл не найден" : "Выбрать файл…", Secondary, "s", false);
        if (FileProbe.FileExists(path) != false) return (name, fg ?? Accent, fg is null ? "a" : fgId, true);
        return (name + " — файл не найден", Secondary, "s", false);
    }

    /// <summary>Несколько имен файлов и точные области их ссылок; запятые и многоточие — обычный текст.</summary>
    public FileLinksLayout FileLinks(string storage, string autoFolder, Rect area, TextAlignment align,
                                     CellVAlign valign, double pad, double scale, bool label = false,
                                     IBrush? fg = null, string fgId = "") =>
        FileLinksLayout.Create(this, storage, autoFolder, area, align, valign, pad, scale, label, fg, fgId);

    /// <summary>Перо иконки: обводка IconStrokeSmall со скругленными концами и углами (как Path в
    /// Viewbox реестра иконок).</summary>
    public Pen IconPen(IBrush stroke)
    {
        if (!_iconPens.TryGetValue(stroke, out var pen))
        {
            pen = new Pen(stroke, IconStroke, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round);
            _iconPens[stroke] = pen;
        }
        return pen;
    }

    /// <summary>Иконка словаря Icons.axaml (поле 24×24) в квадрате <paramref name="size"/> с левым
    /// верхним углом в (x, y), обводка той же толщины, что у Viewbox шапки (IconStrokeSmall в
    /// координатах поля).</summary>
    public void DrawIcon(DrawingContext ctx, Geometry geometry, double x, double y, IBrush stroke,
                         double size = IconSize, double? thickness = null)
    {
        var s   = size / 24.0;
        var pen = thickness is null ? IconPen(stroke) : new Pen(stroke, thickness.Value, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round);
        using (ctx.PushTransform(Matrix.CreateScale(s, s) * Matrix.CreateTranslation(x, y)))
            ctx.DrawGeometry(null, pen, geometry);
    }

    /// <summary>Залитая фигура, вписанная в прямоугольник с сохранением пропорций (PathIcon: Stretch=Uniform).</summary>
    public static void DrawFilledGlyph(DrawingContext ctx, Geometry geometry, Rect target, IBrush fill)
    {
        var b = geometry.Bounds;
        if (b.Width <= 0 || b.Height <= 0) return;
        var s  = Math.Min(target.Width / b.Width, target.Height / b.Height);
        var dx = target.X + (target.Width  - b.Width  * s) / 2 - b.X * s;
        var dy = target.Y + (target.Height - b.Height * s) / 2 - b.Y * s;
        using (ctx.PushTransform(Matrix.CreateScale(s, s) * Matrix.CreateTranslation(dx, dy)))
            ctx.DrawGeometry(fill, null, geometry);
    }

    /// <summary>Галочка — как единый CheckBox приложения (AppCheckBox) в квадрате
    /// <paramref name="square"/> 16×16 (уже на целом пикселе): радиус 2, рамка в толщину линии цветом
    /// текста; отмеченная — заливка и рамка акцентом, белый глиф 12×12 внутри. Недоступная
    /// (CheckBox:disabled, Opacity 0,5) — заливка и рамка полупрозрачные каждая, как рисует их Border.</summary>
    public void DrawCheckbox(DrawingContext ctx, Rect square, bool isChecked, bool enabled = true)
    {
        var (x, y) = (square.X, square.Y);
        var half   = LineWidth / 2;
        // Как Border с рамкой в линию: заливка и обводка одним прямоугольником по краю, сдвинутому
        // внутрь на полтолщины, — углы скругляются так же, как у CheckBox.
        var ring   = new RoundedRect(new Rect(x + half, y + half, CheckSize - LineWidth, CheckSize - LineWidth), 2);
        if (isChecked)
        {
            if (enabled) ctx.DrawRectangle(Accent, new Pen(Accent, LineWidth), ring);
            else         ctx.DrawRectangle(AccentHalf, new Pen(AccentHalf, LineWidth), ring);
            if (Check is not null)
                DrawIcon(ctx, Check, x + (CheckSize - CheckGlyph) / 2, y + (CheckSize - CheckGlyph) / 2,
                         enabled ? Brushes.White : WhiteHalf, CheckGlyph, 2.5);
        }
        else
        {
            ctx.DrawRectangle(null, enabled ? CheckBorder : CheckBorderHalf, ring);
        }
    }

    /// <summary>
    /// Надпись в области: отступ <paramref name="pad"/> с обеих сторон, по вертикали — по
    /// <paramref name="valign"/> на целом пикселе (по центру — как TextBlock с
    /// VerticalAlignment=Center при округлении раскладки; сверху и снизу —
    /// <see cref="CellAlign.TextTop"/>). lineHeight NaN — естественная высота строки (заголовки),
    /// иначе целый токен LineHeightBase (ячейки). <paramref name="clipText"/> — обрезка по
    /// прямоугольнику самой надписи, как у TextBlock с многоточием (выступ первой буквы за левый
    /// край не рисуется; высота — вверх до физического пикселя); иначе обрезка по области только
    /// при переполнении. <paramref name="trimming"/> — многоточие (надписи) или обрезка без него
    /// (поле ввода). <paramref name="centering"/> — как центровать надпись «по центру».
    /// </summary>
    public void DrawText(DrawingContext ctx, string text, Rect area, IBrush brush, string brushId,
                         TextAlignment align, int maxLines, double lineHeight, double pad, bool underline,
                         bool clipText, double scale, TextTrimming? trimming = null,
                         CellVAlign valign = CellVAlign.Center, TextCentering centering = TextCentering.Block)
    {
        if (string.IsNullOrEmpty(text)) return;

        var maxW   = Math.Max(1, area.Width - pad * 2);
        // Центрованная надпись (номер строки, «№» и «+» шапки, ячейки, которые правятся списком) —
        // как TextBlock с HorizontalAlignment=Center: раскладка естественной ширины, а ее левый край
        // ложится на целый пиксель (округление раскладки), а не на половинку от выравнивания внутри
        // полосы. Ячейка, которая правится полем ввода, центрует каждую строку в полосе, как само
        // поле (TextAlignment=Center), — иначе текст сдвигался бы при входе в правку.
        var lines  = align == TextAlignment.Center && centering == TextCentering.Lines;
        var block  = align == TextAlignment.Center && !lines;
        // Поле ввода раскладывает текст по ширине, округленной ВВЕРХ до целого DIP (так делает его
        // презентер текста), — строки по центру считаются от той же ширины, иначе на дробной ширине
        // колонки текст правки стоял бы на полпикселя правее показа (стережет Tests/EditGeometry).
        var layoutW = lines ? Math.Ceiling(maxW - 1e-6) : maxW;
        var layout = Text.Get(text, layoutW, block ? TextAlignment.Left : align, brush, brushId,
                              maxLines, lineHeight, underline ? Underline : null, trimming);
        // Ширина надписи — с правым свесом последнего глифа и округленная вверх до пикселя, как
        // DesiredSize у TextBlock при округлении раскладки.
        var textW  = Math.Min(Math.Ceiling(layout.WidthIncludingTrailingWhitespace * scale) / scale, maxW);
        // Значение списка — TextBlock внутри полосы значения: его ширина округлена вверх до целого
        // DIP (а не пикселя), и раскладка округляет его положение в координатах РОДИТЕЛЯ — на целый
        // пиксель ложится смещение от края ячейки, а не абсолютная координата. Иначе на дробных
        // ширинах показ и правка расходились бы на пиксель (стережет Tests/EditGeometry).
        var x      = !block ? area.X + pad
                   : centering == TextCentering.BlockInArea
                       ? area.X + Snap((area.Width - Math.Min(Math.Ceiling(layout.WidthIncludingTrailingWhitespace - 1e-6), maxW)) / 2, scale)
                   : Snap(area.X + (area.Width - textW) / 2, scale);
        var y      = Snap(area.Y + CellAlign.TextTop(valign, area.Height, layout.Height, TextEdge), scale);

        if (clipText)
        {
            var clipH = Math.Ceiling(layout.Height * scale - 1e-6) / scale;
            using (ctx.PushClip(new Rect(area.X + pad, y, maxW, clipH)))
                layout.Draw(ctx, new Point(x, y));
            return;
        }

        if (layout.Height > area.Height || layout.Width > maxW + 0.5)
        {
            using (ctx.PushClip(area))
                layout.Draw(ctx, new Point(x, y));
            return;
        }
        layout.Draw(ctx, new Point(x, y));
    }
}
