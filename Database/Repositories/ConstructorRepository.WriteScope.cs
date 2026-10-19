using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace SmartBase.Database.Repositories;

public partial class ConstructorRepository
{
    /// <summary>
    /// Составная операция репозитория: проверка свежих данных и все записи выполняются вместе.
    /// Serializable не дает двум клиентам подтвердить правку одного снимка поверх друг друга.
    /// Исключение откатывает и данные, и журнал изменений.
    ///
    /// На PostgreSQL эта защита срабатывает отказом: сосед тронул прочитанное, и сервер
    /// отвечает 40001 (сериализация) или 40P01 (взаимная блокировка) — на любой команде или на
    /// самом коммите. Тогда операция повторяется целиком, на свежих данных: ее же проверки и
    /// решают, писать ли теперь. Повторить можно только свою транзакцию — внешнюю повторяет
    /// ее владелец.
    /// </summary>
    public T InWriteTransaction<T>(Func<T> operation)
    {
        var own = db.Database.CurrentTransaction is null;

        for (var attempt = 1; ; attempt++)
        {
            var started = DateTime.UtcNow;
            try
            {
                using var scope = new WriteScope(db, System.Data.IsolationLevel.Serializable);
                var result = operation();
                scope.Commit();
                return result;
            }
            catch (Exception error) when (own && attempt < WriteConflictAttempts && IsWriteConflict(error))
            {
                // Базу откат уже вернул, трекер очистил WriteScope. Слияния журнала, заведенные
                // в этой попытке, указывают на откаченные строки — забываем их.
                foreach (var key in _editMerges.Where(m => m.Value.LastAt >= started).Select(m => m.Key).ToList())
                    _editMerges.Remove(key);
            }
        }
    }

    /// <summary>Сколько раз пробовать составную операцию, пока соседи перебивают ее снимок.</summary>
    private const int WriteConflictAttempts = 3;

    /// <summary>Отказ сериализации или взаимная блокировка PostgreSQL, на любой глубине вложения.</summary>
    private static bool IsWriteConflict(Exception error) =>
        error is Npgsql.PostgresException { SqlState: "40001" or "40P01" }
        || (error.InnerException is { } inner && IsWriteConflict(inner));

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
            // Флаг — только после коммита: упавший COMMIT (PostgreSQL отдает отказ сериализации
            // и здесь) должен откатиться и очистить трекер, как любой другой сбой.
            _own?.Commit();
            _committed = true;
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
