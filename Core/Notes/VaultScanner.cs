namespace SmartBase.Core.Notes;

/// <summary>
/// Чтение хранилища с диска. Обход ЛЕНИВЫЙ: сразу читается только корень, папка дочитывается при
/// раскрытии. Полный обход (<see cref="ScanAll"/>) нужен единственной кнопке «раскрыть все» и
/// потому ограничен потолками: хранилище на десятки тысяч файлов иначе построило бы столько же
/// узлов, которых никто не увидит, а на сетевой папке заморозило бы окно.
/// </summary>
public static class VaultScanner
{
    /// <summary>Потолок полного обхода. Дошли до него — дерево показано частично (Truncated).</summary>
    public const int MaxNodes = 20_000;

    /// <summary>Страховка от петель по ссылкам: <see cref="VaultRules.IsTraversable"/> ловит
    /// обычные симлинки, глубина — все остальное.</summary>
    public const int MaxDepth = 24;

    /// <summary>Абсолютный путь по относительному внутри хранилища.</summary>
    public static string FullPathOf(string? vaultRoot, string? relative)
    {
        if (string.IsNullOrWhiteSpace(vaultRoot)) return "";
        var rel = VaultRules.Normalize(relative);
        return rel.Length == 0
            ? vaultRoot
            : Path.Combine(vaultRoot, rel.Replace('/', Path.DirectorySeparatorChar));
    }

    /// <summary>Один уровень: содержимое папки без спуска внутрь. Порядок не задается — его
    /// ставит вызывающий через <see cref="VaultRules.SortLevel"/>.</summary>
    public static List<NoteNode> ScanChildren(string? vaultRoot, string? parentRelative)
    {
        var result = new List<NoteNode>();
        var full = FullPathOf(vaultRoot, parentRelative);
        if (full.Length == 0 || !Directory.Exists(full)) return result;

        try
        {
            foreach (var entry in Directory.EnumerateFileSystemEntries(full))
            {
                var node = MakeNode(entry, parentRelative);
                if (node is not null) result.Add(node);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Папку прочитать не дали (права, отключенный диск) — показываем ее пустой, а не
            // роняем весь сайдбар. Настоящую причину человек увидит при первой же операции.
        }
        return result;
    }

    /// <summary>
    /// Полное дерево. <c>Truncated</c> — уперлись в потолок узлов: показанное верно, но неполно,
    /// и об этом нужно сказать вслух, иначе «раскрыть все» молча соврет.
    /// </summary>
    public static (List<NoteNode> Roots, bool Truncated) ScanAll(
        string? vaultRoot, bool descending = false, int maxNodes = MaxNodes, int maxDepth = MaxDepth)
    {
        var count = 0;
        var truncated = false;
        var roots = Walk("", 0);
        return (roots, truncated);

        List<NoteNode> Walk(string relative, int depth)
        {
            var nodes = ScanChildren(vaultRoot, relative);
            VaultRules.SortLevel(nodes, descending);

            foreach (var n in nodes)
            {
                if (++count >= maxNodes) { truncated = true; break; }
                if (!n.IsFolder || depth + 1 >= maxDepth || !CanDescend(n.FullPath)) continue;

                n.Children.AddRange(Walk(n.RelativePath, depth + 1));
                n.IsLoaded = true;
                if (truncated) break;
            }
            return nodes;
        }
    }

    /// <summary>Только папки, полностью — для списка «Расположение» в диалогах. Дешевле полного
    /// обхода: файлы, которых в хранилище заметок большинство, не читаются вовсе.</summary>
    public static List<NoteNode> ScanFolders(string? vaultRoot, bool descending = false, int maxDepth = MaxDepth)
    {
        return Walk("", 0);

        List<NoteNode> Walk(string relative, int depth)
        {
            var nodes = ScanChildren(vaultRoot, relative).Where(n => n.IsFolder).ToList();
            VaultRules.SortLevel(nodes, descending);

            if (depth + 1 >= maxDepth) return nodes;
            foreach (var n in nodes.Where(n => CanDescend(n.FullPath)))
            {
                n.Children.AddRange(Walk(n.RelativePath, depth + 1));
                n.IsLoaded = true;
            }
            return nodes;
        }
    }

    private static NoteNode? MakeNode(string fullPath, string? parentRelative)
    {
        FileAttributes attrs;
        try { attrs = File.GetAttributes(fullPath); }
        catch { return null; }   // исчез между перечислением и чтением атрибутов

        var name = Path.GetFileName(fullPath);
        if (name.Length == 0 || VaultRules.IsHidden(name, attrs)) return null;

        var isFolder = attrs.HasFlag(FileAttributes.Directory);
        return new NoteNode
        {
            Kind         = isFolder ? NoteKind.Folder : NoteKind.File,
            FullPath     = fullPath,
            RelativePath = VaultRules.Combine(parentRelative, name),
            Name         = name,
            IsMarkdown   = !isFolder && VaultRules.IsMarkdown(name),
        };
    }

    private static bool CanDescend(string fullPath)
    {
        try { return VaultRules.IsTraversable(File.GetAttributes(fullPath)); }
        catch { return false; }
    }
}
