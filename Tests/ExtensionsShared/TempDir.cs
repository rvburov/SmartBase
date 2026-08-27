namespace SmartBase.Tests.Extensions.Shared;

/// <summary>
/// Одноразовая временная папка теста: создается в конструкторе, убирается в <see cref="Dispose"/>.
/// Нужна там, где расширение работает с файловой системой — обход папки, переименование,
/// запись результата.
///
/// Уборка best-effort: если файл остался заблокирован (антивирус, не закрытый поток), тест не
/// падает из-за уборки — падать он должен по своей причине, а не по чужой.
/// </summary>
public sealed class TempDir : IDisposable
{
    public string Path { get; }

    /// <param name="tag">Метка в имени папки — по ней в системной temp-папке видно, чей набор
    /// оставил папку, если уборка не отработала. Пусто — просто «sbext_&lt;guid&gt;».</param>
    public TempDir(string tag = "")
    {
        var prefix = tag.Length > 0 ? $"sbext_{tag}_" : "sbext_";
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"{prefix}{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path);
    }

    /// <summary>
    /// Создать в папке пустой файл. Содержимое расширениям, которые этим пользуются, не важно —
    /// они работают с именем, папкой, форматом и путем.
    /// <paramref name="relative"/> может содержать подпапки («Договоры/акт.pdf») — они создаются
    /// сами, поэтому один и тот же метод годится и для плоской папки, и для дерева.
    /// </summary>
    public string Touch(string relative)
    {
        var full = System.IO.Path.Combine(Path, relative);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full)!);
        File.WriteAllText(full, "");
        return full;
    }

    /// <summary>Имена файлов в папке, отсортированные, — для сравнения «что лежит на диске».
    /// Порядок ordinal, а не культурный: сравнение должно быть одинаковым на всех ОС.</summary>
    public string[] Names() =>
        [.. Directory.EnumerateFiles(Path)
                     .Select(System.IO.Path.GetFileName)
                     .Select(n => n!)
                     .OrderBy(n => n, StringComparer.Ordinal)];

    public void Dispose()
    {
        try { if (Directory.Exists(Path)) Directory.Delete(Path, recursive: true); }
        catch { /* best-effort */ }
    }
}
