using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using SmartBase.Core;
using SmartBase.Core.Documents;
using SmartBase.Core.Models;
using SmartBase.Database.DocIndex;
using SmartBase.Database.Repositories;

namespace SmartBase.Tests.Integration;

/// <summary>
/// Реальные файловые значения, лента и локальный индекс при независимых подключениях.
/// Очередь индекса одна на приложение; поиски открывают свои подключения к ее кешу.
/// </summary>
public sealed class ConcurrentLibraryTests
{
    private const int Clients = 8;
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(45);

    [SkippableTheory]
    [InlineData("sqlite")]
    [InlineData("sqlite-shared")]
    [InlineData("postgres")]
    public void Concurrent_file_edits_and_deletions_leave_exact_feed_paths_types_and_cursors(string profile)
    {
        using var f = new MultiUserTestDatabase(profile);
        using var files = new Files();
        var collection = SampleData.AddCollection(f.Repo, "Конкурентная библиотека").Id!.Value;
        var field = SampleData.AddField(f.Repo, collection, "Документ", "file").Id!.Value;
        f.Repo.SetFileFieldInLibrary(field, true);
        var type = f.Repo.DocTypeFieldId(collection)!.Value;
        var active = new HashSet<int> { collection };
        var original = Enumerable.Range(0, Clients).Select(i => files.Write($"first-{i}.txt", $"Первый документ {i}")).ToArray();
        var final = Enumerable.Range(0, Clients).Select(i => files.Write($"final-{i}.md", $"# Последний документ {i}")).ToArray();
        var removed = Enumerable.Range(0, Clients).Select(i => files.Write($"removed-{i}.txt", $"Удаляемая ссылка {i}")).ToArray();
        var records = new int[Clients];
        var deleted = new int[Clients];

        MultiUserTestDatabase.RunClients(f, Clients, (repo, _, client) =>
        {
            var id = SampleData.AddRecord(repo, collection, $"Начало {client}", (client + 1) * 1000).Id!.Value;
            records[client] = id;
            repo.SetFieldValue(id, field, FileValue.Serialize([original[client], final[client]]));
            repo.SetFieldValue(id, type, "Черновик");
            repo.SetFieldValue(id, field, final[client]);
            repo.SetFieldValue(id, type, "Готовый");
            repo.SetRecordTitle(id, $"Окончательный {client}");
            var transient = SampleData.AddRecord(repo, collection, $"Удаляемый {client}").Id!.Value;
            deleted[client] = transient;
            repo.SetFieldValue(transient, field, removed[client]);
            repo.DeleteRecord(transient);

            // Чужая правка может случиться между запросами. Проверяем законность страницы,
            // а не требуем транзакционного снимка от нескольких независимых UI-запросов.
            var page = repo.GetDocPage(collection, type, ConstructorRepository.DocView.All, 0, Clients * 3);
            Assert.Equal(page.Count, page.Select(d => d.Cursor).Distinct().Count());
            Assert.All(page, d => Assert.Equal(collection, d.CollectionId));
            Assert.Single(repo.SearchDocs(active, $"Окончательный {client}", []));
        });

        using var check = f.OpenContext();
        var read = new ConstructorRepository(check);
        var all = read.GetDocPage(collection, type, ConstructorRepository.DocView.All, 0, Clients * 3);
        Assert.Equal(Clients, all.Count);
        Assert.Equal(final.Order(StringComparer.Ordinal), all.Select(d => d.Path).Order(StringComparer.Ordinal));
        Assert.All(all, d => Assert.Equal("Готовый", d.DocType));
        Assert.Equal(Clients, all.Select(d => d.Cursor).Distinct().Count());
        Assert.Equal(Clients, read.CountDocs(collection, type, ConstructorRepository.DocView.All));
        Assert.Equal(Clients, read.GetDocCollectionStats(active)[collection].Total);
        Assert.Equal(Clients, read.GetDocTypeHistograms(active)[collection].Counts["Готовый"]);
        Assert.Equal(final.Order(StringComparer.Ordinal), read.GetDocPaths(active).Order(StringComparer.Ordinal));
        var keys = read.GetDocOrderKeys(collection, type, ConstructorRepository.DocView.All, DocSort.Title);
        Assert.Equal(Clients, keys.Count);
        Assert.Equal(Clients, keys.Select(k => k.RecordId).Distinct().Count());
        var deletedIds = deleted.ToHashSet();
        Assert.False(check.DbRecords.Any(r => deletedIds.Contains(r.Id)));
        Assert.Equal(records.Order(), all.Select(d => d.Id!.Value).Order());
        // Удаление записи убирает ссылку, но не чужой исходный файл на диске.
        Assert.All(original.Concat(final).Concat(removed), p => Assert.True(File.Exists(p)));
        MultiUserTestDatabase.AssertHealthy(check);
    }

    [SkippableTheory]
    [InlineData("sqlite")]
    [InlineData("sqlite-shared")]
    [InlineData("postgres")]
    public void Concurrent_title_and_text_source_changes_keep_each_records_own_file_matches(string profile)
    {
        using var f = new MultiUserTestDatabase(profile);
        using var files = new Files();
        // Файлы существуют до первого построения общего каталога: тест не зависит от
        // автоматического watcher, которого у автоподстановки по договору нет.
        for (var client = 0; client < Clients; client++)
        {
            files.Write($"before-{client}.txt", "До изменения");
            files.Write($"after-{client}.txt", "После изменения");
        }
        var collection = SampleData.AddCollection(f.Repo, "Автофайлы").Id!.Value;
        var source = SampleData.AddField(f.Repo, collection, "Код").Id!.Value;
        var titleFile = SampleData.AddField(f.Repo, collection, "По названию", "file");
        titleFile.ConfigJson = new FileAutoRule(FileAutoRule.TitleSource, files.Directory, "txt").ToConfigJson();
        f.Repo.UpdateField(titleFile);
        var textFile = SampleData.AddField(f.Repo, collection, "По коду", "file");
        textFile.ConfigJson = new FileAutoRule(source.ToString(), files.Directory, "txt").ToConfigJson();
        f.Repo.UpdateField(textFile);
        var ids = new int[Clients];

        MultiUserTestDatabase.RunClients(f, Clients, (repo, _, client) =>
        {
            var id = SampleData.AddRecord(repo, collection, $"before-{client}").Id!.Value;
            ids[client] = id;
            repo.SetFieldValue(id, source, $"before-{client}");
            repo.SetFieldValue(id, source, $"after-{client}");
            repo.SetRecordTitle(id, $"after-{client}");
        });

        using var check = f.OpenContext();
        for (var client = 0; client < Clients; client++)
        {
            var id = ids[client];
            var values = check.DbFieldValues.AsNoTracking().Where(v => v.RecordId == id)
                .ToDictionary(v => v.FieldId, v => v.Value);
            Assert.Equal(files.PathFor($"after-{client}.txt"), values[titleFile.Id!.Value]);
            Assert.Equal(files.PathFor($"after-{client}.txt"), values[textFile.Id!.Value]);
            Assert.Equal($"after-{client}", values[source]);
        }
        MultiUserTestDatabase.AssertHealthy(check);
    }

    [Fact]
    public void Queued_file_work_reads_latest_names_honors_cancellation_and_does_not_restore_deleted_records()
        => QueuedFileWork("sqlite");

    [SkippableTheory]
    [InlineData("sqlite-shared")]
    [InlineData("postgres")]
    public void Queued_file_work_reads_latest_names_on_shared_providers(string profile) => QueuedFileWork(profile);

    private static void QueuedFileWork(string profile)
    {
        using var f = new MultiUserTestDatabase(profile);
        using var files = new Files();
        for (var client = 0; client < Clients; client++)
        {
            files.Write($"before-{client}.txt", "Старая редакция");
            files.Write($"after-{client}.txt", "Новая редакция");
        }
        var collection = SampleData.AddCollection(f.Repo, "Фоновая автоподстановка").Id!.Value;
        var field = SampleData.AddField(f.Repo, collection, "Файл", "file");
        field.ConfigJson = new FileAutoRule(FileAutoRule.TitleSource, files.Directory, "txt").ToConfigJson();
        f.Repo.UpdateField(field);
        var ids = new int[Clients];
        var pending = new ConcurrentBag<FileAutoWork>();
        bool Supported(string path) => path == f.Path;
        void Requested(FileAutoWork work) { if (work.DbPath == f.Path) pending.Add(work); }
        ConstructorRepository.FileAutoWorkSupported += Supported;
        ConstructorRepository.FileAutoWorkRequested += Requested;
        try
        {
            MultiUserTestDatabase.RunClients(f, Clients, (repo, _, client) =>
            {
                var id = SampleData.AddRecord(repo, collection, $"before-{client}").Id!.Value;
                ids[client] = id;
                repo.SetRecordTitle(id, $"after-{client}");
            });
            Assert.True(pending.Count >= Clients);
            using (var unchanged = f.OpenContext()) Assert.Empty(unchanged.DbFieldValues);
            using (var cancelled = new CancellationTokenSource())
            {
                cancelled.Cancel();
                Assert.Equal(ConstructorRepository.FileAutoResolveResult.Cancelled,
                    ConstructorRepository.ExecuteFileAutoWork(pending.First(), cancelled.Token));
            }
            using (var unchanged = f.OpenContext()) Assert.Empty(unchanged.DbFieldValues);
            var deletedIds = ids.Where((_, i) => i % 2 == 0).ToHashSet();
            using (var deleting = f.OpenContext())
            {
                var repo = new ConstructorRepository(deleting);
                foreach (var id in deletedIds) repo.DeleteRecord(id);
            }

            // Создание и переименование могли поставить два задания одной записи.
            // Независимо от порядка оба обязаны читать действительное название,
            // а отсутствие записи не дает права создать ее заново.
            var groups = pending.GroupBy(work => work.RecordId!.Value).ToDictionary(g => g.Key, g => g.ToArray());
            MultiUserTestDatabase.RunClients(f, Clients, (_, _, client) =>
            {
                foreach (var work in groups[ids[client]])
                {
                    var result = ConstructorRepository.ExecuteFileAutoWork(work);
                    Assert.NotEqual(ConstructorRepository.FileAutoResolveResult.Unavailable, result);
                    if (!deletedIds.Contains(ids[client])) Assert.Equal(ConstructorRepository.FileAutoResolveResult.Ok, result);
                }
            });
            using var check = f.OpenContext();
            Assert.False(check.DbRecords.Any(r => deletedIds.Contains(r.Id)));
            Assert.False(check.DbFieldValues.Any(v => deletedIds.Contains(v.RecordId)));
            for (var client = 1; client < Clients; client += 2)
                Assert.Equal(files.PathFor($"after-{client}.txt"), check.DbFieldValues.AsNoTracking()
                    .Single(v => v.RecordId == ids[client] && v.FieldId == field.Id).Value);
            MultiUserTestDatabase.AssertHealthy(check);
        }
        finally
        {
            ConstructorRepository.FileAutoWorkSupported -= Supported;
            ConstructorRepository.FileAutoWorkRequested -= Requested;
        }
    }

    [Fact]
    public async Task Concurrent_queue_producers_and_searches_survive_a_deleted_file_and_keep_one_entry_per_path()
    {
        using var f = new MultiUserTestDatabase("sqlite");
        using var files = new Files();
        var index = new DocTextIndex(files.PathFor(DocTextIndex.FileName));
        await using var service = new DocIndexService(index);
        var sentinel = files.Write("sentinel.txt", "Начало очереди");
        var shared = files.Write("shared.txt", "Общий документ");
        var deleted = files.Write("deleted.txt", "Этот документ удаляется до чтения");
        var paths = Enumerable.Range(0, Clients).Select(i => files.Write($"client-{i}.txt", $"Документ участников {i}")).ToArray();
        var pdf = files.PathFor("blank.pdf");
        using (var document = new PdfSharp.Pdf.PdfDocument()) { document.AddPage(); document.Save(pdf); }
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var blocked = 0;
        var end = new TaskCompletionSource<DocIndexRunEnd>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.Progress += (_, _) =>
        {
            if (Interlocked.CompareExchange(ref blocked, 1, 0) != 0) return;
            entered.Set();
            if (!release.Wait(Patience)) throw new TimeoutException("Не освобожден барьер очереди документов");
        };
        service.Finished += (_, result) => end.TrySetResult(result);
        try
        {
            service.StartPending([sentinel]);
            Assert.True(entered.Wait(Patience));
            var added = 0;
            MultiUserTestDatabase.RunClients(f, Clients, (_, _, client) =>
            {
                Interlocked.Add(ref added, service.StartPending([paths[client], shared, deleted, pdf]));
                Assert.Single(index.Search("начало"));
                Assert.Empty(index.Search("участников"));
            });
            Assert.Equal(Clients + 3, added);
            File.Delete(deleted);
        }
        finally { release.Set(); }

        Assert.True(await service.Wait(Patience));
        var completed = await end.Task.WaitAsync(Patience);
        Assert.False(completed.Failed);
        Assert.Equal(Clients + 4, completed.Done);
        Assert.Equal(Clients, index.Search("участников").Count);
        Assert.Equal(Clients, index.Search("участников").Select(h => h.Path).Distinct().Count());
        var stats = index.Stats();
        Assert.Equal(Clients + 2, stats.Indexed);
        Assert.Equal(1, stats.NoText);
        Assert.Equal(1, stats.Failed);
        var survey = index.Survey(paths.Concat([sentinel, shared, deleted, pdf]).ToArray());
        Assert.Equal(Clients + 3, survey.Census.Total);
        Assert.Equal(Clients + 2, survey.Census.Read);
        Assert.False(survey.Files[DocTextIndex.KeyOf(deleted)].OnDisk);
    }

    [Fact]
    public async Task Stop_clear_and_concurrent_restart_do_not_restore_old_text_or_lose_new_requests()
    {
        using var f = new MultiUserTestDatabase("sqlite");
        using var files = new Files();
        var index = new DocTextIndex(files.PathFor(DocTextIndex.FileName));
        await using var service = new DocIndexService(index);
        var sentinel = files.Write("sentinel.txt", "Прежняя редакция");
        var abandoned = files.Write("abandoned.txt", "Отмененный документ");
        var paths = Enumerable.Range(0, Clients).Select(i => files.Write($"new-{i}.md", $"# Восстановленный документ {i}")).ToArray();
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var blocked = 0;
        service.Progress += (_, _) =>
        {
            if (Interlocked.CompareExchange(ref blocked, 1, 0) != 0) return;
            entered.Set();
            if (!release.Wait(Patience)) throw new TimeoutException("Не освобожден барьер остановки");
        };
        try
        {
            service.StartPending([sentinel, abandoned]);
            Assert.True(entered.Wait(Patience));
            service.Stop();
            index.Clear();
            var added = 0;
            MultiUserTestDatabase.RunClients(f, Clients, (_, _, client) =>
                Interlocked.Add(ref added, service.StartPending([paths[client], paths[client]])));
            Assert.Equal(Clients, added);
        }
        finally { release.Set(); }
        Assert.True(await service.Wait(Patience));
        Assert.False(service.Running);
        Assert.Empty(index.Search("прежняя"));
        Assert.Empty(index.Search("отмененный"));
        Assert.Equal(Clients, index.Search("восстановленный").Count);
        Assert.Equal(Clients, index.Stats().Indexed);
    }

    [Theory]
    [InlineData(2, 1)]
    [InlineData(3_000_000, 2_000_000)]
    public void A_thousand_background_jobs_keep_their_progress_failure_and_lifetime_independent(int total, int done)
    {
        const int count = 1000;
        var prefix = "concurrent-library-" + Guid.NewGuid().ToString("N") + "-";
        var before = BackgroundJobs.All;
        var previous = before.Select(j => j.Id).ToHashSet();
        var changes = 0;
        void Changed()
        {
            Interlocked.Increment(ref changes);
            _ = BackgroundJobs.All;
            _ = BackgroundJobs.Progress();
        }
        BackgroundJobs.Changed += Changed;
        try
        {
            Parallel.For(0, count, i => BackgroundJobs.Report(prefix + i, "Чтение", i.ToString(), done, total, icon: "library"));
            var first = BackgroundJobs.All.Where(j => j.Id.StartsWith(prefix, StringComparison.Ordinal)).ToList();
            Assert.Equal(count, first.Count);
            Assert.All(first, j => { Assert.Equal(done, j.Done); Assert.Equal(total, j.Total); Assert.False(j.Failed); });
            var expectedDone = before.Where(j => j.Total > 0).Sum(j => (long)Math.Clamp(j.Done, 0, j.Total)) + (long)count * done;
            var expectedTotal = before.Where(j => j.Total > 0).Sum(j => (long)j.Total) + (long)count * total;
            var progress = BackgroundJobs.Progress();
            Assert.NotNull(progress);
            Assert.Equal(expectedDone / (double)expectedTotal, progress.Value, 8);
            Parallel.For(0, count, i =>
            {
                if (i % 7 == 0) BackgroundJobs.Fail(prefix + i, "Чтение", $"Сбой {i}", "library");
                else BackgroundJobs.Report(prefix + i, "Чтение", i.ToString(), total, total, icon: "library");
            });
            var second = BackgroundJobs.All.Where(j => j.Id.StartsWith(prefix, StringComparison.Ordinal)).ToList();
            Assert.Equal(count, second.Count);
            Assert.Equal(Enumerable.Range(0, count).Count(i => i % 7 == 0), second.Count(j => j.Failed));
            Assert.All(second.Where(j => !j.Failed), j => Assert.Equal(total, j.Done));
            Parallel.For(0, count, i => BackgroundJobs.Finish(prefix + i));
            Assert.DoesNotContain(BackgroundJobs.All, j => j.Id.StartsWith(prefix, StringComparison.Ordinal));
            Assert.True(previous.SetEquals(BackgroundJobs.All.Select(j => j.Id)));
            Assert.Equal(count * 3, changes);
        }
        finally
        {
            BackgroundJobs.Changed -= Changed;
            for (var i = 0; i < count; i++) BackgroundJobs.Finish(prefix + i);
        }
    }

    private sealed class Files : IDisposable
    {
        internal string Directory { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "sb-concurrent-library-" + Guid.NewGuid().ToString("N"));
        internal Files() => System.IO.Directory.CreateDirectory(Directory);
        internal string PathFor(string name) => System.IO.Path.Combine(Directory, name);
        internal string Write(string name, string contents) { var path = PathFor(name); File.WriteAllText(path, contents); return path; }
        public void Dispose() { try { System.IO.Directory.Delete(Directory, true); } catch { } }
    }
}
