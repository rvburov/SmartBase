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

    // ── Рисование иконки 32×32 (красный кружок + число) ──────────────────────────

    private static nint CreateBadgeIcon(int count)
    {
        const int sz = 32;
        var screen = GetDC(nint.Zero);
        var hdc    = CreateCompatibleDC(screen);
        nint hbmColor = nint.Zero, mask = nint.Zero, oldBmp = nint.Zero, brush = nint.Zero, font = nint.Zero;
        try
        {
            var header = new BITMAPINFOHEADER
            {
                biSize = (uint)Marshal.SizeOf<BITMAPINFOHEADER>(),
                biWidth = sz, biHeight = -sz,   // top-down
                biPlanes = 1, biBitCount = 32, biCompression = 0,
            };
            hbmColor = CreateDIBSection(hdc, ref header, 0, out var bits, nint.Zero, 0);
            if (hbmColor == nint.Zero || bits == nint.Zero) return nint.Zero;
            oldBmp = SelectObject(hdc, hbmColor);

            // Красный залитый круг без обводки.
            brush = CreateSolidBrush(Rgb(220, 50, 47));
            var oldBrush = SelectObject(hdc, brush);
            var oldPen   = SelectObject(hdc, GetStockObject(NullPen));
            Ellipse(hdc, 0, 0, sz, sz);
            SelectObject(hdc, oldBrush);
            SelectObject(hdc, oldPen);

            // Число по центру (или «9+», если тесно).
            font = CreateFont(-19, 0, 0, 0, FwBold, 0, 0, 0, DefaultCharset,
                              0, 0, 0, 0, "Segoe UI");
            var oldFont = SelectObject(hdc, font);
            SetBkMode(hdc, Transparent);
            SetTextColor(hdc, Rgb(255, 255, 255));
            var rc  = new RECT { left = 0, top = 0, right = sz, bottom = sz };
            var txt = count > 9 ? "9+" : count.ToString();
            DrawText(hdc, txt, -1, ref rc, DtCenter | DtVCenter | DtSingleLine);
            SelectObject(hdc, oldFont);

            // GDI не проставляет альфу — делаем нарисованное (не чёрное) непрозрачным,
            // фон — прозрачным, чтобы иконка была кружком, а не квадратом.
            for (var i = 0; i < sz * sz; i++)
            {
                var px = Marshal.ReadInt32(bits, i * 4);
                var opaque = (px & 0x00FFFFFF) != 0;
                Marshal.WriteInt32(bits, i * 4, opaque ? (px | unchecked((int)0xFF000000)) : 0);
            }

            mask = CreateBitmap(sz, sz, 1, 1, nint.Zero);
            var ii = new ICONINFO { fIcon = true, hbmMask = mask, hbmColor = hbmColor };
            var icon = CreateIconIndirect(ref ii);
            return icon;
        }
        finally
        {
            if (oldBmp != nint.Zero) SelectObject(hdc, oldBmp);
            if (brush != nint.Zero) DeleteObject(brush);
            if (font != nint.Zero) DeleteObject(font);
            if (mask != nint.Zero) DeleteObject(mask);
            if (hbmColor != nint.Zero) DeleteObject(hbmColor);
            DeleteDC(hdc);
            ReleaseDC(nint.Zero, screen);
        }
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
    private const uint DtCenter = 0x1, DtVCenter = 0x4, DtSingleLine = 0x20;

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
    private struct RECT { public int left, top, right, bottom; }

    [DllImport("user32.dll")] private static extern nint GetDC(nint hwnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(nint hwnd, nint hdc);
    [DllImport("user32.dll")] private static extern nint CreateIconIndirect(ref ICONINFO ii);
    [DllImport("user32.dll")] private static extern bool DestroyIcon(nint icon);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int DrawText(nint hdc, string text, int count, ref RECT rc, uint format);

    [DllImport("gdi32.dll")] private static extern nint CreateCompatibleDC(nint hdc);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(nint hdc);
    [DllImport("gdi32.dll")] private static extern nint SelectObject(nint hdc, nint obj);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(nint obj);
    [DllImport("gdi32.dll")] private static extern nint GetStockObject(int obj);
    [DllImport("gdi32.dll")] private static extern nint CreateSolidBrush(int color);
    [DllImport("gdi32.dll")] private static extern bool Ellipse(nint hdc, int l, int t, int r, int b);
    [DllImport("gdi32.dll")] private static extern int SetBkMode(nint hdc, int mode);
    [DllImport("gdi32.dll")] private static extern int SetTextColor(nint hdc, int color);
    [DllImport("gdi32.dll")] private static extern nint CreateBitmap(int w, int h, uint planes, uint bpp, nint bits);
    [DllImport("gdi32.dll")]
    private static extern nint CreateDIBSection(nint hdc, ref BITMAPINFOHEADER bmi, uint usage,
                                                out nint bits, nint section, uint offset);
    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)]
    private static extern nint CreateFont(int h, int w, int esc, int orient, int weight,
                                          uint italic, uint underline, uint strikeout, uint charset,
                                          uint outPrec, uint clipPrec, uint quality, uint pitch, string face);
}
