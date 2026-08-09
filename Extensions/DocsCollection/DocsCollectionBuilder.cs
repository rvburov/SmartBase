using System.IO;
using System.Threading;
using SmartBase.Core.Models;
using SmartBase.Database;
using SmartBase.Database.Repositories;

namespace SmartBase.Extensions.DocsCollection;

/// <summary>Найденный документ — будущая запись коллекции.</summary>
/// <param name="Name">Имя файла без расширения — «Наименование» (оно же название записи).</param>
/// <param name="FolderName">Имя папки, в которой лежит файл — «Название папки» (описание записи).</param>
/// <param name="Format">Расширение без точки, в нижнем регистре («pdf»); пусто — файл без расширения.</param>
/// <param name="FullPath">Абсолютный путь — значение поля «Файл» (тип «файл» хранит путь как есть).</param>
public sealed record DocEntry(string Name, string FolderName, string Format, string FullPath);

/// <summary>Прогресс наполнения: сколько записей создано из скольких + имя текущего документа.</summary>
public sealed record DocsProgress(int Current, int Total, string File);

/// <summary>Каркас созданной коллекции: её id и id двух пользовательских полей.</summary>
public sealed record DocsShell(int CollectionId, string CollectionName,
                              int FolderFieldId, int FormatFieldId, int FileFieldId);

/// <summary>Понятная пользователю ошибка расширения (показывается в статусе окна).</summary>
public sealed class DocsCollectionException(string message) : Exception(message);

/// <summary>
/// Логика расширения «Документы в коллекцию» — без UI. Три шага, разнесённые намеренно:
/// <list type="number">
/// <item><see cref="Scan"/> — обход папки (только диск, безопасно в фоне);</item>
/// <item><see cref="CreateShell"/> — коллекция + колонки на ОБЩЕМ репозитории (UI-поток: там же
/// срабатывает проверка прав <c>CreateCollection</c>);</item>
/// <item><see cref="FillRecords"/> — записи на ОТДЕЛЬНОМ подключении в фоне (как это делают
/// массовые операции репозитория, см. <c>AddRecordIsolated</c>/<c>DeleteCollectionIsolated</c>):
/// общий контекст EF принадлежит UI-потоку и делить его с фоном нельзя.</item>
/// </list>
/// Схема коллекции: «Наименование» — переименованная СИСТЕМНАЯ колонка записи (Title),
/// «Название папки» и «Формат файла» — текст, «Файл» — тип «файл» с абсолютным путём.
/// </summary>
public static class DocsCollectionBuilder
{
    // Ключ системной колонки записи (та же строка, что в ConstructorRepository/TableRowVm).
    private const string TitleKey = "__title__";

    /// <summary>Подписи колонок создаваемой коллекции.</summary>
    public const string TitleColumnName  = "Наименование";
    public const string FolderFieldName  = "Название папки";
    public const string FormatFieldName  = "Формат файла";
    public const string FileFieldName    = "Файл";

    // Прогресс постится в UI-поток, поэтому не на каждую запись: на тысячах документов
    // поток сообщений сам по себе тормозит диспетчер сильнее, чем сама вставка.
    private const int ProgressEvery = 25;

    // ── 1. Обход папки ────────────────────────────────────────────────────────

    /// <summary>Разобрать строку фильтра («pdf, docx», «*.pdf; .xlsx») в набор расширений без точек
    /// в нижнем регистре. Пустой набор — брать файлы любого формата.</summary>
    public static IReadOnlySet<string> ParseExtensions(string? raw) =>
        (raw ?? "")
            .Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(s => s.TrimStart('*').TrimStart('.').ToLowerInvariant())
            .Where(s => s.Length > 0)
            .ToHashSet();

    /// <summary>
    /// Собрать документы из папки. <paramref name="recursive"/> — вместе с подпапками.
    /// <paramref name="extensions"/> пуст — берутся все файлы. Скрытые и системные файлы
    /// пропускаются, недоступные подпапки не роняют обход (<see cref="EnumerationOptions"/>).
    /// Порядок — по папке, затем по наименованию: он же станет порядком записей.
    /// </summary>
    public static List<DocEntry> Scan(string root, bool recursive, IReadOnlySet<string> extensions,
                                      CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(root)) throw new DocsCollectionException("Папка не указана.");
        if (!Directory.Exists(root))         throw new DocsCollectionException("Папка не существует или недоступна.");

        var options = new EnumerationOptions
        {
            RecurseSubdirectories = recursive,
            IgnoreInaccessible    = true,
            AttributesToSkip      = FileAttributes.Hidden | FileAttributes.System,
        };

        var docs = new List<DocEntry>();
        foreach (var path in Directory.EnumerateFiles(root, "*", options))
        {
            ct.ThrowIfCancellationRequested();

            var format = Path.GetExtension(path).TrimStart('.').ToLowerInvariant();
            if (extensions.Count > 0 && !extensions.Contains(format)) continue;

            // «.gitignore» и подобные: имени без расширения нет — берём имя файла целиком.
            var name = Path.GetFileNameWithoutExtension(path);
            if (name.Length == 0) name = Path.GetFileName(path);

            docs.Add(new DocEntry(name, FolderNameOf(path), format, path));
        }

        docs.Sort((a, b) =>
        {
            var byFolder = string.Compare(a.FolderName, b.FolderName, StringComparison.CurrentCultureIgnoreCase);
            return byFolder != 0 ? byFolder : string.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase);
        });
        return docs;
    }

    // Имя папки файла (не путь): «…\Договоры\акт.pdf» → «Договоры». Для файла в корне диска
    // имени папки нет — возвращаем корень как есть («C:\»), чтобы описание не осталось пустым.
    private static string FolderNameOf(string filePath)
    {
        var dir = Path.GetDirectoryName(filePath);
        if (string.IsNullOrEmpty(dir)) return "";
        var name = Path.GetFileName(dir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        return name.Length > 0 ? name : dir;
    }

    /// <summary>Имя коллекции по умолчанию — имя выбранной папки (для корня диска — «Документы»).</summary>
    public static string DefaultCollectionName(string root)
    {
        var trimmed = (root ?? "").TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var name    = Path.GetFileName(trimmed);
        return string.IsNullOrWhiteSpace(name) ? "Документы" : name;
    }

    // ── 2. Каркас коллекции (общий репозиторий, UI-поток) ─────────────────────

    /// <summary>
    /// Создать коллекцию и её колонки. Имя занято — добавляется « (2)», « (3)»… Любая ошибка
    /// после создания коллекции удаляет её, чтобы не осталось полупустого каркаса.
    /// </summary>
    public static DocsShell CreateShell(ConstructorRepository repo, string collectionName)
    {
        var existing = repo.GetAllCollections();
        var taken    = existing.Select(c => c.Name).ToHashSet(StringComparer.CurrentCultureIgnoreCase);
        var name     = UniqueName(collectionName, taken);
        var position = existing.Count == 0 ? 1000 : existing.Max(c => c.Position) + 1000;

        var col   = repo.AddCollection(new DbCollection { Name = name, Position = position });
        var colId = col.Id!.Value;

        try
        {
            // Системную колонку записи переименовываем — физически это Title.
            repo.SetSystemFieldName(colId, TitleKey, TitleColumnName, "Название");

            // Название папки — обычное текстовое поле: описание записи (роль IsDescription)
            // заводят только календарь и задачи, расширение к ним отношения не имеет.
            var folder = repo.AddField(new DbField
            {
                CollectionId = colId, Name = FolderFieldName, FieldType = "text", Position = 1000,
            });
            var format = repo.AddField(new DbField
            {
                CollectionId = colId, Name = FormatFieldName, FieldType = "text", Position = 2000,
            });
            // Поле «файл» без правила авто-привязки (ConfigJson пуст): пути проставляем сами —
            // правило умеет лишь одну папку без подпапок и один формат, чего не хватает при
            // рекурсивном обходе и смешанных расширениях.
            var file = repo.AddField(new DbField
            {
                CollectionId = colId, Name = FileFieldName, FieldType = "file", Position = 3000,
            });

            var folderId = folder.Id!.Value;
            var formatId = format.Id!.Value;
            var fileId   = file.Id!.Value;

            // Порядок колонок: Наименование | Название папки | Формат файла | Файл.
            repo.SaveColumnOrder(colId,
                [TitleKey, folderId.ToString(), formatId.ToString(), fileId.ToString()]);

            return new DocsShell(colId, name, folderId, formatId, fileId);
        }
        catch
        {
            TryDelete(repo, colId);
            throw;
        }
    }

    private static string UniqueName(string baseName, HashSet<string> taken)
    {
        var name = string.IsNullOrWhiteSpace(baseName) ? "Документы" : baseName.Trim();
        if (!taken.Contains(name)) return name;
        for (var i = 2; ; i++)
        {
            var candidate = $"{name} ({i})";
            if (!taken.Contains(candidate)) return candidate;
        }
    }

    /// <summary>Убрать недособранную коллекцию (отмена/ошибка). Ошибку удаления глотаем —
    /// важнее показать исходную причину.</summary>
    public static void TryDelete(ConstructorRepository repo, int collectionId)
    {
        try { repo.DeleteCollectionIsolated(collectionId); } catch { /* best-effort */ }
    }

    // ── 3. Записи (отдельное подключение, фон) ────────────────────────────────

    /// <summary>
    /// Наполнить коллекцию записями на СВОЁМ подключении к <paramref name="dbPath"/>: наименование
    /// → Title, название папки, формат и путь к файлу → значения полей каркаса.
    /// Возвращает число созданных записей; отмена бросает <see cref="OperationCanceledException"/>
    /// (вызывающий удаляет коллекцию через <see cref="TryDelete"/>).
    /// </summary>
    public static int FillRecords(string dbPath, DocsShell shell, IReadOnlyList<DocEntry> docs,
                                  IProgress<DocsProgress>? progress, CancellationToken ct)
    {
        using var ctx = new AppDbContext(dbPath);
        ctx.ApplyBulkPerformancePragmas();
        var repo = new ConstructorRepository(ctx);

        // Шаг позиции 1000 оставляет место для вставки строк между документами; на неправдоподобно
        // больших объёмах падаем до 1, чтобы (i+1)*step не переполнил int (как в импорте).
        var step = docs.Count > 2_000_000 ? 1 : 1000;

        var created = 0;
        for (var i = 0; i < docs.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var doc = docs[i];

            var record = repo.AddRecord(new DbRecord
            {
                CollectionId = shell.CollectionId,
                Title        = doc.Name,
                Position     = (i + 1) * step,
            });
            var recordId = record.Id!.Value;

            if (doc.FolderName.Length > 0) repo.SetFieldValue(recordId, shell.FolderFieldId, doc.FolderName);
            if (doc.Format.Length > 0)     repo.SetFieldValue(recordId, shell.FormatFieldId, doc.Format);
            repo.SetFieldValue(recordId, shell.FileFieldId, doc.FullPath);
            created++;

            if (i % ProgressEvery == 0 || i == docs.Count - 1)
                progress?.Report(new DocsProgress(created, docs.Count, doc.Name));
        }

        return created;
    }
}
