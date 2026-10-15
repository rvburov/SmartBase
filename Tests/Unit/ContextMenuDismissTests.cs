namespace SmartBase.Tests.Unit;

/// <summary>
/// Сторож правила «контекстное меню одно и гаснет от любого нажатия мимо».
///
/// Повод: на Windows правый щелчок мимо открытого меню таблицы оставлял новое меню без подложки,
/// которой Avalonia ловит клик мимо, — меню копились поверх друг друга и не закрывались. Правило
/// держит ContextMenuDismiss, но только если приложение его подключило при старте.
///
/// Проверяем исходник: сбой виден лишь на настоящем экране Windows и macOS, а забыть строку можно
/// одной правкой. Само поведение гоняет стенд Tests/GridProto --keys (раздел «Контекстные меню»).
/// </summary>
public sealed class ContextMenuDismissTests
{
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "SmartBase.sln")))
            dir = dir.Parent;
        Assert.NotNull(dir);   // тест бесполезен, если корень репозитория не найден
        return dir!.FullName;
    }

    [Fact]
    public void App_installs_the_context_menu_rule()
    {
        // Строка, а не комментарий: выключенное правило тоже содержит имя метода.
        var installed = File.ReadLines(Path.Combine(RepoRoot(), "App.axaml.cs"))
            .Any(line => !line.TrimStart().StartsWith("//", StringComparison.Ordinal)
                         && line.Contains("ContextMenuDismiss.Install()", StringComparison.Ordinal));

        Assert.True(installed, "App не подключает ContextMenuDismiss — контекстные меню снова будут " +
                               "копиться поверх друг друга и не гаснуть от клика мимо.");
    }
}
