using SmartBase.Database.DocIndex;

namespace SmartBase.Tests.Unit;

/// <summary>
/// Скорость прогона чтения (<see cref="DocReadPace"/>): разогрев в счет не идет. Первые документы
/// прогона медленнее остальных — прогреваются разборщики и кеш диска, — и средняя от начала
/// прогона завышала и «осталось около …», и скорость, которую раздел запоминает для окна
/// подтверждения. Раньше разогрев только откладывал показ, а в среднюю шел.
/// </summary>
public sealed class DocReadPaceTests
{
    // Разогрев — пять документов по 2 с, дальше по 0,1 с.
    private static DocReadPace Run(int done)
    {
        var pace = new DocReadPace();
        var at   = TimeSpan.Zero;
        for (var i = 1; i <= done; i++)
        {
            at += TimeSpan.FromSeconds(i <= DocReadPace.WarmUp ? 2 : 0.1);
            pace.Done(i, at);
        }
        return pace;
    }

    [Fact]
    public void Warm_up_does_not_count()
    {
        var pace = Run(15);

        Assert.Equal(0.1, pace.SecondsPerDocument!.Value, 6);
        // Осталось сто документов по 0,1 с — 10 с; средняя от начала прогона дала бы 73 с.
        Assert.Equal(10, pace.Left(115)!.Value.TotalSeconds, 6);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(9)]
    public void Too_few_documents_after_the_warm_up_give_no_estimate(int done)
    {
        var pace = Run(done);

        Assert.Null(pace.SecondsPerDocument);
        Assert.Null(pace.Left(100));
    }

    [Fact]
    public void The_estimate_appears_with_the_fifth_document_after_the_warm_up()
    {
        var pace = Run(2 * DocReadPace.WarmUp);

        Assert.Equal(0.1, pace.SecondsPerDocument!.Value, 6);
        Assert.Equal(9, pace.Left(100)!.Value.TotalSeconds, 6);
    }

    [Fact]
    public void Nothing_is_left_when_everything_is_read() =>
        Assert.Null(Run(20).Left(20));
}
