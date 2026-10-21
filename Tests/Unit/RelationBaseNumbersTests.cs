using SmartBase.Database;
using SmartBase.Database.Repositories;

namespace SmartBase.Tests.Unit;

/// <summary>Номера баз для межбазовых связей: отделение от номеров реестра и разбор пар,
/// сохраненных в app_settings.</summary>
public sealed class RelationBaseNumbersTests
{
    [Theory]
    [InlineData(0, false)]
    [InlineData(7, false)]
    [InlineData(999_999, false)]
    [InlineData(1_000_000, true)]
    [InlineData(int.MaxValue, true)]
    public void Registry_numbers_never_pass_for_link_numbers(int id, bool link)
    {
        Assert.Equal(link, RelationDbResolver.IsLinkId(id));
        Assert.Equal(id > 0 && !link, ConstructorRepository.IsLegacyRelationDb(id));
    }

    [Fact]
    public void Known_bases_skip_damaged_pairs()
    {
        var parsed = RelationDbResolver.ParseKnownBases("3:1000001;x:5;7:12;;0:1000002;9:2000000000;4:-1").ToList();
        Assert.Equal([(3, 1_000_001), (9, 2_000_000_000)], parsed);
        Assert.Empty(RelationDbResolver.ParseKnownBases(null));
    }
}
