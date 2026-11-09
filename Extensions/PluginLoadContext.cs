using System.Reflection;
using System.Runtime.Loader;

namespace SmartBase.Extensions;

/// <summary>
/// Контекст загрузки сборок одного расширения-плагина. Все, что плагин принес в своей папке —
/// его код, библиотеки (OCR, OpenCV…), их нативные части, — грузится отсюда и живет отдельно от
/// программы: у программы и плагина могут быть разные версии одной библиотеки, и они не мешают
/// друг другу. Чего в папке плагина нет (сама программа, Avalonia, библиотеки .NET), берется у
/// программы — так плагин видит <see cref="IExtension"/>, окна и стили приложения.
///
/// Пути к файлам плагина знает <see cref="AssemblyDependencyResolver"/> по его
/// <c>.deps.json</c>, в том числе нативные библиотеки нужной ОС (<c>runtimes/&lt;rid&gt;/native</c>).
/// </summary>
internal sealed class PluginLoadContext(string mainAssemblyPath, string name)
    : AssemblyLoadContext(name, isCollectible: false)
{
    private readonly AssemblyDependencyResolver _resolver = new(mainAssemblyPath);

    // Сборки, которые плагин обязан брать у программы, даже если по ошибке принес свою копию:
    // через них идет контракт (IExtension, окна, стили). Своя копия дала бы «тот же» тип из
    // другой сборки — программа не увидела бы в плагине расширения. Программа — ровно сборка
    // SmartBase (у плагинов имена SmartBase.Extensions.* — это их собственные сборки);
    // Avalonia и ее графика — целыми семействами.
    private static readonly string[] HostNames = ["SmartBase"];
    private static readonly string[] HostPrefixes = ["Avalonia", "SkiaSharp", "HarfBuzzSharp", "MicroCom", "Tmds.DBus"];

    public static bool IsHostAssembly(string? name) =>
        name is not null &&
        (HostNames.Any(n => name.Equals(n, StringComparison.OrdinalIgnoreCase)) ||
         HostPrefixes.Any(p => name.Equals(p, StringComparison.OrdinalIgnoreCase) ||
                               name.StartsWith(p + ".", StringComparison.OrdinalIgnoreCase)));

    protected override Assembly? Load(AssemblyName assemblyName)
    {
        if (IsHostAssembly(assemblyName.Name)) return null;
        var path = _resolver.ResolveAssemblyToPath(assemblyName);
        return path is null ? null : LoadFromAssemblyPath(path);
    }

    protected override IntPtr LoadUnmanagedDll(string unmanagedDllName)
    {
        var path = _resolver.ResolveUnmanagedDllToPath(unmanagedDllName);
        return path is null ? IntPtr.Zero : LoadUnmanagedDllFromPath(path);
    }
}
