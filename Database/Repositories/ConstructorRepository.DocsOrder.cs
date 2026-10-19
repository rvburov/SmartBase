using Microsoft.EntityFrameworkCore;
using SmartBase.Core.Documents;
using SmartBase.Core.Models;

namespace SmartBase.Database.Repositories;

public partial class ConstructorRepository
{
    public List<DocOrderItem> GetDocOrderKeys(int collectionId, int typeFieldId, DocView view, DocSort sort, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var (where, types) = TypeFilterSql(view);
        var result = new List<DocOrderItem>();
        var paths = sort == DocSort.Modified ? new List<string>() : null;
        var connection = db.Database.GetDbConnection();
        var opened = connection.State != System.Data.ConnectionState.Open;
        if (opened) connection.Open();
        try
        {
            using var command = connection.CreateCommand();
            command.CommandText = $"SELECT r.Id, f.Id, r.Title, v.Value {DocBody} AND ({where})";
            AddParam(command, "@c").Value = collectionId;
            AddParam(command, "@type").Value = typeFieldId > 0 ? typeFieldId : -1;
            for (var i = 0; i < types.Count; i++) AddParam(command, "@t" + i).Value = types[i];
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                ct.ThrowIfCancellationRequested();
                var path = reader.GetString(3);
                result.Add(new DocOrderItem(reader.GetInt32(0), reader.GetInt32(1),
                    DocOrderPlan.Title(reader.IsDBNull(2) ? "" : reader.GetString(2), path),
                    0));
                paths?.Add(path);
            }
        }
        finally { if (opened) connection.Close(); }
        ct.ThrowIfCancellationRequested();
        if (paths is not null)
        {
            // Сначала отпускаем читатель БД, затем спрашиваем диск папками, как обычная лента.
            var stamps = DocStamps.Of(paths, ct);
            for (var i = 0; i < result.Count; i++) result[i] = result[i] with { Modified = stamps[paths[i]].Mtime };
        }
        return result;
    }

    public Task<List<DocOrderItem>> GetDocOrderKeysAsync(int collectionId, int typeFieldId, DocView view, DocSort sort, CancellationToken ct = default) =>
        Task.Run(() =>
        {
            using var context = new AppDbContext(db.DbPath);
            context.ApplyPerformancePragmas();
            return new ConstructorRepository(context).GetDocOrderKeys(collectionId, typeFieldId, view, sort, ct);
        }, ct);

    public List<DocRecord> GetDocsByOrderKeys(int collectionId, int typeFieldId, IReadOnlyList<DocOrderKey> keys, DocView? view = null)
    {
        if (keys.Count == 0) return [];
        var (where, types) = TypeFilterSql(view ?? DocView.All);
        if (keys.Count > 200) throw new ArgumentOutOfRangeException(nameof(keys));
        var connection = db.Database.GetDbConnection();
        var opened = connection.State != System.Data.ConnectionState.Open;
        if (opened) connection.Open();
        var result = new List<DocRecord>();
        try
        {
            using var command = connection.CreateCommand();
            var ids = string.Join(",", keys.Select((_, i) => "@r" + i));
            var pairs = string.Join(",", keys.Select((_, i) => $"(@r{i}, @f{i})"));
            command.CommandText = $"SELECT {DocCols}, COALESCE({DocTypeValueExpr}, ''), r.Position, f.Position " +
                                  $"{DocBody} AND ({where}) AND r.Id IN ({ids}) AND (r.Id, f.Id) IN ({pairs})";
            AddParam(command, "@c").Value = collectionId;
            AddParam(command, "@type").Value = typeFieldId > 0 ? typeFieldId : -1;
            for (var i = 0; i < types.Count; i++) AddParam(command, "@t" + i).Value = types[i];
            for (var i = 0; i < keys.Count; i++)
            {
                AddParam(command, "@r" + i).Value = keys[i].RecordId;
                AddParam(command, "@f" + i).Value = keys[i].FieldId;
            }
            using var reader = command.ExecuteReader();
            while (reader.Read()) result.Add(ReadDocPageRow(reader));
        }
        finally { if (opened) connection.Close(); }
        StampCollection(result, collectionId);
        return result;
    }

    public Task<List<DocRecord>> GetDocsByOrderKeysAsync(int collectionId, int typeFieldId, IReadOnlyList<DocOrderKey> keys, DocView? view = null) =>
        Task.Run(() =>
        {
            using var context = new AppDbContext(db.DbPath);
            context.ApplyPerformancePragmas();
            return new ConstructorRepository(context).GetDocsByOrderKeys(collectionId, typeFieldId, keys, view);
        });
}
