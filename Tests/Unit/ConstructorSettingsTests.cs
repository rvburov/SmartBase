using SmartBase.Utils;

namespace SmartBase.Tests.Unit;

/// <summary>
/// Настройки раздела «Конструктор базы данных» в AppSettings и живой сервис ConstructorSettings:
/// инвариант «в базе хранятся только отличия от стандарта» (дефолт = отсутствие ключа), разбор
/// мусора, событие Changed только на настоящую смену, и два особых случая — выключение
/// «запоминать» стирает запомненное, а раскладка панели и последняя запись пишутся как
/// состояние, без события.
/// </summary>
public sealed class ConstructorSettingsTests
{
    private sealed class FakeStore : ISettingsDatabase
    {
        private readonly Dictionary<string, string> _d = new();
        public bool Has(string key) => _d.ContainsKey(key);
        public string? Get(string key) => _d.TryGetValue(key, out var v) ? v : null;
        public void Set(string key, string value) => _d[key] = value;
        public void Remove(string key) => _d.Remove(key);
        public IReadOnlyList<string> KeysWithPrefix(string prefix) =>
            [.. _d.Keys.Where(k => k.StartsWith(prefix, StringComparison.Ordinal))];
    }

    private static FakeStore Attach()
    {
        var store = new FakeStore();
        AppSettings.AttachStore(store);
        ConstructorSettings.Init();
        return store;
    }

    private static int CountChanges(Action act)
    {
        var n = 0;
        void On() => n++;
        ConstructorSettings.Changed += On;
        try { act(); }
        finally { ConstructorSettings.Changed -= On; }
        return n;
    }

    // ── Дефолты ───────────────────────────────────────────────────────────────

    /// <summary>Стандарт раздела = сегодняшнее поведение программы до появления настроек.</summary>
    [Fact]
    public void Nothing_saved_reads_as_defaults()
    {
        Attach();

        Assert.Equal("text", ConstructorSettings.NewFieldType);
        Assert.False(ConstructorSettings.OpenNewRecord);
        Assert.True(ConstructorSettings.ConfirmDelete);
        Assert.Equal("#007AFF", ConstructorSettings.NewCollectionColor);
        Assert.Equal(36, ConstructorSettings.RowHeight);
        Assert.Equal(160, ConstructorSettings.ColWidth);
        Assert.False(ConstructorSettings.ColResize);
        Assert.False(ConstructorSettings.RowResize);
        Assert.False(ConstructorSettings.OpenByDoubleClick);
        Assert.False(ConstructorSettings.DescriptionPreview);
        Assert.False(ConstructorSettings.RestoreRecord);
        Assert.False(ConstructorSettings.RememberLayout);
        Assert.False(ConstructorSettings.RememberFilters);
        Assert.Equal(3, ConstructorSettings.PollSeconds);
        Assert.Equal(15, ConstructorSettings.BackgroundPollSeconds);
    }

    // ── Дельты ────────────────────────────────────────────────────────────────

    [Fact]
    public void Values_survive_save_and_default_removes_the_key()
    {
        var store = Attach();

        ConstructorSettings.SetOpenNewRecord(true);
        ConstructorSettings.SetConfirmDelete(false);
        ConstructorSettings.SetNewFieldType("number");
        ConstructorSettings.SetRowHeight(44);
        ConstructorSettings.SetColWidth(240);
        ConstructorSettings.SetPollSeconds(30);
        Assert.True(store.Has("constructorOpenNewRecord"));
        Assert.True(store.Has("constructorConfirmDelete"));
        Assert.Equal("number", store.Get("constructorNewFieldType"));
        Assert.Equal("44", store.Get("constructorRowHeight"));
        Assert.Equal("240", store.Get("constructorColWidth"));

        ConstructorSettings.Init();
        Assert.True(ConstructorSettings.OpenNewRecord);
        Assert.False(ConstructorSettings.ConfirmDelete);
        Assert.Equal("number", ConstructorSettings.NewFieldType);
        Assert.Equal(44, ConstructorSettings.RowHeight);
        Assert.Equal(240, ConstructorSettings.ColWidth);
        Assert.Equal(30, ConstructorSettings.PollSeconds);
        Assert.Equal(150, ConstructorSettings.BackgroundPollSeconds);

        // Возврат к стандарту снимает ключи — сброс настройки не оставляет следов.
        ConstructorSettings.SetOpenNewRecord(false);
        ConstructorSettings.SetConfirmDelete(true);
        ConstructorSettings.SetNewFieldType("text");
        ConstructorSettings.SetRowHeight(36);
        ConstructorSettings.SetColWidth(160);
        ConstructorSettings.SetPollSeconds(3);
        Assert.False(store.Has("constructorOpenNewRecord"));
        Assert.False(store.Has("constructorConfirmDelete"));
        Assert.False(store.Has("constructorNewFieldType"));
        Assert.False(store.Has("constructorRowHeight"));
        Assert.False(store.Has("constructorColWidth"));
        Assert.False(store.Has("constructorPollSeconds"));
    }

    /// <summary>Все булевы настройки раздела разом: значение не по стандарту заводит ключ,
    /// возврат к стандарту его снимает, между ними значение переживает перечитывание.</summary>
    [Theory]
    [InlineData("constructorOpenNewRecord")]
    [InlineData("constructorConfirmDelete")]
    [InlineData("constructorColResize")]
    [InlineData("constructorRowResize")]
    [InlineData("constructorOpenDoubleClick")]
    [InlineData("constructorDescriptionPreview")]
    [InlineData("constructorRestoreRecord")]
    [InlineData("constructorRememberLayout")]
    [InlineData("constructorRememberFilters")]
    public void Every_toggle_stores_only_the_delta(string key)
    {
        var store = Attach();
        var (set, read, standard) = Toggle(key);

        set(!standard);
        Assert.True(store.Has(key));
        ConstructorSettings.Init();
        Assert.Equal(!standard, read());

        set(standard);
        Assert.False(store.Has(key));
        ConstructorSettings.Init();
        Assert.Equal(standard, read());
    }

    // Пара «сеттер + чтение + стандарт» по ключу хранилища: держим список в одном месте,
    // чтобы новая булева настройка раздела не осталась без проверки дельты.
    private static (Action<bool> Set, Func<bool> Read, bool Standard) Toggle(string key) => key switch
    {
        "constructorOpenNewRecord" => (ConstructorSettings.SetOpenNewRecord,
            () => ConstructorSettings.OpenNewRecord, AppSettings.ConstructorOpenNewRecordDefault),
        "constructorConfirmDelete" => (ConstructorSettings.SetConfirmDelete,
            () => ConstructorSettings.ConfirmDelete, AppSettings.ConstructorConfirmDeleteDefault),
        "constructorColResize" => (ConstructorSettings.SetColResize,
            () => ConstructorSettings.ColResize, AppSettings.ConstructorColResizeDefault),
        "constructorRowResize" => (ConstructorSettings.SetRowResize,
            () => ConstructorSettings.RowResize, AppSettings.ConstructorRowResizeDefault),
        "constructorOpenDoubleClick" => (ConstructorSettings.SetOpenByDoubleClick,
            () => ConstructorSettings.OpenByDoubleClick, AppSettings.ConstructorOpenByDoubleClickDefault),
        "constructorDescriptionPreview" => (ConstructorSettings.SetDescriptionPreview,
            () => ConstructorSettings.DescriptionPreview, AppSettings.ConstructorDescriptionPreviewDefault),
        "constructorRestoreRecord" => (ConstructorSettings.SetRestoreRecord,
            () => ConstructorSettings.RestoreRecord, AppSettings.ConstructorRestoreRecordDefault),
        "constructorRememberLayout" => (ConstructorSettings.SetRememberLayout,
            () => ConstructorSettings.RememberLayout, AppSettings.ConstructorRememberLayoutDefault),
        "constructorRememberFilters" => (ConstructorSettings.SetRememberFilters,
            () => ConstructorSettings.RememberFilters, AppSettings.ConstructorRememberFiltersDefault),
        _ => throw new ArgumentOutOfRangeException(nameof(key), key, "нет такой настройки раздела"),
    };

    /// <summary>Цвет приводится к верхнему регистру, непохожее на «#RRGGBB» — в стандартный синий.</summary>
    [Fact]
    public void Collection_color_takes_only_hex()
    {
        var store = Attach();

        ConstructorSettings.SetNewCollectionColor("#ff9500");
        Assert.Equal("#FF9500", ConstructorSettings.NewCollectionColor);
        Assert.Equal("#FF9500", store.Get("constructorNewCollectionColor"));

        ConstructorSettings.SetNewCollectionColor("оранжевый");
        Assert.Equal("#007AFF", ConstructorSettings.NewCollectionColor);
        Assert.False(store.Has("constructorNewCollectionColor"));
    }

    // ── Мусор ─────────────────────────────────────────────────────────────────

    /// <summary>Числа из правленого app.db не должны давать ни нулевой строки, ни опроса раз в сутки.</summary>
    [Fact]
    public void Garbage_in_the_store_falls_back_to_defaults()
    {
        var store = Attach();
        store.Set("constructorRowHeight", "0");
        store.Set("constructorColWidth", "5000");
        store.Set("constructorPollSeconds", "86400");
        store.Set("constructorConfirmDelete", "мусор");
        store.Set("constructorNewCollectionColor", "#ZZZZZZ");
        store.Set("constructorNewFieldType", "   ");

        ConstructorSettings.Init();

        Assert.Equal(36, ConstructorSettings.RowHeight);
        Assert.Equal(160, ConstructorSettings.ColWidth);
        Assert.Equal(3, ConstructorSettings.PollSeconds);
        Assert.True(ConstructorSettings.ConfirmDelete);
        Assert.Equal("#007AFF", ConstructorSettings.NewCollectionColor);
        Assert.Equal("text", ConstructorSettings.NewFieldType);
    }

    /// <summary>Значения не из списка молча игнорируются, а не пишутся в базу.</summary>
    [Fact]
    public void Row_height_and_poll_period_take_only_listed_values()
    {
        var store = Attach();

        ConstructorSettings.SetRowHeight(100);
        ConstructorSettings.SetPollSeconds(7);

        Assert.Equal(36, ConstructorSettings.RowHeight);
        Assert.Equal(3, ConstructorSettings.PollSeconds);
        Assert.False(store.Has("constructorRowHeight"));
        Assert.False(store.Has("constructorPollSeconds"));
    }

    /// <summary>Ширина столбца — такой же закрытый список, как высота строки и период опроса:
    /// значение вне списка не применяется, иначе в панели встал бы первый пункт, а действовало
    /// бы другое число.</summary>
    [Fact]
    public void Column_width_takes_only_listed_values()
    {
        var store = Attach();

        ConstructorSettings.SetColWidth(137);
        Assert.Equal(AppSettings.ConstructorColWidthDefault, ConstructorSettings.ColWidth);
        Assert.False(store.Has("constructorColWidth"));

        // Мусор из правленого app.db тоже читается как стандарт, а не как «почти подходящее».
        store.Set("constructorColWidth", "137");
        ConstructorSettings.Init();
        Assert.Equal(AppSettings.ConstructorColWidthDefault, ConstructorSettings.ColWidth);

        foreach (var width in AppSettings.ConstructorColWidths)
        {
            ConstructorSettings.SetColWidth(width);
            Assert.Equal(width, ConstructorSettings.ColWidth);
        }
    }

    // ── Забывание ─────────────────────────────────────────────────────────────

    /// <summary>Выключение «запоминать раскладку» стирает запомненное: иначе повторное включение
    /// вернуло бы раскладку, забытую неизвестно когда.</summary>
    [Fact]
    public void Turning_layout_memory_off_forgets_the_layout()
    {
        var store = Attach();
        ConstructorSettings.SetRememberLayout(true);
        AppSettings.SaveConstructorPropsHeight(320);
        AppSettings.SaveConstructorNameColWidth(210);
        Assert.Equal(320, AppSettings.LoadConstructorPropsHeight());

        ConstructorSettings.SetRememberLayout(false);

        Assert.Equal(0, AppSettings.LoadConstructorPropsHeight());
        Assert.Equal(0, AppSettings.LoadConstructorNameColWidth());
        Assert.False(store.Has("constructorPropsHeight"));
        Assert.False(store.Has("constructorNameColWidth"));
    }

    [Fact]
    public void Turning_filter_memory_off_forgets_the_filters()
    {
        Attach();
        ConstructorSettings.SetRememberFilters(true);
        ConstructorFilterMemory.Save("/base.db", 7, new Dictionary<string, HashSet<string>>
        {
            ["city"] = ["Москва"],
        }, sortKey: null, sortAsc: true);
        Assert.NotNull(ConstructorFilterMemory.Load("/base.db", 7));

        ConstructorSettings.SetRememberFilters(false);

        Assert.Null(ConstructorFilterMemory.Load("/base.db", 7));
    }

    // ── Состояние ─────────────────────────────────────────────────────────────

    /// <summary>Последняя открытая запись — состояние: пишется всегда, читается только при
    /// включенном восстановлении; мусор дает null, а не исключение.</summary>
    [Fact]
    public void Last_record_is_state_and_parses_safely()
    {
        var store = Attach();

        AppSettings.SaveConstructorLastRecord(3, 42);
        Assert.Equal("3:42", store.Get("constructorLastRecord"));
        Assert.Equal((3, 42), AppSettings.ParseConstructorLastRecord(AppSettings.LoadConstructorLastRecord()));

        Assert.Null(AppSettings.ParseConstructorLastRecord(""));
        Assert.Null(AppSettings.ParseConstructorLastRecord("42"));
        Assert.Null(AppSettings.ParseConstructorLastRecord("три:сорок два"));
        Assert.Null(AppSettings.ParseConstructorLastRecord("1:0"));
        // Ноль вместо базы означает «текущая» — между запусками это не адрес: активной может
        // стать другая база, и запись с тем же номером нашлась бы в ней.
        Assert.Null(AppSettings.ParseConstructorLastRecord("0:42"));

        AppSettings.SaveConstructorLastRecord(0, 0);
        Assert.False(store.Has("constructorLastRecord"));
    }

    // ── Событие ───────────────────────────────────────────────────────────────

    [Fact]
    public void Changed_fires_once_per_real_change_and_not_for_the_same_value()
    {
        Attach();
        var stamp = ConstructorSettings.Stamp;

        var fired = CountChanges(() =>
        {
            ConstructorSettings.SetConfirmDelete(false);
            ConstructorSettings.SetConfirmDelete(false);   // то же значение — тишина
            ConstructorSettings.SetRowHeight(100);         // не из списка — тишина
            ConstructorSettings.SetOpenByDoubleClick(true);
        });

        Assert.Equal(2, fired);
        Assert.Equal(stamp + 2, ConstructorSettings.Stamp);
    }
}
