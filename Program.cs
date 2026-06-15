using Avalonia;
using DataBase.Utils;

namespace DataBase;

internal sealed class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        AppSettings.EnsureCreated();
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
                  .UsePlatformDetect()
                  .WithInterFont()
                  .LogToTrace();
}
