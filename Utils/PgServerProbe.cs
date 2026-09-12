using System.Diagnostics;
using System.Net.Sockets;

namespace SmartBase.Utils;

/// <summary>Статус сервера PostgreSQL для окна-помощника установки.</summary>
public enum PgServerStatus
{
    /// <summary>Порт отвечает — сервер работает.</summary>
    Running,
    /// <summary>Порт молчит, но следы установки на этой машине найдены.</summary>
    InstalledStopped,
    /// <summary>Ни работающего сервера, ни следов установки.</summary>
    NotFound,
}

/// <summary>
/// Проба сервера PostgreSQL для окна-помощника (PgServerHelpDialog). Работает без прав
/// администратора и без пароля: доступность — обычное TCP-подключение к порту, наличие
/// установки — поиск типовых каталогов/бинарников текущей ОС. Оба признака честно
/// best-effort: сервер на нестандартном порту или в docker-контейнере проба «не увидит»,
/// установка нестандартным способом не найдется — окно предупреждает об этом текстом.
/// Пробу соединения с паролем и русские тексты ошибок делает PgConnectDialog.
/// </summary>
public static class PgServerProbe
{
    /// <summary>Порт отвечает на TCP-подключение? Не различает PostgreSQL и другой сервис
    /// на том же порту — для помощника этого достаточно (занятый порт сам по себе сигнал).</summary>
    public static async Task<bool> ProbeTcpAsync(string host, int port, TimeSpan timeout)
    {
        try
        {
            // Единственный сокет вне шлюза, и запрет снят ровно на эти три строки. PostgreSQL —
            // СВОЯ база приложения, а не чужая программа: белому списку организации, квотам и
            // журналу обмена здесь нечего делать. А проба порта нужна, чтобы отличить
            // «сервер выключен» от «пароль не тот», — без нее окно-помощник бесполезно.
#pragma warning disable RS0030
            using var client = new TcpClient();
            using var cts    = new CancellationTokenSource(timeout);
            await client.ConnectAsync(host, port, cts.Token);
#pragma warning restore RS0030
            return true;
        }
        catch
        {
            return false;   // отказ, таймаут, кривой адрес — для пробы все едино «не отвечает»
        }
    }

    /// <summary>Есть ли на этой машине следы установленного PostgreSQL (каталоги типовых
    /// установщиков, бинарник psql). Имеет смысл только для localhost.</summary>
    public static bool DetectInstallation()
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                // Установщик EDB/winget ставит в «Program Files\PostgreSQL\<версия>».
                var pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
                return pf.Length > 0 && Directory.Exists(Path.Combine(pf, "PostgreSQL"));
            }
            if (OperatingSystem.IsMacOS())
            {
                return HasDirLike("/opt/homebrew/opt", "postgresql*")
                    || HasDirLike("/usr/local/opt", "postgresql*")
                    || Directory.Exists("/Applications/Postgres.app")
                    || Directory.Exists("/Library/PostgreSQL");   // установщик EDB
            }
            if (OperatingSystem.IsLinux())
            {
                if (Directory.Exists("/usr/lib/postgresql")        // deb-пакеты
                    || Directory.Exists("/usr/pgsql-16")           // rpm-пакеты PGDG
                    || Directory.Exists("/var/lib/pgsql"))         // rpm data dir
                    return true;
                return RunSilent("which", "psql") == 0;
            }
        }
        catch
        {
            // Проба — украшение, а не функциональность: любой сбой = «не нашли».
        }
        return false;
    }

    /// <summary>Работает ли служба postgresql (только Linux/systemd; иначе false).
    /// Отдельно от TCP-пробы: служба может слушать нестандартный порт.</summary>
    public static bool IsSystemdServiceActive()
    {
        if (!OperatingSystem.IsLinux()) return false;
        try { return RunSilent("systemctl", "is-active --quiet postgresql") == 0; }
        catch { return false; }
    }

    /// <summary>Страница загрузки PostgreSQL для текущей ОС.</summary>
    public static string DownloadUrl =>
        OperatingSystem.IsWindows() ? "https://www.postgresql.org/download/windows/"
        : OperatingSystem.IsMacOS() ? "https://www.postgresql.org/download/macosx/"
        : "https://www.postgresql.org/download/linux/";

    // Есть ли в dir подкаталог по маске (например postgresql@16 в /opt/homebrew/opt).
    private static bool HasDirLike(string dir, string pattern)
        => Directory.Exists(dir) && Directory.EnumerateDirectories(dir, pattern).Any();

    // Тихий запуск утилиты: код возврата или -1, вывод глушится. Короткий таймаут —
    // проба не должна завешивать UI даже на сломанной системе.
    private static int RunSilent(string file, string args)
    {
        using var p = Process.Start(new ProcessStartInfo(file, args)
        {
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            UseShellExecute        = false,
        });
        if (p is null) return -1;
        return p.WaitForExit(3000) ? p.ExitCode : -1;
    }
}
