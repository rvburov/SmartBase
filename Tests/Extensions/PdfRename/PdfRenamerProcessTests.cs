using SmartBase.Extensions.PdfRename;

namespace SmartBase.Tests.Extensions.PdfRename;

/// <summary>
/// Применение готового плана к диску: что реально переименовывается, что пропускается, как
/// считаются итоги и что происходит при отмене и при сорвавшейся файловой операции.
///
/// Разделение «план отдельно, применение отдельно» — не случайность расширения, а его контракт:
/// план строится один раз вне UI-потока, а <see cref="PdfRenamer.Process"/> идет по уже
/// принятым решениям. Поэтому здесь всегда сперва BuildPlan, потом Process.
/// </summary>
public sealed class PdfRenamerProcessTests
{
    private static readonly CancellationToken None = CancellationToken.None;

    [Fact]
    public void Dry_run_changes_nothing_on_disk()
    {
        using var dir = new TempDir();
        dir.Touch("АБВГ.001 описание.pdf");
        dir.Touch("ДЕЖЗ.002 другое.pdf");
        var before = dir.Names();

        var summary = PdfRenamer.Process(dir.Path, PdfRenamer.BuildPlan(dir.Path), dryRun: true, null, None);

        Assert.Equal(before, dir.Names());
        Assert.Equal(2, summary.Planned);
        Assert.Equal(0, summary.Renamed);
    }

    [Fact]
    public void Real_run_renames_files_and_flips_the_outcome()
    {
        using var dir = new TempDir();
        dir.Touch("АБВГ.001 описание.pdf");
        var reported = new List<RenameProgress>();

        var summary = PdfRenamer.Process(dir.Path, PdfRenamer.BuildPlan(dir.Path), dryRun: false,
                                         new SyncProgress<RenameProgress>(reported.Add), None);

        Assert.Equal(["АБВГ.001.pdf"], dir.Names());
        Assert.Equal(1, summary.Renamed);
        Assert.Equal(0, summary.Planned);
        Assert.Equal(RenameOutcome.Renamed, Assert.Single(reported).Item.Outcome);
    }

    [Fact]
    public void Conflicts_and_unchanged_are_skipped_on_a_real_run()
    {
        using var dir = new TempDir();
        dir.Touch("АБВГ.001.pdf");            // Unchanged
        dir.Touch("ДЕЖЗ.002 первый.pdf");     // Conflict — двое метят в одно имя
        dir.Touch("ДЕЖЗ.002 второй.pdf");     // Conflict
        var before = dir.Names();

        var summary = PdfRenamer.Process(dir.Path, PdfRenamer.BuildPlan(dir.Path), dryRun: false, null, None);

        Assert.Equal(before, dir.Names());
        Assert.Equal(1, summary.Unchanged);
        Assert.Equal(2, summary.Conflicts);
        Assert.Equal(0, summary.Renamed);
    }

    [Fact]
    public void Counters_add_up_to_the_plan_size()
    {
        using var dir = new TempDir();
        dir.Touch("АБВГ.001.pdf");            // Unchanged
        dir.Touch("ДЕЖЗ.002 описание.pdf");   // WillRename
        dir.Touch("ИКЛМ.003 первый.pdf");     // Conflict
        dir.Touch("ИКЛМ.003 второй.pdf");     // Conflict

        var summary = PdfRenamer.Process(dir.Path, PdfRenamer.BuildPlan(dir.Path), dryRun: false, null, None);

        Assert.Equal(4, summary.Total);
        Assert.Equal(summary.Total,
                     summary.Renamed + summary.Planned + summary.Unchanged + summary.Conflicts + summary.Errors);
        Assert.False(summary.Cancelled);
    }

    // Прогресс несет ИТОГОВЫЙ исход шага, а не плановый: в окне пользователь должен видеть
    // «переименован», а не «будет переименован».
    [Fact]
    public void Progress_reports_every_item_in_order()
    {
        using var dir = new TempDir();
        dir.Touch("АБВГ.001 описание.pdf");
        dir.Touch("ДЕЖЗ.002 другое.pdf");
        var reported = new List<RenameProgress>();

        PdfRenamer.Process(dir.Path, PdfRenamer.BuildPlan(dir.Path), dryRun: false,
                           new SyncProgress<RenameProgress>(reported.Add), None);

        Assert.Equal([1, 2], reported.Select(r => r.Current));
        Assert.All(reported, r => Assert.Equal(2, r.Total));
        Assert.All(reported, r => Assert.Equal(RenameOutcome.Renamed, r.Item.Outcome));
    }

    [Fact]
    public void Cancelled_run_stops_and_leaves_the_rest_untouched()
    {
        using var dir = new TempDir();
        dir.Touch("1АБВ.001 описание.pdf");
        dir.Touch("2ДЕЖ.002 другое.pdf");
        dir.Touch("3ИКЛ.003 третье.pdf");
        var plan = PdfRenamer.BuildPlan(dir.Path);

        // Отменяем сразу после первого шага — второй и третий обработаны быть не должны.
        using var cts = new CancellationTokenSource();
        var progress = new SyncProgress<RenameProgress>(_ => cts.Cancel());

        var summary = PdfRenamer.Process(dir.Path, plan, dryRun: false, progress, cts.Token);

        Assert.True(summary.Cancelled);
        Assert.Equal(1, summary.Renamed);
        Assert.Contains("2ДЕЖ.002 другое.pdf", dir.Names());
        Assert.Contains("3ИКЛ.003 третье.pdf", dir.Names());
    }

    /// <summary>Между построением плана и его применением диск мог измениться: цель, свободная
    /// на момент плана, оказалась занята. File.Move падает — расширение обязано записать это
    /// ошибкой конкретного файла и продолжить, а не оборвать весь пакет.</summary>
    [Fact]
    public void Failed_move_becomes_an_error_and_does_not_stop_the_run()
    {
        using var dir = new TempDir();
        dir.Touch("1АБВ.001 описание.pdf");
        dir.Touch("2ДЕЖ.002 другое.pdf");
        var plan = PdfRenamer.BuildPlan(dir.Path);
        Assert.All(plan, i => Assert.Equal(RenameOutcome.WillRename, i.Outcome));

        dir.Touch("1АБВ.001.pdf");   // цель первого занята уже ПОСЛЕ построения плана
        var reported = new List<RenameProgress>();

        var summary = PdfRenamer.Process(dir.Path, plan, dryRun: false,
                                         new SyncProgress<RenameProgress>(reported.Add), None);

        Assert.Equal(1, summary.Errors);
        Assert.Equal(1, summary.Renamed);   // второй файл все равно обработан
        Assert.Equal(RenameOutcome.Error, reported[0].Item.Outcome);
        Assert.False(string.IsNullOrWhiteSpace(reported[0].Item.Note));
        Assert.Contains("2ДЕЖ.002.pdf", dir.Names());
    }
}
