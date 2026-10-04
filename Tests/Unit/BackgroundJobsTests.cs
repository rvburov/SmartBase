using SmartBase.Core;

namespace SmartBase.Tests.Unit;

/// <summary>
/// Реестр фоновых работ (<see cref="BackgroundJobs"/>) — из него живет значок в панели разделов.
///
/// Реестр один на приложение и статический, поэтому каждый тест убирает за собой: чужая
/// недобранная работа сделала бы соседний тест зеленым или красным по случайности.
/// </summary>
public sealed class BackgroundJobsTests : IDisposable
{
    private const string A = "test.a";
    private const string B = "test.b";

    public void Dispose()
    {
        BackgroundJobs.Finish(A);
        BackgroundJobs.Finish(B);
    }

    [Fact]
    public void An_empty_registry_shows_nothing()
    {
        Assert.False(BackgroundJobs.Any);
        Assert.Null(BackgroundJobs.Progress());
    }

    // Повторный отчет с тем же ключом ОБНОВЛЯЕТ работу. Иначе на каждый прочитанный документ
    // в списке появлялась бы новая строка.
    [Fact]
    public void Reporting_the_same_key_twice_updates_one_job()
    {
        BackgroundJobs.Report(A, "Чтение", "1 из 10", 1, 10);
        BackgroundJobs.Report(A, "Чтение", "7 из 10", 7, 10);

        var jobs = BackgroundJobs.All.Where(j => j.Id == A).ToList();
        Assert.Single(jobs);
        Assert.Equal(7, jobs[0].Done);
    }

    [Fact]
    public void Progress_sums_across_jobs()
    {
        BackgroundJobs.Report(A, "Чтение", "", 5,  10);
        BackgroundJobs.Report(B, "Обновление", "", 5, 10);

        Assert.Equal(0.5, BackgroundJobs.Progress()!.Value, 3);
    }

    // Работа без известного объема не должна обнулять полосу у остальных: иначе одна такая
    // делала бы кольцо пустым при почти доделанной второй.
    [Fact]
    public void A_job_of_unknown_size_does_not_drag_the_progress_down()
    {
        BackgroundJobs.Report(A, "Чтение", "", 9, 10);
        BackgroundJobs.Report(B, "Что-то долгое", "");

        Assert.Equal(0.9, BackgroundJobs.Progress()!.Value, 3);
    }

    // Все работы без объема — мерить нечего, и врать долей нельзя.
    [Fact]
    public void With_no_measurable_job_the_progress_is_unknown()
    {
        BackgroundJobs.Report(A, "Что-то долгое", "");

        Assert.True(BackgroundJobs.Any);
        Assert.Null(BackgroundJobs.Progress());
    }

    // Сорвавшаяся работа ОСТАЕТСЯ в списке: уйди она молча, человек не узнал бы, почему
    // чтение кончилось на половине.
    [Fact]
    public void A_failed_job_stays_visible_and_marks_the_registry()
    {
        BackgroundJobs.Fail(A, "Чтение", "кеш недоступен");

        Assert.True(BackgroundJobs.Any);
        Assert.True(BackgroundJobs.AnyFailed);
        Assert.Contains(BackgroundJobs.All, j => j.Id == A && j.Failed);
    }

    // «Убрать» в панели — это Finish сорвавшейся работы: красный значок гаснет, а соседняя
    // идущая работа остается на месте.
    [Fact]
    public void Removing_a_failed_job_clears_the_red_mark_but_keeps_the_others()
    {
        BackgroundJobs.Fail(A, "Чтение", "Сорвалось на 1 из 2: на диске нет места");
        BackgroundJobs.Report(B, "Обновление", "", 1, 2);

        BackgroundJobs.Finish(A);

        Assert.False(BackgroundJobs.AnyFailed);
        Assert.Contains(BackgroundJobs.All, j => j.Id == B);
    }

    // Следующий запуск той же работы заменяет сорвавшуюся идущей: красное о прошлом прогоне
    // рядом с новым ходом было бы враньем.
    [Fact]
    public void The_next_run_replaces_a_failed_job()
    {
        BackgroundJobs.Fail(A, "Чтение", "кеш недоступен");

        BackgroundJobs.Report(A, "Чтение", "0 из 10", 0, 10);

        Assert.False(BackgroundJobs.AnyFailed);
        Assert.Single(BackgroundJobs.All, j => j.Id == A);
    }

    [Fact]
    public void Finishing_removes_the_job()
    {
        BackgroundJobs.Report(A, "Чтение", "", 1, 2);
        BackgroundJobs.Finish(A);

        Assert.DoesNotContain(BackgroundJobs.All, j => j.Id == A);
    }

    [Fact]
    public void Changes_are_announced()
    {
        var beats = 0;
        void Handler() => beats++;

        BackgroundJobs.Changed += Handler;
        try
        {
            BackgroundJobs.Report(A, "Чтение", "", 1, 2);
            BackgroundJobs.Finish(A);
        }
        finally { BackgroundJobs.Changed -= Handler; }

        Assert.Equal(2, beats);
    }

    // Убирать несуществующую работу — не событие: лишний удар заставлял бы панель
    // перестраиваться на ровном месте.
    [Fact]
    public void Finishing_an_unknown_job_is_silent()
    {
        var beats = 0;
        void Handler() => beats++;

        BackgroundJobs.Changed += Handler;
        try { BackgroundJobs.Finish("test.never-existed"); }
        finally { BackgroundJobs.Changed -= Handler; }

        Assert.Equal(0, beats);
    }
}
