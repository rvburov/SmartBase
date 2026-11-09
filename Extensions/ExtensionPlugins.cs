using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using SmartBase.Utils;

namespace SmartBase.Extensions;

/// <summary>
/// Описание расширения-плагина — файл <c>extension.json</c> в корне его папки (и архива).
/// </summary>
/// <param name="Id">Id расширения; совпадает с <see cref="IExtension.Id"/> и с именем папки.</param>
/// <param name="Name">Имя для экрана настроек — до загрузки самого расширения.</param>
/// <param name="Version">Версия плагина — для показа.</param>
/// <param name="Assembly">Главная сборка плагина (в ней класс, реализующий <see cref="IExtension"/>).</param>
/// <param name="Contract">Версия контракта программы, под которую собран плагин
/// (<see cref="ExtensionPlugins.ContractVersion"/>).</param>
/// <param name="Platforms">Платформы сборки: «win-x64», «linux-x64», «osx-arm64»… Нативные
/// библиотеки внутри у каждой платформы свои, поэтому архив — на одну платформу.</param>
public sealed record PluginManifest(
    string Id, string Name, string Version, string Assembly, int Contract, string[] Platforms);

/// <summary>Состояние установленного плагина — для экрана «Настройки → Расширения».</summary>
public enum PluginState
{
    /// <summary>Загружен и работает.</summary>
    Loaded,
    /// <summary>Не загрузился — причина в <see cref="InstalledPlugin.Problem"/>.</summary>
    Failed,
    /// <summary>Будет удален при следующем запуске программы.</summary>
    PendingRemoval,
    /// <summary>Установлен или обновлен, заработает после перезапуска.</summary>
    PendingInstall,
}

/// <summary>Установленный плагин: что о нем известно и в каком он состоянии.</summary>
public sealed record InstalledPlugin(
    string Id, string Name, string Version, string Folder, PluginState State, string? Problem);

/// <summary>Итог установки из файла: <see cref="Ok"/> или причина отказа.</summary>
public sealed record PluginInstallResult(bool Ok, string Message, PluginManifest? Manifest);

/// <summary>
/// Расширения-плагины: расширение и ВСЕ его библиотеки, нативные части и модели лежат в одной
/// папке вне программы — <see cref="Root"/>/&lt;Id&gt;/. Программа подхватывает папку при
/// запуске; удалили папку — ушли и расширение, и его библиотеки, а программа и остальные
/// расширения работают как прежде. Так тяжелые зависимости одного расширения (OCR, OpenCV —
/// сотни мегабайт) не становятся обязательными для всей программы.
///
/// Установка и удаление — с экрана «Настройки → Расширения»: архив расширения (.zip) с
/// <c>extension.json</c> внутри. Загруженные сборки не выгружаются, а файлы загруженного
/// расширения на Windows заняты, поэтому установка, обновление и удаление выполняются при
/// следующем запуске программы, ДО загрузки расширений: архив распаковывается в
/// <c>.pending/&lt;Id&gt;</c>, удаление отмечается файлом <c>.remove/&lt;Id&gt;</c>. Пользовательских
/// файлов (документов, шаблонов) плагин в своей папке не держит — удаляется только она.
///
/// Плагин зависит только от контракта программы (<see cref="IExtension"/>, окна и стили):
/// сборки программы он берет у нее, все прочее — из своей папки (<see cref="PluginLoadContext"/>).
/// </summary>
public static partial class ExtensionPlugins
{
    /// <summary>Версия контракта «программа ↔ плагин». Растет, когда меняется то, на что
    /// опираются плагины (<see cref="IExtension"/> и окружение); плагин с другой версией не
    /// грузится — с понятной причиной на экране настроек, а не падением программы.</summary>
    public const int ContractVersion = 1;

    public const string ManifestFile = "extension.json";
    private const string PendingDir  = ".pending";
    private const string RemoveDir   = ".remove";

    private static readonly Lock Gate = new();
    private static string? _rootOverride;
    private static List<IExtension>? _loaded;
    private static List<InstalledPlugin> _installed = [];
    private static readonly HashSet<string> LoadedIds = new(StringComparer.Ordinal);

    [GeneratedRegex("^[a-z0-9][a-z0-9-]{0,63}$")]
    private static partial Regex IdPattern();

    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    /// <summary>
    /// Папка расширений-плагинов. Не в роуминговом профиле (там лежат базы и настройки): плагин
    /// с моделями весит сотни мегабайт, и тянуть их по сети при каждом входе незачем. Отладочный
    /// запуск держит их рядом с данными отладки (<c>.localdata/extensions</c>).
    /// </summary>
    public static string Root => _rootOverride ?? DefaultRoot();

    private static string DefaultRoot()
    {
#if DEBUG
        return Path.Combine(AppSettings.AppDataDir, "extensions");
#else
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrEmpty(local)) local = AppSettings.AppDataDir;
        return Path.Combine(local, AppSettings.AppName, "extensions");
#endif
    }

    /// <summary>Платформа этой программы в записи манифеста: «win-x64», «linux-x64», «osx-arm64».</summary>
    public static string CurrentPlatform
    {
        get
        {
            var os = OperatingSystem.IsWindows() ? "win" : OperatingSystem.IsMacOS() ? "osx" : "linux";
            var arch = RuntimeInformation.OSArchitecture switch
            {
                Architecture.X64   => "x64",
                Architecture.Arm64 => "arm64",
                Architecture.X86   => "x86",
                var other          => other.ToString().ToLowerInvariant(),
            };
            return $"{os}-{arch}";
        }
    }

    /// <summary>Загруженные плагины-расширения (после <see cref="Initialize"/>).</summary>
    public static IReadOnlyList<IExtension> Loaded
    {
        get { Initialize(); lock (Gate) return _loaded!; }
    }

    /// <summary>Все установленные плагины с состоянием — для экрана настроек.</summary>
    public static IReadOnlyList<InstalledPlugin> Installed
    {
        get { Initialize(); lock (Gate) return [.. _installed]; }
    }

    /// <summary>
    /// Применить отложенные установки и удаления и загрузить плагины. Вызывается один раз при
    /// запуске (повторный вызов ничего не делает). Ни один сбой плагина не роняет программу:
    /// причина остается в <see cref="Installed"/>.
    /// </summary>
    public static void Initialize()
    {
        lock (Gate)
        {
            if (_loaded is not null) return;
            _loaded = [];
            _installed = [];
            LoadedIds.Clear();

            var root = Root;
            try { Directory.CreateDirectory(root); }
            catch { return; }   // нет доступа к папке — плагинов просто нет

            ApplyPending(root);

            foreach (var dir in Directory.EnumerateDirectories(root).Order(StringComparer.Ordinal))
            {
                var folder = Path.GetFileName(dir);
                if (folder.StartsWith('.')) continue;
                LoadOne(dir, folder);
            }

            // То, что ждет установки, видно на экране сразу (например, если отложенную
            // установку не дала применить занятая папка).
            foreach (var pending in EnumerateManifests(Path.Combine(root, PendingDir)))
                if (!_installed.Any(p => p.Id == pending.Manifest.Id))
                    _installed.Add(new InstalledPlugin(pending.Manifest.Id, pending.Manifest.Name,
                        pending.Manifest.Version, pending.Dir, PluginState.PendingInstall, null));
        }
    }

    /// <summary>Только для тестов: другая папка плагинов и сброс загруженного (сборки, уже
    /// загруженные в процесс, остаются — выгружать их нельзя).</summary>
    internal static void UseRoot(string? root)
    {
        lock (Gate)
        {
            _rootOverride = root;
            _loaded = null;
            _installed = [];
            LoadedIds.Clear();
        }
    }

    // ── Загрузка ──────────────────────────────────────────────────────────────

    private static void LoadOne(string dir, string folder)
    {
        PluginManifest? manifest = null;
        try
        {
            manifest = ReadManifest(Path.Combine(dir, ManifestFile));
            var problem = Validate(manifest, folder);
            if (problem is not null) { Fail(manifest, dir, folder, problem); return; }

            if (ExtensionRegistry.BuiltInIds.Contains(manifest.Id) || LoadedIds.Contains(manifest.Id))
            {
                Fail(manifest, dir, folder, "расширение с таким Id уже есть в программе");
                return;
            }

            var assemblyPath = Path.Combine(dir, manifest.Assembly);
            if (!File.Exists(assemblyPath))
            {
                Fail(manifest, dir, folder, $"нет файла {manifest.Assembly}");
                return;
            }

            var context  = new PluginLoadContext(assemblyPath, $"extension:{manifest.Id}");
            var assembly = context.LoadFromAssemblyPath(assemblyPath);
            var types    = SafeTypes(assembly)
                .Where(t => typeof(IExtension).IsAssignableFrom(t) && t is { IsAbstract: false, IsInterface: false })
                .Where(t => t.GetConstructor(Type.EmptyTypes) is not null)
                .ToList();
            if (types.Count != 1)
            {
                Fail(manifest, dir, folder, types.Count == 0
                    ? "в сборке нет класса расширения"
                    : "в сборке несколько классов расширения");
                return;
            }

            var extension = (IExtension)Activator.CreateInstance(types[0])!;
            if (extension.Id != manifest.Id)
            {
                Fail(manifest, dir, folder, $"Id в сборке «{extension.Id}» не совпадает с extension.json");
                return;
            }

            _loaded!.Add(extension);
            LoadedIds.Add(extension.Id);
            _installed.Add(new InstalledPlugin(manifest.Id, extension.Name, manifest.Version, dir,
                Pending(manifest.Id, RemoveDir) ? PluginState.PendingRemoval : PluginState.Loaded, null));
        }
        catch (Exception ex)
        {
            Fail(manifest, dir, folder, Explain(ex));
        }
    }

    private static void Fail(PluginManifest? manifest, string dir, string folder, string problem)
    {
        var id = manifest?.Id ?? folder;
        _installed.Add(new InstalledPlugin(id, manifest?.Name ?? folder, manifest?.Version ?? "", dir,
            Pending(id, RemoveDir) ? PluginState.PendingRemoval : PluginState.Failed, problem));
        Serilog.Log.Warning("Расширение-плагин «{Folder:l}» не загружено: {Problem:l}", folder, problem);
    }

    private static IEnumerable<Type> SafeTypes(Assembly assembly)
    {
        try { return assembly.GetExportedTypes(); }
        catch (ReflectionTypeLoadException ex) { return ex.Types.OfType<Type>(); }
    }

    private static string Explain(Exception ex) => ex switch
    {
        JsonException                       => "extension.json не читается",
        BadImageFormatException             => "сборка повреждена или собрана не под эту платформу",
        FileNotFoundException f             => $"не найдена библиотека {f.FileName}",
        TypeLoadException or MissingMethodException or MissingMemberException
                                            => "собрано под другую версию программы",
        TargetInvocationException { InnerException: { } inner } => Explain(inner),
        _                                   => ex.Message,
    };

    private static PluginManifest ReadManifest(string path)
    {
        using var stream = File.OpenRead(path);
        return ReadManifest(stream);
    }

    private static PluginManifest ReadManifest(Stream stream) =>
        JsonSerializer.Deserialize<PluginManifest>(stream, Json)
        ?? throw new JsonException("пустой extension.json");

    /// <summary>Проверка манифеста; null — годен.</summary>
    private static string? Validate(PluginManifest manifest, string? folder)
    {
        if (string.IsNullOrWhiteSpace(manifest.Id) || !IdPattern().IsMatch(manifest.Id))
            return "в extension.json неверный Id (латиница в нижнем регистре, цифры и дефис)";
        if (folder is not null && folder != manifest.Id)
            return $"папка называется «{folder}», а Id расширения — «{manifest.Id}»";
        if (string.IsNullOrWhiteSpace(manifest.Assembly) || manifest.Assembly.Contains("..") ||
            Path.IsPathRooted(manifest.Assembly))
            return "в extension.json неверное имя сборки";
        if (manifest.Contract != ContractVersion)
            return $"собрано под другую версию программы (контракт {manifest.Contract}, нужен {ContractVersion})";
        if (manifest.Platforms is null || !manifest.Platforms.Contains(CurrentPlatform, StringComparer.OrdinalIgnoreCase))
            return $"собрано для {string.Join(", ", manifest.Platforms ?? [])}, а эта программа — {CurrentPlatform}";
        return null;
    }

    // ── Отложенные установка и удаление ───────────────────────────────────────

    private static bool Pending(string id, string kind) =>
        File.Exists(Path.Combine(Root, kind, id)) || Directory.Exists(Path.Combine(Root, kind, id));

    // До загрузки: файлы плагина еще никем не заняты.
    private static void ApplyPending(string root)
    {
        var remove = Path.Combine(root, RemoveDir);
        if (Directory.Exists(remove))
            foreach (var marker in Directory.EnumerateFiles(remove))
            {
                var id = Path.GetFileName(marker);
                if (!IdPattern().IsMatch(id)) { TryDelete(marker); continue; }
                var target = Path.Combine(root, id);
                try
                {
                    if (Directory.Exists(target)) Directory.Delete(target, recursive: true);
                    File.Delete(marker);
                }
                catch (Exception ex)
                {
                    Serilog.Log.Warning(ex, "Расширение-плагин «{Id:l}» не удалено, повтор при следующем запуске", id);
                }
            }

        var pending = Path.Combine(root, PendingDir);
        if (!Directory.Exists(pending)) return;
        foreach (var dir in Directory.EnumerateDirectories(pending))
        {
            var id = Path.GetFileName(dir);
            if (!IdPattern().IsMatch(id)) continue;
            var target = Path.Combine(root, id);
            try
            {
                if (Directory.Exists(target)) Directory.Delete(target, recursive: true);
                Directory.Move(dir, target);
            }
            catch (Exception ex)
            {
                Serilog.Log.Warning(ex, "Расширение-плагин «{Id:l}» не установлено, повтор при следующем запуске", id);
            }
        }
    }

    private static IEnumerable<(PluginManifest Manifest, string Dir)> EnumerateManifests(string parent)
    {
        if (!Directory.Exists(parent)) yield break;
        foreach (var dir in Directory.EnumerateDirectories(parent))
        {
            PluginManifest? manifest = null;
            try { manifest = ReadManifest(Path.Combine(dir, ManifestFile)); }
            catch { /* недоделанная распаковка — на экран не выводим */ }
            if (manifest is not null) yield return (manifest, dir);
        }
    }

    /// <summary>
    /// Установить или обновить плагин из архива. Архив проверяется (манифест, платформа,
    /// версия контракта, пути внутри) и распаковывается в <c>.pending</c>; расширение появится
    /// после перезапуска программы. Повторная установка того же Id — обновление.
    /// </summary>
    public static PluginInstallResult Install(string zipPath)
    {
        try
        {
            using var zip = ZipFile.OpenRead(zipPath);
            var manifestEntry = zip.Entries.FirstOrDefault(e => e.FullName == ManifestFile)
                ?? zip.Entries.FirstOrDefault(e => e.Name == ManifestFile &&
                                                   e.FullName.Count(ch => ch == '/') == 1);
            if (manifestEntry is null)
                return new(false, $"В архиве нет файла {ManifestFile} — это не архив расширения.", null);

            PluginManifest manifest;
            using (var stream = manifestEntry.Open()) manifest = ReadManifest(stream);
            var problem = Validate(manifest, folder: null);
            if (problem is not null) return new(false, $"Расширение не подходит: {problem}.", manifest);
            if (ExtensionRegistry.BuiltInIds.Contains(manifest.Id))
                return new(false, $"Расширение «{manifest.Id}» уже встроено в программу.", manifest);

            var prefix = manifestEntry.FullName[..^ManifestFile.Length];   // "" или "папка/"
            if (zip.Entries.All(e => e.FullName != prefix + manifest.Assembly))
                return new(false, $"В архиве нет сборки {manifest.Assembly}.", manifest);

            var root    = Root;
            var staging = Path.Combine(root, PendingDir, $"{manifest.Id}.{Guid.NewGuid():N}.tmp");
            var target  = Path.Combine(root, PendingDir, manifest.Id);
            Directory.CreateDirectory(staging);
            try
            {
                var full = Path.GetFullPath(staging) + Path.DirectorySeparatorChar;
                foreach (var entry in zip.Entries)
                {
                    if (!entry.FullName.StartsWith(prefix, StringComparison.Ordinal)) continue;
                    var relative = entry.FullName[prefix.Length..];
                    if (relative.Length == 0) continue;
                    var destination = Path.GetFullPath(Path.Combine(staging, relative));
                    if (!destination.StartsWith(full, StringComparison.Ordinal))
                        throw new InvalidDataException($"путь вне папки расширения: {entry.FullName}");
                    if (entry.FullName.EndsWith('/')) { Directory.CreateDirectory(destination); continue; }
                    Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                    entry.ExtractToFile(destination, overwrite: true);
                }

                if (Directory.Exists(target)) Directory.Delete(target, recursive: true);
                Directory.Move(staging, target);
            }
            catch
            {
                TryDeleteDir(staging);
                throw;
            }

            // Переустановка снимает отметку об удалении.
            TryDelete(Path.Combine(root, RemoveDir, manifest.Id));
            lock (Gate)
            {
                _installed.RemoveAll(p => p.Id == manifest.Id && p.State != PluginState.Loaded);
                var loaded = _installed.FindIndex(p => p.Id == manifest.Id);
                var entry  = new InstalledPlugin(manifest.Id, manifest.Name, manifest.Version, target,
                                                 PluginState.PendingInstall, null);
                if (loaded >= 0) _installed[loaded] = entry; else _installed.Add(entry);
            }
            return new(true, $"Расширение «{manifest.Name}» {manifest.Version} будет установлено после перезапуска программы.", manifest);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or JsonException)
        {
            return new(false, $"Не удалось установить расширение: {ex.Message}", null);
        }
    }

    /// <summary>Отметить плагин к удалению: папка уйдет при следующем запуске программы,
    /// вместе с его библиотеками и моделями. Документы и шаблоны пользователя не трогаются —
    /// они в папке плагина не лежат.</summary>
    public static void ScheduleRemoval(string id)
    {
        if (!IdPattern().IsMatch(id)) throw new ArgumentException("неверный Id расширения", nameof(id));
        var root = Root;
        Directory.CreateDirectory(Path.Combine(root, RemoveDir));
        File.WriteAllText(Path.Combine(root, RemoveDir, id), "");
        TryDeleteDir(Path.Combine(root, PendingDir, id));   // недоустановленное — тоже прочь
        lock (Gate)
        {
            var index = _installed.FindIndex(p => p.Id == id);
            if (index >= 0) _installed[index] = _installed[index] with { State = PluginState.PendingRemoval };
        }
    }

    /// <summary>Передумали удалять — до перезапуска отметку можно снять.</summary>
    public static void CancelRemoval(string id)
    {
        if (!IdPattern().IsMatch(id)) return;
        TryDelete(Path.Combine(Root, RemoveDir, id));
        lock (Gate)
        {
            var index = _installed.FindIndex(p => p.Id == id);
            if (index >= 0 && _installed[index].State == PluginState.PendingRemoval)
                _installed[index] = _installed[index] with
                {
                    State = LoadedIds.Contains(id) ? PluginState.Loaded : PluginState.Failed,
                };
        }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* повтор при следующем запуске */ }
    }

    private static void TryDeleteDir(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); } catch { /* то же */ }
    }
}
