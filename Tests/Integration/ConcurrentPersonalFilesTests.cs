using System.Collections.Concurrent;
using System.Text;
using SmartBase.Core.Notes;
using SmartBase.Database;
using SmartBase.Views.Notes;

namespace SmartBase.Tests.Integration;

/// <summary>
/// Личные файлы и app.db принадлежат отдельным пользователям. Проверяем параллельный
/// ввод/файловые действия, не приписывая глобальному AppSettings несколько хранилищ сразу.
/// Системная корзина и ее нативный интерфейс восстановления этим набором не имитируются.
/// </summary>
public sealed class ConcurrentPersonalFilesTests
{
    [Fact]
    public void Independent_vaults_keep_content_encoding_and_paths_through_parallel_file_actions()
    {
        using var root = new TempDir();
        RunUsers(32, 32, client =>
        {
            var home = Success(VaultOperations.CreateFolder(root.Path, $"Участник {client:D4}"));
            CheckFileLifecycle(home, client);
        });
        Assert.Equal(32, Directory.GetFiles(root.Path, "План.md", SearchOption.AllDirectories).Length);
        Assert.Empty(Directory.GetFiles(root.Path, "*.sbtmp", SearchOption.AllDirectories));
        Assert.Empty(Directory.GetFiles(root.Path, "*.sbren", SearchOption.AllDirectories));
    }

    [Fact]
    public void Parallel_creators_of_one_note_have_one_winner_and_never_overwrite_existing_content()
    {
        using var root = new TempDir();
        var results = new VaultResult[32];
        RunUsers(results.Length, results.Length, client => results[client] = VaultOperations.CreateNote(root.Path, "Общая"));
        var created = Assert.Single(results.Where(r => r.Ok));
        Assert.All(results.Where(r => !r.Ok), r => Assert.False(string.IsNullOrEmpty(r.Error)));
        Assert.Equal(1, Directory.GetFiles(root.Path, "*.md").Length);
        Assert.Equal(0, new FileInfo(created.Path!).Length);

        const string preserved = "Чужой текст должен сохраниться полностью";
        Success(VaultOperations.WriteNoteAtomic(created.Path!, preserved, false, "\n"));
        RunUsers(results.Length, results.Length, client => results[client] = VaultOperations.CreateNote(root.Path, "Общая"));
        Assert.All(results, r => Assert.False(r.Ok));
        Assert.Equal(preserved, File.ReadAllText(created.Path!));
    }

    [Fact]
    public void Parallel_note_conflicts_keep_both_external_and_unsaved_versions()
    {
        using var root = new TempDir();
        RunUsers(32, 32, client =>
        {
            var home = Success(VaultOperations.CreateFolder(root.Path, $"Участник {client:D4}"));
            var path = Success(VaultOperations.CreateNote(home, "Открытая"));
            Success(VaultOperations.WriteNoteAtomic(path, "Исходная", false, "\n"));
            var opened = VaultOperations.ReadNote(path);
            Assert.Null(opened.Error);
            Assert.NotNull(opened.Content);
            var external = $"Внешняя правка другого редактора {client}, более длинная";
            Success(VaultOperations.WriteNoteAtomic(path, external, false, "\n"));
            Assert.True(NoteAutosave.ChangedOutside(opened.Content!.Snapshot, VaultOperations.Snapshot(path)));

            // Тот же безопасный путь, что при выборе «Сохранить копию» в редакторе.
            var name = NoteAutosave.ConflictName(Path.GetFileName(path), new DateTime(2026, 11, 1, 12, 0, 0));
            var copy = Success(VaultOperations.CreateNote(home, name));
            var mine = $"Несохраненный текст участника {client}";
            Success(VaultOperations.WriteNoteAtomic(copy, mine, opened.Content.Bom, opened.Content.NewLine));
            Assert.Equal(external, File.ReadAllText(path));
            Assert.Equal(mine, File.ReadAllText(copy));
            Assert.NotEqual(path, copy);
            Assert.Equal(2, Directory.GetFiles(home, "*.md").Length);

            Success(VaultOperations.DeletePermanently(path, isFolder: false));
            Assert.True(NoteAutosave.ChangedOutside(opened.Content.Snapshot, VaultOperations.Snapshot(path)));
            Assert.Equal(mine, File.ReadAllText(copy));
        });
    }

    [Fact]
    public void Thousand_independent_personal_stores_roundtrip_settings_and_notes_without_static_store_races()
    {
        using var root = new TempDir();
        try
        {
            // Это 1000 отдельных пользовательских наборов данных, выполняемых восемью
            // работниками. Личные настройки не являются общей многопользовательской БД.
            RunUsers(1000, 8, client =>
            {
                var home = Success(VaultOperations.CreateFolder(root.Path, $"Участник {client:D4}"));
                var source = Path.Combine(home, "database.db");
                var system = Path.Combine(home, "app.db");
                var first = $"Имя {client}";
                var last = $"Фамилия {client}";
                try
                {
                    using (var registry = DatabaseRegistry.Open(source, system))
                    {
                        Assert.True(registry.IsFreshInstall);
                        registry.Set("profile.firstName", first);
                        registry.Set("profile.lastName", "Прежнее");
                        registry.Set("profile.lastName", last);
                        registry.Set("profile.department", $"Подразделение {client % 10}");
                        registry.Set("ext.concurrent.temporary", "Удалить");
                        registry.Remove("ext.concurrent.temporary");
                        registry.Remove("ext.concurrent.temporary");
                        Assert.Null(registry.Get("ext.concurrent.temporary"));
                        Assert.Equal(first, registry.Get("profile.firstName"));
                        Assert.Equal(last, registry.Get("profile.lastName"));
                        Assert.Equal(3, registry.KeysWithPrefix("profile.").Count);
                    }
                    using (var reopened = DatabaseRegistry.Open(source, system))
                    {
                        Assert.False(reopened.IsFreshInstall);
                        Assert.Equal(first, reopened.Get("profile.firstName"));
                        Assert.Equal(last, reopened.Get("profile.lastName"));
                        Assert.Equal($"Подразделение {client % 10}", reopened.Get("profile.department"));
                        Assert.Empty(reopened.KeysWithPrefix("ext.concurrent."));
                    }
                }
                finally
                {
                    // Не держим 1000 простаивающих SQLite-хендлов в одном процессе:
                    // сбрасываем только пул этой уже закрытой личной базы, не соседей.
                    using var key = SqliteGate.NewConnection($"Data Source={system}");
                    SqliteGate.ClearPool(key);
                }
                CheckFileLifecycle(home, client);
            });
            Assert.Equal(1000, Directory.GetFiles(root.Path, "app.db", SearchOption.AllDirectories).Length);
            Assert.Equal(1000, Directory.GetFiles(root.Path, "План.md", SearchOption.AllDirectories).Length);
            Assert.Empty(Directory.GetFiles(root.Path, "*.sbtmp", SearchOption.AllDirectories));
        }
        finally
        {
            // После завершения ВСЕХ работников, перед удалением только нашей фикстуры:
            // pooled SQLite-хендлы на Windows могут еще удерживать app.db открытыми.
            SqliteGate.ClearAllPools();
        }
    }

    private static void CheckFileLifecycle(string home, int client)
    {
        var work = Success(VaultOperations.CreateFolder(home, "Работа"));
        var archive = Success(VaultOperations.CreateFolder(home, "Архив"));
        var note = Success(VaultOperations.CreateNote(work, "Черновик"));
        var text = $"# Участник {client:D4}\nПервая строка\nВторая строка: уникальное содержимое {client}";
        Success(VaultOperations.WriteNoteAtomic(note, text, bom: true, newLine: "\r\n"));
        var read = VaultOperations.ReadNote(note);
        Assert.Null(read.Error);
        Assert.NotNull(read.Content);
        Assert.Equal(text, read.Content!.Text);
        Assert.True(read.Content.Bom);
        Assert.Equal("\r\n", read.Content.NewLine);
        Assert.False(NoteAutosave.ChangedOutside(read.Content.Snapshot, VaultOperations.Snapshot(note)));

        var renamed = Success(VaultOperations.Rename(note, "План", isFolder: false));
        Assert.False(File.Exists(note));
        var duplicate = Success(VaultOperations.Duplicate(renamed, isFolder: false));
        var moved = Success(VaultOperations.Move(renamed, archive, isFolder: false));
        Assert.False(File.Exists(renamed));
        Assert.Equal(File.ReadAllBytes(duplicate), File.ReadAllBytes(moved));
        var renamedFolder = Success(VaultOperations.Rename(archive, "Завершено", isFolder: true));
        var final = Path.Combine(renamedFolder, "План.md");
        Assert.False(Directory.Exists(archive));
        var encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: true);
        var bytes = encoding.GetPreamble().Concat(encoding.GetBytes(text.Replace("\n", "\r\n"))).ToArray();
        Assert.Equal(bytes, File.ReadAllBytes(final));
        Success(VaultOperations.DeletePermanently(duplicate, isFolder: false));
        Assert.False(File.Exists(duplicate));
        Assert.Equal(1, Directory.GetFiles(home, "*.md", SearchOption.AllDirectories).Length);
    }

    private static string Success(VaultResult result)
    {
        Assert.True(result.Ok, result.Error);
        Assert.NotNull(result.Path);
        return result.Path!;
    }

    private static void RunUsers(int count, int workers, Action<int> action)
    {
        var errors = new ConcurrentQueue<Exception>();
        var ready = new CountdownEvent(workers);
        var start = new ManualResetEventSlim(false);
        var threads = Enumerable.Range(0, workers).Select(worker => new Thread(() =>
        {
            try
            {
                ready.Signal();
                if (!start.Wait(TimeSpan.FromSeconds(30))) throw new TimeoutException("Барьер файловых сессий не освобожден");
                for (var client = worker; client < count; client += workers)
                {
                    try { action(client); }
                    catch (Exception ex) { errors.Enqueue(new InvalidOperationException($"Личный набор данных {client}", ex)); }
                }
            }
            catch (Exception ex) { errors.Enqueue(ex); }
        }) { IsBackground = true, Name = "personal-files" }).ToArray();
        var launched = new List<Thread>();
        var prepared = false;
        var joined = true;
        try
        {
            foreach (var thread in threads) { thread.Start(); launched.Add(thread); }
            prepared = ready.Wait(TimeSpan.FromSeconds(30));
        }
        finally
        {
            start.Set();
            var deadline = System.Diagnostics.Stopwatch.StartNew();
            foreach (var thread in launched)
            {
                var remaining = TimeSpan.FromMinutes(3) - deadline.Elapsed;
                joined &= thread.Join(remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero);
            }
            if (joined) { ready.Dispose(); start.Dispose(); }
        }
        Assert.True(prepared, "Файловые сессии не подготовились вовремя");
        Assert.True(joined, "Файловые сессии не завершились за три минуты");
        Assert.True(errors.IsEmpty, string.Join(Environment.NewLine, errors.Select(e => e.ToString())));
    }
}
