using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using SmartBase.Core.Models;
using SmartBase.Database.Entities;
using SmartBase.Utils;

namespace SmartBase.Database.Repositories;

/// <summary>
/// Права и допуск пользователей одной базы конструктора (экземпляр — на DatabaseManager).
///
/// Модель: администратор — создатель базы (первый открывший, когда админов ещё нет),
/// у него всегда все права. Остальные пользователи регистрируются при монтировании
/// (<see cref="EnsureCurrentUserRegistered"/>) и ждут допуска: его даёт администратор
/// в панели (SetApproved) или верный пароль базы (<see cref="TryUnlock"/>). Права —
/// битовые маски <see cref="PermAction"/> в db_permissions: строка с CollectionId=null
/// действует на всю базу, строка с коллекцией переопределяет её; нет строк — у
/// допущенного всё разрешено (администратор снимает лишнее).
///
/// Проверки (<see cref="Can"/>/<see cref="Demand"/>) идут по кеш-снапшоту прав ТЕКУЩЕГО
/// пользователя — ноль запросов к БД на горячих путях (правка ячеек, пакетные операции).
/// После правок в панели администратора вызывается <see cref="Reload"/>; UI подписывается
/// на <see cref="Changed"/> и перечитывает доступность кнопок. Методы, трогающие БД,
/// вызываются с UI-потока (или при монтировании, пока контекст не разделён) — как и все
/// операции общего контекста; Can/Demand можно звать из фоновых потоков (только кеш).
/// </summary>
public sealed class PermissionService(AppDbContext db)
{
    private readonly AppDbContext _db = db;

    // События пользователей идут в общий журнал change_log (targetType "user"):
    // это транспорт живого обновления между машинами — поллер соседа видит строку
    // и перечитывает права/панель администратора (MainWindow.OnRemoteChanges).
    // Без SaveChanges: строка коммитится вместе с операцией.
    private void LogUser(string action, string targetName, string? detail = null) =>
        _db.ChangeLog.Add(new ChangeLogEntity
        {
            Ts         = DateTime.UtcNow.ToString("o"),
            UserKey    = CurrentUser.Key,
            UserName   = CurrentUser.DisplayName,
            Action     = action,
            TargetType = "user",
            TargetName = targetName,
            Detail     = detail,
        });

    // ── Снапшот текущего пользователя (кеш; обновляет Reload) ────────────────
    public int  CurrentUserId { get; private set; }
    public bool IsAdmin       { get; private set; }
    public bool IsApproved    { get; private set; }
    /// <summary>У базы установлен пароль (хеш в db_meta).</summary>
    public bool HasPassword   { get; private set; }
    /// <summary>Автоочистка журнала включена (политика в db_meta, применяется при монтировании).</summary>
    public bool RetentionEnabled { get; private set; }
    /// <summary>Сколько месяцев журнала хранить при включённой автоочистке.</summary>
    public int  RetentionMonths  { get; private set; } = DefaultRetentionMonths;
    private int _dbMask = (int)PermAction.All;
    private Dictionary<int, int> _collectionMasks = [];

    /// <summary>Права пересчитаны (допуск, маски, пароль) — UI перечитывает кнопки
    /// и списки пользователей (опции полей «пользователь»).</summary>
    public event EventHandler? Changed;

    /// <summary>Текущему пользователю доступно содержимое базы.</summary>
    public bool AccessGranted => IsAdmin || IsApproved;

    // ── Свежие чтения (мимо кеша EF) ──────────────────────────────────────────
    //
    // Строки db_users/db_permissions/db_meta меняют СОСЕДИ (другая машина общей базы,
    // вторая копия приложения): выдали допуск, сменили ФИО, поправили права. Общий
    // контекст живёт весь сеанс, и обычный трекинг-запрос вернул бы уже загруженный
    // экземпляр со СТАРЫМИ значениями (EF не перетирает загруженное данными из БД) —
    // допуск «не доезжал» бы до перезапуска, а панель администратора показывала бы
    // устаревший статус. Поэтому все чтения этой службы — AsNoTracking (свежий снимок),
    // а строку, которую собираемся ПРАВИТЬ, перед правкой перечитываем (Entry.Reload).
    private IQueryable<DbUserEntity>       FreshUsers  => _db.DbUsers.AsNoTracking();
    private IQueryable<DbPermissionEntity> FreshPerms  => _db.DbPermissions.AsNoTracking();

    // Отслеживаемая (правимая) строка с актуальными значениями из БД.
    private T? Tracked<T>(T? entity) where T : class
    {
        if (entity is not null) _db.Entry(entity).Reload();
        return entity;
    }

    // ── Регистрация при монтировании ──────────────────────────────────────────

    /// <summary>
    /// Зарегистрировать/синхронизировать текущего пользователя в db_users. Нет ни одного
    /// администратора (новая база, старая база после обновления, личная database.db) —
    /// текущий пользователь становится админом. Иначе новая строка ждёт допуска
    /// (IsApproved=false), у существующей обновляется снапшот ФИО.
    /// </summary>
    public void EnsureCurrentUserRegistered()
    {
        var key      = CurrentUser.Key;
        var me       = _db.DbUsers.FirstOrDefault(u => u.UserKey == key);
        var hasAdmin = _db.DbUsers.Any(u => u.IsAdmin);
        var now      = DateTime.UtcNow.ToString("o");

        if (me is null)
        {
            me = new DbUserEntity
            {
                UserKey     = key,
                DisplayName = CurrentUser.DisplayName,
                IsAdmin     = !hasAdmin,
                IsApproved  = !hasAdmin,
                CreatedAt   = now,
                LastSeenAt  = now,
            };
            _db.DbUsers.Add(me);
            // Присоединение к чужой базе (админ уже есть) — событие в журнал: у админа
            // «в моменте» появится строка «Ожидает допуска» в панели администратора.
            // Первый пользователь новой базы сам себе не сигналит.
            if (hasAdmin) LogUser("user.join", CurrentUser.DisplayName);
            try { _db.SaveChanges(); }
            catch (Microsoft.EntityFrameworkCore.DbUpdateException ex) when (ex.InnerException
                is Microsoft.Data.Sqlite.SqliteException { SqliteErrorCode: 19 }
                or Npgsql.PostgresException { SqlState: "23505" })
            {
                // Гонка регистрации одного пользователя из двух процессов (уникальный индекс
                // UserKey): сосед успел первым — строку не дублируем, работаем с его версией.
                // Отцепляем ВСЁ несохранённое, включая строку журнала user.join, — иначе она
                // уехала бы в БД с чужим ближайшим SaveChanges.
                foreach (var entry in _db.ChangeTracker.Entries()
                             .Where(en => en.State == Microsoft.EntityFrameworkCore.EntityState.Added)
                             .ToList())
                    entry.State = Microsoft.EntityFrameworkCore.EntityState.Detached;
            }
        }
        else
        {
            if (me.DisplayName != CurrentUser.DisplayName)
            {
                // Имя сменилось между сессиями — сигналим соседям (открытые панели
                // администратора и опции полей «пользователь» обновятся без перезахода).
                LogUser("user.rename", CurrentUser.DisplayName,
                        Utils.ChangeDetail.Rename(me.DisplayName, CurrentUser.DisplayName));
                me.DisplayName = CurrentUser.DisplayName;
            }
            if (!hasAdmin && !me.IsAdmin)
            {
                // База осталась без админа (например, строку удалили вручную) — клеймим.
                me.IsAdmin    = true;
                me.IsApproved = true;
            }
            // Отметка активности: администратор видит в панели, кто и когда открывал базу
            // последний раз. Пишется КАЖДОЕ монтирование (поэтому SaveChanges безусловный),
            // но НЕ идёт в журнал — иначе каждое открытие базы будило бы поллеры соседей
            // строкой без содержания.
            me.LastSeenAt = now;
            _db.SaveChanges();
        }

        Reload(notify: false);
    }

    /// <summary>
    /// Синхронизировать снапшот ФИО текущего пользователя в db_users этой базы —
    /// вызывается при правке профиля (точка фиксации в ProfileView), чтобы имя
    /// разошлось по соседям сразу, а не при следующем монтировании. Имя не менялось —
    /// no-op (ни записи, ни события). UI-поток (общий контекст).
    /// </summary>
    public void SyncDisplayName()
    {
        // Перед правкой перечитываем строку: соседский допуск/права, изменённые за сеанс,
        // иначе уехали бы обратно вместе с UPDATE по устаревшему снапшоту.
        var me = Tracked(_db.DbUsers.FirstOrDefault(u => u.UserKey == CurrentUser.Key));
        if (me is null || me.DisplayName == CurrentUser.DisplayName ||
            _db.Entry(me).State == Microsoft.EntityFrameworkCore.EntityState.Detached) return;
        LogUser("user.rename", CurrentUser.DisplayName,
                Utils.ChangeDetail.Rename(me.DisplayName, CurrentUser.DisplayName));
        me.DisplayName = CurrentUser.DisplayName;
        _db.SaveChanges();
    }

    // ── Проверки прав (по кешу, можно из фоновых потоков) ─────────────────────

    /// <summary>Разрешено ли действие текущему пользователю (в коллекции или на уровне базы).</summary>
    public bool Can(PermAction action, int? collectionId = null)
    {
        if (IsAdmin) return true;
        if (!IsApproved) return false;
        var mask = collectionId is int cid && _collectionMasks.TryGetValue(cid, out var m)
            ? m
            : _dbMask;
        return (mask & (int)action) == (int)action;
    }

    /// <summary>Страховка репозитория: действие запрещено → PermissionDeniedException.</summary>
    public void Demand(PermAction action, int? collectionId = null)
    {
        if (!Can(action, collectionId))
            throw new PermissionDeniedException(action);
    }

    /// <summary>Перечитать снапшот прав текущего пользователя из БД.</summary>
    public void Reload(bool notify = true)
    {
        var me = FreshUsers.FirstOrDefault(u => u.UserKey == CurrentUser.Key);
        CurrentUserId = me?.Id ?? 0;
        IsAdmin       = me?.IsAdmin    ?? false;
        IsApproved    = me?.IsApproved ?? false;
        HasPassword   = _db.DbMeta.Any(m => m.Key == MetaPasswordHash);
        RetentionEnabled = GetMeta(MetaRetentionEnabled) == "1";
        RetentionMonths  = int.TryParse(GetMeta(MetaRetentionMonths), out var rm) && rm > 0
            ? rm : DefaultRetentionMonths;

        _dbMask = (int)PermAction.All;
        _collectionMasks = [];
        if (me is not null)
            foreach (var p in FreshPerms.Where(p => p.UserId == me.Id).ToList())
            {
                if (p.CollectionId is int cid) _collectionMasks[cid] = p.ActionsMask;
                else                           _dbMask               = p.ActionsMask;
            }

        if (notify) Changed?.Invoke(this, EventArgs.Empty);
    }

    // ── Панель администратора: пользователи и допуск ──────────────────────────

    /// <summary>Все пользователи базы (сначала ожидающие допуска, затем по имени).</summary>
    public List<DbUserEntity> GetUsers() =>
        FreshUsers.OrderBy(u => u.IsApproved).ThenBy(u => u.DisplayName).ToList();

    /// <summary>Выдать/отозвать допуск. Только администратор.</summary>
    public void SetApproved(int userId, bool approved)
    {
        DemandAdmin();
        var user = Tracked(_db.DbUsers.Find(userId));
        if (user is null || user.IsAdmin) return;   // допуск админа не трогаем
        // Строку снёс сосед, пока панель была открыта, — Reload отцепил её от контекста.
        if (_db.Entry(user).State == Microsoft.EntityFrameworkCore.EntityState.Detached) return;
        user.IsApproved = approved;
        // Событие в журнал: у затронутого пользователя замок снимется/появится сам,
        // без перезапуска (его поллер увидит строку и перечитает права).
        LogUser(approved ? "user.approve" : "user.revoke", user.DisplayName);
        _db.SaveChanges();
        Reload();
    }

    /// <summary>
    /// Назначить/снять роль администратора. Только администратор и только ДРУГОМУ
    /// пользователю: свою роль не меняет никто (иначе последний админ мог бы запереть
    /// базу от самого себя, а «понижение себя» ничем не отличалось бы от потери доступа).
    /// Повышение заодно даёт допуск — иначе новый администратор не попал бы в опции полей
    /// «Пользователь» (GetUserOptions отбирает по IsApproved). Понижение допуск не трогает
    /// и строк прав не создаёт: «нет строк — разрешено всё», лишнее админ снимает галками.
    /// </summary>
    public void SetAdmin(int userId, bool isAdmin)
    {
        DemandAdmin();
        var user = Tracked(_db.DbUsers.Find(userId));
        if (user is null || user.UserKey == CurrentUser.Key) return;   // свою роль не трогаем
        // Строку снёс сосед, пока панель была открыта, — Reload отцепил её от контекста.
        if (_db.Entry(user).State == Microsoft.EntityFrameworkCore.EntityState.Detached) return;
        if (user.IsAdmin == isAdmin) return;
        // Страховка на гонку: пока панель была открыта, соседи могли снять админство
        // с остальных. Базу без единого администратора не оставляем — настроить доступ
        // тогда не смог бы никто.
        if (!isAdmin && !FreshUsers.Any(u => u.IsAdmin && u.Id != userId)) return;

        user.IsAdmin = isAdmin;
        if (isAdmin) user.IsApproved = true;
        // Событие в журнал: у затронутого пользователя панель администратора появится
        // (или исчезнет) сама, без перезапуска — его поллер увидит строку.
        LogUser("user.role", user.DisplayName,
                Utils.ChangeDetail.Rename(isAdmin ? RoleUser : RoleAdmin,
                                          isAdmin ? RoleAdmin : RoleUser));
        _db.SaveChanges();
        Reload();
    }

    /// <summary>Подписи ролей — общие для панели администратора и «до → после» в журнале.</summary>
    public const string RoleUser  = "Пользователь";
    public const string RoleAdmin = "Администратор";

    /// <summary>
    /// Убрать пользователя из базы: строка db_users и его права (каскад db_permissions)
    /// исчезают. Только администратор и только ДРУГОГО: себя из базы не удаляют.
    /// Человек не заблокирован навсегда — при следующем открытии базы он зарегистрируется
    /// заново и снова будет ждать допуска. Записи, где его имя стоит в поле «Пользователь»,
    /// и строки журнала не трогаются: там снапшоты имени, а не ссылки.
    /// </summary>
    public void RemoveUser(int userId)
    {
        DemandAdmin();
        var user = Tracked(_db.DbUsers.Find(userId));
        if (user is null || user.UserKey == CurrentUser.Key) return;   // себя не удаляем
        if (_db.Entry(user).State == Microsoft.EntityFrameworkCore.EntityState.Detached) return;

        // Имя снимаем ДО удаления: после SaveChanges читать сущность уже нельзя.
        LogUser("user.remove", user.DisplayName);
        _db.DbUsers.Remove(user);
        _db.SaveChanges();
        Reload();
    }

    /// <summary>
    /// Действующая маска пользователя для панели (чекбоксы): маска коллекции ??
    /// маска базы ?? всё разрешено.
    /// </summary>
    public int GetMask(int userId, int? collectionId)
    {
        var rows = FreshPerms.Where(p => p.UserId == userId).ToList();
        if (collectionId is int cid &&
            rows.FirstOrDefault(p => p.CollectionId == cid) is { } perCol)
            return perCol.ActionsMask;
        return rows.FirstOrDefault(p => p.CollectionId == null)?.ActionsMask
               ?? (int)PermAction.All;
    }

    /// <summary>Записать маску пользователя (вся база при collectionId=null). Только администратор.</summary>
    public void SetMask(int userId, int? collectionId, int mask)
    {
        DemandAdmin();
        var row = Tracked(_db.DbPermissions
            .FirstOrDefault(p => p.UserId == userId && p.CollectionId == collectionId));
        if (row is null || _db.Entry(row).State == Microsoft.EntityFrameworkCore.EntityState.Detached)
            _db.DbPermissions.Add(new DbPermissionEntity
            {
                UserId       = userId,
                CollectionId = collectionId,
                ActionsMask  = mask,
            });
        else
            row.ActionsMask = mask;
        LogUser("user.rights",
                FreshUsers.Where(u => u.Id == userId).Select(u => u.DisplayName).FirstOrDefault() ?? "");
        _db.SaveChanges();
        Reload();
    }

    private void DemandAdmin()
    {
        if (!IsAdmin)
            throw new InvalidOperationException(
                "Недостаточно прав: управлять доступом может только администратор базы данных.");
    }

    // ── Пароль базы ───────────────────────────────────────────────────────────
    //
    // Программный замок на уровне приложения (файл не шифруется): PBKDF2-хеш в db_meta
    // самой базы — его видят все машины, открывающие файл. Верный пароль даёт допуск
    // без ожидания администратора.

    private const string MetaPasswordHash       = "password.hash";
    private const string MetaPasswordSalt       = "password.salt";
    private const string MetaPasswordIterations = "password.iterations";
    private const int    PasswordIterations     = 100_000;

    // Политика автоочистки журнала — в db_meta самой базы (как пароль): её видят все
    // машины, открывающие файл, и чистят одинаково. Применяет ConstructorRepository
    // при монтировании (DatabaseManager). Задаёт только администратор.
    private const string MetaRetentionEnabled   = "changelog.retention.enabled";
    private const string MetaRetentionMonths    = "changelog.retention.months";
    public  const int    DefaultRetentionMonths = 12;

    /// <summary>Включить/выключить автоочистку журнала и задать срок хранения (месяцев).
    /// Только администратор. Применяется при следующем монтировании базы.</summary>
    public void SetChangeLogRetention(bool enabled, int months)
    {
        DemandAdmin();
        SetMeta(MetaRetentionEnabled, enabled ? "1" : "0");
        SetMeta(MetaRetentionMonths, months.ToString());
        _db.SaveChanges();
        Reload();
    }

    /// <summary>Установить/сменить пароль базы. Только администратор.</summary>
    public void SetPassword(string password)
    {
        DemandAdmin();
        var salt = RandomNumberGenerator.GetBytes(16);
        var hash = Pbkdf2(password, salt, PasswordIterations);
        SetMeta(MetaPasswordHash, Convert.ToBase64String(hash));
        SetMeta(MetaPasswordSalt, Convert.ToBase64String(salt));
        SetMeta(MetaPasswordIterations, PasswordIterations.ToString());
        _db.SaveChanges();
        Reload();
    }

    /// <summary>Убрать пароль базы. Только администратор.</summary>
    public void RemovePassword()
    {
        DemandAdmin();
        _db.DbMeta.RemoveRange(_db.DbMeta.Where(m =>
            m.Key == MetaPasswordHash || m.Key == MetaPasswordSalt || m.Key == MetaPasswordIterations));
        _db.SaveChanges();
        Reload();
    }

    /// <summary>
    /// Попытка открыть базу паролем: верный пароль выдаёт текущему пользователю допуск
    /// (IsApproved) — повторно пароль не спрашивается.
    /// </summary>
    public bool TryUnlock(string password)
    {
        var hash = GetMeta(MetaPasswordHash);
        var salt = GetMeta(MetaPasswordSalt);
        if (hash is null || salt is null) return false;
        var iterations = int.TryParse(GetMeta(MetaPasswordIterations), out var it)
            ? it : PasswordIterations;

        var expected = Convert.FromBase64String(hash);
        var actual   = Pbkdf2(password, Convert.FromBase64String(salt), iterations);
        if (!CryptographicOperations.FixedTimeEquals(expected, actual)) return false;

        var me = Tracked(_db.DbUsers.FirstOrDefault(u => u.UserKey == CurrentUser.Key));
        if (me is not null && !me.IsApproved &&
            _db.Entry(me).State != Microsoft.EntityFrameworkCore.EntityState.Detached)
        {
            me.IsApproved = true;
            // Событие в журнал: в открытой панели администратора строка пользователя
            // сменит «Ожидает допуска» на допущенного без перезахода.
            LogUser("user.unlock", CurrentUser.DisplayName);
            _db.SaveChanges();
        }
        Reload();
        return true;
    }

    private static byte[] Pbkdf2(string password, byte[] salt, int iterations) =>
        Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password), salt, iterations,
                                  HashAlgorithmName.SHA256, 32);

    private string? GetMeta(string key) =>
        _db.DbMeta.AsNoTracking().FirstOrDefault(m => m.Key == key)?.Value;

    private void SetMeta(string key, string value)
    {
        var row = Tracked(_db.DbMeta.Find(key));
        if (row is not null &&
            _db.Entry(row).State == Microsoft.EntityFrameworkCore.EntityState.Detached) row = null;
        if (row is null) _db.DbMeta.Add(new DbMetaEntity { Key = key, Value = value });
        else             row.Value = value;
    }
}
