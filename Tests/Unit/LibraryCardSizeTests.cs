using SmartBase.Database.Repositories;
using SmartBase.Utils;
using SmartBase.Views.Library;

namespace SmartBase.Tests.Unit;

/// <summary>
/// Размер карточки библиотеки: настройка (дельта в хранилище, разбор мусора, событие только на
/// настоящую смену) и геометрия (<see cref="DocTiles"/>).
///
/// Геометрия проверяется арифметикой не для красоты: лента держится на том, что ВСЕ карточки
/// одной высоты. Стоит строкам сниппета перерасти карточку — соседние строки станут разной
/// высоты, прокрутка начнет дергаться, а ползунок «дышать». На экране это ловится только
/// глазами и только на длинной ленте.
/// </summary>
public sealed class LibraryCardSizeTests
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
        LibrarySettings.Init();
        return store;
    }

    private static int CountChanges(Action act)
    {
        var n = 0;
        void On() => n++;
        LibrarySettings.Changed += On;
        try { act(); }
        finally { LibrarySettings.Changed -= On; }
        return n;
    }

    // ── Настройка ─────────────────────────────────────────────────────────────

    [Fact]
    public void Nothing_saved_reads_as_the_default_size()
    {
        Attach();
        Assert.Equal(DocCardSize.Small, LibrarySettings.CardSize);
    }

    /// <summary>
    /// Умолчание настройки и запасной вариант разбора обязаны совпадать. Разойдись они — пустое
    /// хранилище и мусор в нем дали бы РАЗНЫЙ размер карточки, и заметить это можно было бы
    /// только по чужому экрану.
    /// </summary>
    [Fact]
    public void The_parse_fallback_matches_the_declared_default()
    {
        Assert.Equal(DocTiles.Parse(AppSettings.LibraryCardSizeDefault),
                     DocTiles.Parse("такого ключа не бывает"));
    }

    [Fact]
    public void Size_survives_save_and_load()
    {
        var store = Attach();
        LibrarySettings.SetCardSize(DocCardSize.Large);

        AppSettings.AttachStore(store);
        LibrarySettings.Init();
        Assert.Equal(DocCardSize.Large, LibrarySettings.CardSize);
    }

    [Fact]
    public void Default_size_is_stored_as_absence_of_the_key()
    {
        var store = Attach();

        LibrarySettings.SetCardSize(DocCardSize.Medium);
        Assert.True(store.Has("libraryCardSize"));

        LibrarySettings.SetCardSize(DocCardSize.Small);    // стандарт
        Assert.False(store.Has("libraryCardSize"));
    }

    [Fact]
    public void Garbage_in_the_store_falls_back_to_the_default()
    {
        var store = Attach();
        store.Set("libraryCardSize", "гигантская");
        LibrarySettings.Init();

        Assert.Equal(DocCardSize.Small, LibrarySettings.CardSize);
    }

    [Fact]
    public void Same_size_is_silent()
    {
        Attach();
        Assert.Equal(1, CountChanges(() => LibrarySettings.SetCardSize(DocCardSize.Large)));
        Assert.Equal(0, CountChanges(() => LibrarySettings.SetCardSize(DocCardSize.Large)));
    }

    // ── Геометрия ─────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(DocCardSize.Small,  1)]
    [InlineData(DocCardSize.Medium, 3)]
    [InlineData(DocCardSize.Large,  6)]
    public void Each_size_shows_the_promised_number_of_snippet_lines(DocCardSize size, int lines)
    {
        Assert.Equal(lines, DocTiles.Of(size).SnippetLines);
    }

    /// <summary>
    /// Высота карточки обязана вмещать текст: название, подпись, строки сниппета и строку
    /// «Прочитать документ». Числа те же, что в сборщике карточки при стандартном шрифте.
    /// </summary>
    [Theory]
    [InlineData(DocCardSize.Small)]
    [InlineData(DocCardSize.Medium)]
    [InlineData(DocCardSize.Large)]
    public void Card_is_tall_enough_for_its_text(DocCardSize size)
    {
        const double Title = 20, Subtitle = 16, Line = 15, Action = 28, Gap = 2;

        var shape  = DocTiles.Of(size);
        var needed = Title + Subtitle + Gap + Line * shape.SnippetLines + Action;

        Assert.True(shape.CardHeight >= needed,
            $"{size}: карточка {shape.CardHeight}, тексту нужно {needed}");
    }

    /// <summary>
    /// Плитка не выше карточки — иначе она вылезет за ее края. И не шире: это левая колонка,
    /// а не половина строки.
    /// </summary>
    [Theory]
    [InlineData(DocCardSize.Small)]
    [InlineData(DocCardSize.Medium)]
    [InlineData(DocCardSize.Large)]
    public void Tile_fits_inside_the_card(DocCardSize size)
    {
        var shape = DocTiles.Of(size);
        Assert.True(shape.TileHeight <= shape.CardHeight,
            $"{size}: плитка {shape.TileHeight} выше карточки {shape.CardHeight}");
        Assert.True(shape.BoxWidth <= 160, $"{size}: плитка шире левой колонки");
    }

    /// <summary>
    /// Пропорции окошка одинаковы у всех размеров — на этом держится ОДИН кеш миниатюр на три
    /// размера. Разойдутся пропорции — картинка прежней формы будет подставлена новому окошку и
    /// обрежется по центру вместо шапки страницы, молча и без единой ошибки в логах.
    /// </summary>
    [Fact]
    public void All_sizes_keep_the_thumbnail_aspect()
    {
        var aspect = (double)SmartBase.Core.Documents.DocThumbnail.BoxWidth /
                             SmartBase.Core.Documents.DocThumbnail.BoxHeight;

        foreach (var size in new[] { DocCardSize.Small, DocCardSize.Medium, DocCardSize.Large })
        {
            var shape = DocTiles.Of(size);
            Assert.Equal(aspect, shape.BoxWidth / shape.BoxHeight, 3);
        }
    }

    /// <summary>
    /// Картинка в кеше не должна быть уже самого большого окошка — иначе на большой карточке
    /// шапка страницы растянется и расплывется.
    /// </summary>
    [Fact]
    public void Cached_image_is_wide_enough_for_the_largest_tile()
    {
        Assert.True(SmartBase.Core.Documents.DocThumbnail.Width >=
                    DocTiles.Of(DocCardSize.Large).BoxWidth * 2,
            "ширины картинки в кеше не хватает на большую карточку при двойной плотности экрана");
    }

    /// <summary>
    /// Надпись формата растет вместе с плиткой — иначе «PDF» на большой карточке теряется в
    /// пустой ленте. Размер берется ТОКЕНОМ типографики: сырое число обошло бы настройку
    /// «Размер шрифта» стороной.
    /// </summary>
    [Fact]
    public void Format_label_grows_with_the_tile()
    {
        var small  = DocTiles.Of(DocCardSize.Small);
        var medium = DocTiles.Of(DocCardSize.Medium);
        var large  = DocTiles.Of(DocCardSize.Large);

        Assert.True(small.LabelFontFallback < medium.LabelFontFallback);
        Assert.True(medium.LabelFontFallback < large.LabelFontFallback);

        foreach (var shape in new[] { small, medium, large })
        {
            Assert.StartsWith("FontSize", shape.LabelFontKey, StringComparison.Ordinal);
            // Надпись обязана помещаться в ленту по высоте.
            Assert.True(shape.LabelFontFallback * 1.4 <= shape.TapeHeight,
                $"надпись {shape.LabelFontFallback} не влезает в ленту {shape.TapeHeight}");
        }
    }

    [Fact]
    public void Size_keys_survive_a_round_trip()
    {
        foreach (var size in new[] { DocCardSize.Small, DocCardSize.Medium, DocCardSize.Large })
            Assert.Equal(size, DocTiles.Parse(DocTiles.KeyOf(size)));
    }
}
