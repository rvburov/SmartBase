using Serilog;
using SmartBase.Core.Models;
using SmartBase.Database.Repositories;
using SmartBase.Utils;

namespace SmartBase.Database;

/// <summary>
/// Блок «Ссылаются на эту запись» страницы записи: чтение идет в фоне на своих подключениях, и
/// открытие записи не ждет подсчета, сколько бы ссылок ни было. Базы снимаются на UI-потоке
/// (<see cref="Capture"/>): база записи и подключенные другие базы, куда у пользователя есть
/// допуск. Неподключенные базы не читаются, их ссылки не показываются — как в окне удаления.
///
/// Другая база ссылается номером базы записи, а номер ведет в основную копию файла
/// (<see cref="RelationDbResolver.MountedRepoFor"/>): у второй подключенной копии с тем же номером
/// ссылок из других баз нет.
/// </summary>
public sealed class RelationBacklinkReader
{
    private readonly RelationBaseRef _own;
    private readonly IReadOnlyList<RelationBaseRef> _others;

    private RelationBacklinkReader(RelationBaseRef own, IReadOnlyList<RelationBaseRef> others, int recordId, int collectionId)
    {
        _own = own;
        _others = others;
        RecordId = recordId;
        CollectionId = collectionId;
    }

    public int RecordId { get; }
    public int CollectionId { get; }

    /// <summary>Читает ли снимок базу по этому пути (своя или другая подключенная).</summary>
    public bool Reads(string dbPath) => _own.Path == dbPath || _others.Any(b => b.Path == dbPath);

    /// <summary>Снимок баз для записи <paramref name="recordId"/> базы <paramref name="owner"/>. Только
    /// с UI-потока: читает общие репозитории.</summary>
    public static RelationBacklinkReader Capture(ConstructorRepository owner, int recordId, int collectionId)
    {
        var own = RelationBaseRef.Of(owner);
        var others = own.LinkId > 0 && RelationDbResolver.MountedRepoFor(own.LinkId)?.DbPath == owner.DbPath
            ? RelationBaseRef.Mounted().Where(b => b.Path != own.Path && b.Permissions?.AccessGranted != false).ToList()
            : [];
        return new(own, others, recordId, collectionId);
    }

    /// <summary>Разделы блока: своя база первой, затем другие базы, где ссылки есть или могли бы
    /// быть (связь на коллекцию записи). База, которую не удалось прочитать, — раздел с
    /// <see cref="RelationBacklinkSection.Failed"/>.</summary>
    public IReadOnlyList<RelationBacklinkSection> Load(CancellationToken ct)
    {
        using var slow = Logging.Diag.Measure("обратные ссылки страницы записи", Logging.Diag.Background,
            $"{DbErrors.Display(_own.Path)}, запись {RecordId}");
        var sections = new List<RelationBacklinkSection>();
        // Своя база занята или недоступна — ее раздел говорит об этом, другие базы читаются дальше.
        sections.Add(Read(_own, 0));
        foreach (var other in _others)
        {
            ct.ThrowIfCancellationRequested();
            var section = Read(other, other.LinkId);
            if (section.Groups.Count > 0 || section.Failed) sections.Add(section);
        }
        return sections;

        RelationBacklinkSection Read(RelationBaseRef b, int sourceDbId)
        {
            var name = sourceDbId == 0 ? null : b.Name;
            try
            {
                using var opened = b.Open();
                return new(sourceDbId, name, opened.Repo.RelationBacklinkGroups(sourceDbId, RecordId, CollectionId, _own.LinkId, ct));
            }
            catch (Exception ex) when (ex is System.Data.Common.DbException or InvalidOperationException or IOException)
            {
                Log.Warning(ex, "Обратные ссылки из базы {Db:l} не прочитаны", DbErrors.Display(b.Path));
                return new(sourceDbId, name, [], Failed: true);
            }
        }
    }

    /// <summary>Страница источников карточки <paramref name="group"/> (см.
    /// <see cref="ConstructorRepository.RelationBacklinkSources"/>).</summary>
    public RelationBacklinkPage Sources(RelationBacklinkGroup group, string? query, int? before, int take, CancellationToken ct)
    {
        using var opened = BaseOf(group).Open();
        return opened.Repo.RelationBacklinkSources(group, RecordId, query, before, take, ct);
    }

    /// <summary>Точное число источников карточки.</summary>
    public int Total(RelationBacklinkGroup group, CancellationToken ct)
    {
        using var opened = BaseOf(group).Open();
        return opened.Repo.RelationBacklinkTotal(group, RecordId, ct);
    }

    private RelationBaseRef BaseOf(RelationBacklinkGroup group) => group.SourceDbId == 0 ? _own
        : _others.FirstOrDefault(b => b.LinkId == group.SourceDbId)
          ?? throw new InvalidOperationException("База-источник ссылок больше не подключена.");
}
