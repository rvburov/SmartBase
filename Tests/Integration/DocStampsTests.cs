using System.IO;
using SmartBase.Core.Documents;
using SmartBase.Core.Models;

namespace SmartBase.Tests.Integration;

/// <summary>
/// Отметки файлов набором (<c>Core/Documents/DocStamps.cs</c>): время правки и размер, по которым
/// кеш текста понимает, изменился ли документ, а карточка — есть ли файл.
///
/// Главное, что здесь закрепляется: ответ набором совпадает с ответом по одному файлу для
/// ЛЮБОГО пути. Листинг папок — только ускорение. Разойдись он с вопросом по файлу хоть в одном
/// случае, кеш считал бы прочитанное измененным и перечитывал его на каждый заход, либо не
/// замечал бы правок — и то и другое молча.
/// </summary>
public sealed class DocStampsTests : IDisposable
{
    private readonly string _dir;

    public DocStampsTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "sb-stamps-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* занят — уберет система */ }
    }

    // Файл заданного размера — разный размер у соседей, чтобы перепутанная отметка не сошлась
    // случайно.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Cancelled_file_probe_returns_no_partial_result(bool beforeStart)
    {
        using var cts = new CancellationTokenSource();
        var path = Make("cancel", "file.txt", 10);
        var enumerated = false;
        IEnumerable<string> Paths()
        {
            enumerated = true;
            if (!beforeStart) cts.Cancel();
            yield return path;
        }
        if (beforeStart) cts.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => DocStamps.Of(Paths(), cts.Token));
        Assert.Equal(!beforeStart, enumerated);
    }

    private string Make(string folder, string name, int bytes)
    {
        var dir = Path.Combine(_dir, folder);
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, name);
        File.WriteAllBytes(path, new byte[bytes]);
        return path;
    }

    private static void AgreesOneByOne(IReadOnlyCollection<string> paths)
    {
        var batch = DocStamps.Of(paths);
        foreach (var p in paths)
            Assert.True(DocStamps.Of(p) == batch[p], $"отметка набором разошлась для «{p}»");
    }

    /// <summary>
    /// Все особые случаи разом: большая папка (листается), малая (спрашивается по файлу), имена,
    /// которых в большой папке нет, несуществующая папка, тот же файл с прямыми слешами, имя в
    /// другом регистре, путь к папке вместо файла и пустая строка.
    /// </summary>
    [Fact]
    public void A_set_answers_exactly_like_one_by_one()
    {
        var big = Enumerable.Range(0, DocStamps.ListingThreshold * 2 + 4)
                            .Select(i => Make("big", $"file{i:D2}.pdf", 10 + i))
                            .ToList();
        var small = Enumerable.Range(0, 2)
                              .Select(i => Make("small", $"note{i}.txt", 100 + i))
                              .ToList();
        var nowhere = Enumerable.Range(0, DocStamps.ListingThreshold + 2)
                                .Select(i => Path.Combine(_dir, "nowhere", $"gone{i}.pdf"))
                                .ToList();

        var paths = new List<string>();
        paths.AddRange(big);
        paths.Add(Path.Combine(_dir, "big", "absent1.pdf"));
        paths.Add(Path.Combine(_dir, "big", "absent2.pdf"));
        paths.AddRange(small);
        paths.AddRange(nowhere);
        paths.Add(big[3].Replace('\\', '/'));
        paths.Add(Path.Combine(_dir, "big", "FILE04.PDF"));
        paths.Add(Path.Combine(_dir, "small"));
        paths.Add("");

        AgreesOneByOne(paths);

        // Листинг не выдал «файла нет» там, где файл есть: иначе равенство выше держалось бы
        // только потому, что обе стороны ошиблись одинаково.
        var batch = DocStamps.Of(paths);
        Assert.All(big, p => Assert.Equal(new FileInfo(p).Length, batch[p].Size));
        Assert.All(nowhere, p => Assert.Equal(-1, batch[p].Size));
    }

    /// <summary>
    /// Из огромной папки нужны несколько файлов, и лежат они в самом конце листинга. Листинг
    /// бросается, прочитав свой запас записей, — а файлы все равно находятся: чего листинг не
    /// дочитал, спрашивается по файлу.
    /// </summary>
    [Fact]
    public void A_huge_folder_is_abandoned_for_a_few_files_without_losing_them()
    {
        for (var i = 0; i < 1100; i++) Make("huge", $"a-filler{i:D4}.bin", 1);
        var wanted = Enumerable.Range(0, DocStamps.ListingThreshold)
                               .Select(i => Make("huge", $"zz-wanted{i}.pdf", 50 + i))
                               .ToList();

        AgreesOneByOne(wanted);
        var batch = DocStamps.Of(wanted);
        Assert.All(wanted, p => Assert.Equal(new FileInfo(p).Length, batch[p].Size));
    }

    /// <summary>
    /// Правку файла листинг замечает так же, как вопрос по файлу: кеш текста узнает об
    /// изменившемся документе только по этой паре чисел.
    /// </summary>
    [Fact]
    public void An_edit_changes_the_stamp_seen_by_listing()
    {
        var paths = Enumerable.Range(0, DocStamps.ListingThreshold + 1)
                              .Select(i => Make("edit", $"doc{i}.txt", 20))
                              .ToList();
        var before = DocStamps.Of(paths)[paths[2]];

        File.WriteAllBytes(paths[2], new byte[777]);
        var after = DocStamps.Of(paths)[paths[2]];

        Assert.NotEqual(before, after);
        Assert.Equal(777, after.Size);
        Assert.Equal(DocStamps.Of(paths[2]), after);
    }

    /// <summary>
    /// Проверка документов страницы ленты: ответ ложится в сами документы, и карточка в
    /// UI-потоке к диску больше не идет.
    /// </summary>
    [Fact]
    public void Probe_fills_the_documents_and_marks_them_checked()
    {
        var here    = Make("probe", "here.pdf", 321);
        var present = new DocRecord { Path = here };
        var missing = new DocRecord { Path = Path.Combine(_dir, "probe", "gone.pdf") };
        var empty   = new DocRecord { Path = "" };

        DocStamps.Probe([present, missing, empty]);

        Assert.All(new[] { present, missing, empty }, d => Assert.True(d.OnDiskKnown));
        Assert.Equal(321, present.SizeBytes);
        Assert.Equal(File.GetLastWriteTimeUtc(here).Ticks, present.Mtime);
        Assert.Equal(-1, missing.SizeBytes);
        Assert.Equal(-1, empty.SizeBytes);
    }
}
