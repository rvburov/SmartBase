using SmartBase.Core.Models;

namespace SmartBase.Tests.Unit;

/// <summary>
/// Битовые маски прав (<see cref="PermAction"/>), их подписи и исключение отказа.
/// </summary>
public sealed class PermissionsTests
{
    [Fact]
    public void None_is_zero()
    {
        Assert.Equal(0, (int)PermAction.None);
    }

    [Fact]
    public void All_contains_every_individual_action()
    {
        foreach (PermAction a in Enum.GetValues<PermAction>())
        {
            if (a is PermAction.None or PermAction.All) continue;
            Assert.True(PermAction.All.HasFlag(a), $"All должна включать {a}");
        }
    }

    [Fact]
    public void All_is_exact_union_of_flags()
    {
        var union = PermAction.None;
        foreach (PermAction a in Enum.GetValues<PermAction>())
            if (a is not (PermAction.None or PermAction.All))
                union |= a;

        Assert.Equal(PermAction.All, union);
    }

    [Fact]
    public void Bitwise_masking_isolates_single_action()
    {
        var mask = PermAction.EditRecord | PermAction.DeleteRecord;
        Assert.True(mask.HasFlag(PermAction.EditRecord));
        Assert.False(mask.HasFlag(PermAction.CreateCollection));
    }

    [Fact]
    public void Labels_are_defined_for_all_actions()
    {
        foreach (PermAction a in Enum.GetValues<PermAction>())
        {
            if (a is PermAction.None or PermAction.All) continue;
            // Fallback-ветка возвращает a.ToString(); подпись должна быть человеческой, не именем enum.
            Assert.NotEqual(a.ToString(), PermActionLabels.Label(a));
        }
    }

    [Fact]
    public void Denied_message_and_exception_carry_action()
    {
        var ex = new PermissionDeniedException(PermAction.DeleteRecord);
        Assert.Equal(PermAction.DeleteRecord, ex.Action);
        Assert.Contains(PermActionLabels.DeniedMessage(PermAction.DeleteRecord), ex.Message);
    }
}
