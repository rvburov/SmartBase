using System.Text;
using SmartBase.Utils;

namespace SmartBase.Core.Notes;

/// <summary>Снимок файла на момент чтения. По нему видно, изменил ли заметку кто-то снаружи, пока
/// она была открыта: перезаписывать чужую правку молча нельзя.</summary>
public readonly record struct NoteSnapshot(long WriteTicks, long Length);

/// <summary>Текст заметки плюс все, что нужно вернуть при записи байт в байт.</summary>
/// <param name="Bom">В файле была метка порядка байт — вернуть ее, иначе git покажет правку в первой строке.</param>
/// <param name="NewLine">Перевод строки, преобладавший в файле: TextBox хранит только '\n'.</param>
public sealed record NoteContent(string Text, bool Bom, string NewLine, NoteSnapshot Snapshot);

/// <summary>
/// Единственное место раздела, где вызывается System.IO. Все методы возвращают
/// <see cref="VaultResult"/> и не выпускают исключений: раздел живет на чужом диске, и отказ ФС
/// здесь — обычный ход событий, а не поломка программы.
/// </summary>
public static class VaultOperations
{
    /// <summary>Больше этого в редактор не грузим: TextBox на таком объеме перестает отвечать.</summary>
    public const long MaxOpenBytes = 5L * 1024 * 1024;

    /// <summary>Свыше этого не показываем живое превью в разделенном режиме — рендер markdown
    /// идет синхронно на UI-потоке и съел бы набор текста.</summary>
    public const long MaxLivePreviewBytes = 512L * 1024;

    // ── Создание ────────────────────────────────────────────────────────────────

    public static VaultResult CreateFolder(string parentFullPath, string name)
    {
        if (VaultNames.Validate(name) is { } err) return VaultResult.Fail(err);

        var folder = name.Trim();
        if (Taken(parentFullPath, folder)) return VaultResult.Fail("Папка с таким названием уже существует");

        var target = Path.Combine(parentFullPath, folder);
        try
        {
            Directory.CreateDirectory(target);
            return VaultResult.Success(target);
        }
        catch (Exception ex) { return VaultResult.Fail(VaultErrors.Describe(ex, target)); }
    }

    public static VaultResult CreateNote(string parentFullPath, string name)
    {
        if (VaultNames.Validate(name) is { } err) return VaultResult.Fail(err);

        var file = VaultNames.EnsureMdExtension(name);
        if (Taken(parentFullPath, file)) return VaultResult.Fail("Заметка с таким названием уже существует");

        var target = Path.Combine(parentFullPath, file);
        try
        {
            // CreateNew, а не WriteAllText: между проверкой и записью файл мог появиться
            // (синхронизатор, вторая копия программы) — честно падаем, а не затираем чужое.
            using (new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.Read)) { }
            return VaultResult.Success(target);
        }
        catch (Exception ex) { return VaultResult.Fail(VaultErrors.Describe(ex, target)); }
    }

    // ── Переименование, дубль, перенос ──────────────────────────────────────────

    public static VaultResult Rename(string fullPath, string newName, bool isFolder)
    {
        if (VaultNames.Validate(newName) is { } err) return VaultResult.Fail(err);

        var parent = Path.GetDirectoryName(fullPath);
        if (string.IsNullOrEmpty(parent)) return VaultResult.Fail("Не удалось определить папку файла.");

        var oldName = Path.GetFileName(fullPath);
        var name    = isFolder ? newName.Trim() : VaultNames.EnsureMdExtension(newName);
        if (string.Equals(oldName, name, StringComparison.Ordinal)) return VaultResult.Success(fullPath);

        var target = Path.Combine(parent, name);

        // Смена ТОЛЬКО регистра. На macOS и Windows файловая система обычно не различает регистр,
        // и прямое «а.md» → «А.md» падает «файл уже существует». Идем через промежуточное имя.
        var caseOnly = string.Equals(oldName, name, StringComparison.OrdinalIgnoreCase);
        if (!caseOnly && Taken(parent, name))
            return VaultResult.Fail(isFolder ? "Папка с таким названием уже существует"
                                             : "Заметка с таким названием уже существует");
        try
        {
            if (caseOnly)
            {
                var temp = Path.Combine(parent, name + ".sbren");
                MoveEntry(fullPath, temp, isFolder);
                MoveEntry(temp, target, isFolder);
            }
            else MoveEntry(fullPath, target, isFolder);

            return VaultResult.Success(target);
        }
        catch (Exception ex) { return VaultResult.Fail(VaultErrors.Describe(ex, target)); }
    }

    /// <param name="progress">Сколько файлов уже скопировано — для окна прогресса при большой папке.</param>
    public static VaultResult Duplicate(string fullPath, bool isFolder, IProgress<int>? progress = null)
    {
        var parent = Path.GetDirectoryName(fullPath);
        if (string.IsNullOrEmpty(parent)) return VaultResult.Fail("Не удалось определить папку файла.");

        var name   = VaultNames.NextCopyName(Path.GetFileName(fullPath), isFolder, n => Taken(parent, n));
        var target = Path.Combine(parent, name);
        try
        {
            if (isFolder)
            {
                var copied = 0;
                CopyDirectory(fullPath, target, progress, ref copied);
            }
            else File.Copy(fullPath, target);

            return VaultResult.Success(target);
        }
        catch (Exception ex) { return VaultResult.Fail(VaultErrors.Describe(ex, target)); }
    }

    /// <summary>
    /// Сколько файлов и папок лежит внутри — с потолком <paramref name="cap"/>: вопрос «долго ли
    /// это будет» не должен сам оказаться долгим. Дошли до потолка — значит «много», точное число
    /// уже не важно.
    /// </summary>
    public static int CountEntries(string folderFullPath, int cap)
    {
        var count = 0;
        Walk(folderFullPath);
        return count;

        void Walk(string dir)
        {
            if (count >= cap) return;
            try
            {
                foreach (var entry in Directory.EnumerateFileSystemEntries(dir))
                {
                    if (++count >= cap) return;
                    if (Directory.Exists(entry)) Walk(entry);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Недоступная подпапка не должна ронять подсчет — она просто не учтется.
            }
        }
    }

    public static VaultResult Move(string fullPath, string targetFolderFullPath, bool isFolder)
    {
        var parent = Path.GetDirectoryName(fullPath) ?? "";
        if (SamePath(parent, targetFolderFullPath)) return VaultResult.Success(fullPath);

        // Папку нельзя убрать внутрь самой себя: она бы исчезла вместе со всем содержимым.
        if (isFolder && IsInsideDirectory(targetFolderFullPath, fullPath))
            return VaultResult.Fail("Нельзя переместить папку внутрь самой себя");

        // Имя в целевой папке занято — берем «(копия)»-вариант, как дубль в конструкторе.
        // Молчать нельзя: вызывающий показывает, под каким именем вещь легла.
        var name   = Path.GetFileName(fullPath);
        var free   = VaultNames.NextFreeName(name, isFolder, n => Taken(targetFolderFullPath, n));
        var target = Path.Combine(targetFolderFullPath, free);
        try
        {
            MoveEntry(fullPath, target, isFolder);
            return VaultResult.Success(target);
        }
        catch (Exception ex) { return VaultResult.Fail(VaultErrors.Describe(ex, target)); }
    }

    // ── Удаление ────────────────────────────────────────────────────────────────

    /// <summary>В системную корзину. Не вышло — <c>Ok = false</c>, и вызывающий отдельно
    /// спрашивает про безвозвратное удаление: молча стирать чужие файлы нельзя.</summary>
    public static VaultResult Delete(string fullPath) =>
        SystemTrash.TryDelete(fullPath)
            ? VaultResult.Success(fullPath)
            : VaultResult.Fail("Не удалось переместить в корзину.");

    /// <summary>Безвозвратно — только после отдельного подтверждения человеком.</summary>
    public static VaultResult DeletePermanently(string fullPath, bool isFolder)
    {
        try
        {
            if (isFolder) Directory.Delete(fullPath, recursive: true);
            else          File.Delete(fullPath);
            return VaultResult.Success(fullPath);
        }
        catch (Exception ex) { return VaultResult.Fail(VaultErrors.Describe(ex, fullPath)); }
    }

    // ── Чтение и запись заметки ─────────────────────────────────────────────────

    public static (NoteContent? Content, string? Error) ReadNote(string fullPath)
    {
        try
        {
            var info = new FileInfo(fullPath);
            if (!info.Exists) return (null, "Файл не найден — возможно, его удалила другая программа.");
            if (info.Length > MaxOpenBytes)
                return (null, "Файл слишком большой для редактора заметок.");

            var bytes = File.ReadAllBytes(fullPath);
            var bom   = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;
            var skip  = bom ? 3 : 0;
            var text  = new UTF8Encoding(false).GetString(bytes, skip, bytes.Length - skip);

            // TextBox хранит перевод строки как '\n'. Запоминаем, что было в файле, и возвращаем
            // это при записи — иначе система контроля версий покажет измененным весь файл.
            var newLine = text.Contains("\r\n") ? "\r\n" : "\n";

            return (new NoteContent(text.Replace("\r\n", "\n"), bom, newLine,
                                    new NoteSnapshot(info.LastWriteTimeUtc.Ticks, info.Length)), null);
        }
        catch (Exception ex) { return (null, VaultErrors.Describe(ex, fullPath)); }
    }

    /// <summary>
    /// Запись через временный файл: оборванная посередине запись не оставит от заметки огрызок.
    /// </summary>
    public static VaultResult WriteNoteAtomic(string fullPath, string text, bool bom, string newLine)
    {
        // Временный файл — в ТОЙ ЖЕ папке: File.Replace не работает через границу тома.
        var tmp = fullPath + ".sbtmp";
        try
        {
            var body = text.Replace("\r\n", "\n");
            if (newLine == "\r\n") body = body.Replace("\n", "\r\n");

            File.WriteAllText(tmp, body, new UTF8Encoding(bom));

            // Replace, а не Move(overwrite): он сохраняет права и атрибуты файла, поэтому
            // синхронизаторы (iCloud, Dropbox) не считают заметку появившейся заново.
            if (File.Exists(fullPath)) File.Replace(tmp, fullPath, null, ignoreMetadataErrors: true);
            else                       File.Move(tmp, fullPath);

            return VaultResult.Success(fullPath);
        }
        catch (Exception ex)
        {
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { /* хвост уберется при следующей записи */ }
            return VaultResult.Fail(VaultErrors.Describe(ex, fullPath));
        }
    }

    /// <summary>Снимок для сверки с внешними правками. null — файла нет или он недоступен.</summary>
    public static NoteSnapshot? Snapshot(string fullPath)
    {
        try
        {
            var info = new FileInfo(fullPath);
            return info.Exists ? new NoteSnapshot(info.LastWriteTimeUtc.Ticks, info.Length) : null;
        }
        catch { return null; }
    }

    // ── Общее ───────────────────────────────────────────────────────────────────

    /// <summary>Занято ли имя в папке. File.Exists и Directory.Exists учитывают регистр так же,
    /// как сама ФС, — отдельная возня с регистром здесь не нужна.</summary>
    public static bool Taken(string parentFullPath, string name)
    {
        var p = Path.Combine(parentFullPath, name);
        return File.Exists(p) || Directory.Exists(p);
    }

    private static void MoveEntry(string from, string to, bool isFolder)
    {
        if (isFolder) Directory.Move(from, to);
        else          File.Move(from, to);
    }

    private static bool SamePath(string a, string b)
    {
        try
        {
            return Path.TrimEndingDirectorySeparator(Path.GetFullPath(a))
                       .Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(b)), VaultRules.Comparison);
        }
        catch { return false; }
    }

    private static bool IsInsideDirectory(string candidate, string root)
    {
        try
        {
            var c = Path.TrimEndingDirectorySeparator(Path.GetFullPath(candidate));
            var r = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
            return c.Equals(r, VaultRules.Comparison)
                || c.StartsWith(r + Path.DirectorySeparatorChar, VaultRules.Comparison);
        }
        catch { return false; }
    }

    private static void CopyDirectory(string from, string to, IProgress<int>? progress, ref int copied)
    {
        Directory.CreateDirectory(to);

        foreach (var dir in Directory.EnumerateDirectories(from))
            CopyDirectory(dir, Path.Combine(to, Path.GetFileName(dir)), progress, ref copied);

        foreach (var file in Directory.EnumerateFiles(from))
        {
            File.Copy(file, Path.Combine(to, Path.GetFileName(file)));
            progress?.Report(++copied);
        }
    }
}
