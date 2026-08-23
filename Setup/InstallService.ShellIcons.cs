using System.Runtime.InteropServices;
using SmartBase.Utils;

namespace SmartBase.Setup;

/// <summary>
/// Приведение оболочки в соответствие с только что замененным бинарем.
///
/// Обновление «по воздуху» (<c>--apply-update</c>) заменяет ровно один файл — сам
/// <c>SmartBase[.exe]</c>. Все, что установщик создал вокруг него, остается от прежней
/// сборки, и человек видит это как «иконка не поменялась». Причин две, и они разные:
///
///   • <b>Кеш оболочки.</b> Ярлык <c>.lnk</c> не изменился ни байтом, поэтому у Проводника
///     нет повода перечитывать значок цели — он отдает его из своего кеша иконок. Кеш
///     пользовательский, отсюда и «на одних ПК обновилось, на других нет».
///   • <b>Настоящее устаревание.</b> <c>SmartBase-Setup[.exe]</c> рядом с приложением,
///     иконка Linux-ярлыка <c>icon.png</c> и версия в «Параметры → Приложения» остаются
///     от прежней сборки по-настоящему, а не из-за кеша.
///
/// Здесь собрано все, что надо сделать после замены бинаря. Ничего не создается заново:
/// обновляется только то, что уже есть на месте. Обновление не должно возвращать ярлык,
/// который человек удалил, и не должно записывать в список приложений портативную копию.
/// </summary>
public static partial class InstallService
{
    /// <summary>
    /// Обновить окружение установленной копии после замены бинаря: установщик рядом,
    /// ярлыки, запись в списке приложений, кеш иконок оболочки.
    ///
    /// Вызывается из режима <c>--apply-update</c>, то есть кодом уже НОВОЙ сборки: правка
    /// начинает работать на том же обновлении, которым и приезжает, — чинить установленные
    /// копии отдельным выпуском не придется.
    /// </summary>
    /// <param name="installedPath">Замененный артефакт: файл (Win/Linux) или <c>.app</c>-бандл (macOS).</param>
    /// <param name="sourceArtifact">Артефакт-источник, из которого шла замена (файл или бандл).</param>
    public static void RefreshAfterUpdate(string installedPath, string sourceArtifact)
    {
        var installDir = Path.GetDirectoryName(installedPath);
        if (string.IsNullOrEmpty(installDir)) return;

        // Каждый шаг сам по себе: не легло одно — остальное все равно надо сделать.
        // Обновление уже применилось, и падать после успешной замены бинаря нельзя.
        Try(() => RefreshSetupBesideApp(installDir, sourceArtifact));
        Try(() => RegisterUninstall(installDir, onlyIfRegistered: true));

        if (OperatingSystem.IsWindows())
        {
            Try(() => RefreshShortcutsWindows(installDir));
            Try(() => RefreshShellIconCacheWindows(Path.Combine(installDir, AppExeName)));
        }
        else if (OperatingSystem.IsLinux())
        {
            Try(() => RefreshShortcutsLinux(installDir));
        }
        else if (IsMac)
        {
            // Launch Services держит иконку бандла у себя; после подмены бандла его надо
            // перерегистрировать, иначе Dock и Finder рисуют прежнюю картинку.
            Try(() => RegisterLaunchServices(installedPath));
        }
    }

    private static void Try(Action step)
    {
        try { step(); }
        catch { /* обновление уже применилось — окружение чиним best-effort */ }
    }

    // ── Установщик рядом с приложением ────────────────────────────────────────

    /// <summary>
    /// Обновить <c>SmartBase-Setup[.exe]</c> в папке установки. При установке его кладет
    /// <c>CopySetupBesideApp</c>, а обновление до сих пор проходило мимо — и рядом с новой
    /// программой лежал установщик прежней сборки: со старой иконкой (уже по-настоящему,
    /// не из кеша) и старой версией, которую читает <c>Discovery</c> и по которой окно
    /// установщика решает, писать «Обновить» или «Переустановить».
    ///
    /// Только если файл уже есть: его отсутствие означает либо установку до 1.0.5, либо
    /// портативную копию — класть туда 157 МБ без спроса неправильно.
    ///
    /// Публичный отдельно от <see cref="RefreshAfterUpdate"/> ради теста: сам RefreshAfterUpdate
    /// трогает систему (ярлыки, реестр, кеш иконок), и вызов из теста испортил бы машину.
    /// </summary>
    public static void RefreshSetupBesideApp(string installDir, string sourceArtifact)
    {
        var target = SetupInstalledPath(installDir);
        if (PathsEqual(sourceArtifact, target)) return;

        if (IsMac)
        {
            if (!Directory.Exists(target)) return;
            DeleteRecursive(target);
            CopyTreeSync(sourceArtifact, target);
            MakeExecutable(MacInnerBinary(target));
            ClearQuarantine(target);
            return;
        }

        if (!File.Exists(target)) return;
        File.Copy(sourceArtifact, target, overwrite: true);
        MakeExecutable(target);
    }

    // ── Windows: ярлыки ───────────────────────────────────────────────────────

    /// <summary>
    /// Перезаписать ярлыки, которые ведут на наш <c>.exe</c>. Содержимое ярлыка при этом не
    /// меняется — важна сама перезапись: у файла обновляется дата, и оболочка перечитывает
    /// значок цели вместо того, чтобы отдать его из кеша.
    ///
    /// Ищем не по своему списку, а обходом: кроме ярлыков установщика в Пуске, на рабочем
    /// столе и в папке установки, человек мог сделать свои копии, а закрепленный на панели
    /// задач ярлык Windows создает сама — отдельным файлом в
    /// <c>…\Quick Launch\User Pinned\TaskBar</c>, о котором установщик ничего не знает.
    /// Трогаем строго те, у которых <c>TargetPath</c> — наш бинарь.
    /// </summary>
    private static void RefreshShortcutsWindows(string installDir)
    {
        var exe = Path.Combine(installDir, AppExeName);
        if (!File.Exists(exe)) return;

        var roots = new[]
        {
            DesktopDir,
            Environment.GetFolderPath(Environment.SpecialFolder.Programs),   // Пуск целиком, а не только своя папка
            installDir,
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                         "Microsoft", "Internet Explorer", "Quick Launch", "User Pinned", "TaskBar"),
        };

        // Один запуск PowerShell на весь обход: COM-объект WScript.Shell создается дорого,
        // а ярлыков может быть несколько десятков.
        var script =
            "$ErrorActionPreference='SilentlyContinue'; " +
            $"$exe = {PsQuote(exe)}; " +
            // Обе стороны сравнения приводим к полному имени. Ярлык хранит путь развернутым,
            // а нам он может прийти в коротком виде 8.3 (`C:\Users\RV4F61~1.BUR\…`) — тогда
            // строки не совпадут и обход молча ничего не найдет.
            "$i = Get-Item -LiteralPath $exe; if ($i) { $exe = $i.FullName } " +
            $"$roots = @({string.Join(", ", roots.Select(PsQuote))}); " +
            "$w = New-Object -ComObject WScript.Shell; " +
            "foreach ($r in $roots) { " +
                "if (-not (Test-Path -LiteralPath $r)) { continue } " +
                "Get-ChildItem -LiteralPath $r -Filter *.lnk -Recurse -Force | ForEach-Object { " +
                    "$s = $w.CreateShortcut($_.FullName); " +
                    "$t = $s.TargetPath; " +
                    "if ($t) { $ti = Get-Item -LiteralPath $t; if ($ti) { $t = $ti.FullName } } " +
                    // Явная ссылка на значок вместо «бери у цели»: так оболочка знает, откуда
                    // его перечитать, и не держится за то, что запомнила при создании ярлыка.
                    // Склейка, а не строка в кавычках: весь скрипт уходит внутрь -Command "…",
                    // и свои двойные кавычки разорвали бы аргумент.
                    "if ($t -eq $exe) { $s.IconLocation = ($exe + ',0'); $s.Save() } " +
                "} " +
            "}";
        // С ограничением по времени: на рабочем столе может лежать ярлык на недоступную сетевую
        // папку, и чтение его цели упирается в таймаут SMB. Обновление уже применилось —
        // подвесить его перед перезапуском приложения нельзя.
        RunHidden("powershell", $"-NoProfile -NonInteractive -Command \"{script}\"", wait: true, timeoutMs: 30_000);
    }

    // ── Windows: кеш иконок оболочки ──────────────────────────────────────────

    private const int  SHCNE_UPDATEITEM   = 0x00002000;
    private const int  SHCNE_UPDATEDIR    = 0x00001000;
    private const int  SHCNE_ASSOCCHANGED = 0x08000000;
    private const uint SHCNF_IDLIST       = 0x0000;
    private const uint SHCNF_PATHW        = 0x0005;
    private const uint SHCNF_FLUSH        = 0x1000;   // вернуться, когда оболочка примет событие

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern void SHChangeNotify(int eventId, uint flags, nint item1, nint item2);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, EntryPoint = "SHChangeNotify")]
    private static extern void SHChangeNotifyPath(int eventId, uint flags, string item1, nint item2);

    /// <summary>
    /// Попросить оболочку забыть запомненные значки. Перезаписи файла для этого мало:
    /// <c>.exe</c> меняет не Проводник, а чужой процесс, и до кеша иконок это не доходит.
    ///
    /// Два шага, разные по охвату: <c>SHChangeNotify</c> сообщает про конкретный файл и папку
    /// и разом сбрасывает значки во всех открытых окнах, а <c>ie4uinit</c> перестраивает сам
    /// кеш иконок пользователя — до него события не добираются. Ключ <c>-show</c> понимают
    /// Windows 10/11, <c>-ClearIconCache</c> — версии постарше; лишний вызов молча ничего
    /// не делает, поэтому зовем оба и не разбираем версию системы.
    ///
    /// Чего здесь намеренно нет — сноса <c>iconcache_*.db</c> с перезапуском
    /// <c>explorer.exe</c>: у человека закроются окна Проводника, а обновление программы
    /// такого права не дает. Это остается ручным средством, см. docs/developer/features/icons.md.
    /// </summary>
    private static void RefreshShellIconCacheWindows(string exe)
    {
        var dir = Path.GetDirectoryName(exe);

        SHChangeNotifyPath(SHCNE_UPDATEITEM, SHCNF_PATHW | SHCNF_FLUSH, exe, 0);
        if (!string.IsNullOrEmpty(dir))
            SHChangeNotifyPath(SHCNE_UPDATEDIR, SHCNF_PATHW | SHCNF_FLUSH, dir, 0);
        SHChangeNotify(SHCNE_ASSOCCHANGED, SHCNF_IDLIST | SHCNF_FLUSH, 0, 0);

        RunHidden("ie4uinit.exe", "-show",           wait: true, timeoutMs: 10_000);
        RunHidden("ie4uinit.exe", "-ClearIconCache", wait: true, timeoutMs: 10_000);
    }

    // ── Linux: ярлыки .desktop ────────────────────────────────────────────────

    /// <summary>
    /// На Linux иконка ярлыка — не ресурс внутри бинаря, а отдельный файл <c>icon.png</c>
    /// рядом с приложением, на который ссылаются уже созданные <c>.desktop</c>. Обновление
    /// бинаря его не трогало, и в меню оставалась картинка прежней сборки — здесь она
    /// перезаписывается из вшитого ресурса новой.
    ///
    /// Сами <c>.desktop</c> по содержанию не меняются, но их перезапись обновляет дату:
    /// среда рабочего стола перечитывает запись вместе с иконкой.
    /// </summary>
    private static void RefreshShortcutsLinux(string installDir)
    {
        var icon = Path.Combine(installDir, "icon.png");
        if (File.Exists(icon)) ExtractIcon(icon);

        var exe     = Path.Combine(installDir, AppExeName);
        var desktop = DesktopExecQuote(exe);

        RewriteIfExists(Path.Combine(AppsDir, $"{AppSettings.AppName}.desktop"),
                        AppSettings.AppName, desktop, icon);
        RewriteIfExists(Path.Combine(AppsDir, $"{AppSettings.AppName}-uninstall.desktop"),
                        $"Uninstall {AppSettings.AppName}", $"{desktop} --uninstall", icon);

        var onDesktop = Path.Combine(DesktopDir, $"{AppSettings.AppName}.desktop");
        if (File.Exists(onDesktop))
        {
            WriteDesktopEntry(onDesktop, AppSettings.AppName, desktop, icon);
            MakeExecutable(onDesktop);   // GNOME требует +x у запускаемого ярлыка на рабочем столе
        }

        RefreshDesktopDb();

        static void RewriteIfExists(string path, string name, string exec, string icon)
        {
            if (File.Exists(path)) WriteDesktopEntry(path, name, exec, icon);
        }
    }
}
