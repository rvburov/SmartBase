using SmartBase.Integrations;

namespace SmartBase.Tests.Unit;

/// <summary>
/// Мастер-пароль (<see cref="MasterPassword"/>) и исходящее направление
/// (<see cref="OutgoingDirection"/>) — чистая логика без базы и без экрана.
///
/// Проверяется то, чем обходят согласование: подтвердили одно, а работает другое; разрешение,
/// выданное навсегда; забытый и потому незаметный обмен. Плюс отказы пароля — испорченный хэш
/// обязан считаться несовпадением, а не отсутствием проверки.
/// </summary>
public sealed class OutgoingDirectionTests
{
    private static readonly DateTimeOffset Now =
        new(new DateTime(2026, 9, 11, 12, 0, 0, DateTimeKind.Utc));

    private static IntegrationRequest Push(string body = "{\"итог\":\"готово\"}") =>
        IntegrationRequest.New() with
        {
            Id     = 7,
            Name   = "Отдать итог",
            Method = RequestMethod.Post,
            Path   = "/result",
            Body   = body,
        };

    // ── Мастер-пароль ─────────────────────────────────────────────────────────

    [Fact]
    public void The_password_matches_its_own_hash()
    {
        var hash = MasterPassword.Hash("секрет-организации");

        Assert.True (MasterPassword.Matches(hash, "секрет-организации"));
        Assert.False(MasterPassword.Matches(hash, "секрет-организаци"));
        Assert.False(MasterPassword.Matches(hash, ""));
    }

    /// <summary>Соль своя у каждого хэша: два одинаковых пароля дают разные строки.</summary>
    [Fact]
    public void The_same_password_hashes_differently_every_time()
    {
        Assert.NotEqual(MasterPassword.Hash("один"), MasterPassword.Hash("один"));
    }

    /// <summary>
    /// Испорченная строка хэша — НЕСОВПАДЕНИЕ, а не «проверки нет». Иначе достаточно испортить
    /// одну букву в политике, чтобы подтверждать направления чем угодно.
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("не хэш вовсе")]
    [InlineData("pbkdf2$210000$соль$хэш")]           // не base64
    [InlineData("pbkdf2$0$AAAA$AAAA")]               // ноль итераций
    [InlineData("scrypt$210000$AAAA$AAAA")]          // чужой алгоритм
    [InlineData("pbkdf2$210000$AAAA")]               // частей меньше
    public void A_broken_hash_matches_nothing(string stored)
    {
        Assert.False(MasterPassword.Matches(stored, "любой пароль"));
    }

    [Fact]
    public void A_hash_is_recognised_as_present_or_absent()
    {
        Assert.True (MasterPassword.Defined(MasterPassword.Hash("пароль")));
        Assert.False(MasterPassword.Defined(""));
        Assert.False(MasterPassword.Defined("просто текст"));
    }

    // ── Политика ──────────────────────────────────────────────────────────────

    private static EgressPolicy Source(string master, string name = "тест") =>
        new(EgressMode.Internal, [], [], [], 100, 1024, 10, null, false, name, master);

    /// <summary>
    /// Пароля нет ни в одном источнике — подтверждать нечем, и это НЕ «можно всем». Отсутствие
    /// настройки обязано быть безопасным состоянием.
    /// </summary>
    [Fact]
    public void Without_a_master_in_the_policy_nothing_can_be_confirmed()
    {
        var set = EgressPolicySet.Of(Source(""));

        Assert.False(set.HasMaster);
        Assert.False(set.MasterMatches("что угодно"));
        Assert.False(set.MasterMatches(""));
    }

    [Fact]
    public void A_single_source_decides_alone()
    {
        var set = EgressPolicySet.Of(Source(MasterPassword.Hash("правильный")));

        Assert.True (set.MasterMatches("правильный"));
        Assert.False(set.MasterMatches("другой"));
    }

    /// <summary>
    /// Правило то же, что у остальных рамок, — ПЕРЕСЕЧЕНИЕ: пароль обязан подойти каждому
    /// источнику, который его назвал. Два источника с разными паролями означают, что две
    /// инстанции не договорились, и подтвердить нельзя ни одним.
    /// </summary>
    [Fact]
    public void The_password_must_satisfy_every_source_that_names_one()
    {
        var same = EgressPolicySet.Of(Source(MasterPassword.Hash("общий"), "реестр"),
                                      Source(MasterPassword.Hash("общий"), "папка"));

        Assert.True(same.MasterMatches("общий"));

        var apart = EgressPolicySet.Of(Source(MasterPassword.Hash("первый"), "реестр"),
                                       Source(MasterPassword.Hash("второй"), "папка"));

        Assert.False(apart.MasterMatches("первый"));
        Assert.False(apart.MasterMatches("второй"));
    }

    /// <summary>Источник, пароля не назвавший, в пересечении не участвует и не мешает.</summary>
    [Fact]
    public void A_source_without_a_master_neither_helps_nor_blocks()
    {
        var set = EgressPolicySet.Of(Source(MasterPassword.Hash("общий"), "реестр"),
                                     Source("", "папка"));

        Assert.True(set.HasMaster);
        Assert.True(set.MasterMatches("общий"));
    }

    // ── Что считается исходящим ───────────────────────────────────────────────

    /// <summary>
    /// Граница проходит по методу: <c>GET</c> спрашивает, остальные меняют что-то на той
    /// стороне. Требовать мастер-пароль на каждое чтение значило бы запереть обмен, который и
    /// так ограничен белым списком и квотами.
    /// </summary>
    [Theory]
    [InlineData(RequestMethod.Get,    false)]
    [InlineData(RequestMethod.Post,   true)]
    [InlineData(RequestMethod.Put,    true)]
    [InlineData(RequestMethod.Patch,  true)]
    [InlineData(RequestMethod.Delete, true)]
    public void Reading_is_not_an_outgoing_direction(RequestMethod method, bool outward)
    {
        Assert.Equal(outward, (IntegrationRequest.New() with { Method = method }).Outward);
    }

    // ── Отпечаток ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Подтвердить безобидный запрос и дописать в него выгрузку — самый дешевый обход
    /// согласования. Отпечаток считается по всему, что влияет на уходящее.
    /// </summary>
    [Fact]
    public void Everything_that_leaves_changes_the_fingerprint()
    {
        var request = Push();
        var same    = request.Fingerprint();

        Assert.Equal(same, Push().Fingerprint());

        Assert.NotEqual(same, (request with { Body   = "{\"итог\":\"другое\"}" }).Fingerprint());
        Assert.NotEqual(same, (request with { Path   = "/other" }).Fingerprint());
        Assert.NotEqual(same, (request with { Method = RequestMethod.Put }).Fingerprint());
        Assert.NotEqual(same, (request with
        {
            Headers = [new IntegrationHeader("X-Куда", "наружу")],
        }).Fingerprint());
    }

    /// <summary>
    /// Имя карточки в отпечаток не входит: переименование не меняет того, что уходит, а
    /// сбрасывать подтверждение из-за него значило бы приучить подтверждать не глядя.
    /// </summary>
    [Fact]
    public void Renaming_the_card_keeps_the_confirmation()
    {
        var request = Push();

        Assert.Equal(request.Fingerprint(), (request with { Name = "Другое имя" }).Fingerprint());
    }

    /// <summary>Перестановка заголовков меняет то, что уходит, — значит, меняет и отпечаток.</summary>
    [Fact]
    public void Header_order_is_part_of_the_fingerprint()
    {
        var first  = Push() with { Headers = [new("A", "1"), new("B", "2")] };
        var second = Push() with { Headers = [new("B", "2"), new("A", "1")] };

        Assert.NotEqual(first.Fingerprint(), second.Fingerprint());
    }

    // ── Жизнь направления ─────────────────────────────────────────────────────

    [Fact]
    public void A_fresh_confirmation_works()
    {
        var request   = Push();
        var direction = OutgoingDirection.Confirm(request, "Петров П. П.", Now, days: 90);

        Assert.Null(direction.Trouble(request, Now));
        Assert.True(direction.Works(request, Now.AddDays(20)));
        Assert.Contains("Петров П. П.", direction.Describe(), StringComparison.Ordinal);

        // Срок в 90 дней сам по себе до 90 дней не доживет: простой считается от подтверждения,
        // и направление, которым ни разу не воспользовались, отключится через месяц. Так и надо —
        // подтвержденный впрок обмен ничем не отличается от забытого.
        Assert.False(direction.Works(request, Now.AddDays(80)));
    }

    /// <summary>Подтвердили одно, а работает другое — направление молчит и говорит почему.</summary>
    [Fact]
    public void An_edited_request_loses_its_confirmation()
    {
        var direction = OutgoingDirection.Confirm(Push(), "Петров П. П.", Now, days: 90);

        var edited = Push("{\"итог\":\"и еще все записи\"}");

        Assert.Contains("правили после подтверждения", direction.Trouble(edited, Now)!,
                        StringComparison.Ordinal);
    }

    /// <summary>Разрешение, выданное навсегда, через год не помнит никто, а работает оно по-прежнему.</summary>
    [Fact]
    public void An_expired_confirmation_stops_working()
    {
        var request   = Push();
        var direction = OutgoingDirection.Confirm(request, "Петров П. П.", Now, days: 30);

        Assert.True(direction.Works(request, Now.AddDays(29)));
        Assert.Contains("срок", direction.Trouble(request, Now.AddDays(31))!, StringComparison.Ordinal);
    }

    /// <summary>
    /// Настроенный и забытый обмен — открытая дверь, о которой никто не думает. Месяц простоя, и
    /// направление отключается само.
    /// </summary>
    [Fact]
    public void An_idle_direction_switches_itself_off()
    {
        var request   = Push();
        var direction = OutgoingDirection.Confirm(request, "Петров П. П.", Now, days: 365);

        Assert.True(direction.Works(request, Now.AddDays(OutgoingDirection.IdleDays - 1)));

        Assert.Contains("не пользовались",
                        direction.Trouble(request, Now.AddDays(OutgoingDirection.IdleDays + 1))!,
                        StringComparison.Ordinal);
    }

    /// <summary>Использование сдвигает отсчет простоя — иначе работающий обмен отключался бы сам.</summary>
    [Fact]
    public void Using_the_direction_restarts_the_idle_count()
    {
        var request = Push();

        var direction = OutgoingDirection.Confirm(request, "Петров П. П.", Now, days: 365) with
        {
            UsedAt = Now.AddDays(25),
        };

        Assert.True(direction.Works(request, Now.AddDays(50)));
    }

    /// <summary>Срок не уезжает дальше года, даже если попросили тысячу дней.</summary>
    [Fact]
    public void A_confirmation_never_reaches_further_than_a_year()
    {
        var direction = OutgoingDirection.Confirm(Push(), "Петров П. П.", Now, days: 10_000);

        Assert.Equal(Now.AddDays(OutgoingDirection.MaxDays), direction.Until);
    }

    /// <summary>Подтверждение чужому запросу не годится: ключ проверяется первым.</summary>
    [Fact]
    public void A_confirmation_of_another_request_does_not_count()
    {
        var direction = OutgoingDirection.Confirm(Push(), "Петров П. П.", Now, days: 90);

        Assert.NotNull(direction.Trouble(Push() with { Id = 8 }, Now));
    }
}
