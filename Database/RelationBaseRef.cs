using SmartBase.Database.Repositories;

namespace SmartBase.Database;

/// <summary>Снимок базы для фоновой работы со связями (окно удаления, обратные ссылки страницы
/// записи): постоянный номер, путь и имя. Берется на UI-потоке; общий контекст в фон не
/// уходит — фоновая работа открывает свое подключение (<see cref="Open"/>).</summary>
internal sealed record RelationBaseRef(int LinkId, string Path, string? Name)
{
    /// <summary>Формы базы цели у ребер этой базы на свои записи: 0 и, у старых ребер, свой номер.</summary>
    public int[] Targets => LinkId > 0 ? [0, LinkId] : [0];

    public Opened Open()
    {
        var context = new AppDbContext(Path);
        return new(context, new ConstructorRepository(context));
    }

    public sealed record Opened(AppDbContext Context, ConstructorRepository Repo) : IDisposable
    {
        public void Dispose() => Context.Dispose();
    }

    public static RelationBaseRef Of(ConstructorRepository repo)
    {
        var linkId = RelationDbResolver.DbIdFor(repo);
        return new(linkId, repo.DbPath, linkId > 0 ? RelationDbResolver.DbName(linkId) : null);
    }

    /// <summary>Подключенные базы с номером связи, по одной на номер (копия файла с тем же номером —
    /// не та база, в которую ведут ссылки).</summary>
    public static List<RelationBaseRef> Mounted() => RelationDbResolver.MountedReposExcept(0)
        .Select(p => new RelationBaseRef(p.DbId, p.Repo.DbPath, RelationDbResolver.DbName(p.DbId))).ToList();
}
