namespace SmartBase.Core.Notes;

/// <summary>Что за узел дерева хранилища: папка или файл.</summary>
public enum NoteKind { Folder, File }

/// <summary>
/// Узел дерева заметок. Ключ идентичности — <see cref="RelativePath"/>, а не абсолютный путь
/// и не число: файлы переименовывают и переносят снаружи, а само хранилище переезжает на
/// другой диск или другую машину. Относительный путь переживает и то, и другое, поэтому по
/// нему хранятся раскрытые ветки, выделение и «последняя открытая заметка» в настройках.
/// </summary>
public sealed class NoteNode
{
    public NoteKind Kind { get; init; }

    /// <summary>Абсолютный путь — им работают файловые операции.</summary>
    public string FullPath { get; init; } = "";

    /// <summary>Путь от корня хранилища: разделитель всегда '/', без крайних слешей.</summary>
    public string RelativePath { get; init; } = "";

    /// <summary>Имя с расширением.</summary>
    public string Name { get; init; } = "";

    /// <summary>Файл открывается в разделе (markdown). Остальные видны, но не открываются.</summary>
    public bool IsMarkdown { get; init; }

    public bool IsFolder => Kind == NoteKind.Folder;

    public bool IsExpanded { get; set; }

    /// <summary>Дети уже прочитаны с диска. Обход ленивый — см. <see cref="VaultScanner"/>.</summary>
    public bool IsLoaded { get; set; }

    public List<NoteNode> Children { get; } = [];

    /// <summary>Имя без расширения markdown — то, что правится в шапке раздела. У папок и
    /// прочих файлов — имя целиком: скрывать у них расширение значило бы дать стереть его
    /// при переименовании.</summary>
    public string Stem => IsMarkdown ? Path.GetFileNameWithoutExtension(Name) : Name;
}
