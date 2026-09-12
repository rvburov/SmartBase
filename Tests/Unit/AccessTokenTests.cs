using SmartBase.Integrations;

namespace SmartBase.Tests.Unit;

/// <summary>
/// Токен доступа как модель: текстовый вид, проверка секрета и ключ робота
/// (docs/ROADMAP-API.md, 10.12).
///
/// Главное, за чем тут следят: секрет не восстанавливается из хранимого, ключ строки в тексте
/// токена есть (иначе каждое обращение перебирало бы все токены подряд), а испорченный текст
/// разбирается в отказ, а не в исключение — его присылает чужая программа, и падать из-за
/// опечатки в чужом конфигурационном файле программа не должна.
/// </summary>
public sealed class AccessTokenTests
{
    [Fact]
    public void A_token_reads_back_as_its_key_and_secret()
    {
        var secret = AccessToken.NewSecret();
        var text   = AccessToken.Compose(42, secret);

        Assert.True(AccessToken.TryParse(text, out var id, out var back));
        Assert.Equal(42, id);
        Assert.Equal(secret, back);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("sb_42")]                      // нет секрета
    [InlineData("sb_42_")]                     // секрет пустой
    [InlineData("xx_42_abc")]                  // чужое начало
    [InlineData("sb_ноль_abc")]                // ключ не число
    [InlineData("sb_0_abc")]                   // ключа 0 в реестре не бывает
    [InlineData("sb_-1_abc")]
    [InlineData("sb_42_abc_extra")]            // лишняя часть
    public void Garbage_is_refused_and_never_throws(string text)
    {
        Assert.False(AccessToken.TryParse(text, out _, out _));
    }

    [Fact]
    public void Null_is_refused_too()
    {
        Assert.False(AccessToken.TryParse(null, out _, out _));
    }

    /// <summary>
    /// Секрет проверяется по хэшу и не хранится: из строки реестра его не достать, а значит,
    /// правка app.db не отдает токен.
    /// </summary>
    [Fact]
    public void The_secret_is_checked_by_hash_and_is_not_stored()
    {
        var secret = AccessToken.NewSecret();
        var hash   = AccessToken.HashOf(secret);

        var token = new AccessToken(1, "Учетная система", 1, hash, "robot.1@пк", "", "", true);

        Assert.True(token.Matches(secret));
        Assert.False(token.Matches(secret + "1"));
        Assert.False(token.Matches(""));
        Assert.DoesNotContain(secret, hash, StringComparison.Ordinal);
    }

    /// <summary>Два токена с одним секретом дают разные хэши — соль своя у каждого.</summary>
    [Fact]
    public void The_same_secret_hashes_differently_every_time()
    {
        const string secret = "одинаковый";

        Assert.NotEqual(AccessToken.HashOf(secret), AccessToken.HashOf(secret));
    }

    /// <summary>
    /// Ключ робота включает машину: токены лежат в app.db ОДНОЙ машины, а база общая, и два
    /// компьютера, выдавшие токен с одним именем, обязаны получить разных роботов.
    /// </summary>
    [Fact]
    public void Robots_of_different_machines_are_different_users()
    {
        Assert.NotEqual(AccessToken.KeyOf(1, "ПК-17"), AccessToken.KeyOf(1, "ПК-18"));
        Assert.NotEqual(AccessToken.KeyOf(1, "ПК-17"), AccessToken.KeyOf(2, "ПК-17"));
        Assert.Equal(AccessToken.KeyOf(1, "ПК-17"),    AccessToken.KeyOf(1, "пк-17"));
    }

    [Fact]
    public void A_token_without_a_name_is_refused()
    {
        Assert.NotEqual("", AccessToken.NameTrouble(""));
        Assert.NotEqual("", AccessToken.NameTrouble("   "));
        Assert.NotEqual("", AccessToken.NameTrouble(new string('я', AccessToken.MaxNameLength + 1)));
        Assert.Equal("",    AccessToken.NameTrouble("Обмен учетной системы"));
    }
}
