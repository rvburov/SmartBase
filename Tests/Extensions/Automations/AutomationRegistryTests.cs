using SmartBase.Extensions;
using SmartBase.Extensions.Automations;

namespace SmartBase.Tests.Extensions.Automations;

/// <summary>
/// Реестр автоматизаций (<see cref="AutomationRegistry"/>) и мост в действие расширения — то, на
/// чем держится обещание шага 13: <b>новый класс сам появляется в списке шагов сценария</b>.
///
/// Ни один тест не называет конкретную автоматизацию по имени: список найденных — это то, что
/// лежит в папке, и удаление любой из них набор ломать не должно. Проверяются правила, а не
/// содержимое: ключи уникальны и похожи на слаги, каждая создается без аргументов, а
/// обернутая в действие она имеет вид «строки → строки».
/// </summary>
public sealed class AutomationRegistryTests
{
    [Fact]
    public void Keys_are_unique()
    {
        AutomationRegistry.EnsureUniqueKeys();   // не бросает — тезок нет

        var keys = AutomationRegistry.All.Select(a => a.Key).ToList();
        Assert.Equal(keys.Count, keys.Distinct(StringComparer.Ordinal).Count());
    }

    /// <summary>Ключ хранится в шаге сценария и переживает выпуски: слаг без пробелов.</summary>
    [Fact]
    public void Keys_look_like_slugs()
    {
        foreach (var automation in AutomationRegistry.All)
        {
            Assert.False(string.IsNullOrWhiteSpace(automation.Key));
            Assert.DoesNotContain(' ', automation.Key);
            Assert.Equal(automation.Key.Trim(), automation.Key);
        }
    }

    [Fact]
    public void Every_automation_says_what_it_does()
    {
        foreach (var automation in AutomationRegistry.All)
        {
            Assert.False(string.IsNullOrWhiteSpace(automation.Name),
                         $"Автоматизация {automation.Key} без имени — в списке шагов ее не выбрать");

            Assert.False(string.IsNullOrWhiteSpace(automation.What),
                         $"Автоматизация {automation.Key} без описания — человеку не понять, что она делает");
        }
    }

    [Fact]
    public void Every_automation_is_found_by_its_key()
    {
        foreach (var automation in AutomationRegistry.All)
            Assert.Same(automation, AutomationRegistry.FindByKey(automation.Key));
    }

    [Fact]
    public void Unknown_key_is_not_found()
    {
        Assert.Null(AutomationRegistry.FindByKey("нет-такой-автоматизации"));
    }

    /// <summary>
    /// Главное обещание шага: автоматизацию не надо нигде перечислять. Класс лежит в папке —
    /// значит, он уже в действиях расширения, и ставить его шагом можно сразу.
    /// </summary>
    [Fact]
    public void Every_automation_is_offered_as_a_step()
    {
        var extension = new AutomationsExtension();

        foreach (var automation in AutomationRegistry.All)
        {
            var action = extension.Actions.FirstOrDefault(a => a.Key == automation.Key);

            Assert.True(action is not null,
                        $"Автоматизация «{automation.Name}» не попала в список шагов сценария");

            Assert.Equal(ExtensionActionKind.Rows, action!.Kind);
            Assert.Equal(automation.Name, action.Name);
            Assert.False(action.MakesFile);
        }
    }

    /// <summary>Заготовка не собирается: ее расширение — .cs.txt, и в реестре ее быть не может.</summary>
    [Fact]
    public void The_template_is_not_an_automation()
    {
        Assert.DoesNotContain(AutomationRegistry.All, a => a.Key == "моя-автоматизация");
    }
}
