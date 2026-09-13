using SmartBase.Integrations;

namespace SmartBase.Tests.Unit;

/// <summary>
/// Разбор файла политики организации (<see cref="PolicyJson"/>). Правило, которое здесь
/// сторожится: **что не разобралось, то запрещено.** Половина политики опаснее ее отсутствия —
/// отсутствие видно на экране «Безопасность», а тихо потерянная строка белого списка нет.
///
/// Поэтому отдельными тестами закрыты все виды порчи файла: не тот тип поля, незнакомый режим,
/// нечитаемая дата, версия из будущего. Каждый обязан давать отказ целиком, а не «возьмем что
/// поняли».
/// </summary>
public sealed class PolicyJsonTests
{
    private const string Source = "тест";

    // ── Прием по сети ─────────────────────────────────────────────────────────

    /// <summary>
    /// Слушать адрес машины разрешает политика, и по умолчанию — нет: поля в файле не назвали,
    /// значит сетевой прием запрещен. Это то же правило, что у остальных рамок: отсутствие
    /// настройки обязано быть безопасным состоянием.
    /// </summary>
    [Fact]
    public void Listening_on_the_network_is_forbidden_unless_the_policy_says_otherwise()
    {
        var silent = PolicyJson.Parse("""{ "version": 1, "mode": "internal" }""", Source, out _);

        Assert.NotNull(silent);
        Assert.False(silent!.AllowListen);

        var allowed = PolicyJson.Parse(
            """{ "version": 1, "mode": "internal", "allowNetworkListen": true }""", Source, out _);

        Assert.True(allowed!.AllowListen);
    }

    /// <summary>Не тот тип поля — отказ целиком, как и у остальных: опечатку нельзя понимать.</summary>
    [Fact]
    public void A_listening_flag_that_is_not_a_flag_refuses_the_whole_file()
    {
        var policy = PolicyJson.Parse(
            """{ "version": 1, "allowNetworkListen": "да" }""", Source, out var error);

        Assert.Null(policy);
        Assert.Contains("allowNetworkListen", error, StringComparison.Ordinal);
    }

    // ── Разбор целого файла ───────────────────────────────────────────────────

    [Fact]
    public void Full_policy_is_read_field_by_field()
    {
        const string json = """
        {
          "version": 1,
          "mode": "internal",
          "hosts":    ["*.zavod.local", "api.1c-obmen.local"],
          "networks": ["10.0.0.0/8"],
          "folders":  ["/obmen"],
          "maxRowsPerRun":  5000,
          "maxBytesPerDay": 52428800,
          "maxRunsPerHour": 60,
          "validUntil": "2026-12-31",
          "userMayAddHosts": true
        }
        """;

        var policy = PolicyJson.Parse(json, Source, out var error);

        Assert.NotNull(policy);
        Assert.Equal("", error);
        Assert.Equal(EgressMode.Internal, policy!.Mode);
        Assert.Equal(["*.zavod.local", "api.1c-obmen.local"], policy.Hosts);
        Assert.Equal(["10.0.0.0/8"], policy.Networks);
        Assert.Equal(["/obmen"], policy.Folders);
        Assert.Equal(5000, policy.MaxRowsPerRun);
        Assert.Equal(52_428_800, policy.MaxBytesPerDay);
        Assert.Equal(60, policy.MaxRunsPerHour);
        Assert.Equal(new DateOnly(2026, 12, 31), policy.ValidUntil);
        Assert.True(policy.UserMayAddHosts);
        Assert.Equal(Source, policy.Source);
    }

    /// <summary>
    /// Пустой объект — это «ничего не разрешено», а не «все по умолчанию разрешено». Забытое
    /// поле обязано сужать права, иначе неполный файл открывает больше полного.
    /// </summary>
    [Fact]
    public void Missing_fields_fall_back_to_the_strictest_values()
    {
        var policy = PolicyJson.Parse("{}", Source, out var error);

        Assert.NotNull(policy);
        Assert.Equal("", error);
        Assert.Equal(EgressMode.Isolated, policy!.Mode);
        Assert.Empty(policy.Hosts);
        Assert.Empty(policy.Networks);
        Assert.Empty(policy.Folders);
        Assert.Equal(0, policy.MaxRowsPerRun);
        Assert.Equal(0, policy.MaxBytesPerDay);
        Assert.Equal(0, policy.MaxRunsPerHour);
        Assert.Null(policy.ValidUntil);
        Assert.False(policy.UserMayAddHosts);
    }

    /// <summary>Политику пишут руками, поэтому комментарии и лишняя запятая в конце допустимы.</summary>
    [Fact]
    public void Comments_and_trailing_commas_are_tolerated()
    {
        const string json = """
        {
          // белый список отдела информационных технологий
          "mode": "external",
          "hosts": ["api.zavod.local",],
        }
        """;

        var policy = PolicyJson.Parse(json, Source, out _);

        Assert.NotNull(policy);
        Assert.Equal(EgressMode.External, policy!.Mode);
        Assert.Equal(["api.zavod.local"], policy.Hosts);
    }

    /// <summary>Незнакомое поле — не ошибка: файл может быть написан для более новой сборки.</summary>
    [Fact]
    public void Unknown_field_is_ignored()
    {
        var policy = PolicyJson.Parse("""{"mode":"external","чтоТоНовое":42}""", Source, out _);

        Assert.NotNull(policy);
        Assert.Equal(EgressMode.External, policy!.Mode);
    }

    // ── Порча файла ───────────────────────────────────────────────────────────

    [Theory]
    [InlineData("",                                  "пуст")]
    [InlineData("   ",                               "пуст")]
    [InlineData("не json вовсе",                     "не разобрался")]
    [InlineData("[1,2,3]",                           "ожидался объект")]
    [InlineData("""{"mode":"полный доступ"}""",      "незнакомый режим")]
    [InlineData("""{"mode":42}""",                   "должно быть строкой")]
    [InlineData("""{"validUntil":"31.12.2026"}""",   "не разобралась")]
    [InlineData("""{"validUntil":2026}""",           "должно быть строкой")]
    [InlineData("""{"hosts":"api.zavod.local"}""",   "списком строк")]
    [InlineData("""{"hosts":[1,2]}""",               "не-строка")]
    [InlineData("""{"maxRowsPerRun":"много"}""",     "целым числом")]
    [InlineData("""{"userMayAddHosts":"да"}""",      "true или false")]
    [InlineData("""{"version":2}""",                 "новее поддерживаемой")]
    public void Broken_policy_is_rejected_with_a_readable_reason(string json, string expectedPartOfError)
    {
        var policy = PolicyJson.Parse(json, Source, out var error);

        Assert.Null(policy);
        Assert.Contains(expectedPartOfError, error, StringComparison.Ordinal);
    }

    /// <summary>
    /// Версия старее нашей читается: поля с тех пор только добавлялись, и отказ означал бы,
    /// что обновление программы на части машин выключает обмен на остальных.
    /// </summary>
    [Fact]
    public void Older_format_version_is_still_read()
    {
        var policy = PolicyJson.Parse("""{"version":1,"mode":"external"}""", Source, out _);

        Assert.NotNull(policy);
    }

    [Fact]
    public void Blank_entries_in_lists_are_dropped()
    {
        var policy = PolicyJson.Parse("""{"hosts":["api.zavod.local","","   "]}""", Source, out _);

        Assert.NotNull(policy);
        Assert.Equal(["api.zavod.local"], policy!.Hosts);
    }

    /// <summary>Отрицательная квота — это не «без ограничений», а ноль.</summary>
    [Fact]
    public void Negative_quota_is_clamped_to_zero()
    {
        var policy = PolicyJson.Parse("""{"maxRowsPerRun":-1,"maxBytesPerDay":-100}""", Source, out _);

        Assert.NotNull(policy);
        Assert.Equal(0, policy!.MaxRowsPerRun);
        Assert.Equal(0, policy.MaxBytesPerDay);
    }

    // ── Мастер-пароль: подсчет и проверка сходятся ────────────────────────────

    /// <summary>
    /// Строка, посчитанная окном «Мастер-пароль для политики», принимается собственной проверкой
    /// программы: попадает в файл, читается разбором и подтверждает тот самый пароль.
    ///
    /// Цепочка короткая, но живет в трех местах — счет (<see cref="MasterPassword.Hash"/>),
    /// разбор файла и сверка (<see cref="EgressPolicySet.MasterMatches"/>), — и разойтись им
    /// достаточно в одном знаке формата. Разойдись они молча, администратор впишет в политику
    /// строку, которую программа не примет, и узнает об этом в момент, когда некому будет
    /// подтвердить исходящее направление.
    /// </summary>
    [Fact]
    public void A_hash_computed_by_the_program_is_accepted_by_the_program()
    {
        var hash = MasterPassword.Hash("пароль-организации");

        var policy = PolicyJson.Parse($$"""{"mode":"external","masterHash":"{{hash}}"}""",
                                      Source, out var error);

        Assert.NotNull(policy);
        Assert.Equal("", error);
        Assert.True(policy!.HasMaster);

        var set = EgressPolicySet.Of(policy);

        Assert.True (set.HasMaster);
        Assert.True (set.MasterMatches("пароль-организации"));
        Assert.False(set.MasterMatches("другой-пароль"));
    }

    /// <summary>
    /// Пустая строка мастер-пароля — это «пароля нет», а не «подходит любой». Так выглядит
    /// политика, в которую строку еще не вписали, и подтверждать направления ею нельзя ничем.
    /// </summary>
    [Fact]
    public void A_policy_without_a_master_hash_confirms_nothing()
    {
        var policy = PolicyJson.Parse("""{"mode":"external","masterHash":""}""", Source, out _);

        Assert.NotNull(policy);
        Assert.False(policy!.HasMaster);
        Assert.False(EgressPolicySet.Of(policy).MasterMatches(""));
    }

    // ── Прокси и срок хранения журнала ────────────────────────────────────────

    /// <summary>Оба новых поля читаются; отсутствуют — прямое соединение и вечное хранение.</summary>
    [Fact]
    public void The_proxy_and_the_journal_term_are_read()
    {
        var named = PolicyJson.Parse("""
            {"mode":"external","proxy":"http://proxy.zavod.local:3128","journalKeepDays":365}
            """, Source, out var error);

        Assert.Equal("", error);
        Assert.Equal("http://proxy.zavod.local:3128", named!.Proxy);
        Assert.True(named.HasProxy);
        Assert.Equal(365, named.JournalKeepDays);

        var plain = PolicyJson.Parse("""{"mode":"external"}""", Source, out _);

        Assert.Equal("", plain!.Proxy);
        Assert.False(plain.HasProxy);
        Assert.Equal(0, plain.JournalKeepDays);
    }

    /// <summary>Не тот тип — отказ разбора целиком, как и у всех прочих полей.</summary>
    [Fact]
    public void A_proxy_of_the_wrong_type_refuses_the_whole_file()
    {
        Assert.Null(PolicyJson.Parse("""{"mode":"external","proxy":3128}""", Source, out var error));
        Assert.Contains("proxy", error, StringComparison.Ordinal);

        Assert.Null(PolicyJson.Parse("""{"mode":"external","journalKeepDays":"год"}""",
                                     Source, out var second));
        Assert.Contains("journalKeepDays", second, StringComparison.Ordinal);
    }

    /// <summary>
    /// Срок хранения журнала у набора — САМЫЙ КОРОТКИЙ из названных, а 0 значит «вечно» и в счет
    /// не идет. Иначе источник, промолчавший о сроке, отменял бы срок, назначенный другим.
    /// </summary>
    [Fact]
    public void The_shortest_journal_term_wins_and_silence_is_not_a_term()
    {
        var year  = PolicyJson.Parse("""{"mode":"external","journalKeepDays":365}""", Source, out _)!;
        var month = PolicyJson.Parse("""{"mode":"external","journalKeepDays":30}""",  Source, out _)!;
        var mute  = PolicyJson.Parse("""{"mode":"external"}""",                        Source, out _)!;

        Assert.Equal(30,  EgressPolicySet.Of(year, month).JournalKeepDays);
        Assert.Equal(365, EgressPolicySet.Of(year, mute).JournalKeepDays);
        Assert.Equal(0,   EgressPolicySet.Of(mute).JournalKeepDays);
    }
}
