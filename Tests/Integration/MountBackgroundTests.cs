using SmartBase.Core;
using SmartBase.Database;
using SmartBase.Database.Entities;

namespace SmartBase.Tests.Integration;

public sealed class MountBackgroundTests
{
    [Fact]
    public void Failure_updates_forget_and_dispose_follow_the_same_background_job()
    {
        var app = TempDatabase.NewTempPath();
        var path = TempDatabase.NewTempPath();
        try
        {
            using var registry = DatabaseRegistry.Open(path, app);
            var def = registry.GetAll().First(d => d.IsDefault);
            using var ws = Workspace.Attach(registry, DatabaseManager.Open(def.Id, "default", path));
            using var queue = new MountQueue(ws);
            const string name = "Проверка фонового подключения";
            queue.MarkFailed(471, name, "нет сети");
            var job = Assert.Single(BackgroundJobs.All.Where(j => j.Detail.StartsWith(name)));
            Assert.True(job.Failed);
            queue.UpdateFailedText(471, "повтор через минуту");
            job = Assert.Single(BackgroundJobs.All.Where(j => j.Id == job.Id));
            Assert.Contains("повтор через минуту", job.Detail);
            BackgroundJobs.Finish(job.Id);
            queue.UpdateFailedText(471, "повтор позже");
            Assert.DoesNotContain(BackgroundJobs.All, j => j.Id == job.Id);
            queue.MarkFailed(471, name, "повторный сбой");
            Assert.Contains(BackgroundJobs.All, j => j.Id == job.Id && j.Failed);
            queue.Forget(471);
            Assert.DoesNotContain(BackgroundJobs.All, j => j.Id == job.Id);
            queue.MarkFailed(472, name, "сбой");
            queue.Dispose();
            queue.MarkFailed(473, name, "поздний ответ");
            Assert.DoesNotContain(BackgroundJobs.All, j => j.Detail.StartsWith(name));
        }
        finally
        {
            TempDatabase.Cleanup(app);
            TempDatabase.Cleanup(path);
        }
    }

    [Fact]
    public void Successful_mount_finishes_its_background_job()
    {
        var app = TempDatabase.NewTempPath();
        var path = TempDatabase.NewTempPath();
        var other = TempDatabase.NewTempPath();
        try
        {
            using (DatabaseManager.Open(811, "prepare", other)) { }
            using var registry = DatabaseRegistry.Open(path, app);
            var def = registry.GetAll().First(d => d.IsDefault);
            using var ws = Workspace.Attach(registry, DatabaseManager.Open(def.Id, "default", path));
            using var queue = new MountQueue(ws);
            using var completed = new ManualResetEventSlim();
            DatabaseManager? mounted = null;
            var sawJob = false;
            queue.Changed += (_, _) => sawJob |= BackgroundJobs.All.Any(j => j.Detail.StartsWith("Готовая база:"));
            queue.Completed += (_, manager) => { mounted = manager; completed.Set(); };
            queue.Enqueue(new DatabaseRefEntity { Id = 811, Name = "Готовая база", Path = other, Provider = "sqlite" });
            Assert.True(completed.Wait(TimeSpan.FromSeconds(30)));
            Assert.NotNull(mounted);
            Assert.True(sawJob);
            Assert.DoesNotContain(BackgroundJobs.All, j => j.Detail.StartsWith("Готовая база:"));
        }
        finally
        {
            TempDatabase.Cleanup(app);
            TempDatabase.Cleanup(path);
            TempDatabase.Cleanup(other);
        }
    }
}
