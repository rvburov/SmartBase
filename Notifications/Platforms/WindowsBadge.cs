using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace SmartBase.Notifications.Platforms;

/// <summary>
/// Windows: значок-счётчик как overlay-иконка на кнопке приложения в панели задач через
/// <c>ITaskbarList3.SetOverlayIcon</c> (штатный механизм для неупакованных Win32-приложений).
/// Саму иконку (красный кружок с числом) рисуем средствами GDI — без сторонних библиотек и
/// System.Drawing. Хэндл главного окна берётся через переданный провайдер. Всё под
/// <c>try/catch</c>: сбой интеропа не должен ломать приложение. Проверять на реальной Windows.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class WindowsBadge : IAppBadge
{
    private readonly Func<nint> _hwnd;
    private ITaskbarList3? _taskbar;

    public WindowsBadge(Func<nint> hwnd) => _hwnd = hwnd;

    public void SetCount(int count)
    {
        try
        {
            var hwnd = _hwnd();
            if (hwnd == nint.Zero) return;

            var bar = Taskbar();
            if (bar is null) return;

            if (count <= 0)
            {
                bar.SetOverlayIcon(hwnd, nint.Zero, null);
                return;
            }

            var icon = CreateBadgeIcon(count);
            if (icon == nint.Zero) return;
            try { bar.SetOverlayIcon(hwnd, icon, count.ToString()); }
            finally { DestroyIcon(icon); }   // панель задач делает свою копию — можно удалять
        }
        catch { /* сбой интеропа — тихо пропускаем */ }
    }

    private ITaskbarList3? Taskbar()
    {
        if (_taskbar is not null) return _taskbar;
        var type = Type.GetTypeFromCLSID(ClsidTaskbarList);
        if (type is null) return null;
        if (Activator.CreateInstance(type) is not ITaskbarList3 bar) return null;
        bar.HrInit();
        _taskbar = bar;
        return _taskbar;
    }

    // ── Рисование иконки (красный кружок + число) ────────────────────────────────
    //
    // Три вещи, без которых кружок выглядит кривым:
    //
    // 1. РАЗМЕР. Панель задач ждёт overlay-иконку размером с «маленький значок» системы
    //    (SM_CXSMICON — 16 px при 100 %, больше при масштабировании экрана). Иконку другого
    //    размера шелл ужимает своим быстрым фильтром, и ровный круг превращается в огрызок.
    // 2. СГЛАЖИВАНИЕ. GDI не умеет сглаживать ни Ellipse, ни текст без ClearType. На 16 px
    //    ступеньки видно сразу. Поэтому рисуем в 4 раза крупнее и уменьшаем усреднением —
    //    доля закрашенных подпикселей и даёт альфу края (у ClearType вместо этого цветная
    //    бахрома по краям букв, на прозрачном фоне она выглядит грязно, поэтому он выключен).
    // 3. ЦЕНТР ЧИСЛА. DrawText(DT_VCENTER) центрирует СТРОКУ, у которой снизу запас под
    //    выносные элементы (у «р», «у»), а у цифр их нет — число уезжало вверх. Центрируем
    //    сами цифры: по высоте прописного знака относительно базовой линии.
    private const int Supersample = 4;

    private static nint CreateBadgeIcon(int count)
    {
        var sz = GetSystemMetrics(SmCxSmIcon);
        sz = sz <= 0 ? 16 : Math.Clamp(sz, 16, 64);
        var hi = sz * Supersample;

        var screen = GetDC(nint.Zero);
        var hdc    = CreateCompatibleDC(screen);
        nint big = nint.Zero, small = nint.Zero, oldBmp = nint.Zero,
             brush = nint.Zero, font = nint.Zero, mask = nint.Zero;
        try
        {
            big = CreateDib(hdc, hi, out var bigBits);
            if (big == nint.Zero) return nint.Zero;
            oldBmp = SelectObject(hdc, big);

            // Красный залитый круг без обводки.
            brush = CreateSolidBrush(Rgb(220, 50, 47));
            var oldBrush = SelectObject(hdc, brush);
            var oldPen   = SelectObject(hdc, GetStockObject(NullPen));
            Ellipse(hdc, 0, 0, hi, hi);
            SelectObject(hdc, oldBrush);
            SelectObject(hdc, oldPen);

            // Число по центру (или «9+», если тесно).
            var txt = count > 9 ? "9+" : count.ToString();
            font = BoldFont(-(int)(hi * 0.60));
            var oldFont = SelectObject(hdc, font);
            SetBkMode(hdc, Transparent);
            SetTextColor(hdc, Rgb(255, 255, 255));

            // Круг узкий по краям, поэтому надпись вписываем в горизонтальную хорду: меряем
            // её и при нужде пересоздаём шрифт мельче. Без этого «9+» вылезает за кружок,
            // а привязка к длине строки («две цифры — шрифт X») врёт на других шрифтах и DPI.
            var maxWidth = (int)(hi * 0.62);
            if (GetTextExtentPoint32(hdc, txt, txt.Length, out var ext) && ext.cx > maxWidth)
            {
                var fitted = BoldFont(-(int)(hi * 0.60 * maxWidth / ext.cx));
                if (fitted != nint.Zero)
                {
                    SelectObject(hdc, fitted);
                    DeleteObject(font);
                    font = fitted;
                }
            }

            GetTextMetrics(hdc, out var tm);
            var capHeight = tm.tmAscent - tm.tmInternalLeading;   // высота цифры без запасов
            SetTextAlign(hdc, TaCenter | TaBaseline);
            TextOut(hdc, hi / 2, (hi + capHeight) / 2, txt, txt.Length);
            SelectObject(hdc, oldFont);

            small = CreateDib(hdc, sz, out var smallBits);
            if (small == nint.Zero) return nint.Zero;
            Downsample(bigBits, hi, smallBits, sz);

            // Маска обязана быть НУЛЕВОЙ: CreateBitmap с null оставляет содержимое
            // неопределённым, и мусор в ней выгрызает из значка случайные пиксели.
            mask = CreateBitmap(sz, sz, 1, 1, new byte[(sz + 15) / 16 * 2 * sz]);
            var ii = new ICONINFO { fIcon = true, hbmMask = mask, hbmColor = small };
            return CreateIconIndirect(ref ii);
        }
        finally
        {
            if (oldBmp != nint.Zero) SelectObject(hdc, oldBmp);
            if (brush != nint.Zero) DeleteObject(brush);
            if (font  != nint.Zero) DeleteObject(font);
            if (mask  != nint.Zero) DeleteObject(mask);
            if (big   != nint.Zero) DeleteObject(big);
            if (small != nint.Zero) DeleteObject(small);
            DeleteDC(hdc);
            ReleaseDC(nint.Zero, screen);
        }
    }

    private static nint BoldFont(int height) =>
        CreateFont(height, 0, 0, 0, FwBold, 0, 0, 0, DefaultCharset,
                   0, 0, NonAntialiasedQuality, 0, "Segoe UI");

    // Квадратная 32-битная top-down DIB-секция: рисуем в неё и читаем пиксели напрямую.
    private static nint CreateDib(nint hdc, int side, out nint bits)
    {
        var header = new BITMAPINFOHEADER
        {
            biSize = (uint)Marshal.SizeOf<BITMAPINFOHEADER>(),
            biWidth = side, biHeight = -side,   // top-down
            biPlanes = 1, biBitCount = 32, biCompression = 0,
        };
        var dib = CreateDIBSection(hdc, ref header, 0, out bits, nint.Zero, 0);
        return bits == nint.Zero ? nint.Zero : dib;
    }

    // Уменьшение усреднением: доля закрашенных подпикселей становится альфой, их средний
    // цвет — цветом. Фон (чёрный, GDI оставляет его нетронутым) считается пустым, поэтому
    // край круга и буквы получают плавную полупрозрачную кромку вместо ступенек.
    private static void Downsample(nint src, int hi, nint dst, int sz)
    {
        var from  = new int[hi * hi];
        Marshal.Copy(src, from, 0, from.Length);
        var to    = new int[sz * sz];
        var total = Supersample * Supersample;

        for (var y = 0; y < sz; y++)
        for (var x = 0; x < sz; x++)
        {
            int filled = 0, r = 0, g = 0, b = 0;
            for (var dy = 0; dy < Supersample; dy++)
            for (var dx = 0; dx < Supersample; dx++)
            {
                var px = from[(y * Supersample + dy) * hi + x * Supersample + dx] & 0x00FFFFFF;
                if (px == 0) continue;
                filled++;
                b += px & 0xFF; g += (px >> 8) & 0xFF; r += (px >> 16) & 0xFF;
            }
            to[y * sz + x] = filled == 0
                ? 0
                : (filled * 255 / total) << 24 | (r / filled) << 16 | (g / filled) << 8 | (b / filled);
        }
        Marshal.Copy(to, 0, dst, to.Length);
    }

    private static int Rgb(int r, int g, int b) => r | (g << 8) | (b << 16);

    // ── COM: ITaskbarList3 ───────────────────────────────────────────────────────

    private static readonly Guid ClsidTaskbarList = new("56fdf344-fd6d-11d0-958a-006097c9a090");

    [ComImport, Guid("ea1afb91-9e28-4b86-90e9-9e9f8a5eefaf"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ITaskbarList3
    {
        [PreserveSig] int HrInit();
        // Слоты vtable до SetOverlayIcon — объявлены для выравнивания, не вызываются.
        void AddTab(nint h); void DeleteTab(nint h); void ActivateTab(nint h); void SetActiveAlt(nint h);
        void MarkFullscreenWindow(nint h, bool f);
        void SetProgressValue(nint h, ulong c, ulong t);
        void SetProgressState(nint h, int s);
        void RegisterTab(nint t, nint m); void UnregisterTab(nint t);
        void SetTabOrder(nint t, nint b); void SetTabActive(nint t, nint m, int r);
        void ThumbBarAddButtons(nint h, uint n, nint b);
        void ThumbBarUpdateButtons(nint h, uint n, nint b);
        void ThumbBarSetImageList(nint h, nint himl);
        [PreserveSig] int SetOverlayIcon(nint hwnd, nint icon, [MarshalAs(UnmanagedType.LPWStr)] string? desc);
    }

    // ── GDI/USER interop ─────────────────────────────────────────────────────────

    private const int Transparent = 1, NullPen = 8, FwBold = 700, DefaultCharset = 1;
    // Сглаживание шрифта выключено намеренно: его делает уменьшение (см. Downsample),
    // а ClearType дал бы цветную бахрому по краям цифр на прозрачном фоне.
    private const uint NonAntialiasedQuality = 3;
    private const uint TaCenter = 6, TaBaseline = 24;
    private const int  SmCxSmIcon = 49;   // ширина «маленького значка» системы (с учётом масштаба)

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFOHEADER
    {
        public uint biSize; public int biWidth; public int biHeight;
        public ushort biPlanes; public ushort biBitCount; public uint biCompression;
        public uint biSizeImage; public int biXPelsPerMeter; public int biYPelsPerMeter;
        public uint biClrUsed; public uint biClrImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ICONINFO { public bool fIcon; public int xHotspot; public int yHotspot; public nint hbmMask; public nint hbmColor; }

    [StructLayout(LayoutKind.Sequential)]
    private struct SIZE { public int cx, cy; }

    // Только начало TEXTMETRICW — дальше полей, нужных для центровки цифр, не читаем,
    // но структура обязана совпадать по раскладке целиком, иначе GDI испортит стек.
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct TEXTMETRICW
    {
        public int tmHeight, tmAscent, tmDescent, tmInternalLeading, tmExternalLeading;
        public int tmAveCharWidth, tmMaxCharWidth, tmWeight, tmOverhang;
        public int tmDigitizedAspectX, tmDigitizedAspectY;
        public char tmFirstChar, tmLastChar, tmDefaultChar, tmBreakChar;
        public byte tmItalic, tmUnderlined, tmStruckOut, tmPitchAndFamily, tmCharSet;
    }

    [DllImport("user32.dll")] private static extern nint GetDC(nint hwnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(nint hwnd, nint hdc);
    [DllImport("user32.dll")] private static extern nint CreateIconIndirect(ref ICONINFO ii);
    [DllImport("user32.dll")] private static extern bool DestroyIcon(nint icon);
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);

    [DllImport("gdi32.dll")] private static extern nint CreateCompatibleDC(nint hdc);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(nint hdc);
    [DllImport("gdi32.dll")] private static extern nint SelectObject(nint hdc, nint obj);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(nint obj);
    [DllImport("gdi32.dll")] private static extern nint GetStockObject(int obj);
    [DllImport("gdi32.dll")] private static extern nint CreateSolidBrush(int color);
    [DllImport("gdi32.dll")] private static extern bool Ellipse(nint hdc, int l, int t, int r, int b);
    [DllImport("gdi32.dll")] private static extern int SetBkMode(nint hdc, int mode);
    [DllImport("gdi32.dll")] private static extern int SetTextColor(nint hdc, int color);
    [DllImport("gdi32.dll")] private static extern nint CreateBitmap(int w, int h, uint planes, uint bpp, byte[] bits);
    [DllImport("gdi32.dll")] private static extern int SetTextAlign(nint hdc, uint align);
    [DllImport("gdi32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetTextMetricsW")]
    private static extern bool GetTextMetrics(nint hdc, out TEXTMETRICW tm);
    [DllImport("gdi32.dll", CharSet = CharSet.Unicode, EntryPoint = "TextOutW")]
    private static extern bool TextOut(nint hdc, int x, int y, string text, int len);
    [DllImport("gdi32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetTextExtentPoint32W")]
    private static extern bool GetTextExtentPoint32(nint hdc, string text, int len, out SIZE size);
    [DllImport("gdi32.dll")]
    private static extern nint CreateDIBSection(nint hdc, ref BITMAPINFOHEADER bmi, uint usage,
                                                out nint bits, nint section, uint offset);
    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)]
    private static extern nint CreateFont(int h, int w, int esc, int orient, int weight,
                                          uint italic, uint underline, uint strikeout, uint charset,
                                          uint outPrec, uint clipPrec, uint quality, uint pitch, string face);
}
