using SmartBase.Setup;

namespace SmartBase.Tests.Unit;

/// <summary>
/// Установка в НЕСТАНДАРТНУЮ папку: удаление обязано уносить только свои файлы и не трогать
/// чужие. Это главный риск сценария «поставил в Документы» — папка общая, и рекурсивный снос
/// каталога стёр бы там всё.
///
/// Тестируется <see cref="InstallService.RemoveOwnedFilesAndPrune"/>, а не Uninstall целиком:
/// Uninstall дополнительно снимает автозапуск и удаляет ярлыки меню «Пуск» — вызов из теста
/// испортил бы машину, на которой он идёт.
/// </summary>
public sealed class InstallServiceTests
{
    private static string NewTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "sb-install-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    // Кладёт в папку файлы приложения — те самые, что создаёт установка на этой ОС.
    private static void CreateOwnedFiles(string dir)
    {
        foreach (var f in InstallService.OwnedInstallFiles(dir))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(f)!);
            File.WriteAllText(f, "owned");
        }
    }

    // ── Главный сценарий: общая папка ──────────────────────────────────────────

    [Fact]
    public void Uninstall_from_shared_folder_keeps_foreign_files_and_folder()
    {
        var dir = NewTempDir();
        try
        {
            CreateOwnedFiles(dir);

            // Чужое добро, оказавшееся в той же папке: файлы, вложенная папка, файл внутри неё.
            var doc       = Path.Combine(dir, "договор.docx");
            var sub       = Path.Combine(dir, "Фотографии");
            var subFile   = Path.Combine(sub, "отпуск.jpg");
            var lookalike = Path.Combine(dir, "SmartBase-заметки.txt");   // имя похоже, но файл не наш
            File.WriteAllText(doc, "чужое");
            Directory.CreateDirectory(sub);
            File.WriteAllText(subFile, "чужое");
            File.WriteAllText(lookalike, "чужое");

            InstallService.RemoveOwnedFilesAndPrune(dir);

            Assert.True(Directory.Exists(dir), "папку с чужими файлами удалять нельзя");
            Assert.True(File.Exists(doc));
            Assert.True(Directory.Exists(sub));
            Assert.True(File.Exists(subFile));
            Assert.True(File.Exists(lookalike), "файл с похожим именем не наш — трогать нельзя");

            foreach (var f in InstallService.OwnedInstallFiles(dir))
                Assert.False(File.Exists(f), $"свой файл должен быть удалён: {f}");
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    // ── Выделенная папка: уходит целиком ───────────────────────────────────────

    [Fact]
    public void Uninstall_from_dedicated_folder_removes_the_folder_itself()
    {
        var dir = NewTempDir();
        var existed = true;
        try
        {
            CreateOwnedFiles(dir);
            InstallService.RemoveOwnedFilesAndPrune(dir);
            existed = Directory.Exists(dir);
            Assert.False(existed, "в папке не осталось ничего чужого — она должна исчезнуть");
        }
        finally { if (existed && Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void Uninstall_is_silent_when_nothing_is_installed()
    {
        var dir = NewTempDir();
        try
        {
            var foreign = Path.Combine(dir, "чужой.txt");
            File.WriteAllText(foreign, "чужое");

            InstallService.RemoveOwnedFilesAndPrune(dir);   // наших файлов нет — не должно бросать

            Assert.True(File.Exists(foreign));
            Assert.True(Directory.Exists(dir));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    // ── Список своих файлов не выходит за пределы папки ────────────────────────

    [Fact]
    public void Owned_files_never_escape_the_install_folder()
    {
        var dir = Path.Combine(Path.GetTempPath(), "sb-owned-check");
        var files = InstallService.OwnedInstallFiles(dir);

        Assert.NotEmpty(files);
        foreach (var f in files)
        {
            var full = Path.GetFullPath(f);
            Assert.StartsWith(Path.GetFullPath(dir) + Path.DirectorySeparatorChar, full, StringComparison.Ordinal);
            // Ровно один уровень: вложенных путей установщик в папке установки не создаёт.
            Assert.Equal(Path.GetFullPath(dir), Path.GetDirectoryName(full));
        }
    }

    [Fact]
    public void Owned_files_include_the_installer_lying_next_to_the_app()
    {
        var dir   = Path.Combine(Path.GetTempPath(), "sb-owned-check");
        var files = InstallService.OwnedInstallFiles(dir).Select(Path.GetFileName).ToList();

        Assert.Contains(InstallService.AppExeName,   files);
        Assert.Contains(InstallService.SetupExeName, files);   // с 1.0.5 лежит рядом и удаляется вместе
    }

    // ── Приведение пути к «выделенной» папке ───────────────────────────────────

    // Пропусков здесь нет намеренно: Tests/Unit — обязательный быстрый гейт без SkippableFact.
    // Вместо пропуска проверяется ожидаемое поведение той ОС, на которой идёт прогон.
    [Fact]
    public void Chosen_folder_gets_a_dedicated_subfolder_outside_mac()
    {
        var docs     = Path.Combine(Path.GetTempPath(), "Документы");
        var expected = InstallService.IsMac
            ? docs                                     // единица установки — .app-бандл, подпапка не нужна
            : Path.Combine(docs, "SmartBase");         // иначе бинарь лёг бы прямо в «Документы»
        Assert.Equal(expected, InstallService.EnsureDedicatedDir(docs));
    }

    [Fact]
    public void Folder_already_named_after_the_app_is_left_as_is()
    {
        var dir = Path.Combine(Path.GetTempPath(), "SmartBase");
        Assert.Equal(dir, InstallService.EnsureDedicatedDir(dir));   // верно на всех ОС
    }

    [Fact]
    public void Folder_that_already_holds_the_app_is_left_as_is()
    {
        // Обновление копии, поставленной в «неправильную» папку прошлой версией, не должно
        // уводить установку на уровень глубже — иначе рядом появилась бы вторая копия.
        var dir = NewTempDir();
        try
        {
            var app = InstallService.InstalledPath(dir);
            if (InstallService.IsMac) Directory.CreateDirectory(app);   // .app-бандл — это каталог
            else                      File.WriteAllText(app, "app");

            Assert.Equal(dir, InstallService.EnsureDedicatedDir(dir));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void Trailing_separator_does_not_create_an_empty_level()
    {
        var docs     = Path.Combine(Path.GetTempPath(), "Документы") + Path.DirectorySeparatorChar;
        var expected = InstallService.IsMac
            ? docs
            : Path.Combine(Path.GetTempPath(), "Документы", "SmartBase");
        Assert.Equal(expected, InstallService.EnsureDedicatedDir(docs));
    }
}
