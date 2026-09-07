using SmartBase.Setup;

namespace SmartBase.Tests.Unit;

/// <summary>
/// Текст после неудавшейся самозамены (<see cref="InstallService.UpdateFailedMessage"/>) называет
/// причину: старый процесс не завершился (зависшая сетевая папка — искать в Диспетчере задач),
/// файл занят при живом ожидании, и общий текст для флага без причины от старого апдейтера.
/// </summary>
public sealed class UpdateFailedMessageTests
{
    [Fact]
    public void Old_process_reason_points_to_task_manager_and_reboot()
    {
        var text = InstallService.UpdateFailedMessage(InstallService.UpdateFailedOldProcess);
        Assert.Contains("не завершился", text);
        Assert.Contains("Диспетчере задач", text);
        Assert.Contains("перезагрузите", text);
        Assert.Contains("прежней версии", text);
    }

    [Fact]
    public void File_locked_reason_names_other_holders()
    {
        var text = InstallService.UpdateFailedMessage(InstallService.UpdateFailedFileLocked);
        Assert.Contains("занят другим процессом", text);
        Assert.Contains("антивирус", text);
        Assert.DoesNotContain("Диспетчере задач", text);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("unknown")]
    [InlineData("")]
    public void Unknown_reason_falls_back_to_generic_text(string? reason)
    {
        var text = InstallService.UpdateFailedMessage(reason);
        Assert.Contains("файл был занят другим процессом", text);
        Assert.Contains("попробуйте обновиться еще раз", text);
    }
}
