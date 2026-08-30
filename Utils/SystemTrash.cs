using System.Runtime.InteropServices;
using System.Text;

namespace SmartBase.Utils;

/// <summary>
/// Перенос файла или папки в системную корзину. Нужен разделу «Заметки»: там удаляются НАСТОЯЩИЕ
/// файлы пользователя, и безвозвратное <c>File.Delete</c> по клику в дереве было бы ловушкой.
///
/// Единого API у .NET нет, поэтому три ветки — как в <see cref="Autostart"/>:
/// Windows — SHFileOperationW с FOF_ALLOWUNDO, macOS — <c>NSFileManager trashItemAtURL:</c> через
/// Objective-C runtime (прием из <c>Notifications/Platforms/MacBadge</c>), Linux — спецификация
/// freedesktop.org (перенос в ~/.local/share/Trash + файл .trashinfo).
///
/// Любой сбой — <c>false</c> без исключения: вызывающий предлагает удалить безвозвратно, и решение
/// остается за человеком.
/// </summary>
public static class SystemTrash
{
    public static bool TryDelete(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        if (!File.Exists(path) && !Directory.Exists(path)) return false;

        try
        {
            if (OperatingSystem.IsWindows()) return WindowsTrash(path);
            if (OperatingSystem.IsMacOS())   return MacTrash(path);
            if (OperatingSystem.IsLinux())   return LinuxTrash(path);
        }
        catch { /* корзина недоступна — решение о безвозвратном удалении принимает человек */ }

        return false;
    }

    // ── Windows ──────────────────────────────────────────────────────────────────

    private const uint   FoDelete           = 0x0003;
    private const ushort FofSilent          = 0x0004;
    private const ushort FofNoConfirmation  = 0x0010;
    private const ushort FofAllowUndo       = 0x0040;   // ← собственно «в корзину»
    private const ushort FofNoErrorUi       = 0x0400;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ShFileOpStruct
    {
        public nint   hwnd;
        public uint   wFunc;
        [MarshalAs(UnmanagedType.LPWStr)] public string  pFrom;
        [MarshalAs(UnmanagedType.LPWStr)] public string? pTo;
        public ushort fFlags;
        public int    fAnyOperationsAborted;
        public nint   hNameMappings;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpszProgressTitle;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHFileOperationW(ref ShFileOpStruct fileOp);

    private static bool WindowsTrash(string path)
    {
        // pFrom — список путей с ДВОЙНЫМ нулем в конце. Маршалер добавит один сам, второй наш.
        var op = new ShFileOpStruct
        {
            wFunc  = FoDelete,
            pFrom  = path + '\0',
            fFlags = FofAllowUndo | FofNoConfirmation | FofSilent | FofNoErrorUi,
        };
        return SHFileOperationW(ref op) == 0 && op.fAnyOperationsAborted == 0;
    }

    // ── macOS ────────────────────────────────────────────────────────────────────

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
    private static extern nint SendPtr(nint receiver, nint selector, nint arg);

    [DllImport(Libobjc, EntryPoint = "objc_msgSend")]
    private static extern byte SendTrash(nint receiver, nint selector, nint url, nint outUrl, nint error);

    private static bool MacTrash(string path)
    {
        // [[NSFileManager defaultManager] trashItemAtURL:[NSURL fileURLWithPath:path]
        //                                resultingItemURL:nil error:nil]
        var str = SendStr(GetClass("NSString"), Sel("stringWithUTF8String:"), path);
        if (str == nint.Zero) return false;

        var url = SendPtr(GetClass("NSURL"), Sel("fileURLWithPath:"), str);
        if (url == nint.Zero) return false;

        var manager = Send(GetClass("NSFileManager"), Sel("defaultManager"));
        if (manager == nint.Zero) return false;

        return SendTrash(manager, Sel("trashItemAtURL:resultingItemURL:error:"),
                         url, nint.Zero, nint.Zero) != 0;
    }

    // ── Linux ────────────────────────────────────────────────────────────────────

    private static bool LinuxTrash(string path)
    {
        var dataHome = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
        if (string.IsNullOrWhiteSpace(dataHome))
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (string.IsNullOrEmpty(home)) return false;
            dataHome = Path.Combine(home, ".local", "share");
        }

        var files = Path.Combine(dataHome, "Trash", "files");
        var info  = Path.Combine(dataHome, "Trash", "info");
        Directory.CreateDirectory(files);
        Directory.CreateDirectory(info);

        var source   = Path.GetFullPath(path);
        var isFolder = Directory.Exists(source);
        var name     = FreeTrashName(files, info, Path.GetFileName(Path.TrimEndingDirectorySeparator(source)));

        // Сначала .trashinfo, потом перенос: если перенос сорвется, останется лишний описатель,
        // а не файл в корзине без описателя (такой не восстановить).
        File.WriteAllText(
            Path.Combine(info, name + ".trashinfo"),
            "[Trash Info]\n" +
            $"Path={EscapePath(source)}\n" +
            $"DeletionDate={DateTime.Now:yyyy-MM-ddTHH:mm:ss}\n",
            new UTF8Encoding(false));

        var target = Path.Combine(files, name);
        try
        {
            if (isFolder) Directory.Move(source, target);
            else          File.Move(source, target);
        }
        catch (IOException)
        {
            // Другой том: Directory.Move так не умеет — копируем и убираем оригинал.
            if (isFolder) { CopyDirectory(source, target); Directory.Delete(source, recursive: true); }
            else          { File.Copy(source, target);     File.Delete(source); }
        }
        return true;
    }

    private static string FreeTrashName(string files, string info, string name)
    {
        var stem = Path.GetFileNameWithoutExtension(name);
        var ext  = Path.GetExtension(name);
        var candidate = name;
        var n = 2;
        while (File.Exists(Path.Combine(files, candidate))
            || Directory.Exists(Path.Combine(files, candidate))
            || File.Exists(Path.Combine(info, candidate + ".trashinfo")))
        {
            candidate = $"{stem}.{n++}{ext}";
        }
        return candidate;
    }

    // Спецификация требует URL-экранирование, но разделители путей остаются как есть.
    private static string EscapePath(string path) =>
        string.Join('/', path.Split('/').Select(Uri.EscapeDataString));

    private static void CopyDirectory(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (var dir in Directory.EnumerateDirectories(from))
            CopyDirectory(dir, Path.Combine(to, Path.GetFileName(dir)));
        foreach (var file in Directory.EnumerateFiles(from))
            File.Copy(file, Path.Combine(to, Path.GetFileName(file)));
    }
}
