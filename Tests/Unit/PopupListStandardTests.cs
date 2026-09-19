using System.Text.RegularExpressions;

namespace SmartBase.Tests.Unit;

/// <summary>
/// Сторож стандарта всплывающих списков и кнопок-выпадашек.
///
/// Повод завести: три подменю сайдбара «Отображение свойств даты/исполнителя/файла» — один и тот
/// же список с поиском, но на стандарт перевели только календарь. Задачи остались с голым
/// ScrollViewer (чекбоксы вплотную к рамке, полоса внутри), а панель библиотеки родилась копией
/// задачной и унаследовала то же. Разошлись они молча: собирается разметка в коде, и глазами это
/// видно только на живом экране.
///
/// Проверяем исходник, а не поведение: собрать Avalonia-контрол в юнит-тесте нельзя, зато можно
/// поймать ровно ту правку, с которой начинается расхождение — «скопировал соседнюю панель».
/// </summary>
public sealed class PopupListStandardTests
{
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "SmartBase.sln")))
            dir = dir.Parent;
        Assert.NotNull(dir);   // тест бесполезен, если корень репозитория не найден
        return dir!.FullName;
    }

    private static string Read(params string[] parts) =>
        File.ReadAllText(Path.Combine(RepoRoot(), Path.Combine(parts)));

    /// <summary>Тело метода целиком: от сигнатуры до следующего метода того же уровня.</summary>
    private static string MethodBody(string source, string signature)
    {
        var start = source.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(start >= 0, $"в SidebarTree больше нет метода {signature}");
        var end = source.IndexOf("\n    private", start + signature.Length, StringComparison.Ordinal);
        return end < 0 ? source[start..] : source[start..end];
    }

    private static readonly string[] Panels =
    [
        "private Control BuildDateFieldsPanel",
        "private Control BuildUserFieldsPanel",
        "private Control BuildFileFieldsPanel",
    ];

    [Theory]
    [InlineData("private Control BuildDateFieldsPanel")]
    [InlineData("private Control BuildUserFieldsPanel")]
    [InlineData("private Control BuildFileFieldsPanel")]
    public void Every_field_panel_lays_out_its_list_through_the_shared_helper(string signature)
    {
        var body = MethodBody(Read("Controls", "Components", "Sidebar", "SidebarTree.axaml.cs"), signature);

        // Голый ScrollViewer в карточке — это отсутствие воздуха и полоса из шаблона вместо
        // вынесенной: ровно то расхождение, которое пришлось чинить руками.
        Assert.DoesNotContain("Child        = new ScrollViewer", body);
        Assert.DoesNotContain("Child           = new ScrollViewer", body);
        Assert.Contains("ScrollBarLink.", body);

        // У карточки-контейнера Padding обязан быть нулевым, иначе полосы отойдут от краев.
        Assert.Contains("new Thickness(0)", body);
        Assert.Contains("ClipToBounds", body);

        // Высота — из общего токена, а не литералом.
        Assert.Contains("PopupListHeight", body);
        Assert.Contains("PopupContentWidth", body);
    }

    /// <summary>
    /// «(Выделить все)» есть там, где ролей у коллекции может быть несколько, и его нет там, где
    /// роль одна. У задач исполнитель ОДИН (запрет живет в ConstructorRepository.Tasks), поэтому
    /// шапка «отметить все» предлагала бы действие, которое тут же откатится.
    /// </summary>
    [Fact]
    public void Select_all_header_matches_whether_the_role_may_repeat()
    {
        var source = Read("Controls", "Components", "Sidebar", "SidebarTree.axaml.cs");
        // Ищем именно создание чекбокса: сам оборот «(Выделить все)» встречается и в
        // комментарии, объясняющем, почему у исполнителя этой строки нет.
        const string Header = "Content = \"(Выделить все)\"";

        Assert.Contains(Header, MethodBody(source, Panels[0]));         // поля-даты
        Assert.DoesNotContain(Header, MethodBody(source, Panels[1]));   // исполнитель
        Assert.Contains(Header, MethodBody(source, Panels[2]));         // файловые поля
    }

    /// <summary>
    /// Кнопка «Тип документа» — не ComboBox, но выглядеть обязана как он. Вид живет в классе
    /// стиля: сеттер, прописанный на самой кнопке, перебил бы состояния (приоритет локального
    /// значения выше приоритета стиля), и подсветка открытой панели молча не сработала бы.
    /// </summary>
    [Fact]
    public void Navbar_dropdown_button_keeps_the_standard_class()
    {
        var navbar = Read("Controls", "Components", "Navbar", "NavbarLibrary.axaml");

        Assert.Contains("Classes=\"navdrop\"", navbar);
        // Вид — только из стиля: на самой кнопке этих свойств быть не должно.
        Assert.DoesNotContain("Background=\"{StaticResource BrushSecondaryBg}\"", navbar);
        Assert.DoesNotContain("CornerRadius=", navbar);
        // Высота — токеном, а не числом.
        Assert.Contains("Height=\"{StaticResource ControlHeightStandard}\"", navbar);

        var styles = Read("Assets", "Styles", "Controls.axaml");
        Assert.Contains("Selector=\"Button.navdrop\"", styles);
        // Открытая панель подсвечивает якорь — иначе непонятно, чей это список.
        Assert.Matches(new Regex(@"Button\.navdrop\.open\s*/template/\s*ContentPresenter"), styles);
    }
}
