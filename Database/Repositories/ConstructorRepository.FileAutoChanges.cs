using System.Runtime.CompilerServices;
using Microsoft.EntityFrameworkCore;

namespace SmartBase.Database.Repositories;

/// <summary>Сохраненный запрос: только адрес и id; общего DbContext нет.</summary>
public sealed record FileAutoWork(string DbPath, int FieldId, int? RecordId);

public partial class ConstructorRepository
{
    public static event Action<FileAutoWork>? FileAutoWorkRequested;
    public static event Func<string, bool>? FileAutoWorkSupported;
    private sealed class PendingFileWork
    {
        internal readonly Dictionary<int, Dictionary<int, FileAutoWork>> Fields = [];
    }
    private static readonly ConditionalWeakTable<AppDbContext, PendingFileWork> PendingFiles = new();

    private bool QueueAutoFileWork(int fieldId, int recordId)
    {
        if (FileAutoWorkRequested is null) return false;
        if (FileAutoWorkSupported is { } supported
            && !supported.GetInvocationList().Cast<Func<string, bool>>()
                .Any(accepts => InvokePublicWriteCallback(() => accepts(DbPath)))) return false;
        var work = new FileAutoWork(DbPath, fieldId, recordId);
        if (db.Database.CurrentTransaction is not null)
        {
            var pending = PendingFiles.GetValue(db, _ => new());
            if (!pending.Fields.TryGetValue(fieldId, out var rows)) pending.Fields[fieldId] = rows = [];
            if (rows.ContainsKey(0)) return true;
            if (rows.Count >= 128) { rows.Clear(); rows[0] = work with { RecordId = null }; }
            else rows[recordId] = work;
        }
        else PublishFileWork(work);
        return true;
    }

    private static void PublishFileWork(FileAutoWork work)
    {
        try { FileAutoWorkRequested?.Invoke(work); }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        { Serilog.Log.Warning("Не удалось поставить обновление файлов в очередь; прежние привязки сохранены"); }
    }

    internal static void FileAutoTransactionStarted(AppDbContext context)
    {
        if (PendingFiles.TryGetValue(context, out var pending)) pending.Fields.Clear();
    }

    internal static void FileAutoTransactionCommitted(AppDbContext context)
    {
        SqliteGate.ReleaseConnectionAdmission(context.Database.GetDbConnection());
        if (!PendingFiles.TryGetValue(context, out var pending)) return;
        var work = pending.Fields.Values.SelectMany(rows => rows.Values).ToArray();
        pending.Fields.Clear();
        foreach (var item in work) PublishFileWork(item);
    }

    /// <summary>Вызывается планировщиком в фоне. Все чтения и запись — на своем контексте.</summary>
    public static FileAutoResolveResult ExecuteFileAutoWork(FileAutoWork work, CancellationToken ct = default)
    {
        try
        {
            ct.ThrowIfCancellationRequested();
            using var context = NewIntegrityContext(work.DbPath, writable: true);
            return new ConstructorRepository(context)
                .ResolveAutoFileFieldCore(work.FieldId, null, ct, refresh: false, recordId: work.RecordId);
        }
        catch (OperationCanceledException) { return FileAutoResolveResult.Cancelled; }
        catch (Exception ex) when (ex is System.Data.Common.DbException or IOException or UnauthorizedAccessException)
        { return FileAutoResolveResult.Unavailable; }
    }
}
