using SmartBase.Database;
using SmartBase.Database.Repositories;

namespace SmartBase.Tests.Benchmarks;

/// <summary>Команды модуля и независимая сверка после завершения всех участников.</summary>
internal interface IMultiUserScenario
{
    void Execute(AppDbContext context, ConstructorRepository repo, MultiUserClient client, int round);
    IEnumerable<MultiUserCheck> Verify(MultiUserClient[] clients);
}
