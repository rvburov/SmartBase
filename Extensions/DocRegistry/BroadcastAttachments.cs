namespace SmartBase.Extensions.DocRegistry;

/// <summary>Выбранное вложение: локальный путь, имя для получателя и размер для подтверждения.</summary>
public sealed record BroadcastAttachment(string Path, string Name, long Length);

/// <summary>
/// Список вложений рассылки. Папка раскрывается один раз, без подпапок: новые файлы,
/// появившиеся после выбора, не должны незаметно попасть в подтвержденную отправку.
/// Содержимое здесь не загружается в память и не отправляется в сеть.
/// </summary>
public static class BroadcastAttachments
{
    public static IReadOnlyList<BroadcastAttachment> FromFile(string path) => [Inspect(path)];

    public static IReadOnlyList<BroadcastAttachment> FromFolder(string path)
    {
        var files = Directory.GetFiles(path, "*", SearchOption.TopDirectoryOnly)
            .OrderBy(Path.GetFileName, StringComparer.Ordinal).ToArray();
        if (files.Length == 0)
            throw new InvalidOperationException("В выбранной папке нет файлов для отправки.");

        // Перечень целиком проверяется до возврата: недоступный файл не должен приводить
        // к молчаливой отправке только части выбранной папки.
        return files.Select(Inspect).ToArray();
    }

    /// <summary>Проверить доступность и размер снимка перед началом рассылки.</summary>
    public static void Validate(IReadOnlyList<BroadcastAttachment> attachments)
    {
        foreach (var attachment in attachments)
        {
            var actual = Inspect(attachment.Path);
            if (actual.Length != attachment.Length)
                throw new InvalidOperationException(
                    $"Размер файла «{attachment.Name}» изменился. Выберите вложения заново.");
        }
    }

    private static BroadcastAttachment Inspect(string path)
    {
        var full = Path.GetFullPath(path);
        // Открытие проверяет не только существование, но и возможность прочитать файл.
        using var stream = File.Open(full, FileMode.Open, FileAccess.Read, FileShare.Read);
        return new(full, Path.GetFileName(full), stream.Length);
    }
}
