using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace SmartBase.Notifications.Platforms;

/// <summary>
/// macOS: баннер шлет САМ процесс приложения через <c>NSUserNotificationCenter</c> — тогда
/// система рисует иконку приложения (из .app-бандла) и по клику активирует именно наше окно.
/// Библиотечный Apple-бэкенд DesktopNotifications нефункционален (заглушка), поэтому Cocoa
/// дергаем напрямую через Objective-C runtime (libobjc). Для клика создаем на лету класс-делегат
/// и ставим его в <c>center.delegate</c>; метку записи кладем в <c>userInfo</c> и достаем при клике.
///
/// <c>NSUserNotification</c> помечен deprecated (с macOS 11), но продолжает работать из
/// подписанного .app; современная замена — UNUserNotificationCenter (задача на потом).
/// Требует запуск из .app-бандла: в dev (`dotnet run`) идентичности/иконки может не быть.
/// </summary>
[SupportedOSPlatform("macos")]
internal sealed class MacNotifier : INativeNotifier
{
    private const string Libobjc = "/usr/lib/libobjc.dylib";

    public event Action<string>? Activated;

    // Один нотифайер на приложение — статический мост для C-колбэка делегата.
    private static MacNotifier? _instance;
    private static nint _delegate;   // держим объект-делегат живым (center.delegate — weak)

    public MacNotifier()
    {
        _instance = this;
        EnsureDelegate();
    }

    public void Show(string title, string body, string tag)
    {
        try
        {
            var n = Send(Send(GetClass("NSUserNotification"), Sel("alloc")), Sel("init"));
            if (n == nint.Zero) return;
            SendPtr(n, Sel("setTitle:"), NSString(title));
            SendPtr(n, Sel("setInformativeText:"), NSString(body));
            if (!string.IsNullOrEmpty(tag))
            {
                var dict = SendPtrPtr(GetClass("NSDictionary"), Sel("dictionaryWithObject:forKey:"),
                                      NSString(tag), NSString("tag"));
                SendPtr(n, Sel("setUserInfo:"), dict);
            }

            var center = Send(GetClass("NSUserNotificationCenter"), Sel("defaultUserNotificationCenter"));
            SendPtr(center, Sel("deliverNotification:"), n);
        }
        catch { /* Cocoa недоступна (не .app / сбой) — тихо пропускаем */ }
    }

    // ── Делегат для клика ────────────────────────────────────────────────────────

    private void EnsureDelegate()
    {
        if (_delegate != nint.Zero) return;
        try
        {
            var cls = objc_allocateClassPair(GetClass("NSObject"), "SBUserNotifDelegate", 0);
            if (cls == nint.Zero) return;

            // userNotificationCenter:didActivateNotification: (клик) и
            // userNotificationCenter:shouldPresentNotification: (показать, даже если окно активно).
            unsafe
            {
                class_addMethod(cls, Sel("userNotificationCenter:didActivateNotification:"),
                    (nint)(delegate* unmanaged<nint, nint, nint, nint, void>)&DidActivate, "v@:@@");
                class_addMethod(cls, Sel("userNotificationCenter:shouldPresentNotification:"),
                    (nint)(delegate* unmanaged<nint, nint, nint, nint, byte>)&ShouldPresent, "c@:@@");
            }
            objc_registerClassPair(cls);

            _delegate = Send(Send(cls, Sel("alloc")), Sel("init"));
            var center = Send(GetClass("NSUserNotificationCenter"), Sel("defaultUserNotificationCenter"));
            SendPtr(center, Sel("setDelegate:"), _delegate);
        }
        catch { /* не удалось поставить делегат — уведомления все равно покажутся, но без клика */ }
    }

    [UnmanagedCallersOnly]
    private static void DidActivate(nint self, nint cmd, nint center, nint notification)
    {
        try
        {
            var userInfo = Send(notification, Sel("userInfo"));
            if (userInfo == nint.Zero) return;
            var val = SendPtr(userInfo, Sel("objectForKey:"), NSString("tag"));
            if (val == nint.Zero) return;
            var utf8 = Send(val, Sel("UTF8String"));
            var tag  = Marshal.PtrToStringUTF8(utf8);
            if (!string.IsNullOrEmpty(tag)) _instance?.Activated?.Invoke(tag);
        }
        catch { /* колбэк не должен ронять AppKit */ }
    }

    // Показывать баннер, даже когда наше окно активно (иначе macOS его прячет).
    [UnmanagedCallersOnly]
    private static byte ShouldPresent(nint self, nint cmd, nint center, nint notification) => 1;

    // ── libobjc ──────────────────────────────────────────────────────────────────

    private static nint NSString(string s) =>
        SendStr(GetClass("NSString"), Sel("stringWithUTF8String:"), s);

    [DllImport(Libobjc, EntryPoint = "objc_getClass")]
    private static extern nint GetClass([MarshalAs(UnmanagedType.LPStr)] string name);
    [DllImport(Libobjc, EntryPoint = "sel_registerName")]
    private static extern nint Sel([MarshalAs(UnmanagedType.LPStr)] string name);
    [DllImport(Libobjc)]
    private static extern nint objc_allocateClassPair(nint super, [MarshalAs(UnmanagedType.LPStr)] string name, nuint extra);
    [DllImport(Libobjc)]
    private static extern void objc_registerClassPair(nint cls);
    [DllImport(Libobjc)]
    private static extern bool class_addMethod(nint cls, nint sel, nint imp, [MarshalAs(UnmanagedType.LPStr)] string types);

    [DllImport(Libobjc, EntryPoint = "objc_msgSend")]
    private static extern nint Send(nint receiver, nint sel);
    [DllImport(Libobjc, EntryPoint = "objc_msgSend")]
    private static extern nint SendStr(nint receiver, nint sel, [MarshalAs(UnmanagedType.LPUTF8Str)] string arg);
    [DllImport(Libobjc, EntryPoint = "objc_msgSend")]
    private static extern nint SendPtr(nint receiver, nint sel, nint arg);
    [DllImport(Libobjc, EntryPoint = "objc_msgSend")]
    private static extern nint SendPtrPtr(nint receiver, nint sel, nint a, nint b);
}
