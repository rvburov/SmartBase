using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using SmartBase.Database.Entities;

namespace SmartBase.Database.Repositories;

/// <summary>
/// Постоянный номер базы для межбазовых связей. Лежит в самой базе (db_meta), поэтому одинаков
/// на всех компьютерах, у SQLite-файла и у базы PostgreSQL, и переживает удаление базы из списка
/// и повторное подключение. Номер реестра app.db для этого не годится: это счетчик одной
/// установки, у коллеги тот же номер означает другую базу.
/// </summary>
public partial class ConstructorRepository
{
    internal const string RelationBaseKey = "relation.base";

    // Номер читается часто (каждая ячейка-связь сверяет «своя ли это база»), а меняется только
    // при заведении. Ключ — адрес базы: фоновые подключения той же базы видят тот же номер.
    private static readonly ConcurrentDictionary<string, int> RelationBaseIds = new(StringComparer.Ordinal);

    // У базы без номера (подключена только на чтение) пустое чтение помнится недолго: номер ищут
    // списки подключенных баз на UI-потоке при каждой загрузке страницы, и без памяти каждый поиск
    // был бы запросом к db_meta. Номер, заведенный за это время другим компьютером, виден через
    // MissedBaseIdTtl, заведенный здесь (EnsureRelationBaseId) — сразу.
    private static readonly ConcurrentDictionary<string, long> RelationBaseMisses = new(StringComparer.Ordinal);
    internal static long MissedBaseIdTtl = 30_000;

    /// <summary>Номер базы для связей; 0 — номера нет (база еще не подключалась на запись).</summary>
    public int RelationBaseId
    {
        get
        {
            if (RelationBaseIds.TryGetValue(DbPath, out var cached)) return cached;
            if (RelationBaseMisses.TryGetValue(DbPath, out var until) && Environment.TickCount64 < until) return 0;
            var stored = ReadRelationBaseId(db);
            if (stored > 0) RelationBaseIds[DbPath] = stored;
            else RelationBaseMisses[DbPath] = Environment.TickCount64 + MissedBaseIdTtl;
            return stored;
        }
    }

    /// <summary>При подключении: прочитать номер базы или завести новый. Номер, занятый на этом
    /// компьютере другой базой (<paramref name="taken"/>), новой базе не достается. Без права
    /// записи номер не заводится — такая база не может быть целью новой межбазовой связи.</summary>
    public int EnsureRelationBaseId(IReadOnlyCollection<int>? taken = null)
    {
        var stored = ReadRelationBaseId(db);
        if (stored == 0)
        {
            int candidate;
            do candidate = RandomNumberGenerator.GetInt32(RelationDbResolver.MinLinkId, int.MaxValue);
            while (taken?.Contains(candidate) == true);
            var row = new DbMetaEntity { Key = RelationBaseKey, Value = candidate.ToString(CultureInfo.InvariantCulture) };
            try
            {
                db.DbMeta.Add(row);
                db.SaveChanges();
                stored = candidate;
            }
            catch (DbUpdateException)
            {
                // Тот же файл в эту минуту подключил другой компьютер и успел первым: берем его номер.
                db.Entry(row).State = EntityState.Detached;
                stored = ReadRelationBaseId(db);
            }
        }
        if (stored > 0) RelationBaseIds[DbPath] = stored;
        return stored;
    }

    private static int ReadRelationBaseId(AppDbContext context)
    {
        var raw = context.DbMeta.AsNoTracking().Where(m => m.Key == RelationBaseKey).Select(m => m.Value).FirstOrDefault();
        return int.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out var id)
               && RelationDbResolver.IsLinkId(id) ? id : 0;
    }

    /// <summary>Забыть номер, запомненный по адресу (файл базы подменили или база отключена).</summary>
    internal static void ForgetRelationBaseId(string dbPath)
    {
        RelationBaseIds.TryRemove(dbPath, out _);
        RelationBaseMisses.TryRemove(dbPath, out _);
    }
}
