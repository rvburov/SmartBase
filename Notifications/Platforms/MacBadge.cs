using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace SmartBase.Notifications.Platforms;

/// <summary>
/// macOS: значок-счетчик на иконке в Dock через <c>NSApp.dockTile.badgeLabel</c>.
/// Cocoa-класса в .NET нет, поэтому дергаем Objective-C runtime напрямую (libobjc):
/// <c>[[NSApplication sharedApplication] dockTile] setBadgeLabel: "N"]</c>; пустая метка
/// (nil) убирает значок. Вызывается из UI-потока (главный поток AppKit).
/// </summary>
[SupportedOSPlatform("macos")]
internal sealed class MacBadge : IAppBadge
{
    private const string Libobjc = "/usr/lib/libobjc.dylib";

    [DllImport(Libobjc, EntryPoint = "objc_getClass")]
    private static extern nint GetClass([MarshalAs(UnmanagedType.LPStr)] string name);

    [DllImport(Libobjc, EntryPoint = "sel_registerName")]
    private static extern nint Sel([MarshalAs(UnmanagedType.LPStr)] string name);

    [DllImport(Libobjc, EntryPoint = "objc_msgSend")]
    private static extern nint Send(nint receiver, nint selector);

    [DllImport(Libobjc, EntryPoint = "objc_msgSend")]
    private static extern nint SendStr(nint receiver, nint selector,
                                       [MarshalAs(UnmanagedType.LPUTF8Str)] string arg);

    [DllImport(Libobjc, EntryPoint = "objc_msgSend")]
    private static extern void SendPtr(nint receiver, nint selector, nint arg);

    public void SetCount(int count)
    {
        try
        {
            var app = Send(GetClass("NSApplication"), Sel("sharedApplication"));
            if (app == nint.Zero) return;
            var tile = Send(app, Sel("dockTile"));
            if (tile == nint.Zero) return;

            // count <= 0 → nil (значок исчезает); иначе NSString с числом.
            var label = count > 0
                ? SendStr(GetClass("NSString"), Sel("stringWithUTF8String:"), count.ToString())
                : nint.Zero;

            SendPtr(tile, Sel("setBadgeLabel:"), label);
        }
        catch { /* runtime недоступен — тихо пропускаем */ }
    }
}
