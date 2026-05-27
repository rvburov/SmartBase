using Avalonia;
using MyPlanner.Utils;

namespace MyPlanner;

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
