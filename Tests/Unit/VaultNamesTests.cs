using SmartBase.Core.Notes;

namespace SmartBase.Tests.Unit;

/// <summary>
/// Имена в хранилище заметок. Проверка строгая и одинаковая на всех системах: хранилище обычно
/// синхронизируется в облако и уезжает на другие машины, поэтому файл, который создается на macOS,
/// но не создается на Windows, — потерянная заметка.
/// </summary>
public sealed class VaultNamesTests
{
    // ── Проверка ────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("Заметка")]
    [InlineData("Заметка.md")]
    [InlineData("Отчет за 2026 год")]
    [InlineData("a.b.c")]
    public void Good_Names_Pass(string name) => Assert.Null(VaultNames.Validate(name));

    [Theory]
    [InlineData("",        "пусто")]
    [InlineData("   ",     "одни пробелы")]
    [InlineData(".",       "точка")]
    [InlineData("..",      "две точки")]
    [InlineData("Имя.",    "точка в конце")]
    [InlineData(".скрытый","ведущая точка прячет файл из дерева")]
    [InlineData("а/б",     "разделитель пути")]
    [InlineData(@"а\б",    "разделитель пути Windows")]
    [InlineData("а?б",     "запрещено в Windows")]
    [InlineData("а:б",     "запрещено в Windows")]
    [InlineData("а*б",     "запрещено в Windows")]
    [InlineData("а|б",     "запрещено в Windows")]
    [InlineData("CON",     "служебное имя Windows")]
    [InlineData("nul.md",  "служебное имя Windows даже с расширением")]
    [InlineData("COM1",    "служебное имя Windows")]
    public void Bad_Names_Are_Rejected(string name, string why) =>
        Assert.False(VaultNames.Validate(name) is null, why);

    [Fact]
    public void Too_Long_Name_Is_Rejected() =>
        Assert.NotNull(VaultNames.Validate(new string('я', VaultNames.MaxNameLength + 1)));

    // ── Расширение ──────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("Заметка",          "Заметка.md")]
    [InlineData("Заметка.md",       "Заметка.md")]
    [InlineData("Заметка.markdown", "Заметка.markdown")]
    [InlineData("Заметка.txt",      "Заметка.txt.md")]
    public void Md_Extension_Is_Added_When_Missing(string input, string expected) =>
        Assert.Equal(expected, VaultNames.EnsureMdExtension(input));

    [Fact]
    public void Folders_Have_No_Extension() =>
        Assert.Equal("", VaultNames.ExtensionOf("Версия 1.0", isFolder: true));

    [Fact]
    public void File_Extension_Is_Taken_From_The_Tail() =>
        Assert.Equal(".md", VaultNames.ExtensionOf("Заметка.md", isFolder: false));

    // ── Свободное имя и копии ───────────────────────────────────────────────────

    [Fact]
    public void Free_Name_Is_Returned_As_Is() =>
        Assert.Equal("Заметка.md", VaultNames.NextFreeName("Заметка.md", false, _ => false));

    [Fact]
    public void Taken_Name_Gets_Copy_Suffix() =>
        Assert.Equal("Заметка (копия).md",
                     VaultNames.NextFreeName("Заметка.md", false, n => n == "Заметка.md"));

    [Fact]
    public void Copy_Suffix_Goes_Before_The_Extension()
    {
        // Иначе «Заметка.md (копия)» перестанет быть markdown и не откроется.
        var name = VaultNames.NextCopyName("Заметка.md", isFolder: false, _ => false);

        Assert.Equal("Заметка (копия).md", name);
        Assert.True(VaultRules.IsMarkdown(name));
    }

    [Fact]
    public void Copies_Are_Numbered_Not_Stacked()
    {
        var taken = new HashSet<string> { "Заметка.md", "Заметка (копия).md" };

        Assert.Equal("Заметка (копия) 2.md",
                     VaultNames.NextCopyName("Заметка.md", false, taken.Contains));
    }

    [Fact]
    public void Copy_Of_A_Copy_Does_Not_Stack_The_Word()
    {
        var taken = new HashSet<string> { "Заметка (копия).md" };

        Assert.Equal("Заметка (копия) 2.md",
                     VaultNames.NextCopyName("Заметка (копия).md", false, taken.Contains));
    }

    [Fact]
    public void Folder_Copy_Keeps_Dots_In_The_Name() =>
        Assert.Equal("Версия 1.0 (копия)",
                     VaultNames.NextCopyName("Версия 1.0", isFolder: true, _ => false));

    [Theory]
    [InlineData("Отчет (копия)",             "Отчет")]
    [InlineData("Отчет (копия) 2",           "Отчет")]
    [InlineData("Отчет (копия) (копия)",     "Отчет")]
    [InlineData("Отчет",                     "Отчет")]
    public void Copy_Suffix_Is_Stripped(string input, string expected) =>
        Assert.Equal(expected, VaultNames.StripCopySuffix(input));
}
