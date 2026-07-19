using System.Security.Cryptography;
using System.Text;
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

    // ── Снапшот текущего пользователя (кеш; обновляет Reload) ────────────────
    public int  CurrentUserId { get; private set; }
    public bool IsAdmin       { get; private set; }
    public bool IsApproved    { get; private set; }
    /// <summary>У базы установлен пароль (хеш в db_meta).</summary>
    public bool HasPassword   { get; private set; }
    private int _dbMask = (int)PermAction.All;
    private Dictionary<int, int> _collectionMasks = [];

    /// <summary>Права пересчитаны (допуск, маски, пароль) — UI перечитывает кнопки.</summary>
    public event EventHandler? Changed;

    /// <summary>Текущему пользователю доступно содержимое базы.</summary>
    public bool AccessGranted => IsAdmin || IsApproved;

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

        if (me is null)
        {
            me = new DbUserEntity
            {
                UserKey     = key,
                DisplayName = CurrentUser.DisplayName,
                IsAdmin     = !hasAdmin,
                IsApproved  = !hasAdmin,
                CreatedAt   = DateTime.UtcNow.ToString("o"),
            };
            _db.DbUsers.Add(me);
            try { _db.SaveChanges(); }
            catch (Microsoft.EntityFrameworkCore.DbUpdateException ex) when (ex.InnerException
                is Microsoft.Data.Sqlite.SqliteException { SqliteErrorCode: 19 }
                or Npgsql.PostgresException { SqlState: "23505" })
            {
                // Гонка регистрации одного пользователя из двух процессов (уникальный индекс
                // UserKey): сосед успел первым — строку не дублируем, работаем с его версией.
                _db.Entry(me).State = Microsoft.EntityFrameworkCore.EntityState.Detached;
            }
        }
        else
        {
            var dirty = false;
            if (me.DisplayName != CurrentUser.DisplayName)
            {
                me.DisplayName = CurrentUser.DisplayName;
                dirty = true;
            }
            if (!hasAdmin && !me.IsAdmin)
            {
                // База осталась без админа (например, строку удалили вручную) — клеймим.
                me.IsAdmin    = true;
                me.IsApproved = true;
                dirty = true;
            }
            if (dirty) _db.SaveChanges();
        }

        Reload(notify: false);
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
        var me = _db.DbUsers.FirstOrDefault(u => u.UserKey == CurrentUser.Key);
        CurrentUserId = me?.Id ?? 0;
        IsAdmin       = me?.IsAdmin    ?? false;
        IsApproved    = me?.IsApproved ?? false;
        HasPassword   = _db.DbMeta.Any(m => m.Key == MetaPasswordHash);

        _dbMask = (int)PermAction.All;
        _collectionMasks = [];
        if (me is not null)
            foreach (var p in _db.DbPermissions.Where(p => p.UserId == me.Id).ToList())
            {
                if (p.CollectionId is int cid) _collectionMasks[cid] = p.ActionsMask;
                else                           _dbMask               = p.ActionsMask;
            }

        if (notify) Changed?.Invoke(this, EventArgs.Empty);
    }

    // ── Панель администратора: пользователи и допуск ──────────────────────────

    /// <summary>Все пользователи базы (сначала ожидающие допуска, затем по имени).</summary>
    public List<DbUserEntity> GetUsers() =>
        _db.DbUsers.OrderBy(u => u.IsApproved).ThenBy(u => u.DisplayName).ToList();

    /// <summary>Выдать/отозвать допуск. Только администратор.</summary>
    public void SetApproved(int userId, bool approved)
    {
        DemandAdmin();
        var user = _db.DbUsers.Find(userId);
        if (user is null || user.IsAdmin) return;   // допуск админа не трогаем
        user.IsApproved = approved;
        _db.SaveChanges();
        Reload();
    }

    /// <summary>
    /// Действующая маска пользователя для панели (чекбоксы): маска коллекции ??
    /// маска базы ?? всё разрешено.
    /// </summary>
    public int GetMask(int userId, int? collectionId)
    {
        var rows = _db.DbPermissions.Where(p => p.UserId == userId).ToList();
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
        var row = _db.DbPermissions
            .FirstOrDefault(p => p.UserId == userId && p.CollectionId == collectionId);
        if (row is null)
            _db.DbPermissions.Add(new DbPermissionEntity
            {
                UserId       = userId,
                CollectionId = collectionId,
                ActionsMask  = mask,
            });
        else
            row.ActionsMask = mask;
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

        var me = _db.DbUsers.FirstOrDefault(u => u.UserKey == CurrentUser.Key);
        if (me is not null && !me.IsApproved)
        {
            me.IsApproved = true;
            _db.SaveChanges();
        }
        Reload();
        return true;
    }

    private static byte[] Pbkdf2(string password, byte[] salt, int iterations) =>
        Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password), salt, iterations,
                                  HashAlgorithmName.SHA256, 32);

    private string? GetMeta(string key) =>
        _db.DbMeta.FirstOrDefault(m => m.Key == key)?.Value;

    private void SetMeta(string key, string value)
    {
        var row = _db.DbMeta.Find(key);
        if (row is null) _db.DbMeta.Add(new DbMetaEntity { Key = key, Value = value });
        else             row.Value = value;
    }
}
