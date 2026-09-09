using System.Security.Cryptography;
using System.Text;

namespace SmartBase.Integrations;

/// <summary>
/// Проверка подписи политики организации. Нужна там, где источник лежит в папке, доступной на
/// запись, — прежде всего в сетевой папке раздачи: без подписи любой, кто может писать в эту
/// папку, переписал бы белый список.
///
/// Схема простая и обычная для такого: рядом с <c>policy.json</c> лежит <c>policy.json.sig</c> —
/// подпись RSA-SHA256 над БАЙТАМИ файла, записанная в base64. Открытый ключ вшит в программу
/// (<see cref="PublicKeyBase64"/>), закрытый остается у организации. Подменили файл — подпись
/// не сойдется; подменили и подпись — тем более, ключа для нее нет.
///
/// Проверка нужна не всем источникам: реестр <c>HKLM</c> и папка установки защищены правами
/// самой операционной системы, туда без администратора не записать. Кому подпись обязательна,
/// решает <see cref="PolicyLoader"/>.
/// </summary>
public static class PolicySignature
{
    /// <summary>
    /// Открытый ключ организации в виде base64 от SubjectPublicKeyInfo (то, что дает
    /// <c>openssl rsa -pubout -outform DER</c>). Пусто — ключа в сборке нет, и тогда ни одна
    /// подпись не считается верной: источники, которым подпись обязательна, отбрасываются.
    ///
    /// Так и задумано. Пустой ключ означает «организация подписью не пользуется», и в этом
    /// случае политика раздается через реестр или папку установки, где ее защищают права
    /// доступа. Молча доверять неподписанному файлу из общей папки нельзя — это ровно та
    /// дыра, ради которой подпись и заведена.
    ///
    /// Как завести: сгенерировать пару, положить base64 открытого ключа сюда, пересобрать
    /// программу, а файл политики подписывать закрытым ключом при каждой раздаче.
    /// </summary>
    public const string PublicKeyBase64 = "";

    /// <summary>Вшит ли в эту сборку открытый ключ организации.</summary>
    public static bool KeyPresent => !string.IsNullOrWhiteSpace(PublicKeyBase64);

    /// <summary>
    /// Сходится ли подпись с содержимым — ключом, вшитым в эту сборку. Так проверяет политику
    /// загрузчик. <paramref name="error"/> объясняет отказ словами: текст уходит на экран
    /// «Безопасность» и в журнал.
    /// </summary>
    public static bool Verify(byte[]? content, string? signatureBase64, out string error)
    {
        if (!KeyPresent && content is { Length: > 0 } && !string.IsNullOrWhiteSpace(signatureBase64))
        {
            error = "открытый ключ организации в сборку не вшит — подписанные источники не принимаются";
            return false;
        }

        return VerifyWith(PublicKeyBase64, content, signatureBase64, out error);
    }

    /// <summary>
    /// То же, но ключ передается явно. Нужно там, где ключ не из сборки: проверка чужой
    /// подписи при разборе, диагностика «этот ли ключ» и тесты самой проверки — иначе
    /// единственный по-настоящему важный путь (подпись верна) остался бы непроверенным.
    /// </summary>
    public static bool VerifyWith(string? publicKeyBase64, byte[]? content, string? signatureBase64, out string error)
    {
        error = "";

        if (content is null || content.Length == 0)
        {
            error = "подписывать нечего: содержимое пусто";
            return false;
        }

        if (string.IsNullOrWhiteSpace(signatureBase64))
        {
            error = "подписи рядом с файлом политики нет";
            return false;
        }

        if (string.IsNullOrWhiteSpace(publicKeyBase64))
        {
            error = "открытого ключа для проверки нет";
            return false;
        }

        byte[] key, signature;
        try
        {
            key       = Convert.FromBase64String(publicKeyBase64.Trim());
            signature = Convert.FromBase64String(signatureBase64.Trim());
        }
        catch (FormatException)
        {
            error = "подпись или ключ записаны не в base64";
            return false;
        }

        try
        {
            using var rsa = RSA.Create();
            rsa.ImportSubjectPublicKeyInfo(key, out _);

            if (!rsa.VerifyData(content, signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1))
            {
                error = "подпись не сошлась с содержимым файла политики";
                return false;
            }

            return true;
        }
        catch (CryptographicException ex)
        {
            error = $"подпись проверить не удалось: {ex.Message}";
            return false;
        }
    }

    /// <inheritdoc cref="Verify(byte[], string, out string)"/>
    public static bool Verify(string? content, string? signatureBase64, out string error) =>
        Verify(Bytes(content), signatureBase64, out error);

    /// <inheritdoc cref="VerifyWith(string, byte[], string, out string)"/>
    public static bool VerifyWith(string? publicKeyBase64, string? content, string? signatureBase64, out string error) =>
        VerifyWith(publicKeyBase64, Bytes(content), signatureBase64, out error);

    /// <summary>
    /// Байты содержимого — всегда UTF-8 без метки порядка байт. Кодировка задана жестко и
    /// намеренно: подпись считается по байтам, и файл, перечитанный в другой кодировке, дал бы
    /// другую подпись при том же тексте.
    /// </summary>
    private static byte[]? Bytes(string? content) =>
        content is null ? null : new UTF8Encoding(false).GetBytes(content);
}
