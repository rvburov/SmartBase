using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace SmartBase.Database;

/// <summary>
/// Границы транзакций для <see cref="ChangeBus"/>. Внутри транзакции <c>SaveChanges</c>
/// откладывает свои изменения (<see cref="AppDbContext.NoteSaved"/>): до коммита их не видит
/// ни соседнее подключение, ни обновление экрана на нем — а после отката их нет вовсе.
/// Коммит выпускает отложенное одним событием, откат выбрасывает. Новая транзакция на том же
/// контексте начинает с чистого листа: хвост транзакции, брошенной без <c>Commit</c>/
/// <c>Rollback</c> (просто <c>Dispose</c>), сюда не доезжает — EF откатывает ее молча.
///
/// Перехватчик, а не правка мест коммита: своих транзакций у репозитория десятки
/// (<c>WriteScope</c>, массовые операции на изолированных подключениях, расширения), и все
/// они проходят через EF — одной точки достаточно.
/// </summary>
public sealed class ChangeBusInterceptor : DbTransactionInterceptor
{
    public static readonly ChangeBusInterceptor Instance = new();

    private ChangeBusInterceptor() { }

    public override InterceptionResult<DbTransaction> TransactionStarting(DbConnection connection,
        TransactionStartingEventData eventData, InterceptionResult<DbTransaction> result)
    {
        if (!result.HasResult && eventData.Context is AppDbContext context)
            SqliteGate.PrepareWriteTransaction(connection, context.Database.GetCommandTimeout() ?? 30);
        return result;
    }

    public override ValueTask<InterceptionResult<DbTransaction>> TransactionStartingAsync(DbConnection connection,
        TransactionStartingEventData eventData, InterceptionResult<DbTransaction> result,
        CancellationToken cancellationToken = default)
    {
        if (!result.HasResult && eventData.Context is AppDbContext context)
            SqliteGate.PrepareWriteTransaction(connection, context.Database.GetCommandTimeout() ?? 30, cancellationToken);
        return ValueTask.FromResult(result);
    }

    public override DbTransaction TransactionStarted(DbConnection connection, TransactionEndEventData eventData,
                                                     DbTransaction result)
    {
        SqliteGate.ClearPreparedWriteTransaction(connection);
        (eventData.Context as AppDbContext)?.OnTransactionStarted();
        if (eventData.Context is AppDbContext context) Repositories.ConstructorRepository.RelationTransactionStarted(context);
        if (eventData.Context is AppDbContext fileContext) Repositories.ConstructorRepository.FileAutoTransactionStarted(fileContext);
        if (eventData.Context is AppDbContext periodContext) Repositories.ConstructorRepository.DatePeriodTransactionStarted(periodContext);
        return result;
    }

    public override ValueTask<DbTransaction> TransactionStartedAsync(DbConnection connection,
                                                                     TransactionEndEventData eventData,
                                                                     DbTransaction result,
                                                                     CancellationToken cancellationToken = default)
    {
        SqliteGate.ClearPreparedWriteTransaction(connection);
        (eventData.Context as AppDbContext)?.OnTransactionStarted();
        if (eventData.Context is AppDbContext context) Repositories.ConstructorRepository.RelationTransactionStarted(context);
        if (eventData.Context is AppDbContext fileContext) Repositories.ConstructorRepository.FileAutoTransactionStarted(fileContext);
        if (eventData.Context is AppDbContext periodContext) Repositories.ConstructorRepository.DatePeriodTransactionStarted(periodContext);
        return ValueTask.FromResult(result);
    }

    public override InterceptionResult TransactionCommitting(DbTransaction transaction,
        TransactionEventData eventData, InterceptionResult result)
    {
        if (eventData.Context is AppDbContext context) Repositories.ConstructorRepository.DatePeriodTransactionCommitting(context);
        return result;
    }

    public override ValueTask<InterceptionResult> TransactionCommittingAsync(DbTransaction transaction,
        TransactionEventData eventData, InterceptionResult result, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (eventData.Context is AppDbContext context) Repositories.ConstructorRepository.DatePeriodTransactionCommitting(context);
        return ValueTask.FromResult(result);
    }

    public override void TransactionCommitted(DbTransaction transaction, TransactionEndEventData eventData)
    {
        SqliteGate.ReleaseTransactionAdmission(transaction);
        (eventData.Context as AppDbContext)?.OnTransactionCommitted();
        if (eventData.Context is AppDbContext context) Repositories.ConstructorRepository.RelationTransactionCommitted(context);
        if (eventData.Context is AppDbContext fileContext) Repositories.ConstructorRepository.FileAutoTransactionCommitted(fileContext);
        if (eventData.Context is AppDbContext periodContext) Repositories.ConstructorRepository.DatePeriodTransactionEnded(periodContext);
    }

    public override Task TransactionCommittedAsync(DbTransaction transaction, TransactionEndEventData eventData,
                                                   CancellationToken cancellationToken = default)
    {
        SqliteGate.ReleaseTransactionAdmission(transaction);
        (eventData.Context as AppDbContext)?.OnTransactionCommitted();
        if (eventData.Context is AppDbContext context) Repositories.ConstructorRepository.RelationTransactionCommitted(context);
        if (eventData.Context is AppDbContext fileContext) Repositories.ConstructorRepository.FileAutoTransactionCommitted(fileContext);
        if (eventData.Context is AppDbContext periodContext) Repositories.ConstructorRepository.DatePeriodTransactionEnded(periodContext);
        return Task.CompletedTask;
    }

    public override void TransactionRolledBack(DbTransaction transaction, TransactionEndEventData eventData)
    {
        SqliteGate.ReleaseTransactionAdmission(transaction);
        (eventData.Context as AppDbContext)?.OnTransactionRolledBack();
        if (eventData.Context is AppDbContext context) Repositories.ConstructorRepository.RelationTransactionRolledBack(context);
        if (eventData.Context is AppDbContext fileContext) Repositories.ConstructorRepository.FileAutoTransactionStarted(fileContext);
        if (eventData.Context is AppDbContext periodContext) Repositories.ConstructorRepository.DatePeriodTransactionEnded(periodContext);
    }

    public override Task TransactionRolledBackAsync(DbTransaction transaction, TransactionEndEventData eventData,
                                                    CancellationToken cancellationToken = default)
    {
        SqliteGate.ReleaseTransactionAdmission(transaction);
        (eventData.Context as AppDbContext)?.OnTransactionRolledBack();
        if (eventData.Context is AppDbContext context) Repositories.ConstructorRepository.RelationTransactionRolledBack(context);
        if (eventData.Context is AppDbContext fileContext) Repositories.ConstructorRepository.FileAutoTransactionStarted(fileContext);
        if (eventData.Context is AppDbContext periodContext) Repositories.ConstructorRepository.DatePeriodTransactionEnded(periodContext);
        return Task.CompletedTask;
    }

    public override void TransactionFailed(DbTransaction transaction, TransactionErrorEventData eventData) =>
        SqliteGate.ReleaseCompletedTransactionAdmission(transaction);

    public override Task TransactionFailedAsync(DbTransaction transaction, TransactionErrorEventData eventData,
        CancellationToken cancellationToken = default)
    {
        SqliteGate.ReleaseCompletedTransactionAdmission(transaction);
        return Task.CompletedTask;
    }
}
