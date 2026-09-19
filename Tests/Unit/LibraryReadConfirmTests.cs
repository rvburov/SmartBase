using SmartBase.Utils;

namespace SmartBase.Tests.Unit;

/// <summary>
/// Когда чтение документов спрашивает подтверждение (<see cref="LibrarySettings"/>).
///
/// Правило двухступенчатое: пока скорость не измерена — по числу документов, после первого
/// прогона — по времени. Второе честнее: у одного человека библиотека из текстовых PDF, у
/// другого — из тяжелых сканов, и одно и то же число документов означает у них разное.
/// </summary>
public sealed class LibraryReadConfirmTests
{
    private sealed class FakeStore : ISettingsDatabase
    {
        private readonly Dictionary<string, string> _d = new();
        public bool Has(string key) => _d.ContainsKey(key);
        public string? Get(string key) => _d.TryGetValue(key, out var v) ? v : null;
        public void Set(string key, string value) => _d[key] = value;
        public void Remove(string key) => _d.Remove(key);
        public IReadOnlyList<string> KeysWithPrefix(string prefix) =>
            _d.Keys.Where(k => k.StartsWith(prefix, StringComparison.Ordinal)).ToList();
    }

    private static void Fresh()
    {
        AppSettings.AttachStore(new FakeStore());
        LibrarySettings.Init();
    }

    // ── Скорость не измерена ─────────────────────────────────────────────────

    [Fact]
    public void Without_a_measurement_a_small_batch_starts_at_once()
    {
        Fresh();
        Assert.False(LibrarySettings.NeedsReadConfirmation(10));
    }

    [Fact]
    public void Without_a_measurement_a_large_batch_asks()
    {
        Fresh();
        Assert.True(LibrarySettings.NeedsReadConfirmation(LibrarySettings.ConfirmReadFromCount));
    }

    // ── Скорость измерена ────────────────────────────────────────────────────

    // Быстрая библиотека: тысяча документов по 20 мс — двадцать секунд, ждать можно и молча.
    [Fact]
    public void A_fast_library_does_not_ask_even_for_a_thousand_documents()
    {
        Fresh();
        LibrarySettings.RememberSpeed(0.02);

        Assert.False(LibrarySettings.NeedsReadConfirmation(1000));
    }

    // Медленная библиотека: те же сто документов по две секунды — это уже три минуты.
    [Fact]
    public void A_slow_library_asks_even_for_a_hundred()
    {
        Fresh();
        LibrarySettings.RememberSpeed(2.0);

        Assert.True(LibrarySettings.NeedsReadConfirmation(100));
    }

    // Измеренная скорость ПЕРЕБИВАЕТ счет по числу: в этом и смысл замера.
    [Fact]
    public void A_measurement_overrides_the_count_rule()
    {
        Fresh();
        var many = LibrarySettings.ConfirmReadFromCount;
        Assert.True(LibrarySettings.NeedsReadConfirmation(many));

        LibrarySettings.RememberSpeed(0.001);
        Assert.False(LibrarySettings.NeedsReadConfirmation(many));
    }

    // ── Память о скорости ────────────────────────────────────────────────────

    [Fact]
    public void The_first_measurement_is_taken_as_is()
    {
        Fresh();
        LibrarySettings.RememberSpeed(0.5);

        Assert.Equal(0.5, LibrarySettings.SecondsPerDocument!.Value, 4);
    }

    // Сглаживание: один прогон по папке сканов иначе выдал бы за правду скорость, которой в
    // остальной библиотеке нет.
    [Fact]
    public void A_new_measurement_only_moves_the_remembered_speed_part_way()
    {
        Fresh();
        LibrarySettings.RememberSpeed(0.3);
        LibrarySettings.RememberSpeed(3.0);

        var now = LibrarySettings.SecondsPerDocument!.Value;
        Assert.True(now > 0.3 && now < 3.0, $"ожидалось между 0,3 и 3,0, получено {now}");
        Assert.Equal(0.3 * 2 / 3 + 3.0 / 3, now, 4);
    }

    // Мусор не должен затирать память о нормальном замере.
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(double.NaN)]
    public void Nonsense_is_ignored(double bad)
    {
        Fresh();
        LibrarySettings.RememberSpeed(0.4);
        LibrarySettings.RememberSpeed(bad);

        Assert.Equal(0.4, LibrarySettings.SecondsPerDocument!.Value, 4);
    }

    [Fact]
    public void The_speed_survives_a_restart()
    {
        var store = new FakeStore();
        AppSettings.AttachStore(store);
        LibrarySettings.Init();
        LibrarySettings.RememberSpeed(0.75);

        // «Перезапуск»: то же хранилище, повторное чтение настроек.
        AppSettings.AttachStore(store);
        LibrarySettings.Init();

        Assert.Equal(0.75, LibrarySettings.SecondsPerDocument!.Value, 4);
    }

    // Пустое хранилище — честное «не знаю», а не стандартное число: скорость не настройка,
    // а память о замере.
    [Fact]
    public void An_empty_store_means_unknown_not_a_default()
    {
        Fresh();
        Assert.Null(LibrarySettings.SecondsPerDocument);
    }
}
