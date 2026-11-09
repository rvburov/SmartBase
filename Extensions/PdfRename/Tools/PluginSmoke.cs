// Проверка установленного плагина в ОТДЕЛЬНОМ процессе: плагин грузится в свой контекст
// загрузки (как в программе — Extensions/PluginLoadContext.cs), и его OCR-компоненты
// поднимаются по-настоящему. Отдельный процесс нужен потому, что нативный Paddle Inference
// нельзя загрузить в один процесс дважды (вторая копия роняет процесс на регистрации protobuf),
// а тестовый процесс свою копию уже держит.
//
//   dotnet run --file Extensions/PdfRename/Tools/PluginSmoke.cs -- <папка плагина>
//
// Код возврата 0 — OCR готов; 1 — есть проблемы (печатаются); 2 — плагин не загрузился.
using System.Reflection;
using System.Runtime.Loader;

if (args.Length != 1) { Console.Error.WriteLine("Нужна папка плагина"); return 2; }
var main = Path.Combine(Path.GetFullPath(args[0]), "SmartBase.Extensions.PdfRename.dll");
if (!File.Exists(main)) { Console.Error.WriteLine($"Нет {main}"); return 2; }

var context = new PluginContext(main);
var assembly = context.LoadFromAssemblyPath(main);
var components = assembly.GetType("SmartBase.Extensions.PdfRename.Ocr.OcrComponents");
if (components is null) { Console.Error.WriteLine("В сборке нет OcrComponents"); return 2; }

var problems = (IReadOnlyList<string>)components.GetMethod("CheckReady")!.Invoke(null, null)!;
foreach (var p in problems) Console.WriteLine("Проблема: " + p);
if (problems.Count > 0) return 1;
Console.WriteLine("OCR готов: Tesseract и PaddleOCR поднялись в контексте плагина");
return 0;

sealed class PluginContext(string main) : AssemblyLoadContext("plugin-smoke", isCollectible: false)
{
    private readonly AssemblyDependencyResolver _resolver = new(main);

    protected override Assembly? Load(AssemblyName name)
    {
        var path = _resolver.ResolveAssemblyToPath(name);
        return path is null ? null : LoadFromAssemblyPath(path);
    }

    protected override IntPtr LoadUnmanagedDll(string name)
    {
        var path = _resolver.ResolveUnmanagedDllToPath(name);
        return path is null ? IntPtr.Zero : LoadUnmanagedDllFromPath(path);
    }
}
