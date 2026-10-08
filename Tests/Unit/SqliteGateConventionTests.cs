using System.Text.RegularExpressions;

namespace SmartBase.Tests.Unit;

/// <summary>
/// Сторож правила «подключение SQLite — только через <c>SqliteGate</c>»
/// (<c>Database/SqliteGate.cs</c>). Пул Microsoft.Data.Sqlite изредка отдает одно подключение
/// двум владельцам (dotnet/efcore#39008), и замок пула спасает, только если через него
/// открываются ВСЕ подключения этого пула и через него же пул чистят. Подключение мимо замка
/// снова пускает гонку, а тестом ее ловит лишь стресс на тысячах открытий
/// (<c>SqliteGateTests</c>), — поэтому правило проверяется по тексту, как
/// <see cref="PdfGateConventionTests"/>.
///
/// Мимо замка — это <c>new SqliteConnection(…)</c> (и с полным именем, и <c>SqliteConnection x =
/// new(…)</c>), очистка пула самим <c>SqliteConnection</c> и <c>UseSqlite</c> не с подключением
/// из <c>SqliteGate.NewConnection</c>: из строки EF создал бы подключение сам.
/// </summary>
public sealed class SqliteGateConventionTests
{
    /// <summary>Папки с кодом приложения. Tests/ не сканируется: стенды и тесты открывают файлы
    /// сами, и пул тестов чистится через SqliteGate уже в общих помощниках.</summary>
    private static readonly string[] SourceDirs =
    [
        "Controls", "Core", "Database", "Extensions", "Integrations",
        "Logging", "Notifications", "Settings", "Setup", "Utils", "ViewModels", "Views",
    ];

    private static readonly string GateFile = Path.Combine("Database", "SqliteGate.cs");

    private static readonly Regex Bypass = new(
        @"new\s+(?:Microsoft\.Data\.Sqlite\.)?SqliteConnection\s*\("          // new SqliteConnection(…)
        + @"|\bSqliteConnection\??\s+\w+\s*=\s*new\s*\("                      // SqliteConnection x = new(…)
        + @"|\bSqliteConnection\s*\.\s*Clear(?:All)?Pools?\s*\("               // SqliteConnection.ClearPool(…)
        + @"|UseSqlite\s*\((?!\s*SqliteGate\.NewConnection\s*\()",              // UseSqlite(строка)
        RegexOptions.Compiled);

    [Fact]
    public void Connections_are_made_only_through_the_gate()
    {
        var offenders = Sources(RepoRoot())
            .Where(s => !s.Path.Equals(GateFile, StringComparison.OrdinalIgnoreCase))
            .Where(s => Bypass.IsMatch(s.Text))
            .Select(s => s.Path)
            .ToList();

        Assert.True(offenders.Count == 0,
            "Подключение SQLite создается или пул чистится мимо SqliteGate — так пул снова может " +
            "отдать одно подключение двум владельцам. Берите SqliteGate.NewConnection, " +
            "SqliteGate.ClearPool и SqliteGate.ClearAllPools: " + string.Join(", ", offenders));
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "SmartBase.sln")))
            dir = dir.Parent;
        Assert.NotNull(dir);   // тест бесполезен, если корень репозитория не найден
        return dir!.FullName;
    }

    private static IEnumerable<(string Path, string Text)> Sources(string root)
    {
        foreach (var file in Directory.EnumerateFiles(root, "*.cs", SearchOption.TopDirectoryOnly))
            yield return (Path.GetRelativePath(root, file), File.ReadAllText(file));

        foreach (var dir in SourceDirs)
        {
            var path = Path.Combine(root, dir);
            if (!Directory.Exists(path)) continue;

            foreach (var file in Directory.EnumerateFiles(path, "*.cs", SearchOption.AllDirectories))
                yield return (Path.GetRelativePath(root, file), File.ReadAllText(file));
        }
    }
}
