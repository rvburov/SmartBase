using System.Data.Common;
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

    public override DbTransaction TransactionStarted(DbConnection connection, TransactionEndEventData eventData,
                                                     DbTransaction result)
    {
        (eventData.Context as AppDbContext)?.OnTransactionStarted();
        if (eventData.Context is AppDbContext context) Repositories.ConstructorRepository.RelationTransactionStarted(context);
        if (eventData.Context is AppDbContext fileContext) Repositories.ConstructorRepository.FileAutoTransactionStarted(fileContext);
        return result;
    }

    public override ValueTask<DbTransaction> TransactionStartedAsync(DbConnection connection,
                                                                     TransactionEndEventData eventData,
                                                                     DbTransaction result,
                                                                     CancellationToken cancellationToken = default)
    {
        (eventData.Context as AppDbContext)?.OnTransactionStarted();
        if (eventData.Context is AppDbContext context) Repositories.ConstructorRepository.RelationTransactionStarted(context);
        if (eventData.Context is AppDbContext fileContext) Repositories.ConstructorRepository.FileAutoTransactionStarted(fileContext);
        return ValueTask.FromResult(result);
    }

    public override void TransactionCommitted(DbTransaction transaction, TransactionEndEventData eventData)
    {
        (eventData.Context as AppDbContext)?.OnTransactionCommitted();
        if (eventData.Context is AppDbContext context) Repositories.ConstructorRepository.RelationTransactionCommitted(context);
        if (eventData.Context is AppDbContext fileContext) Repositories.ConstructorRepository.FileAutoTransactionCommitted(fileContext);
    }

    public override Task TransactionCommittedAsync(DbTransaction transaction, TransactionEndEventData eventData,
                                                   CancellationToken cancellationToken = default)
    {
        (eventData.Context as AppDbContext)?.OnTransactionCommitted();
        if (eventData.Context is AppDbContext context) Repositories.ConstructorRepository.RelationTransactionCommitted(context);
        if (eventData.Context is AppDbContext fileContext) Repositories.ConstructorRepository.FileAutoTransactionCommitted(fileContext);
        return Task.CompletedTask;
    }

    public override void TransactionRolledBack(DbTransaction transaction, TransactionEndEventData eventData)
    {
        (eventData.Context as AppDbContext)?.OnTransactionRolledBack();
        if (eventData.Context is AppDbContext context) Repositories.ConstructorRepository.RelationTransactionRolledBack(context);
        if (eventData.Context is AppDbContext fileContext) Repositories.ConstructorRepository.FileAutoTransactionStarted(fileContext);
    }

    public override Task TransactionRolledBackAsync(DbTransaction transaction, TransactionEndEventData eventData,
                                                    CancellationToken cancellationToken = default)
    {
        (eventData.Context as AppDbContext)?.OnTransactionRolledBack();
        if (eventData.Context is AppDbContext context) Repositories.ConstructorRepository.RelationTransactionRolledBack(context);
        if (eventData.Context is AppDbContext fileContext) Repositories.ConstructorRepository.FileAutoTransactionStarted(fileContext);
        return Task.CompletedTask;
    }
}
