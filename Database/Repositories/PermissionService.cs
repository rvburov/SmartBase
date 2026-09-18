using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Serilog;
using SmartBase.Core.Models;
using SmartBase.Database.Entities;
using SmartBase.Utils;

namespace SmartBase.Database.Repositories;

/// <summary>
/// Права и допуск пользователей одной базы конструктора (экземпляр — на DatabaseManager).
///
/// Модель: администратор — создатель базы (первый открывший, когда админов еще нет),
/// у него всегда все права. Остальные пользователи регистрируются при монтировании
/// (<see cref="EnsureCurrentUserRegistered"/>) и ждут допуска: его дает администратор
/// в панели (SetApproved) или верный пароль базы (<see cref="TryUnlock"/>). Права —
/// битовые маски <see cref="PermAction"/> в db_permissions: строка с CollectionId=null
/// действует на всю базу, строка с коллекцией переопределяет ее; нет строк — у
/// допущенного все разрешено (администратор снимает лишнее).
///
/// Проверки (<see cref="Can"/>/<see cref="Demand"/>) идут по кеш-снапшоту прав ТЕКУЩЕГО
/// пользователя — ноль запросов к БД на горячих путях (правка ячеек, пакетные операции).
/// После правок в панели администратора вызывается <see cref="Reload"/>; UI подписывается
/// на <see cref="Changed"/> и перечитывает доступность кнопок. Методы, трогающие БД,
/// вызываются с UI-потока (или при монтировании, пока контекст не разделен) — как и все
/// операции общего контекста; Can/Demand можно звать из фоновых потоков (только кеш).
/// </summary>
public sealed partial class PermissionService(AppDbContext db)
{
    private readonly AppDbContext _db = db;

    /// <summary>
    /// Ключ базы в реестре — им подписываются строки журнала безопасности о решениях по доступу
    /// (<see cref="AccessTrace"/>). Ставит <c>DatabaseManager</c> при монтировании; 0 — база вне
    /// реестра (фоновые контексты, тесты), и тогда строка пишется без базы.
    /// </summary>
    public int DbId { get; init; }

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
    /// <summary>Сколько месяцев журнала хранить при включенной автоочистке.</summary>
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
    // контекст живет весь сеанс, и обычный трекинг-запрос вернул бы уже загруженный
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
    /// текущий пользователь становится админом. Иначе новая строка ждет допуска
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
                // Первый открывший базу решение по себе принял тем, что ее завел; остальные
                // ждут решения администратора — до него строка «ждет допуска», а не «отозван».
                AccessDecided = !hasAdmin,
                CreatedAt   = now,
                LastSeenAt  = now,
            };
            _db.DbUsers.Add(me);
            // Присоединение к чужой базе (админ уже есть) — событие в журнал: у админа
            // «в моменте» появится строка «Ожидает допуска» в панели администратора.
            // Первый пользователь новой базы сам себе не сигналит.
            if (hasAdmin) LogUser("user.join", CurrentUser.DisplayName);
            // Диагностика программы: события допуска — история того, кто и как получил доступ.
            if (hasAdmin)
                Log.Information("Пользователь {User:l} зарегистрирован в базе {Db:l} и ждет допуска", CurrentUser.DisplayName, _db.DisplayPath);
            else
                Log.Information("Пользователь {User:l} открыл базу {Db:l} первым и стал ее администратором", CurrentUser.DisplayName, _db.DisplayPath);
            try { _db.SaveChanges(); }
            catch (Microsoft.EntityFrameworkCore.DbUpdateException ex) when (ex.InnerException
                is Microsoft.Data.Sqlite.SqliteException { SqliteErrorCode: 19 }
                or Npgsql.PostgresException { SqlState: "23505" })
            {
                // Гонка регистрации одного пользователя из двух процессов (уникальный индекс
                // UserKey): сосед успел первым — строку не дублируем, работаем с его версией.
                // Отцепляем ВСЕ несохраненное, включая строку журнала user.join, — иначе она
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
                me.IsAdmin       = true;
                me.IsApproved    = true;
                me.AccessDecided = true;
            }
            // Отметка активности: администратор видит в панели, кто и когда открывал базу
            // последний раз. Пишется КАЖДОЕ монтирование (поэтому SaveChanges безусловный),
            // но НЕ идет в журнал — иначе каждое открытие базы будило бы поллеры соседей
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
        // Перед правкой перечитываем строку: соседский допуск/права, измененные за сеанс,
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
        PasswordAccessEnabled = GetMeta(MetaPasswordAccess) != "0";
        LastPasswordUnlock = System.DateTimeOffset.TryParse(
                GetMeta(MetaPasswordUnlockedAt), System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.RoundtripKind, out var unlockedAt)
            ? unlockedAt
            : null;
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
        // Строку снес сосед, пока панель была открыта, — Reload отцепил ее от контекста.
        if (_db.Entry(user).State == Microsoft.EntityFrameworkCore.EntityState.Detached) return;
        user.IsApproved    = approved;
        // Решение принято: отозванный допуск не должен читаться как «еще не решали» —
        // в панели это разные слова («Отозван» и «Ждет допуска»).
        user.AccessDecided = true;
        // Событие в журнал: у затронутого пользователя замок снимется/появится сам,
        // без перезапуска (его поллер увидит строку и перечитает права).
        LogUser(approved ? "user.approve" : "user.revoke", user.DisplayName);
        _db.SaveChanges();
        // В журнал безопасности — после сохранения: события, которого не случилось, там быть
        // не должно (так же пишутся остальные решения ниже).
        AccessTrace.Decision(DbId, user.DisplayName,
                             approved ? "допуск выдан" : "допуск отозван", DateTimeOffset.Now);
        Log.Information("Допуск {Action:l}: {User:l} ({Db:l})", approved ? "выдан" : "отозван", user.DisplayName, _db.DisplayPath);
        Reload();
    }

    /// <summary>
    /// Назначить/снять роль администратора. Только администратор и только ДРУГОМУ
    /// пользователю: свою роль не меняет никто (иначе последний админ мог бы запереть
    /// базу от самого себя, а «понижение себя» ничем не отличалось бы от потери доступа).
    /// Повышение заодно дает допуск — иначе новый администратор не попал бы в опции полей
    /// «Пользователь» (GetUserOptions отбирает по IsApproved). Понижение допуск не трогает
    /// и строк прав не создает: «нет строк — разрешено все», лишнее админ снимает галками.
    /// </summary>
    public void SetAdmin(int userId, bool isAdmin)
    {
        DemandAdmin();
        var user = Tracked(_db.DbUsers.Find(userId));
        if (user is null || user.UserKey == CurrentUser.Key) return;   // свою роль не трогаем
        // Строку снес сосед, пока панель была открыта, — Reload отцепил ее от контекста.
        if (_db.Entry(user).State == Microsoft.EntityFrameworkCore.EntityState.Detached) return;
        if (user.IsAdmin == isAdmin) return;
        // Робот администратором не бывает. Токен — это пароль в чужом файле настроек: сделав его
        // администратором, базу отдают тому, кто этот файл прочитает. Запрет стоит здесь, а не
        // только в панели: панель — не единственный вход в этот метод.
        if (isAdmin && Integrations.AccessToken.IsRobotKey(user.UserKey)) return;
        // Страховка на гонку: пока панель была открыта, соседи могли снять админство
        // с остальных. Базу без единого администратора не оставляем — настроить доступ
        // тогда не смог бы никто.
        if (!isAdmin && !FreshUsers.Any(u => u.IsAdmin && u.Id != userId)) return;

        user.IsAdmin = isAdmin;
        if (isAdmin)
        {
            user.IsApproved    = true;
            user.AccessDecided = true;
        }
        // Событие в журнал: у затронутого пользователя панель администратора появится
        // (или исчезнет) сама, без перезапуска — его поллер увидит строку.
        LogUser("user.role", user.DisplayName,
                Utils.ChangeDetail.Rename(isAdmin ? RoleUser : RoleAdmin,
                                          isAdmin ? RoleAdmin : RoleUser));
        _db.SaveChanges();
        AccessTrace.Decision(DbId, user.DisplayName,
                             $"роль: {(isAdmin ? RoleAdmin : RoleUser)}", DateTimeOffset.Now);
        Log.Information("Роль изменена: {User:l} → {Role:l} ({Db:l})", user.DisplayName, isAdmin ? RoleAdmin : RoleUser, _db.DisplayPath);
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
        var removedName = user.DisplayName;
        LogUser("user.remove", removedName);
        _db.DbUsers.Remove(user);
        _db.SaveChanges();
        AccessTrace.Decision(DbId, removedName, "пользователь убран из базы", DateTimeOffset.Now);
        Log.Information("Пользователь удален из базы: {User:l} ({Db:l})", removedName, _db.DisplayPath);
        Reload();
    }

    /// <summary>
    /// Действующая маска пользователя для панели (чекбоксы): маска коллекции ??
    /// маска базы ?? все разрешено.
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
    // самой базы — его видят все машины, открывающие файл. Верный пароль дает допуск
    // без ожидания администратора.

    private const string MetaPasswordHash       = "password.hash";
    private const string MetaPasswordSalt       = "password.salt";
    private const string MetaPasswordIterations = "password.iterations";
    private const int    PasswordIterations     = 100_000;

    // Момент последнего входа паролем (ISO 8601 «o», инвариантная культура). Хранится отдельной
    // строкой в db_meta, а не выводится из журнала: у change_log нет вторичных индексов, и искать
    // в нем последний user.unlock значило бы сканировать миллионы строк на медленном сетевом
    // диске ради одной даты в менеджере паролей. Ключ «password.*» нарочно: дата относится к
    // конкретному паролю, и смена или снятие пароля стирают ее вместе с ним.
    private const string MetaPasswordUnlockedAt = "password.unlocked_at";

    /// <summary>
    /// Когда кто-то в последний раз открыл себе доступ паролем этой базы; <c>null</c> — с тех пор,
    /// как пароль задан, так никто не входил (или входили до того, как программа начала это
    /// отмечать). Показывает менеджер паролей: пароль, которым давно не пользуются, стоит
    /// выключить.
    /// </summary>
    public DateTimeOffset? LastPasswordUnlock { get; private set; }

    // Политика автоочистки журнала — в db_meta самой базы (как пароль): ее видят все
    // машины, открывающие файл, и чистят одинаково. Применяет ConstructorRepository
    // при монтировании (DatabaseManager). Задает только администратор.
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
        var had  = HasPassword;   // «установлен» и «сменен» — разные события для проверяющего
        var salt = RandomNumberGenerator.GetBytes(16);
        var hash = Pbkdf2(password, salt, PasswordIterations);
        SetMeta(MetaPasswordHash, Convert.ToBase64String(hash));
        SetMeta(MetaPasswordSalt, Convert.ToBase64String(salt));
        SetMeta(MetaPasswordIterations, PasswordIterations.ToString());
        // Дата последнего входа относится к прежнему паролю: новым еще никто не входил.
        _db.DbMeta.RemoveRange(_db.DbMeta.Where(m => m.Key == MetaPasswordUnlockedAt));
        _db.SaveChanges();
        AccessTrace.Decision(DbId, PasswordSubject,
                             had ? "пароль базы сменен" : "пароль базы установлен", DateTimeOffset.Now);
        Reload();
    }

    /// <summary>Убрать пароль базы. Только администратор.</summary>
    public void RemovePassword()
    {
        DemandAdmin();
        _db.DbMeta.RemoveRange(_db.DbMeta.Where(m =>
            m.Key == MetaPasswordHash || m.Key == MetaPasswordSalt || m.Key == MetaPasswordIterations
            || m.Key == MetaPasswordUnlockedAt));
        _db.SaveChanges();
        AccessTrace.Decision(DbId, PasswordSubject, "пароль базы убран", DateTimeOffset.Now);
        Reload();
    }

    /// <summary>Кого касается решение, когда оно про саму базу, а не про человека.</summary>
    private const string PasswordSubject = "пароль базы данных";

    // Доступ других пользователей по паролю — политика базы в db_meta рядом с самим паролем, и
    // потому одинаковая для SQLite и PostgreSQL и для всех машин. Нет строки — включено: так
    // работали базы, где пароль поставили до появления тумблера. Ключ нарочно не «password.*»:
    // «Убрать пароль» стирает пароль, а не решение администратора о входе паролем.
    private const string MetaPasswordAccess = "access.password.enabled";

    /// <summary>Администратор не выключал вход паролем (сам пароль при этом может быть не задан).</summary>
    public bool PasswordAccessEnabled { get; private set; } = true;

    /// <summary>Замок базы открывается паролем: пароль задан и вход по нему не выключен.</summary>
    public bool PasswordAccess => HasPassword && PasswordAccessEnabled;

    /// <summary>
    /// Включить или выключить доступ других пользователей по паролю. Только администратор.
    /// Выключенный — ждущий допуска входит только после разрешения администратора, даже зная
    /// пароль; пароль при этом сохраняется.
    /// </summary>
    public void SetPasswordAccess(bool enabled)
    {
        DemandAdmin();
        SetMeta(MetaPasswordAccess, enabled ? "1" : "0");
        _db.SaveChanges();
        AccessTrace.Decision(DbId, PasswordSubject,
                             enabled ? "способ входа: вход по паролю"
                                     : "способ входа: подтверждение администратора",
                             DateTimeOffset.Now);
        Log.Information("Доступ других пользователей по паролю {State:l}: {Db:l}",
                        enabled ? "включен" : "выключен", _db.DisplayPath);
        Reload();
    }

    /// <summary>
    /// Попросить доступ к базе — щелчок по замку. Нужен тому, кого администратор убрал из базы,
    /// пока она была у него открыта: строки пользователя больше нет, и без нее администратор не
    /// видит ни человека, ни запроса до следующего открытия базы. Строка заводится так же, как при
    /// открытии, — ждущей допуска (<see cref="EnsureCurrentUserRegistered"/>, событие user.join),
    /// и доступ снова дает только администратор или пароль базы. Строка есть — запрос уже виден.
    /// </summary>
    public void RequestAccess()
    {
        if (FreshUsers.Any(u => u.UserKey == CurrentUser.Key)) return;

        // Удаленная соседом строка могла остаться в кеше общего контекста — отцепляем ее, иначе EF
        // держал бы за живую строку, которой в базе уже нет.
        foreach (var stale in _db.ChangeTracker.Entries<DbUserEntity>()
                     .Where(en => en.Entity.UserKey == CurrentUser.Key)
                     .ToList())
            stale.State = Microsoft.EntityFrameworkCore.EntityState.Detached;

        EnsureCurrentUserRegistered();
    }

    /// <summary>
    /// Попытка открыть базу паролем: верный пароль выдает текущему пользователю допуск
    /// (IsApproved) — повторно пароль не спрашивается.
    /// </summary>
    public bool TryUnlock(string password) => Unlock(password).Length == 0;

    /// <summary>
    /// То же, что <see cref="TryUnlock"/>, но с причиной отказа словами (пусто — вошел): окно ввода
    /// показывает ее как есть. «Неверный пароль» и «вход паролем выключен» лечатся по-разному.
    /// </summary>
    public string Unlock(string password)
    {
        // Политика читается свежей, а не из снимка: окно ввода могло быть открыто до того, как
        // администратор выключил вход паролем.
        if (GetMeta(MetaPasswordAccess) == "0")
            return "Вход по паролю выключен администратором базы данных";

        // Подбор пароля: после трех промахов подряд ввод ждет, после пяти — ждет дольше.
        // Ожидание считается отказом в приеме, а не промахом, поэтому счет от него не растет.
        if (WaitLeft(_db.DisplayPath) is { } left)
            return $"Слишком много попыток. Повторите через {left} с.";

        var hash = GetMeta(MetaPasswordHash);
        var salt = GetMeta(MetaPasswordSalt);
        if (hash is null || salt is null) return "У базы данных нет пароля";
        var iterations = int.TryParse(GetMeta(MetaPasswordIterations), out var it)
            ? it : PasswordIterations;

        var expected = Convert.FromBase64String(hash);
        var actual   = Pbkdf2(password, Convert.FromBase64String(salt), iterations);
        if (!CryptographicOperations.FixedTimeEquals(expected, actual))
        {
            // Неверный пароль — событие безопасности: кто и в какую базу пытался войти. Строка
            // ложится в журнал безопасности, а не только в «Диагностику»: подбор пароля базы
            // должен быть виден администратору и проверяющему, а не лежать в файле логов.
            var misses = NoteMiss(_db.DisplayPath);
            AccessTrace.Signin(DbId, ok: false,
                               $"неверный пароль, промах подряд: {misses}", DateTimeOffset.Now);
            Log.Warning("Неверный пароль базы {Db:l} (пользователь {User:l})", _db.DisplayPath, CurrentUser.DisplayName);
            return "Неверный пароль";
        }

        // Пароль подошел — счет промахов обнуляется: ждать следующего ввода незачем.
        ForgetMisses(_db.DisplayPath);
        AccessTrace.Signin(DbId, ok: true, "пароль верный, доступ к базе открыт", DateTimeOffset.Now);

        // Убрали из базы, пока она была открыта, — верный пароль сначала возвращает человека в
        // список пользователей, иначе допуск выдавать было бы некому.
        RequestAccess();

        var me = Tracked(_db.DbUsers.FirstOrDefault(u => u.UserKey == CurrentUser.Key));
        if (me is not null && !me.IsApproved &&
            _db.Entry(me).State != Microsoft.EntityFrameworkCore.EntityState.Detached)
        {
            me.IsApproved    = true;
            // Вход по паролю — тоже решение по допуску: администратор разрешил его заранее,
            // выбрав «Вход по паролю» способом входа.
            me.AccessDecided = true;
            // Событие в журнал: в открытой панели администратора строка пользователя
            // сменит «Ожидает допуска» на допущенного без перезахода.
            LogUser("user.unlock", CurrentUser.DisplayName);
            // Момент входа — рядом с паролем, одной строкой db_meta: менеджер паролей показывает
            // его администратору, не сканируя журнал (см. MetaPasswordUnlockedAt). Коммитится
            // вместе с допуском — вход без отметки или отметка без входа невозможны.
            SetMeta(MetaPasswordUnlockedAt,
                    DateTimeOffset.UtcNow.ToString("o", System.Globalization.CultureInfo.InvariantCulture));
            _db.SaveChanges();
            Log.Information("Допуск по паролю базы: {User:l} ({Db:l})", CurrentUser.DisplayName, _db.DisplayPath);
        }
        Reload();
        return "";
    }

    // ── Пауза после промахов ──────────────────────────────────────────────────
    //
    // Счет живет в памяти программы и своим для каждой базы: файл базы ездит по сетевой папке,
    // и писать туда «эта машина ошиблась трижды» значило бы рассказывать о промахах всем
    // остальным. Перезапуск программы счет обнуляет — и это честно: пауза защищает от подбора
    // руками у чужого стола, а не от того, у кого файл базы уже на руках (такому паузы не
    // помеха вовсе — от него бережет длина пароля и PBKDF2).
    //
    // Порог намеренно выше числа обычных опечаток: три промаха подряд человек делает и по
    // раскладке, и по Caps Lock.
    private const int  SlowAfter  = 3;
    private const int  SlowerAfter = 5;
    private const int  SlowSeconds   = 3;
    private const int  SlowerSeconds = 10;

    private static readonly Dictionary<string, (int Misses, DateTimeOffset NextTry)> Misses =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Сколько секунд еще ждать; <c>null</c> — можно пробовать.</summary>
    private static int? WaitLeft(string key)
    {
        lock (Misses)
        {
            if (!Misses.TryGetValue(key, out var state)) return null;

            var left = (state.NextTry - DateTimeOffset.UtcNow).TotalSeconds;
            return left > 0 ? (int)Math.Ceiling(left) : null;
        }
    }

    /// <summary>Записать промах и вернуть, сколько их подряд.</summary>
    private static int NoteMiss(string key)
    {
        lock (Misses)
        {
            var count = (Misses.TryGetValue(key, out var state) ? state.Misses : 0) + 1;

            var pause = count >= SlowerAfter ? SlowerSeconds
                      : count >= SlowAfter   ? SlowSeconds
                      : 0;

            Misses[key] = (count, DateTimeOffset.UtcNow.AddSeconds(pause));
            return count;
        }
    }

    private static void ForgetMisses(string key)
    {
        lock (Misses) Misses.Remove(key);
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
