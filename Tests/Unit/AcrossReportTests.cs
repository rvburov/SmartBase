using SmartBase.Core.Models;
using SmartBase.Views.Profile;

namespace SmartBase.Tests.Unit;

/// <summary>
/// Итог массовой правки области «все базы данных» (docs/ROADMAP-API.md, шаг 15). Показывается
/// окном, когда какая-то база правку не приняла.
///
/// Проверяется не форматирование, а обещания, которые фраза дает человеку: базы названы по имени,
/// а «не записалось» и «пропущено» не сваливаются в одно.
/// </summary>
public sealed class AcrossReportTests
{
    [Fact]
    public void The_outcome_names_the_right_and_the_decision()
    {
        var allowed = AcrossReport.Outcome(PermAction.DeleteRecord, true, ["Продажи"], [], []);
        var denied  = AcrossReport.Outcome(PermAction.DeleteRecord, false, ["Продажи"], [], []);

        Assert.Contains(PermActionLabels.Title(PermAction.DeleteRecord), allowed);
        Assert.Contains("разрешено", allowed);
        Assert.Contains("запрещено", denied);
    }

    /// <summary>
    /// Поломка и норма названы порознь: «не записалось» требует действия немедленно, «пропущено»
    /// — обычное дело (администратору право не нужно, ожидающему допуска его некуда класть).
    /// </summary>
    [Fact]
    public void A_failure_is_not_mixed_with_a_skip()
    {
        var text = AcrossReport.Outcome(PermAction.EditRecord, true,
                                        ["Продажи"],
                                        ["Склад — вас понизили"],
                                        ["Кадры — администратор"]);

        Assert.Contains("Не записалось: Склад — вас понизили", text);
        Assert.Contains("Пропущено: Кадры — администратор", text);
    }

    /// <summary>Ни одна база не приняла правку — это сказано, а не показано пустым списком.</summary>
    [Fact]
    public void Touching_nothing_is_stated()
    {
        var text = AcrossReport.Outcome(PermAction.EditRecord, true, [], [], ["Кадры — ждет допуска"]);

        Assert.Contains("ни одной базы не затронуто", text);
        Assert.Contains("Кадры — ждет допуска", text);
    }
}
