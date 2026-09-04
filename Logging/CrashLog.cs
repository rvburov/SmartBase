using SmartBase.Utils;

namespace SmartBase.Logging;

/// <summary>
/// Независимый резерв диагностики — текстовый <c>crash.log</c> рядом с данными приложения.
/// Работает без Serilog и до него: сбой на старте, падение с фонового потока, когда буфер
/// приемника еще не сброшен, и отказ самого приемника (<c>logs.db</c> заблокирован или
/// поврежден — тогда Serilog молча роняет записи, и журнал выглядит пустым ровно тогда,
/// когда нужен; его внутренние ошибки <see cref="AppLog"/> направляет сюда через SelfLog).
/// Дозапись, ротации нет; время локальное (приемник Serilog пишет UTC — учитывать при сверке).
/// </summary>
public static class CrashLog
{
    public static string Path => System.IO.Path.Combine(AppSettings.AppDataDir, "crash.log");

    /// <summary>Дописать запись. Best-effort: сбой записи не должен маскировать исходную ошибку.</summary>
    public static void Append(string source, string text)
    {
        try
        {
            Directory.CreateDirectory(AppSettings.AppDataDir);
            File.AppendAllText(Path,
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {source} v{AppSettings.AppVersion}{Environment.NewLine}" +
                $"{text}{Environment.NewLine}{Environment.NewLine}");
        }
        catch { /* некуда писать — значит, некуда */ }
    }
}
