using System.Security.Cryptography;
using System.Text;
using SmartBase.Integrations;

namespace SmartBase.Tests.Unit;

/// <summary>
/// Проверка подписи политики (<see cref="PolicySignature"/>). Подпись нужна источникам, которые
/// лежат в папке, доступной на запись, — прежде всего сетевой папке раздачи: без нее любой, кто
/// может писать в эту папку, переписал бы белый список.
///
/// Ключ проверки передается явно (<c>VerifyWith</c>): иначе единственный по-настоящему важный
/// путь — «подпись верна» — остался бы непроверенным, потому что вшитый в сборку ключ из теста
/// не подменить.
/// </summary>
public sealed class PolicySignatureTests
{
    private const string Policy = """{"mode":"external","hosts":["api.zavod.local"]}""";

    /// <summary>Пара ключей и подпись — как их делает организация при раздаче политики.</summary>
    private static (string PublicKey, string Signature) Sign(string content)
    {
        using var rsa = RSA.Create(2048);

        var signature = rsa.SignData(
            new UTF8Encoding(false).GetBytes(content),
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);

        return (Convert.ToBase64String(rsa.ExportSubjectPublicKeyInfo()), Convert.ToBase64String(signature));
    }

    // ── Подпись сходится ──────────────────────────────────────────────────────

    [Fact]
    public void Correct_signature_passes()
    {
        var (key, signature) = Sign(Policy);

        Assert.True(PolicySignature.VerifyWith(key, Policy, signature, out var error));
        Assert.Equal("", error);
    }

    // ── Подпись не сходится ───────────────────────────────────────────────────

    /// <summary>Подменили файл — подпись перестает сходиться. Ради этого все и заведено.</summary>
    [Fact]
    public void Tampered_content_fails()
    {
        var (key, signature) = Sign(Policy);
        const string tampered = """{"mode":"external","hosts":["api.attacker.example"]}""";

        Assert.False(PolicySignature.VerifyWith(key, tampered, signature, out var error));
        Assert.Contains("не сошлась", error, StringComparison.Ordinal);
    }

    /// <summary>Подменили и подпись заодно — не помогает: закрытого ключа у подменившего нет.</summary>
    [Fact]
    public void Signature_from_another_key_fails()
    {
        var (key, _) = Sign(Policy);
        var (_, foreignSignature) = Sign(Policy);

        Assert.False(PolicySignature.VerifyWith(key, Policy, foreignSignature, out var error));
        Assert.Contains("не сошлась", error, StringComparison.Ordinal);
    }

    [Fact]
    public void Missing_signature_fails()
    {
        var (key, _) = Sign(Policy);

        Assert.False(PolicySignature.VerifyWith(key, Policy, null, out var error));
        Assert.Contains("подписи рядом", error, StringComparison.Ordinal);
    }

    [Fact]
    public void Missing_key_fails()
    {
        var (_, signature) = Sign(Policy);

        Assert.False(PolicySignature.VerifyWith("", Policy, signature, out var error));
        Assert.Contains("ключа", error, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("не base64 вовсе")]
    [InlineData("QUJD")]           // разбирается из base64, но ключом не является
    public void Garbage_key_fails_without_throwing(string key)
    {
        var (_, signature) = Sign(Policy);

        Assert.False(PolicySignature.VerifyWith(key, Policy, signature, out var error));
        Assert.NotEqual("", error);
    }

    [Fact]
    public void Garbage_signature_fails_without_throwing()
    {
        var (key, _) = Sign(Policy);

        Assert.False(PolicySignature.VerifyWith(key, Policy, "не base64 вовсе", out var error));
        Assert.NotEqual("", error);
    }

    [Fact]
    public void Empty_content_fails()
    {
        var (key, signature) = Sign(Policy);

        Assert.False(PolicySignature.VerifyWith(key, "", signature, out var error));
        Assert.Contains("пусто", error, StringComparison.Ordinal);
    }

    // ── Ключ этой сборки ──────────────────────────────────────────────────────

    /// <summary>
    /// Пока ключ организации в сборку не вшит, подписанные источники не принимаются — и это
    /// правильное поведение, а не недоделка: неподписанный файл из общей папки и есть та дыра,
    /// ради которой подпись заведена. Тест фиксирует состояние сборки: появится ключ — он
    /// покажет, что состояние сменилось намеренно.
    /// </summary>
    [Fact]
    public void Build_without_an_embedded_key_accepts_no_signature()
    {
        if (PolicySignature.KeyPresent) return;   // ключ вшит — проверять здесь нечего

        var (_, signature) = Sign(Policy);

        Assert.False(PolicySignature.Verify(Policy, signature, out var error));
        Assert.Contains("не вшит", error, StringComparison.Ordinal);
    }
}
