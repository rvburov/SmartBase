using System.Net;
using SmartBase.Integrations;

namespace SmartBase.Tests.Unit;

/// <summary>
/// Политика организации и ее пересечение (<see cref="EgressPolicy"/>,
/// <see cref="EgressPolicySet"/>). Главное, что здесь сторожится, — два правила, на которых
/// держится весь контур:
///
/// 1. пустой набор источников значит «ничего нельзя», а не «ограничений нет»;
/// 2. разрешено только то, что разрешают ВСЕ источники, — подложенный рядом свой файл
///    политики не может ничего открыть.
///
/// Общая картина — docs/developer/features/integrations/overview.md.
/// </summary>
public sealed class EgressPolicyTests
{
    private static EgressPolicy Policy(
        EgressMode mode = EgressMode.External,
        string[]?  hosts = null,
        string[]?  networks = null,
        string[]?  folders = null,
        int        rows = 1000,
        long       bytes = 1024,
        int        runs = 10,
        DateOnly?  until = null,
        bool       userMayAddHosts = false) =>
        new(mode, hosts ?? [], networks ?? [], folders ?? [],
            rows, bytes, runs, until, userMayAddHosts, "тест");

    // ── Пустой набор ──────────────────────────────────────────────────────────

    /// <summary>
    /// Отсутствие политики обязано быть безопасным состоянием. Проверяется каждая дверь:
    /// хватит одной, которая при пустом наборе отвечает «да», — и контура нет.
    /// </summary>
    [Fact]
    public void Set_without_sources_allows_nothing()
    {
        var set = EgressPolicySet.Closed;

        Assert.False(set.Any);
        Assert.Equal(EgressMode.Isolated, set.Mode);
        Assert.False(set.AllowsHost("api.zavod.local"));
        Assert.False(set.AllowsAddress(IPAddress.Parse("10.0.0.1")));
        Assert.False(set.AllowsFolder(Path.GetTempPath()));
        Assert.False(set.UserMayAddHosts);
        Assert.Equal(0, set.MaxRowsPerRun);
        Assert.Equal(0, set.MaxBytesPerDay);
        Assert.Equal(0, set.MaxRunsPerHour);
    }

    // ── Пересечение ───────────────────────────────────────────────────────────

    [Fact]
    public void Host_is_allowed_only_when_every_source_allows_it()
    {
        var wide   = Policy(hosts: ["*.zavod.local", "api.postavshik.example"]);
        var narrow = Policy(hosts: ["*.zavod.local"]);

        var set = EgressPolicySet.Of(wide, narrow);

        Assert.True(set.AllowsHost("api.zavod.local"));
        Assert.False(set.AllowsHost("api.postavshik.example"));   // разрешает только один из двух
    }

    /// <summary>Свой файл политики рядом не открывает ничего: пересечение только сужает.</summary>
    [Fact]
    public void Extra_source_can_only_narrow_the_list()
    {
        var official = Policy(hosts: ["*.zavod.local"]);
        var homemade = Policy(hosts: ["*.zavod.local", "api.attacker.example"]);

        var set = EgressPolicySet.Of(official, homemade);

        Assert.False(set.AllowsHost("api.attacker.example"));
        Assert.True(set.AllowsHost("api.zavod.local"));
    }

    [Fact]
    public void Strictest_mode_wins()
    {
        Assert.Equal(EgressMode.Isolated, EgressPolicySet.Of(
            Policy(mode: EgressMode.External),
            Policy(mode: EgressMode.Isolated)).Mode);

        Assert.Equal(EgressMode.Internal, EgressPolicySet.Of(
            Policy(mode: EgressMode.External),
            Policy(mode: EgressMode.Internal)).Mode);
    }

    [Fact]
    public void Smallest_quota_wins()
    {
        var set = EgressPolicySet.Of(
            Policy(rows: 5000, bytes: 50_000_000, runs: 60),
            Policy(rows: 100,  bytes: 1_000_000,  runs: 6));

        Assert.Equal(100, set.MaxRowsPerRun);
        Assert.Equal(1_000_000, set.MaxBytesPerDay);
        Assert.Equal(6, set.MaxRunsPerHour);
    }

    [Fact]
    public void Nearest_expiry_wins_and_expiry_is_inclusive()
    {
        var set = EgressPolicySet.Of(
            Policy(until: new DateOnly(2026, 12, 31)),
            Policy(until: new DateOnly(2026, 10, 1)));

        Assert.Equal(new DateOnly(2026, 10, 1), set.ValidUntil);
        Assert.False(set.Expired(new DateOnly(2026, 10, 1)));   // последний день еще действует
        Assert.True(set.Expired(new DateOnly(2026, 10, 2)));
    }

    [Fact]
    public void Policy_without_expiry_never_expires()
    {
        var set = EgressPolicySet.Of(Policy(until: null));

        Assert.Null(set.ValidUntil);
        Assert.False(set.Expired(new DateOnly(2099, 1, 1)));
    }

    [Fact]
    public void User_may_add_hosts_only_when_every_source_allows_it()
    {
        Assert.True(EgressPolicySet.Of(
            Policy(userMayAddHosts: true),
            Policy(userMayAddHosts: true)).UserMayAddHosts);

        Assert.False(EgressPolicySet.Of(
            Policy(userMayAddHosts: true),
            Policy(userMayAddHosts: false)).UserMayAddHosts);
    }

    // ── Сети и папки ──────────────────────────────────────────────────────────

    [Fact]
    public void Address_is_allowed_only_when_every_source_allows_it()
    {
        var set = EgressPolicySet.Of(
            Policy(networks: ["10.0.0.0/8", "192.168.0.0/16"]),
            Policy(networks: ["10.0.0.0/8"]));

        Assert.True(set.AllowsAddress(IPAddress.Parse("10.0.7.15")));
        Assert.False(set.AllowsAddress(IPAddress.Parse("192.168.1.1")));
    }

    [Fact]
    public void Folder_root_covers_its_subfolders()
    {
        var root = Path.Combine(Path.GetTempPath(), "obmen");
        var set  = EgressPolicySet.Of(Policy(folders: [root]));

        Assert.True(set.AllowsFolder(root));
        Assert.True(set.AllowsFolder(Path.Combine(root, "vhodyaschie")));
        Assert.True(set.AllowsFolder(Path.Combine(root, "vhodyaschie", "kd")));
    }

    /// <summary>
    /// Соседняя папка с похожим именем — не вложенная. Проверка строкового префикса без
    /// разделителя пустила бы «obmen2» внутрь корня «obmen».
    /// </summary>
    [Fact]
    public void Sibling_folder_with_a_similar_name_is_not_inside()
    {
        var root = Path.Combine(Path.GetTempPath(), "obmen");
        var set  = EgressPolicySet.Of(Policy(folders: [root]));

        Assert.False(set.AllowsFolder(Path.Combine(Path.GetTempPath(), "obmen2")));
        Assert.False(set.AllowsFolder(Path.GetTempPath()));
    }

    /// <summary>Выход наверх через «..» не выводит за корень: сравнивается полный путь.</summary>
    [Fact]
    public void Walking_up_out_of_the_root_is_denied()
    {
        var root = Path.Combine(Path.GetTempPath(), "obmen");
        var set  = EgressPolicySet.Of(Policy(folders: [root]));

        Assert.False(set.AllowsFolder(Path.Combine(root, "..", "chuzhaya")));
        Assert.True(set.AllowsFolder(Path.Combine(root, "vhodyaschie", "..", "ishodyaschie")));
    }
}
