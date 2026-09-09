using System.Net;
using SmartBase.Integrations;

namespace SmartBase.Tests.Unit;

/// <summary>
/// Сборка действующей политики из найденных источников (<see cref="PolicyLoader"/>).
///
/// Главный тест файла — <see cref="Broken_source_locks_the_gate_instead_of_being_dropped"/>.
/// Соблазн выбросить негодный источник и работать на остальных велик, а последствие ровно
/// обратное задуманному: разрешено то, что разрешают ВСЕ источники, значит выброшенный
/// источник РАСШИРЯЕТ права — и порча самого строгого файла превращается в способ открыть
/// дверь. Поэтому негодный источник запирает контур целиком.
/// </summary>
public sealed class PolicyLoaderTests
{
    private const string Wide   = """{"mode":"external","hosts":["*.zavod.local","api.postavshik.example"],"networks":["10.0.0.0/8"]}""";
    private const string Narrow = """{"mode":"external","hosts":["*.zavod.local"],"networks":["10.0.0.0/8"]}""";

    private static PolicyCandidate From(string source, string? json, bool signed = false, string? signature = null) =>
        new(source, json, signature, signed);

    // ── Ничего не нашли ───────────────────────────────────────────────────────

    [Fact]
    public void No_sources_means_nothing_is_allowed()
    {
        var result = PolicyLoader.Load([]);

        Assert.False(result.Set.Any);
        Assert.Equal(EgressMode.Isolated, result.Set.Mode);
        Assert.False(result.Set.AllowsHost("api.zavod.local"));
        Assert.Empty(result.Reads);
    }

    // ── Обычная работа ────────────────────────────────────────────────────────

    [Fact]
    public void Single_good_source_is_accepted()
    {
        var result = PolicyLoader.Load([From("реестр HKLM", Narrow)]);

        Assert.True(result.Set.AllowsHost("api.zavod.local"));
        Assert.Equal(EgressMode.External, result.Set.Mode);

        var read = Assert.Single(result.Reads);
        Assert.True(read.Accepted);
        Assert.Equal("реестр HKLM", read.Source);
    }

    [Fact]
    public void Two_sources_intersect()
    {
        var result = PolicyLoader.Load([From("реестр HKLM", Wide), From("папка установки", Narrow)]);

        Assert.True(result.Set.AllowsHost("api.zavod.local"));
        Assert.False(result.Set.AllowsHost("api.postavshik.example"));   // разрешает только один
        Assert.True(result.Set.AllowsAddress(IPAddress.Parse("10.0.7.15")));
        Assert.Equal(2, result.Reads.Count);
        Assert.All(result.Reads, read => Assert.True(read.Accepted));
    }

    // ── Порча источника ───────────────────────────────────────────────────────

    /// <summary>
    /// Битый файл рядом с исправным не «просто игнорируется»: контур запирается целиком, и на
    /// экране «Безопасность» видно, какой именно источник виноват.
    /// </summary>
    [Fact]
    public void Broken_source_locks_the_gate_instead_of_being_dropped()
    {
        var result = PolicyLoader.Load([From("реестр HKLM", Narrow), From("папка раздачи", "мусор")]);

        Assert.False(result.Set.AllowsHost("api.zavod.local"));
        Assert.Equal(EgressMode.Isolated, result.Set.Mode);

        var broken = result.Reads.Single(read => read.Source == "папка раздачи");
        Assert.False(broken.Accepted);
        Assert.Contains("не разобрался", broken.Note, StringComparison.Ordinal);

        Assert.True(result.Reads.Single(read => read.Source == "реестр HKLM").Accepted);
    }

    /// <summary>
    /// Недоступный источник — тоже запрет. Подставить последнюю известную копию обязан
    /// вызывающий: «источник был, а что в нем, мы не знаем» безопасно трактовать только так.
    /// </summary>
    [Fact]
    public void Unreachable_source_locks_the_gate()
    {
        var result = PolicyLoader.Load([From("реестр HKLM", Narrow), From("папка раздачи", null)]);

        Assert.False(result.Set.AllowsHost("api.zavod.local"));

        var unreachable = result.Reads.Single(read => read.Source == "папка раздачи");
        Assert.False(unreachable.Accepted);
        Assert.Contains("недоступен", unreachable.Note, StringComparison.Ordinal);
    }

    /// <summary>Подставленная копия читается как обычный источник — с пометкой в имени.</summary>
    [Fact]
    public void Cached_copy_is_read_like_any_other_source()
    {
        var result = PolicyLoader.Load([From("папка раздачи (последняя известная)", Narrow)]);

        Assert.True(result.Set.AllowsHost("api.zavod.local"));
        Assert.True(Assert.Single(result.Reads).Accepted);
    }

    // ── Подпись ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Источнику из папки, доступной на запись, подпись обязательна. Без ключа в сборке такой
    /// источник не принимается — и контур запирается, а не работает на остальных источниках.
    /// </summary>
    [Fact]
    public void Source_that_requires_a_signature_is_rejected_without_a_valid_one()
    {
        var result = PolicyLoader.Load([
            From("реестр HKLM", Narrow),
            From("папка раздачи", Narrow, signed: true, signature: null),
        ]);

        Assert.False(result.Set.AllowsHost("api.zavod.local"));

        var signed = result.Reads.Single(read => read.Source == "папка раздачи");
        Assert.False(signed.Accepted);
        Assert.NotEqual("", signed.Note);
    }

    /// <summary>Источникам без требования подписи она и не нужна: их защищают права доступа.</summary>
    [Fact]
    public void Source_without_a_signature_requirement_is_accepted_as_is()
    {
        var result = PolicyLoader.Load([From("реестр HKLM", Narrow, signed: false)]);

        Assert.True(Assert.Single(result.Reads).Accepted);
        Assert.True(result.Set.AllowsHost("api.zavod.local"));
    }

    // ── Отчет для экрана ──────────────────────────────────────────────────────

    /// <summary>
    /// Порядок строк отчета — порядок чтения источников: человек на экране «Безопасность»
    /// видит их в том же порядке, в каком они перечислены в документации.
    /// </summary>
    [Fact]
    public void Reads_keep_the_order_of_sources()
    {
        var result = PolicyLoader.Load([
            From("реестр HKLM", Narrow),
            From("папка установки", Narrow),
            From("папка раздачи", null),
        ]);

        Assert.Equal(["реестр HKLM", "папка установки", "папка раздачи"],
            result.Reads.Select(read => read.Source));
    }
}
