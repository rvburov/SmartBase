using SmartBase.Database.DocIndex;
using SmartBase.Views.Settings;

namespace SmartBase.Tests.Unit;

/// <summary>
/// Сводка кеша документов в настройках (<see cref="LibraryCacheSummary"/>). Раньше чужие форматы,
/// битые файлы и файлы тяжелее порога складывались в одно «Не прочитано», и по нему нельзя было
/// понять, поможет ли поднять порог. Проверяется, что исходы названы порознь, что пустые исходы
/// не шумят и что у файлов тяжелее порога есть подсказка про порог.
/// </summary>
public sealed class LibraryCacheSummaryTests
{
    [Fact]
    public void Every_outcome_is_named_on_its_own()
    {
        var text = LibraryCacheSummary.Compose(
            new DocCacheStats(Indexed: 1190, NoText: 12, Unsupported: 40, Failed: 3, TooBig: 2, Bytes: 0), 0);

        Assert.Contains("Разобрано документов: 1190.", text);
        Assert.Contains("Без текстового слоя: 12.", text);
        Assert.Contains("Формат без чтения текста: 40.", text);
        Assert.Contains("Не удалось прочитать: 3.", text);
        Assert.Contains("Тяжелее порога: 2 — прочитаются, если поднять порог.", text);
        // Прежнее «Не прочитано» путалось с «еще не читали» из раздела.
        Assert.DoesNotContain("Не прочитано", text);
    }

    // «Разобрано» стоит всегда — даже ноль говорит, что кеш пуст; остальные исходы только когда
    // они есть: «Не удалось прочитать: 0» человеку ничего не говорит.
    [Fact]
    public void Absent_outcomes_stay_silent_but_the_read_count_is_always_there()
    {
        var empty = LibraryCacheSummary.Compose(new DocCacheStats(0, 0, 0, 0, 0, 0), 0);

        Assert.StartsWith("Разобрано документов: 0. Занято на диске:", empty);
        foreach (var outcome in new[] { "текстового слоя:", "Формат без чтения", "Не удалось", "Тяжелее порога" })
            Assert.DoesNotContain(outcome, empty);
    }

    // Размер — кеш текста вместе с миниатюрами: очистка удаляет и то, и другое.
    [Fact]
    public void The_size_counts_the_cache_and_the_thumbnails()
    {
        var text = LibraryCacheSummary.Compose(new DocCacheStats(1, 0, 0, 0, 0, Bytes: 3L * 1024 * 1024),
                                               thumbsBytes: 2L * 1024 * 1024);

        Assert.Contains("Занято на диске: 5 МБ.", text);
    }
}
