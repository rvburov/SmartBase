namespace SmartBase.Core.Notes;

/// <summary>
/// Итог файловой операции: успех с получившимся путем либо готовый к показу текст ошибки.
/// Исключения ФС наружу не выпускаются — их переводит <see cref="VaultErrors"/>: раздел работает
/// с чужими файлами на чужом диске, и «упало исключение» здесь норма, а не поломка программы.
/// </summary>
public sealed record VaultResult(bool Ok, string? Error, string? Path)
{
    public static VaultResult Success(string path) => new(true, null, path);
    public static VaultResult Fail(string error)   => new(false, error, null);
}
