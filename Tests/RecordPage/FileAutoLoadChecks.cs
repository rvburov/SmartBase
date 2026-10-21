using System.Diagnostics;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;
using SmartBase.Tests.Benchmarks;

/// <summary>Настоящий dispatcher/ввод Avalonia во время создания и запросов каталога.
/// Десять миллионов имен синтетические: скорость реального сетевого диска не измеряется.</summary>
internal static class FileAutoLoadChecks
{
    public static int Run(string? files)
    {
        var input = new TextBox { Text = "", Width = 280 };
        var status = new TextBlock();
        var window = new Window
        {
            Width = 400, Height = 140, Title = "Проверка большого каталога",
            Content = new StackPanel { Children = { input, status } },
        };
        long timerTicks = 0, inputs = 0;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(20) };
        timer.Tick += (_, _) => { timerTicks++; status.Text = timerTicks.ToString(); };
        var clock = Stopwatch.StartNew();
        double previous = 0, maximumGap = 0;
        window.Show();
        window.Activate();
        Dispatcher.UIThread.RunJobs();
        input.Focus();
        timer.Start();
        try
        {
            var result = FileAutoBench.Run(["--files", files ?? "10000000"], () =>
            {
                var now = clock.Elapsed.TotalMilliseconds;
                if (inputs > 0) maximumGap = Math.Max(maximumGap, now - previous);
                previous = now;
                Dispatcher.UIThread.RunJobs();
                input.Text = "";
                input.Focus();
                window.KeyTextInput("x");
                Dispatcher.UIThread.RunJobs();
                if (input.Text != "x") throw new InvalidOperationException("Интерфейс не принял ввод во время поиска.");
                inputs++;
            });
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                mode = "Avalonia.Headless", timerTicks, acceptedInputs = inputs,
                maximumUiPumpGapMs = maximumGap, elapsedMs = clock.Elapsed.TotalMilliseconds,
            }));
            return result == 0 && timerTicks > 0 && inputs > 0 ? 0 : 1;
        }
        finally { timer.Stop(); window.Close(); Dispatcher.UIThread.RunJobs(); }
    }
}
