using Avalonia;
using Avalonia.Headless;
using SmartBase.Tests.TasksQuickAdd;
using SmartBase.Utils;

AppBuilder.Configure<SmartBase.App>()
    .UseSkia()
    .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
    .SetupWithoutStarting();
AppSettings.AttachStore(new MemorySettings());
CurrentUser.AttachStore(new MemorySettings());
FontScale.Init();
TaskSettings.Init();
var output = Path.Combine(Path.GetTempPath(), "smartbase-task-entry-" + Guid.NewGuid().ToString("N"));
var outputIndex = Array.IndexOf(args, "--out");
if (outputIndex >= 0 && outputIndex + 1 < args.Length) output = args[outputIndex + 1];
Directory.CreateDirectory(output);
var result = FocusChecks.Run(output);
result |= AssignerChecks.Run();
return result;

sealed class MemorySettings : ISettingsDatabase
{
    private readonly Dictionary<string, string> _values = [];
    public string? Get(string key) => _values.GetValueOrDefault(key);
    public void Set(string key, string value) => _values[key] = value;
    public void Remove(string key) => _values.Remove(key);
    public IReadOnlyList<string> KeysWithPrefix(string prefix) =>
        _values.Keys.Where(key => key.StartsWith(prefix, StringComparison.Ordinal)).ToArray();
}
