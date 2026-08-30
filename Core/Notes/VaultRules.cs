namespace SmartBase.Core.Notes;

/// <summary>
/// Правила хранилища заметок: что показывать, куда заходить, что открывается и в каком порядке
/// выводится. Только чистые функции, диск здесь не читается — поэтому правила покрыты тестами,
/// а <see cref="VaultScanner"/> остается тонким.
/// </summary>
public static class VaultRules
{
    /// <summary>
    /// Сравнение путей. На Linux файловая система различает регистр, на macOS и Windows — как
    /// правило нет. Ключи раскрытия и выделения обязаны сравниваться так же, как их сравнивает
    /// сама ФС, иначе «Работа» и «работа» разъедутся на одну и ту же папку.
    /// </summary>
    public static readonly StringComparison Comparison =
        OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;

    /// <inheritdoc cref="Comparison"/>
    public static readonly StringComparer PathComparer =
        OperatingSystem.IsLinux() ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase;

    /// <summary>
    /// Скрытые элементы: ведущая точка (.obsidian, .git, .trash, .DS_Store) либо системные флаги.
    /// Точка-правило — то же, что делает сам Obsidian; черный список имен всегда неполон. Флаги
    /// нужны Windows: там Thumbs.db и desktop.ini скрыты без всякой точки.
    /// </summary>
    public static bool IsHidden(string name, FileAttributes attrs) =>
        (name.Length > 0 && name[0] == '.')
        || attrs.HasFlag(FileAttributes.Hidden)
        || attrs.HasFlag(FileAttributes.System);

    /// <summary>Заходить внутрь можно во все, кроме точек повторного разбора: симлинк на
    /// родителя дал бы бесконечную рекурсию при «раскрыть все».</summary>
    public static bool IsTraversable(FileAttributes attrs) =>
        !attrs.HasFlag(FileAttributes.ReparsePoint);

    /// <summary>Открывается только markdown. Остальные файлы в дереве видны, но не открываются.</summary>
    public static bool IsMarkdown(string name) =>
        name.EndsWith(".md", Comparison) || name.EndsWith(".markdown", Comparison);

    /// <summary>Относительный путь в каноническом виде: разделитель '/', без крайних слешей.</summary>
    public static string Normalize(string? relativePath) =>
        (relativePath ?? "").Replace('\\', '/').Trim('/');

    /// <summary>Относительный путь ребенка: «А/Б» + «В.md» → «А/Б/В.md».</summary>
    public static string Combine(string? parentRelative, string name)
    {
        var p = Normalize(parentRelative);
        return p.Length == 0 ? name : $"{p}/{name}";
    }

    /// <summary>Родительская папка относительного пути: «А/Б/В.md» → «А/Б», «В.md» → «».</summary>
    public static string ParentOf(string? relativePath)
    {
        var p = Normalize(relativePath);
        var i = p.LastIndexOf('/');
        return i < 0 ? "" : p[..i];
    }

    /// <summary>Лежит ли путь внутри поддерева. Сам корень поддерева тоже считается «внутри» —
    /// на этом стоит запрет переносить папку в саму себя.</summary>
    public static bool IsInside(string? relativePath, string? subtreeRoot)
    {
        var p = Normalize(relativePath);
        var r = Normalize(subtreeRoot);
        if (r.Length == 0) return true;                       // корень хранилища вмещает все
        return p.Equals(r, Comparison) || p.StartsWith(r + "/", Comparison);
    }

    /// <summary>
    /// Замена префикса пути при переименовании или переносе: («А/Б», «А», «Новая») → «Новая/Б».
    /// Нужна, чтобы раскрытые ветки не схлопывались после операции над папкой.
    /// </summary>
    public static string Rebase(string relativePath, string oldRoot, string newRoot)
    {
        var p = Normalize(relativePath);
        var o = Normalize(oldRoot);
        var n = Normalize(newRoot);
        if (o.Length == 0) return p;
        if (p.Equals(o, Comparison)) return n;
        return p.StartsWith(o + "/", Comparison) ? n + p[o.Length..] : p;
    }

    /// <summary>Порядок в дереве: папки всегда выше файлов, внутри уровня — по имени.</summary>
    public static int Compare(NoteNode a, NoteNode b, bool descending)
    {
        if (a.IsFolder != b.IsFolder) return a.IsFolder ? -1 : 1;
        var byName = NameKey(a.Name).CompareTo(NameKey(b.Name));
        return descending ? -byName : byName;
    }

    /// <summary>
    /// Ключ сортировки по имени — тот же, что в сайдбаре конструктора: имена, начинающиеся с
    /// цифры, выводятся отдельной первой группой. Одинаковое правило в двух деревьях важнее
    /// «умной» сортировки, которая была бы только здесь.
    /// </summary>
    public static (int, string) NameKey(string? name)
    {
        var s = (name ?? "").TrimStart();
        return s.Length > 0 && char.IsDigit(s[0]) ? (0, s.ToLowerInvariant()) : (1, s.ToLowerInvariant());
    }

    /// <summary>Сортирует уровень дерева на месте (папки выше файлов, затем имя).</summary>
    public static void SortLevel(List<NoteNode> nodes, bool descending) =>
        nodes.Sort((a, b) => Compare(a, b, descending));
}
