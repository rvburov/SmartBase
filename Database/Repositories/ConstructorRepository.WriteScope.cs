using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace SmartBase.Database.Repositories;

public partial class ConstructorRepository
{
    /// <summary>
    /// Составная операция репозитория: проверка свежих данных и все записи выполняются вместе.
    /// Serializable не дает двум клиентам подтвердить правку одного снимка поверх друг друга.
    /// Исключение откатывает и данные, и журнал изменений.
    /// </summary>
    public T InWriteTransaction<T>(Func<T> operation)
    {
        using var scope = new WriteScope(db, System.Data.IsolationLevel.Serializable);
        var result = operation();
        scope.Commit();
        return result;
    }

    /// <summary>
    /// Открывает транзакцию записи — но только если своей еще нет. Нужна там, где составная
    /// операция собрана из шагов, каждый из которых умеет коммитить сам.
    ///
    /// Зачем. Создание ОДНОЙ задачи шло 9–11 независимыми автокоммитами (а на первой задаче
    /// в коллекции — до 15–20): EnsureTaskCollection, EnsureRoles, AddField, AddRecord или
    /// AddCalendarRecord, три SetFieldValue, и каждый SetFieldValue внутри себя писал еще
    /// 3–4 раза (значение, аудит db_records, calendar_index). Каждый коммит — свой цикл
    /// захвата и снятия файловой блокировки; на сетевой папке (журнал DELETE) писатель на
    /// коммите берет PENDING+EXCLUSIVE и НЕ пускает новых читателей. То есть один клик по
    /// «добавить задачу» открывал полтора десятка окон, в любое из которых чужое чтение
    /// попадало с «database is locked» — и наоборот. Плюс задача была неатомарной: обрыв
    /// посередине оставлял запись без исполнителя или коллекцию наполовину размеченной.
    ///
    /// Почему нельзя просто обернуть все в BeginTransaction. SQLite вложенных транзакций не
    /// поддерживает, а Microsoft.Data.Sqlite на второй BeginTransaction кидает
    /// InvalidOperationException. Шаги при этом вызываются и по отдельности (тумблер роли в
    /// диалоге свойства, правка одной ячейки), где транзакция нужна своя. Поэтому scope
    /// смотрит на <c>Database.CurrentTransaction</c>: своей нет — начинает и отвечает за
    /// коммит/откат; чужая есть — не делает ничего и оставляет решение владельцу.
    /// </summary>
    private WriteScope BeginWrite() => new(db);

    private sealed class WriteScope : IDisposable
    {
        private readonly AppDbContext          _ctx;
        private readonly IDbContextTransaction? _own;   // null — транзакцию держит внешний вызов
        private bool _committed;

        public WriteScope(AppDbContext ctx, System.Data.IsolationLevel? isolation = null)
        {
            _ctx = ctx;
            _own = ctx.Database.CurrentTransaction is not null ? null
                : isolation is { } level ? ctx.Database.BeginTransaction(level)
                : ctx.Database.BeginTransaction();
        }

        public void Commit()
        {
            _committed = true;
            _own?.Commit();
        }

        public void Dispose()
        {
            if (_own is null) return;   // не наша транзакция — не наш и откат

            if (!_committed)
            {
                // Откат вернул строки в БД, но трекер держит правки примененными: следующее
                // чтение отдало бы, например, «размеченную» роль, которой в базе нет.
                // Чистим — перечитается. Сам Rollback может упасть, если соединение уже
                // умерло; исходное исключение важнее, поэтому глушим.
                try { _own.Rollback(); } catch { /* исходная ошибка уже летит наверх */ }
                _ctx.ChangeTracker.Clear();
            }
            _own.Dispose();
        }
    }
}
