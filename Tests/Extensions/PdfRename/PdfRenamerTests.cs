using SmartBase.Extensions.PdfRename;

namespace SmartBase.Tests.Extensions.PdfRename;

/// <summary>
/// Правило переименования и ПЛАН, который расширение показывает до того, как что-то тронет на
/// диске: имя обрезается по первому пробелу, а каждому файлу заранее назначается исход
/// (переименуем / нечего менять / конфликт). План — единственное, что видит пользователь в
/// режиме проверки, поэтому ошибка в нем дороже ошибки в самом переименовании.
///
/// Диск здесь нужен только для отбора файлов и поиска занятых имен; ничего не меняется —
/// применение плана проверяется отдельно (PdfRenamerProcessTests).
/// </summary>
public sealed class PdfRenamerTests
{
    // ── Правило имени ─────────────────────────────────────────────────────────

    [Theory]
    [InlineData("АБВГ.301234.001 Сборочный чертеж.pdf", "АБВГ.301234.001.pdf")]
    [InlineData("БезПробела.pdf",                       "БезПробела.pdf")]
    [InlineData("А Б В.pdf",                            "А.pdf")]
    [InlineData("много   пробелов.pdf",                 "много.pdf")]
    public void Name_is_cut_at_the_first_space(string source, string expected) =>
        Assert.Equal(expected, PdfRenamer.Transform(source));

    // Расширение всегда клеится в нижнем регистре — «.PDF» на выходе не остается.
    [Fact]
    public void Extension_is_always_written_in_lower_case()
    {
        Assert.Equal("ИМЯ.pdf", PdfRenamer.Transform("ИМЯ ФАЙЛА.PDF"));
        Assert.Equal("ИМЯ.pdf", PdfRenamer.Transform("ИМЯ.PDF"));
    }

    // Имя, начинающееся с пробела, дало бы пустую «голову» — из этого и рождается конфликт
    // «имя стало бы пустым» (проверка ниже уже на плане).
    [Fact]
    public void Leading_space_produces_an_empty_head()
    {
        Assert.Equal(".pdf", PdfRenamer.Transform(" ведущий пробел.pdf"));
        Assert.Equal(".pdf", PdfRenamer.Transform(".pdf"));
    }

    // ── Отбор файлов ──────────────────────────────────────────────────────────

    [Fact]
    public void Only_top_level_pdf_files_are_listed()
    {
        using var dir = new TempDir();
        dir.Touch("документ.pdf");
        dir.Touch("СКАН.PDF");
        dir.Touch("заметка.txt");
        dir.Touch("таблица.xlsx");
        Directory.CreateDirectory(Path.Combine(dir.Path, "подпапка"));
        File.WriteAllText(Path.Combine(dir.Path, "подпапка", "вложенный.pdf"), "");

        var files = PdfRenamer.ListPdfFiles(dir.Path);

        Assert.Equal(2, files.Count);
        Assert.Contains("документ.pdf", files);
        Assert.Contains("СКАН.PDF", files);
    }

    // Порядок списка задает порядок плана и порядок отчета в окне — он обязан быть
    // детерминированным и не зависеть от того, как папку вернула файловая система.
    [Fact]
    public void Files_are_listed_case_insensitively_sorted()
    {
        using var dir = new TempDir();
        dir.Touch("b.pdf");
        dir.Touch("A.pdf");
        dir.Touch("c.pdf");

        Assert.Equal(["A.pdf", "b.pdf", "c.pdf"], PdfRenamer.ListPdfFiles(dir.Path));
    }

    // ── Исходы плана ──────────────────────────────────────────────────────────

    [Fact]
    public void File_that_changes_name_is_planned_for_rename()
    {
        using var dir = new TempDir();
        dir.Touch("АБВГ.001 описание.pdf");

        var item = Assert.Single(PdfRenamer.BuildPlan(dir.Path));

        Assert.Equal(RenameOutcome.WillRename, item.Outcome);
        Assert.Equal("АБВГ.001.pdf", item.NewName);
        Assert.Null(item.Note);
    }

    [Fact]
    public void File_that_does_not_change_is_unchanged()
    {
        using var dir = new TempDir();
        dir.Touch("АБВГ.001.pdf");

        var item = Assert.Single(PdfRenamer.BuildPlan(dir.Path));

        Assert.Equal(RenameOutcome.Unchanged, item.Outcome);
        Assert.Equal("имя не изменилось", item.Note);
    }

    // Различие только в регистре расширения — тоже «нечего менять»: сравнение
    // OrdinalIgnoreCase, иначе файл бесконечно «переименовывался» бы сам в себя.
    [Fact]
    public void Case_only_difference_counts_as_unchanged()
    {
        using var dir = new TempDir();
        dir.Touch("АБВГ.001.PDF");

        var item = Assert.Single(PdfRenamer.BuildPlan(dir.Path));

        Assert.Equal(RenameOutcome.Unchanged, item.Outcome);
    }

    [Fact]
    public void Name_that_would_become_empty_is_a_conflict()
    {
        using var dir = new TempDir();
        dir.Touch(".pdf");   // «голова» имени пуста — переименовывать не во что

        var item = Assert.Single(PdfRenamer.BuildPlan(dir.Path));

        Assert.Equal(RenameOutcome.Conflict, item.Outcome);
        Assert.Equal("имя стало бы пустым", item.Note);
    }

    [Fact]
    public void Two_files_aiming_at_one_name_are_both_conflicts()
    {
        using var dir = new TempDir();
        dir.Touch("АБВГ.001 первый.pdf");
        dir.Touch("АБВГ.001 второй.pdf");

        var plan = PdfRenamer.BuildPlan(dir.Path);

        Assert.Equal(2, plan.Count);
        Assert.All(plan, i =>
        {
            Assert.Equal(RenameOutcome.Conflict, i.Outcome);
            Assert.Equal("несколько файлов → одно имя", i.Note);
        });
    }

    [Fact]
    public void Occupied_target_name_is_a_conflict()
    {
        using var dir = new TempDir();
        dir.Touch("АБВГ.001.pdf");            // цель уже лежит на диске
        dir.Touch("АБВГ.001 описание.pdf");

        var plan = PdfRenamer.BuildPlan(dir.Path);
        var moving = plan.Single(i => i.OldName == "АБВГ.001 описание.pdf");

        Assert.Equal(RenameOutcome.Conflict, moving.Outcome);
        Assert.Equal("файл уже существует", moving.Note);
    }

    /// <summary>Файл, который САМ является целью, не должен попадать в счетчик коллизий —
    /// иначе пара «уже приведенный к правилу + новый» объявлялась бы конфликтом с обеих
    /// сторон, и правильный файл получал бы ложную пометку.</summary>
    [Fact]
    public void File_that_is_itself_the_target_is_not_a_collision()
    {
        using var dir = new TempDir();
        dir.Touch("АБВГ.001.pdf");
        dir.Touch("АБВГ.001 описание.pdf");

        var plan  = PdfRenamer.BuildPlan(dir.Path);
        var target = plan.Single(i => i.OldName == "АБВГ.001.pdf");

        Assert.Equal(RenameOutcome.Unchanged, target.Outcome);
    }

    [Fact]
    public void Empty_folder_gives_an_empty_plan()
    {
        using var dir = new TempDir();

        Assert.Empty(PdfRenamer.BuildPlan(dir.Path));
    }

    // План — режим проверки: диска он не касается вовсе.
    [Fact]
    public void Building_a_plan_changes_nothing_on_disk()
    {
        using var dir = new TempDir();
        dir.Touch("АБВГ.001 описание.pdf");
        dir.Touch("ДЕЖЗ.002 другое.pdf");
        var before = dir.Names();

        PdfRenamer.BuildPlan(dir.Path);

        Assert.Equal(before, dir.Names());
    }
}
