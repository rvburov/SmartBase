using SmartBase.Core.Models;
using SmartBase.Views.Profile;

namespace SmartBase.Tests.Unit;

/// <summary>
/// Две фразы области «все базы данных» (docs/ROADMAP-API.md, шаг 15).
///
/// Проверяется не форматирование, а обещания, которые эти фразы дают человеку: область действия
/// массовой правки названа ДО щелчка, базы названы по имени, а «не записалось» и «пропущено»
/// не сваливаются в одно.
/// </summary>
public sealed class AcrossReportTests
{
    // ── Куда уйдут правки ─────────────────────────────────────────────────────

    [Fact]
    public void Every_touched_database_is_named()
    {
        var text = AcrossReport.Where(["Продажи", "Склад"], [], []);

        Assert.Contains("Продажи", text);
        Assert.Contains("Склад", text);
    }

    /// <summary>
    /// Закрытая база названа по имени и с причиной: права лежат в ее файле, и человеку надо
    /// понять, что делать, — открыть базу и повторить.
    /// </summary>
    [Fact]
    public void A_closed_database_is_named_with_its_reason()
    {
        var text = AcrossReport.Where(["Продажи"], [], ["Архив"]);

        Assert.Contains("Архив — не открыта", text);
        Assert.Contains("в закрытый файл их не положить", text);
    }

    /// <summary>«Открыта, но админ не вы» лечится не тем же, чем «не открыта», — и сказано иначе.</summary>
    [Fact]
    public void Not_being_an_admin_is_a_different_reason_than_being_closed()
    {
        var text = AcrossReport.Where(["Продажи"], ["Кадры"], ["Архив"]);

        Assert.Contains("Кадры — вы не администратор", text);
        Assert.Contains("Архив — не открыта", text);
    }

    /// <summary>Нечего править — сказано прямо, а не пустой строкой.</summary>
    [Fact]
    public void Nowhere_to_write_is_said_out_loud()
    {
        var text = AcrossReport.Where([], [], ["Архив"]);

        Assert.Contains("сейчас нет", text);
        Assert.Contains("Архив", text);
    }

    /// <summary>Все базы открыты и все ваши — лишней фразы про «не затронуты» нет.</summary>
    [Fact]
    public void Nothing_left_aside_means_no_aside_sentence()
    {
        var text = AcrossReport.Where(["Продажи", "Склад"], [], []);

        Assert.DoesNotContain("Не затронуты", text);
    }

    // ── Что вышло ─────────────────────────────────────────────────────────────

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
