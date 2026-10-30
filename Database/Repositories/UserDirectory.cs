using Microsoft.EntityFrameworkCore;
using SmartBase.Database.Entities;
using SmartBase.Utils;

namespace SmartBase.Database.Repositories;

/// <summary>Имена участников базы для авторства и свойств «Пользователь».</summary>
public sealed class UserDirectory(AppDbContext db)
{
    public int CurrentUserId { get; private set; }
    public event EventHandler? Changed;

    private void LogUser(string action, string name, string? detail = null) =>
        db.ChangeLog.Add(new ChangeLogEntity
        {
            Ts = DateTime.UtcNow.ToString("o"), UserKey = CurrentUser.Key,
            UserName = CurrentUser.DisplayName, Action = action,
            TargetType = "user", TargetName = name, Detail = detail,
        });

    public void EnsureCurrentUserRegistered()
    {
        var me = db.DbUsers.FirstOrDefault(u => u.UserKey == CurrentUser.Key);
        var stamp = DateTime.UtcNow.ToString("o");
        if (me is null)
        {
            me = new DbUserEntity
            {
                UserKey = CurrentUser.Key, DisplayName = CurrentUser.DisplayName,
                CreatedAt = stamp, LastSeenAt = stamp,
            };
            db.DbUsers.Add(me);
            LogUser("user.join", me.DisplayName);
            try { db.SaveChanges(); }
            catch (DbUpdateException ex) when (ex.InnerException
                is Microsoft.Data.Sqlite.SqliteException { SqliteErrorCode: 19 }
                or Npgsql.PostgresException { SqlState: "23505" })
            {
                // Другая копия зарегистрировала ту же учетную запись первой.
                // Убираем только несохраненные строки этой попытки.
                db.Entry(me).State = EntityState.Detached;
                foreach (var entry in db.ChangeTracker.Entries<ChangeLogEntity>()
                    .Where(e => e.State == EntityState.Added && e.Entity.Action == "user.join"
                        && e.Entity.UserKey == CurrentUser.Key).ToList())
                    entry.State = EntityState.Detached;
            }
        }
        else
        {
            db.Entry(me).Reload();
            if (db.Entry(me).State == EntityState.Detached)
            {
                EnsureCurrentUserRegistered();
                return;
            }
            if (me.DisplayName != CurrentUser.DisplayName)
            {
                LogUser("user.rename", CurrentUser.DisplayName,
                    ChangeDetail.Rename(me.DisplayName, CurrentUser.DisplayName));
                me.DisplayName = CurrentUser.DisplayName;
            }
            me.LastSeenAt = stamp;
            db.SaveChanges();
        }
        Reload(notify: false);
    }

    public void SyncDisplayName()
    {
        var me = db.DbUsers.FirstOrDefault(u => u.UserKey == CurrentUser.Key);
        if (me is null) { EnsureCurrentUserRegistered(); Changed?.Invoke(this, EventArgs.Empty); return; }
        db.Entry(me).Reload();
        if (db.Entry(me).State == EntityState.Detached)
        { EnsureCurrentUserRegistered(); Changed?.Invoke(this, EventArgs.Empty); return; }
        if (me.DisplayName == CurrentUser.DisplayName) return;
        LogUser("user.rename", CurrentUser.DisplayName,
            ChangeDetail.Rename(me.DisplayName, CurrentUser.DisplayName));
        me.DisplayName = CurrentUser.DisplayName;
        db.SaveChanges();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Reload(bool notify = true)
    {
        CurrentUserId = db.DbUsers.AsNoTracking().Where(u => u.UserKey == CurrentUser.Key)
            .Select(u => u.Id).FirstOrDefault();
        if (notify) Changed?.Invoke(this, EventArgs.Empty);
    }

    public List<DbUserEntity> GetUsers() =>
        db.DbUsers.AsNoTracking().OrderBy(u => u.DisplayName).ThenBy(u => u.Id).ToList();

    public List<string> GetUserOptions(string? includeValue = null)
    {
        var names = db.DbUsers.AsNoTracking().OrderBy(u => u.DisplayName)
            .Select(u => u.DisplayName).ToList();
        if (!string.IsNullOrEmpty(includeValue) && !names.Contains(includeValue)) names.Add(includeValue);
        return names;
    }

    public void RemoveUser(int userId)
    {
        var user = db.DbUsers.Find(userId);
        if (user is null) return;
        db.Entry(user).Reload();
        if (db.Entry(user).State == EntityState.Detached) return;
        LogUser("user.remove", user.DisplayName);
        db.DbUsers.Remove(user);
        db.SaveChanges();
        Reload();
    }
}
