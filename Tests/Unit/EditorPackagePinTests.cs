using System.Text.RegularExpressions;

namespace SmartBase.Tests.Unit;

/// <summary>
/// Версия AvaloniaEdit — на ней стоит редактор заметок, и понизить ее нельзя.
///
/// В 11.0.0 есть дыра, из-за которой приложение падает целиком при обычном клике мимо
/// редактора: `TextAreaTextInputMethodClient.Selection` не проверяет, что `TextArea` уже
/// отцеплен, а Avalonia 11.2 спрашивает его ровно в этот момент
/// (`SetTextArea(null)` → `RaiseSurroundingTextChanged` → NullReferenceException).
///
/// Пин легко принять за лишний: пакет один и никем больше не тянется. Раньше 11.0.0 приходила
/// транзитивно с `Markdown.Avalonia.SyntaxHigh`; тот пакет убран вместе со своим движком показа,
/// но понижать версию по-прежнему нельзя.
///
/// Автоматикой это не ловится: путь чисто интерфейсный — фокус, курсор, системный ввод, — а
/// экранных тестов в проекте нет. Значит сторожить остается сам номер версии.
/// </summary>
public sealed class EditorPackagePinTests
{
    private const int MinMajor = 11;
    private const int MinMinor = 1;

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "SmartBase.sln")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }

    [Fact]
    public void AvaloniaEdit_Is_Pinned_Above_The_Broken_Version()
    {
        var csproj = File.ReadAllText(Path.Combine(RepoRoot(), "SmartBase.csproj"));
        var match  = Regex.Match(csproj,
            """PackageReference\s+Include="Avalonia\.AvaloniaEdit"\s+Version="(\d+)\.(\d+)\.(\d+)"\s*/>""");

        Assert.True(match.Success,
                    "Явная ссылка на Avalonia.AvaloniaEdit пропала из SmartBase.csproj — вернется "
                    + "транзитивная 11.0.0, и редактор заметок начнет ронять приложение при потере фокуса.");

        var major = int.Parse(match.Groups[1].Value);
        var minor = int.Parse(match.Groups[2].Value);

        Assert.True(major > MinMajor || (major == MinMajor && minor >= MinMinor),
                    $"Avalonia.AvaloniaEdit {major}.{minor} — ниже {MinMajor}.{MinMinor}. "
                    + "В 11.0.0 клик мимо редактора заметок валит приложение с NullReferenceException.");
    }
}
