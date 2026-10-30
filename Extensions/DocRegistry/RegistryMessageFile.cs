using System.IO.Compression;
using System.Xml;

namespace SmartBase.Extensions.DocRegistry;

/// <summary>Ограниченное вложение книги для регистрации через сообщение. Ручной импорт не меняет.</summary>
public static class RegistryMessageFile
{
    public const int MaxFileBytes = 10 * 1024 * 1024;
    public const long MaxExpandedBytes = 64L * 1024 * 1024;
    public const int MaxRows = 1000;
    public const int MaxCells = 100000;
    private const int MaxEntries = 2048;

    /// <summary>Читает существующий шаблон журнала, сохраняя настоящие номера строк первого листа.</summary>
    public static IReadOnlyList<ImportRow> Read(byte[] bytes, string fileName,
                                               CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var name = SafeName(fileName);
        var extension = Path.GetExtension(name);
        if (!RegistryFile.Extensions.Contains(extension, StringComparer.OrdinalIgnoreCase))
            throw new DocRegistryException("Файл: нужен формат .xlsx или .xlsm; старый .xls не поддерживается.");
        if (bytes.Length == 0) throw new DocRegistryException("Файл пустой.");
        if (bytes.Length > MaxFileBytes)
            throw new DocRegistryException($"Файл больше допустимых {MaxFileBytes / 1024 / 1024} МБ.");

        CheckArchive(bytes, ct);
        var directory = Path.Combine(Path.GetTempPath(), "smartbase_registry_message_" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(directory);
            // Имя отправителя используется только в ответе. В путь попадает наше имя и проверенное расширение.
            var path = Path.Combine(directory, "input" + extension.ToLowerInvariant());
            File.WriteAllBytes(path, bytes);
            ct.ThrowIfCancellationRequested();
            var rows = RegistryFile.Read(path, MaxRows, ct);
            ct.ThrowIfCancellationRequested();
            return rows;
        }
        finally
        {
            try { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }
    }

    internal static string SafeName(string? name)
    {
        var value = Path.GetFileName((name ?? "").Replace('\\', '/')).Trim();
        if (value.Length == 0) throw new DocRegistryException("Файл: не указано имя вложения с расширением .xlsx или .xlsm.");
        if (value.Length > 240 || value.Any(char.IsControl))
            throw new DocRegistryException("Файл: недопустимое имя вложения.");
        return value;
    }

    private static void CheckArchive(byte[] bytes, CancellationToken ct)
    {
        try
        {
            using var stream = new MemoryStream(bytes, writable: false);
            using var archive = new ZipArchive(stream, ZipArchiveMode.Read);
            if (archive.Entries.Count > MaxEntries)
                throw new DocRegistryException("В книге слишком много внутренних частей.");
            if (archive.GetEntry("[Content_Types].xml") is null || archive.GetEntry("xl/workbook.xml") is null)
                throw new DocRegistryException("Файл не является книгой .xlsx или .xlsm.");
            if (archive.Entries.Select(e => e.FullName).Distinct(StringComparer.Ordinal).Count() != archive.Entries.Count)
                throw new DocRegistryException("В книге повторяются имена внутренних частей.");

            long total = 0;
            var cells = 0;
            var buffer = new byte[81920];
            foreach (var entry in archive.Entries)
            {
                ct.ThrowIfCancellationRequested();
                if (entry.Length > MaxExpandedBytes - total)
                    throw new DocRegistryException($"Распакованная книга больше допустимых {MaxExpandedBytes / 1024 / 1024} МБ.");
                using (var content = entry.Open())
                {
                    int read;
                    while ((read = content.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        ct.ThrowIfCancellationRequested();
                        total += read;
                        if (total > MaxExpandedBytes)
                            throw new DocRegistryException($"Распакованная книга больше допустимых {MaxExpandedBytes / 1024 / 1024} МБ.");
                    }
                }
                if (!entry.FullName.StartsWith("xl/worksheets/", StringComparison.Ordinal)
                    || !entry.FullName.EndsWith(".xml", StringComparison.OrdinalIgnoreCase)) continue;
                using var worksheet = entry.Open();
                using var reader = XmlReader.Create(worksheet, new XmlReaderSettings
                {
                    DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null,
                    MaxCharactersInDocument = MaxExpandedBytes,
                });
                while (reader.Read())
                {
                    ct.ThrowIfCancellationRequested();
                    if (reader.Depth > 64) throw new DocRegistryException("Структура листа книги слишком сложная.");
                    if (reader.NodeType == XmlNodeType.Element && reader.LocalName == "c" && ++cells > MaxCells)
                        throw new DocRegistryException($"В книге больше допустимых {MaxCells} ячеек.");
                }
            }
        }
        catch (Exception error) when (error is InvalidDataException or XmlException or NotSupportedException)
        {
            throw new DocRegistryException("Не удалось прочитать книгу: файл поврежден или имеет неверный формат.");
        }
    }
}
