using System.IO.Compression;
using System.Runtime.Loader;
using System.Text.Json;
using SmartBase.Extensions;
using SmartBase.Extensions.PdfRename;
using SmartBase.Tests.Extensions.PdfRename.Samples;

namespace SmartBase.Tests.Extensions.PdfRename.Plugin;

/// <summary>
/// Группа ТЗ 16: расширение — отдельный плагин. Архив собранного плагина (сборка, OCR,
/// модели, нативные библиотеки) ставится через тот же механизм, что и кнопка
/// «Настройки → Расширения → Установить из файла…», загружается в свой контекст, а удаление
/// убирает только его папку: программа, встроенные расширения, документы и шаблоны остаются.
/// Последовательно: механизм плагинов — статическое состояние процесса.
/// </summary>
[Collection("Plugins")]
public sealed class PluginInstallTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "sb-plugins-" + Guid.NewGuid().ToString("N")[..8]);

    public void Dispose()
    {
        ExtensionPlugins.UseRoot(null);
        ExtensionRegistry.Reset();
        try { Directory.Delete(_root, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    /// <summary>Папка сборки плагина (artifacts/bin/SmartBase.Extensions.PdfRename/&lt;конфигурация&gt;).</summary>
    private static string PluginBuildDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "SmartBase.csproj"))) dir = dir.Parent;
        Assert.NotNull(dir);
#if DEBUG
        const string configuration = "debug";
#else
        const string configuration = "release";
#endif
        var build = Path.Combine(dir!.FullName, "artifacts", "bin", "SmartBase.Extensions.PdfRename", configuration);
        Assert.True(File.Exists(Path.Combine(build, "extension.json")), $"нет собранного плагина в {build}");
        return build;
    }

    private string Pack()
    {
        var zip = Path.Combine(_root, "pdf-rename.zip");
        Directory.CreateDirectory(_root);
        // Без сжатия: проверяется механизм, а не архиватор; сотни мегабайт библиотек сжимать долго.
        ZipFile.CreateFromDirectory(PluginBuildDir(), zip, CompressionLevel.NoCompression, includeBaseDirectory: false);
        return zip;
    }

    [Fact]
    public void Manifest_matches_the_extension()
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(PluginBuildDir(), "extension.json")));
        var root = doc.RootElement;
        Assert.Equal(PdfRenameExtension.ExtensionId, root.GetProperty("id").GetString());
        Assert.Equal(typeof(PdfRenameExtension).Assembly.GetName().Name + ".dll", root.GetProperty("assembly").GetString());
        Assert.Equal(ExtensionPlugins.ContractVersion, root.GetProperty("contract").GetInt32());
        Assert.Contains(ExtensionPlugins.CurrentPlatform, root.GetProperty("platforms").EnumerateArray().Select(p => p.GetString()));
    }

    [Fact]
    public void Extension_is_not_built_into_the_program()
    {
        Assert.DoesNotContain(PdfRenameExtension.ExtensionId, ExtensionRegistry.BuiltInIds);
        var host = typeof(IExtension).Assembly;
        Assert.Null(host.GetType("SmartBase.Extensions.PdfRename.PdfRenameExtension"));
        Assert.Null(host.GetType("SmartBase.Extensions.PdfRename.PdfRenamer"));
    }

    [OcrFact]
    public void Install_load_and_remove_leave_program_documents_and_templates_intact()
    {
        var zip = Pack();
        var plugins = Path.Combine(_root, "extensions");
        var documents = Path.Combine(_root, "Документы");
        Directory.CreateDirectory(Path.Combine(documents, "Шаблоны"));
        File.WriteAllText(Path.Combine(documents, "скан.pdf"), "%PDF-1.4 документ");
        File.WriteAllText(Path.Combine(documents, "Шаблоны", "Чертеж.pdf"), "%PDF-1.4 шаблон");
        var builtIn = ExtensionRegistry.BuiltIn.Select(e => e.Id).ToList();

        // 1. Программа запущена (плагинов нет), установка: архив проверен и разложен в
        //    .pending — заработает после перезапуска.
        ExtensionPlugins.UseRoot(plugins);
        ExtensionRegistry.Reset();
        Assert.Empty(ExtensionPlugins.Loaded);
        var result = ExtensionPlugins.Install(zip);
        Assert.True(result.Ok, result.Message);
        Assert.Contains(ExtensionPlugins.Installed, p => p.Id == "pdf-rename" && p.State == PluginState.PendingInstall);

        // 2. «Перезапуск»: плагин переезжает в свою папку и грузится в отдельный контекст.
        ExtensionPlugins.UseRoot(plugins);
        ExtensionRegistry.Reset();
        var loaded = Assert.Single(ExtensionPlugins.Loaded);
        Assert.Equal("pdf-rename", loaded.Id);
        Assert.IsAssignableFrom<IExtension>(loaded);
        Assert.IsNotType<PdfRenameExtension>(loaded);   // свой экземпляр сборки, не из теста
        var context = AssemblyLoadContext.GetLoadContext(loaded.GetType().Assembly);
        Assert.NotSame(AssemblyLoadContext.Default, context);
        var folder = Path.Combine(plugins, "pdf-rename");
        Assert.StartsWith(folder, loaded.GetType().Assembly.Location);
        Assert.True(Directory.Exists(Path.Combine(folder, "ocr", "paddle", "eslav_PP_OCRv5_mobile_rec")));
        Assert.Equal([.. builtIn, "pdf-rename"], ExtensionRegistry.All.Select(e => e.Id));
        ExtensionRegistry.EnsureUniqueIds();

        // Распознавание из папки установленного плагина — в отдельном процессе (две копии
        // нативного Paddle в одном процессе несовместимы, а тестовый процесс свою уже держит).
        if (OperatingSystem.IsLinux())
        {
            var (code, output) = RunSmoke(folder);
            Assert.True(code == 0, output);
        }

        // 3. Удаление: отметка сейчас, папка — при следующем запуске.
        ExtensionPlugins.ScheduleRemoval("pdf-rename");
        Assert.Contains(ExtensionPlugins.Installed, p => p.Id == "pdf-rename" && p.State == PluginState.PendingRemoval);
        if (OperatingSystem.IsWindows()) return;   // загруженные сборки на Windows заняты до выхода из программы

        ExtensionPlugins.UseRoot(plugins);
        ExtensionRegistry.Reset();
        Assert.Empty(ExtensionPlugins.Loaded);
        Assert.False(Directory.Exists(folder));
        Assert.Equal(builtIn, ExtensionRegistry.All.Select(e => e.Id));
        Assert.True(File.Exists(Path.Combine(documents, "скан.pdf")));
        Assert.True(File.Exists(Path.Combine(documents, "Шаблоны", "Чертеж.pdf")));
        Assert.True(File.Exists(zip));   // и сам архив пользователя не тронут
    }

    private static (int Code, string Output) RunSmoke(string pluginFolder)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "SmartBase.csproj"))) dir = dir.Parent;
        var script = Path.Combine(dir!.FullName, "Extensions", "PdfRename", "Tools", "PluginSmoke.cs");
        var start = new System.Diagnostics.ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = Path.GetTempPath(),
        };
        foreach (var a in new[] { "run", "--file", script, "--", pluginFolder }) start.ArgumentList.Add(a);
        using var process = System.Diagnostics.Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        Assert.True(process.WaitForExit(TimeSpan.FromMinutes(5)), "проверка плагина не уложилась в 5 минут");
        return (process.ExitCode, stdout.Result + stderr.Result);
    }

    [Fact]
    public void Wrong_archives_are_rejected_without_side_effects()
    {
        var plugins = Path.Combine(_root, "extensions");
        ExtensionPlugins.UseRoot(plugins);
        Directory.CreateDirectory(_root);

        string Zip(string name, params (string Path, string Content)[] entries)
        {
            var path = Path.Combine(_root, name);
            using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
            foreach (var (entryPath, content) in entries)
                using (var w = new StreamWriter(archive.CreateEntry(entryPath).Open())) w.Write(content);
            return path;
        }
        string Manifest(string id = "demo-ext", string platform = "", int contract = 1, string assembly = "Demo.dll") =>
            JsonSerializer.Serialize(new { id, name = "Демо", version = "1.0", assembly, contract,
                platforms = new[] { platform.Length == 0 ? ExtensionPlugins.CurrentPlatform : platform } });

        Assert.False(ExtensionPlugins.Install(Zip("empty.zip", ("readme.txt", "x"))).Ok);
        Assert.Contains("plan9-mips", ExtensionPlugins.Install(Zip("platform.zip", ("extension.json", Manifest(platform: "plan9-mips")), ("Demo.dll", "x"))).Message);
        Assert.False(ExtensionPlugins.Install(Zip("contract.zip", ("extension.json", Manifest(contract: 99)), ("Demo.dll", "x"))).Ok);
        Assert.False(ExtensionPlugins.Install(Zip("noasm.zip", ("extension.json", Manifest()))).Ok);
        Assert.False(ExtensionPlugins.Install(Zip("builtin.zip", ("extension.json", Manifest(id: ExtensionRegistry.BuiltIn[0].Id)), ("Demo.dll", "x"))).Ok);
        Assert.False(ExtensionPlugins.Install(Zip("slip.zip", ("extension.json", Manifest()), ("Demo.dll", "x"), ("../../evil.txt", "x"))).Ok);
        Assert.False(File.Exists(Path.Combine(_root, "evil.txt")));
        Assert.Empty(ExtensionPlugins.Installed);
    }
}

[CollectionDefinition("Plugins", DisableParallelization = true)]
public sealed class PluginsCollection;
