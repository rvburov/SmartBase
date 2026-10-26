using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using SmartBase.Core;
using SmartBase.Core.Documents;
using SmartBase.Core.Models;
using SmartBase.Database;
using SmartBase.Database.DocIndex;
using SmartBase.Database.Repositories;

namespace SmartBase.Tests.Benchmarks;

/// <summary>
/// Библиотечные карточки, автоподстановка, полнотекстовый кеш и фоновые работы.
/// Участники имеют отдельные контексты; очередь чтения общая, как в одном приложении.
/// </summary>
internal sealed class MultiUserLibraryScenario : IMultiUserScenario
{
    private readonly MultiUserFixture _fixture;
    private readonly int _collection, _manualFile, _autoFile, _code, _type;
    private readonly string[] _beforeCode, _afterCode, _original, _final;
    private readonly string[] _allFiles;
    private readonly string _jobs;
    private readonly DocTextIndex _index;
    private readonly DocIndexService _service;
    private readonly ConcurrentDictionary<int, Expected> _records = new();
    private readonly ConcurrentDictionary<string, byte> _requestedPaths = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<int, byte> _failedJobs = new();
    private readonly ConcurrentQueue<Exception> _indexFailures = new();

    private sealed record Expected(int Id, string Title, string[] ManualPaths, string AutoPath, string Type)
    {
        internal IEnumerable<string> Paths => ManualPaths.Concat(AutoPath.Length == 0 ? [] : new[] { AutoPath });
    }

    private MultiUserLibraryScenario(MultiUserFixture fixture, int collection, int manualFile, int autoFile,
        int code, int type, string[] beforeCode, string[] afterCode, string[] original, string[] final)
    {
        _fixture = fixture;
        (_collection, _manualFile, _autoFile, _code, _type) = (collection, manualFile, autoFile, code, type);
        (_beforeCode, _afterCode, _original, _final) = (beforeCode, afterCode, original, final);
        _allFiles = beforeCode.Concat(afterCode).Concat(original).Concat(final).ToArray();
        _jobs = "multiuser-library-" + Guid.NewGuid().ToString("N") + "-";
        _index = new DocTextIndex(Path.Combine(fixture.DataDirectory, DocTextIndex.FileName));
        _service = new DocIndexService(_index);
        _service.Progress += (_, progress) => BackgroundJobs.Report(_jobs + "index", "Чтение документов",
            progress.File, progress.Done, progress.Total, stop: _service.Stop, icon: "library");
        _service.Finished += (_, result) =>
        {
            if (result.Error is { } error)
            {
                _indexFailures.Enqueue(error);
                BackgroundJobs.Fail(_jobs + "index", "Чтение документов", error.GetType().Name, "library");
            }
            else BackgroundJobs.Finish(_jobs + "index");
        };
    }

    internal static IMultiUserScenario Create(MultiUserFixture fixture)
    {
        var directory = Path.Combine(fixture.DataDirectory, "library-files");
        Directory.CreateDirectory(directory);
        var count = fixture.Options.Clients;
        var before = new string[count];
        var after = new string[count];
        var original = new string[count];
        var final = new string[count];
        for (var client = 0; client < count; client++)
        {
            before[client] = Path.Combine(directory, $"code-{client:D4}.txt");
            after[client] = Path.Combine(directory, $"code-{client:D4}-updated.txt");
            original[client] = Path.Combine(directory, $"manual-{client:D4}.txt");
            final[client] = Path.Combine(directory, $"manual-{client:D4}-updated.md");
            File.WriteAllText(before[client], $"Предыдущая редакция {client}");
            File.WriteAllText(after[client], $"librarydocument автоматический документ участника {client}");
            File.WriteAllText(original[client], $"Предыдущее приложение {client}");
            File.WriteAllText(final[client], $"# librarydocument приложение участника {client}");
        }
        // Снимок каталога строится только после подготовки всех имен: watcher в продукте нет.
        using var context = new AppDbContext(fixture.Connection);
        context.ApplyPerformancePragmas();
        var repo = new ConstructorRepository(context);
        var collection = repo.AddCollection(new DbCollection { Name = "Нагрузка библиотеки", Position = 3000 }).Id!.Value;
        var code = repo.AddField(new DbField { CollectionId = collection, Name = "Код файла", FieldType = "text", Position = 1000 }).Id!.Value;
        var manual = repo.AddField(new DbField { CollectionId = collection, Name = "Приложение", FieldType = "file", Position = 2000 }).Id!.Value;
        var automatic = repo.AddField(new DbField
        {
            CollectionId = collection, Name = "По коду", FieldType = "file", Position = 3000,
            ConfigJson = new FileAutoRule(code.ToString(), directory, "txt").ToConfigJson(),
        }).Id!.Value;
        repo.SetFileFieldInLibrary(manual, true);
        repo.SetFileFieldInLibrary(automatic, true);
        return new MultiUserLibraryScenario(fixture, collection, manual, automatic, code,
            repo.DocTypeFieldId(collection)!.Value, before, after, original, final);
    }

    public void Execute(AppDbContext context, ConstructorRepository repo, MultiUserClient client, int round)
    {
        var job = _jobs + "client-" + client.Id;
        try
        {
            BackgroundJobs.Report(job, "Библиотека", "Создание", 0, 5, icon: "library");
            var title = $"library-{client.Id:D4}-round-{round:D4}";
            var id = client.Measure("library-create", () => repo.AddRecord(new DbRecord
            {
                CollectionId = _collection, Title = title, Position = (client.Id + 1) * 1000,
            })).Id!.Value;
            var expected = new Expected(id, title, [], "", "");
            _records[id] = expected;
            client.Measure("library-file-value", () => repo.SetFieldValue(id, _manualFile,
                FileValue.Serialize([_original[client.Id], _final[client.Id]])));
            _records[id] = expected = expected with { ManualPaths = [_original[client.Id], _final[client.Id]] };
            client.Measure("library-file-edit", () => repo.SetFieldValue(id, _manualFile, _final[client.Id]));
            _records[id] = expected = expected with { ManualPaths = [_final[client.Id]] };
            BackgroundJobs.Report(job, "Библиотека", "Автоподстановка", 1, 5, icon: "library");
            client.Measure("library-auto-source", () => repo.SetFieldValue(id, _code, Path.GetFileNameWithoutExtension(_beforeCode[client.Id])));
            _records[id] = expected = expected with { AutoPath = _beforeCode[client.Id] };
            client.Measure("library-auto-edit", () => repo.SetFieldValue(id, _code, Path.GetFileNameWithoutExtension(_afterCode[client.Id])));
            _records[id] = expected = expected with { AutoPath = _afterCode[client.Id] };
            client.Measure("library-type", () => repo.SetFieldValue(id, _type, "Готовый"));
            _records[id] = expected = expected with { Type = "Готовый" };
            var active = new HashSet<int> { _collection };
            BackgroundJobs.Report(job, "Библиотека", "Поиск и страница", 2, 5, icon: "library");
            client.Measure("library-search", () =>
            {
                var own = repo.SearchDocs(active, title, [], limit: 10);
                if (own.Count != 2 || own.Any(d => d.Id != id)
                    || !own.Select(d => d.Path).ToHashSet(StringComparer.Ordinal).SetEquals(expected.Paths))
                    throw new InvalidOperationException("Поиск библиотеки потерял карточки текущего участника");
                var page = repo.GetDocPage(_collection, _type, ConstructorRepository.DocView.All, 0, 20);
                if (page.Count == 0 || page.Select(d => d.Cursor).Distinct().Count() != page.Count)
                    throw new InvalidOperationException("Страница библиотеки пуста или повторяет курсор файла");
            });
            BackgroundJobs.Report(job, "Библиотека", "Очередь чтения", 3, 5, icon: "library");
            var paths = expected.Paths.ToArray();
            client.Measure("library-index-enqueue", () => _service.Start(paths));
            foreach (var path in paths) _requestedPaths[DocTextIndex.KeyOf(path)] = 0;
            client.Measure("library-index-search", () =>
            {
                // Очередь еще может читать документы; отсутствие находки пока допустимо,
                // но посторонний/несуществующий путь в выдаче не допустим.
                var found = _index.Search("librarydocument", 20);
                if (found.Any(hit => !File.Exists(hit.Path)))
                    throw new InvalidOperationException("Поиск кеша вернул несуществующий файл");
            });
            BackgroundJobs.Report(job, "Библиотека", "Удаление временной карточки", 4, 5, icon: "library");
            var transient = client.Measure("library-transient-create", () => repo.AddRecord(new DbRecord
            {
                CollectionId = _collection, Title = title + "-transient", Position = (client.Id + 1) * 1000,
            })).Id!.Value;
            _records[transient] = new Expected(transient, title + "-transient", [], "", "");
            client.Measure("library-transient-delete", () => repo.DeleteRecord(transient));
            _records.TryRemove(transient, out _);
            BackgroundJobs.Finish(job);
            context.ChangeTracker.Clear();
        }
        catch
        {
            _failedJobs[client.Id] = 0;
            BackgroundJobs.Fail(job, "Библиотека", "Операция участника завершилась ошибкой", "library");
            throw;
        }
    }

    public IEnumerable<MultiUserCheck> Verify(MultiUserClient[] clients)
    {
        try
        {
            var drained = _service.Wait(TimeSpan.FromSeconds(120)).GetAwaiter().GetResult();
            yield return new("Очередь библиотеки закончила чтение всех поставленных документов", drained);
            if (!drained) yield break;
            using var context = new AppDbContext(_fixture.Connection);
            context.ApplyPerformancePragmas();
            var repo = new ConstructorRepository(context);
            var active = new HashSet<int> { _collection };
            var expected = _records.Values.ToDictionary(r => r.Id);
            var rows = context.DbRecords.AsNoTracking().Where(r => r.CollectionId == _collection).ToList();
            var total = repo.CountDocs(_collection, _type, ConstructorRepository.DocView.All);
            var docs = repo.GetDocPage(_collection, _type, ConstructorRepository.DocView.All, 0, total + 1);
            var expectedCards = expected.Values.SelectMany(r => r.Paths.Select(p => (r.Id, Path: p, r.Type))).ToHashSet();
            var actualCards = docs.Select(d => (d.Id!.Value, d.Path, d.DocType)).ToHashSet();
            yield return new("Библиотечные записи совпадают с подтвержденными командами",
                rows.Select(r => r.Id).ToHashSet().SetEquals(expected.Keys), $"факт {rows.Count}, подтверждено {expected.Count}");
            yield return new("Файлы, типы и карточки библиотеки совпадают с подтвержденными значениями",
                actualCards.SetEquals(expectedCards) && docs.Count == expectedCards.Count
                && rows.All(r => expected.TryGetValue(r.Id, out var e) && r.Title == e.Title));
            yield return new("Курсоры библиотеки не повторяются", docs.Count == docs.Select(d => d.Cursor).Distinct().Count());
            yield return new("Счетчики, гистограмма типов и перепись библиотеки согласованы",
                total == expectedCards.Count && repo.GetDocCollectionStats(active)[_collection].Total == total
                && repo.GetDocTypeHistograms(active)[_collection].Counts.Values.Sum() == total
                && repo.GetDocPaths(active).Order(StringComparer.Ordinal)
                    .SequenceEqual(expectedCards.Select(c => c.Path).Order(StringComparer.Ordinal)));
            var keys = repo.GetDocOrderKeys(_collection, _type, ConstructorRepository.DocView.All, DocSort.Title);
            yield return new("Глобальный порядок содержит каждую библиотечную карточку",
                keys.Count == total && keys.Select(k => (k.RecordId, k.FieldId, k.FileIndex)).Distinct().Count() == total);
            var hits = _index.Search("librarydocument", _fixture.Options.Clients * 2 + 10);
            yield return new("Полнотекстовый поиск прочитал каждый запрошенный физический документ",
                hits.Select(h => h.Path).ToHashSet(StringComparer.Ordinal).SetEquals(_requestedPaths.Keys));
            yield return new("Очередь не скрыла сбой чтения кеша", _indexFailures.IsEmpty && _index.Stats().Failed == 0);
            yield return new("Операции с карточками не удалили исходные файлы", _allFiles.All(File.Exists));
            var jobs = BackgroundJobs.All.Where(j => j.Id.StartsWith(_jobs + "client-", StringComparison.Ordinal)).ToList();
            yield return new("Фоновые работы участников независимы: успех убран, ошибки остаются видимыми",
                jobs.All(j => j.Failed) && jobs.Select(j => j.Id).ToHashSet()
                    .SetEquals(_failedJobs.Keys.Select(id => _jobs + "client-" + id)));
        }
        finally { _service.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
    }
}
