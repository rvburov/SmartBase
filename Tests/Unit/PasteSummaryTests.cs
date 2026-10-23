using SmartBase.Core.Models;
using SmartBase.Database.Repositories;
using SmartBase.Views.ConstructorDb;

namespace SmartBase.Tests.Unit;

/// <summary>Итог вставки называет причину каждого пропуска: отказ самой связи (несколько записей в
/// связь на одну, записи нет в связанной коллекции, база цели недоступна) — не «неоднозначное
/// название».</summary>
public sealed class PasteSummaryTests
{
    private static readonly Dictionary<PasteSkipReason, int> NoSkips = [];

    [Fact]
    public void Nothing_to_say_without_changes_and_skips() =>
        Assert.Null(TableView.PasteSummary(default, NoSkips));

    [Fact]
    public void Each_link_refusal_has_its_own_reason()
    {
        var outcome = new TableView.WriteOutcome(Changed: 5, RelationNotFound: 0, RelationAmbiguous: 1,
            RelationSeveralForSingle: 4, RelationTargetsMissing: 3, RelationTargetUnavailable: 2,
            RelationSelfLinks: 6);

        Assert.Equal("Вставлено 5 значений, пропущено 16: 6 связей записи на саму себя, "
                     + "4 связей с несколькими записями там, где связь допускает одну, "
                     + "3 связей на записи, которых нет в связанной коллекции, "
                     + "2 связей с записями недоступной базы, 1 связей с неоднозначным названием.",
                     TableView.PasteSummary(outcome, NoSkips));
    }

    [Fact]
    public void Link_conflicts_are_not_counted_as_ambiguous_titles()
    {
        var links = new ConstructorRepository.LinkBulkResult(3, 6, [], ConditionRefused: 1, ConditionUnchecked: 2,
            ConditionProblem: "Условие сломано.", SeveralForSingle: 2, TargetsMissing: 3, TargetUnavailable: 4, SelfLinks: 1);

        // Несколько записей в связь на одну — одна причина, текстом их вставили или из своего буфера.
        var outcome = TableView.OutcomeOf(7, new TableView.TextSkips(NotFound: 1, Ambiguous: 5, SeveralForSingle: 8), links);

        Assert.Equal(new TableView.WriteOutcome(7, 1, 5, 1, 2, "Условие сломано.", RelationSeveralForSingle: 10,
            RelationTargetsMissing: 3, RelationTargetUnavailable: 4, RelationSelfLinks: 1), outcome);
    }

    /// <summary>Текст, который ничего не нашел или нашел не то, связей не трогает: пустой набор id у
    /// пропуска не превращается в «снять связи».</summary>
    [Fact]
    public void Text_that_is_skipped_writes_no_links()
    {
        var found = new Dictionary<int, Dictionary<string, ConstructorRepository.TitleResolution>>
        {
            [7] = new()
            {
                ["П1, П2"] = new(ConstructorRepository.TitleMatch.SeveralForSingle, []),
                ["Дубль"] = new(ConstructorRepository.TitleMatch.Ambiguous, []),
                ["Нет"] = new(ConstructorRepository.TitleMatch.NotFound, []),
                [""] = new(ConstructorRepository.TitleMatch.Found, []),
                ["П1"] = new(ConstructorRepository.TitleMatch.Found, [11]),
            },
        };

        var (writes, skips) = TableView.LinkWritesOf(
            [(1, 7, "П1, П2", null), (2, 7, "Дубль", null), (3, 7, "Нет", null), (4, 7, "", null), (5, 7, "П1", null)], found);

        Assert.Equal(new TableView.TextSkips(NotFound: 1, Ambiguous: 1, SeveralForSingle: 1), skips);
        Assert.Equal([4, 5], writes.Select(w => w.RecordId));
        Assert.Equal([11], writes.Single(w => w.RecordId == 5).TargetIds);
    }

    [Fact]
    public void Refused_links_alone_are_still_reported()
    {
        var outcome = new TableView.WriteOutcome(0, 0, 0, RelationTargetsMissing: 2);

        Assert.Equal("Вставлять было нечего, пропущено 2: 2 связей на записи, которых нет в связанной коллекции.",
                     TableView.PasteSummary(outcome, new Dictionary<PasteSkipReason, int> { [PasteSkipReason.WrongFormat] = 0 }));
    }
}
