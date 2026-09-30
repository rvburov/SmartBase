using SmartBase.Views.Library;

namespace SmartBase.Tests.Unit;

/// <summary>
/// Повторы загрузки упавшей страницы ленты (<c>Views/Library/DocPageRetry.cs</c>). Страница не
/// должна ни остаться «Загрузка…» навсегда, ни долбить недоступную базу без конца: несколько
/// попыток с растущей паузой, потом — снятие с учета.
/// </summary>
public sealed class DocPageRetryTests
{
    [Fact]
    public void Retries_come_with_a_growing_pause_and_then_stop()
    {
        var retry = new DocPageRetry();

        Assert.Equal(TimeSpan.FromSeconds(1), retry.Fail(7));
        Assert.Equal(TimeSpan.FromSeconds(3), retry.Fail(7));
        Assert.Equal(TimeSpan.FromSeconds(9), retry.Fail(7));
        Assert.Null(retry.Fail(7));
    }

    /// <summary>После снятия с учета страница, запрошенная заново, получает свежий счет.</summary>
    [Fact]
    public void After_giving_up_the_page_starts_over()
    {
        var retry = new DocPageRetry();
        for (var i = 0; i <= DocPageRetry.MaxRetries; i++) retry.Fail(7);

        Assert.Equal(TimeSpan.FromSeconds(1), retry.Fail(7));
    }

    [Fact]
    public void Success_forgets_the_failures()
    {
        var retry = new DocPageRetry();
        retry.Fail(7);
        retry.Fail(7);

        retry.Done(7);

        Assert.Equal(TimeSpan.FromSeconds(1), retry.Fail(7));
    }

    [Fact]
    public void Pages_are_counted_apart()
    {
        var retry = new DocPageRetry();
        retry.Fail(1);
        retry.Fail(1);

        Assert.Equal(TimeSpan.FromSeconds(1), retry.Fail(2));
        Assert.Equal(TimeSpan.FromSeconds(9), retry.Fail(1));
    }
}
