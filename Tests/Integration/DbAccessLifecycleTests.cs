using SmartBase.Core.Models;
using SmartBase.Database;
using SmartBase.Database.Entities;
using SmartBase.Database.Repositories;
using SmartBase.Utils;

namespace SmartBase.Tests.Integration;

/// <summary>
/// Жизненный цикл пользователя в ЧУЖОЙ базе (docs/developer/architecture/users-permissions.md):
/// присоединился → ждёт → вошёл паролем → получил/потерял права → базу закрыли.
///
/// Три вещи, ради которых этот файл существует.
///  1. Что видит человек. Открыл общую базу — содержимое закрыто, пока админ не подтвердит
///     (или пока не введён пароль базы). Не-админ не может ни выдать себе допуск, ни
///     подсмотреть/сменить пароль, ни вычистить журнал — иначе «права» ничего не значат.
///  2. Транспорт живого обновления. Единственный канал между машинами — строки change_log
///     с TargetType=="user": поллер соседа (MainWindow.OnRemoteChanges) реагирует ТОЛЬКО на
///     них, а внутри процесса списки перестраивает событие PermissionService.Changed
///     (MainWindow.HookPermissionEvents → OnDbUsersChanged). Нет строки журнала — у соседа
///     замок не снимется до перезапуска; нет события — выпадающие списки пользователей
///     и кнопки останутся от прошлого состояния.
///  3. Устойчивость к правкам соседа. Общий AppDbContext живёт весь сеанс, поэтому служба
///     прав читает db_users/db_permissions/db_meta свежими (AsNoTracking), а правимую строку
///     перечитывает (Entry.Reload) и проверяет на Detached. Ломается — «допуск не доезжает»
///     и панель администратора падает DbUpdateConcurrencyException на удалённой соседом строке.
///
/// Поэтому КАЖДАЯ проверка состояния идёт через ОТДЕЛЬНЫЙ <c>new AppDbContext(path)</c>:
/// чтение через общий контекст вернуло бы то, что он и так держит в кеше, и регрессия
/// «сосед правит — мы не видим» осталась бы незамеченной.
/// </summary>
public sealed class DbAccessLifecycleTests
{
    // Регистрация соседа в базе — как PermissionService при монтировании на его машине
    // (вторым подключением, т.е. буквально «с другого компьютера»).
    private static int JoinUser(string path, string key, string name, bool approved = false)
    {
        using var neighbour = new AppDbContext(path);
        var user = new DbUserEntity
        {
            UserKey     = key,
            DisplayName = name,
            IsApproved  = approved,
            CreatedAt   = DateTime.UtcNow.ToString("o"),
        };
        neighbour.DbUsers.Add(user);
        neighbour.SaveChanges();
        return user.Id;
    }

    // Понижает текущего пользователя до заданной роли и перечитывает снапшот прав:
    // так выглядит машина рядового сотрудника (базу создал не он).
    private static void Demote(TempDatabase db, bool admin, bool approved)
    {
        var me = db.Context.DbUsers.First(u => u.UserKey == CurrentUser.Key);
        me.IsAdmin    = admin;
        me.IsApproved = approved;
        db.Context.SaveChanges();
        db.Permissions.Reload(notify: false);
    }

    // ── 1. Присоединение к чужой базе ─────────────────────────────────────────

    /// <summary>
    /// Открыл базу, которую создал кто-то другой: доступа нет, пока админ не подтвердит.
    /// Про новичка админу сообщает ровно одна строка журнала user.join — она и зажигает
    /// строку «Ожидает допуска» в открытой панели. Первый пользователь новой базы сам себе
    /// не сигналит, а повторное монтирование не плодит ни пользователей, ни сигналов
    /// (иначе каждый запуск приложения дёргал бы панели всех соседей).
    /// </summary>
    [Fact]
    public void Joining_database_with_existing_admin_waits_for_approval()
    {
        var path = TempDatabase.NewTempPath();
        try
        {
            // Первый монт: базы ещё нет — текущий пользователь стал её администратором.
            using (var owner = DatabaseManager.Open(1, "общая", path))
            {
                Assert.True(owner.Permissions.IsAdmin);
                using (var born = new AppDbContext(path))
                    Assert.Equal(0, born.ChangeLog.Count(e => e.Action == "user.join"));

                // Переписываем историю: базу создал сосед, меня в ней ещё нет.
                owner.Context.DbUsers.Add(new DbUserEntity
                {
                    UserKey     = "ivanov@pc1",
                    DisplayName = "Иванов Иван",
                    IsAdmin     = true,
                    IsApproved  = true,
                    CreatedAt   = DateTime.UtcNow.ToString("o"),
                });
                owner.Context.DbUsers.Remove(
                    owner.Context.DbUsers.First(u => u.UserKey == CurrentUser.Key));
                owner.Context.SaveChanges();
            }

            // Второй монт: я открываю ЧУЖУЮ базу.
            using var mine = DatabaseManager.Open(1, "общая", path);

            Assert.False(mine.Permissions.IsAdmin);
            Assert.False(mine.Permissions.IsApproved);
            Assert.False(mine.Permissions.AccessGranted);

            using (var check = new AppDbContext(path))
            {
                var me = check.DbUsers.Single(u => u.UserKey == CurrentUser.Key);
                Assert.False(me.IsAdmin);
                Assert.False(me.IsApproved);
                Assert.Equal(CurrentUser.DisplayName, me.DisplayName);

                // Сигнал администратору: «пришёл новичок» — единственный способ узнать о нём
                // не перезапуская приложение.
                var join = Assert.Single(check.ChangeLog.Where(e => e.Action == "user.join").ToList());
                Assert.Equal("user", join.TargetType);
                Assert.Equal(CurrentUser.DisplayName, join.TargetName);
                Assert.Equal(CurrentUser.Key, join.UserKey);
            }

            // Третий монт: то же приложение открыли ещё раз — ни второй строки пользователя,
            // ни повторного «пришёл новичок» быть не должно.
            using (DatabaseManager.Open(1, "общая", path)) { }

            using (var again = new AppDbContext(path))
            {
                Assert.Equal(1, again.DbUsers.Count(u => u.UserKey == CurrentUser.Key));
                Assert.Equal(1, again.ChangeLog.Count(e => e.Action == "user.join"));
            }

            // Содержимое базы закрыто: репозиторий не пускает даже прямым вызовом.
            Assert.Throws<PermissionDeniedException>(() => SampleData.AddCollection(mine.Constructor));
        }
        finally { TempDatabase.Cleanup(path); }
    }

    // ── 2. Вход паролем базы ──────────────────────────────────────────────────

    /// <summary>
    /// Админ выдал пароль базы вместо ручного подтверждения: верный пароль снимает замок
    /// сразу (без перезахода) и оставляет след user.unlock — иначе администратор никогда
    /// не увидит, что человек вошёл. Неверный пароль не должен ни давать допуск, ни
    /// оставлять шум в журнале.
    /// </summary>
    [Fact]
    public void Correct_password_grants_access_to_waiting_user()
    {
        using var db = new TempDatabase();
        db.Permissions.SetPassword("секрет-базы");

        Demote(db, admin: false, approved: false);
        Assert.False(db.Permissions.AccessGranted);

        int baseline;
        using (var before = new AppDbContext(db.Path)) baseline = before.ChangeLog.Count();

        // Опечатка в пароле — состояние обязано остаться нетронутым.
        Assert.False(db.Permissions.TryUnlock("не тот пароль"));
        Assert.False(db.Permissions.AccessGranted);
        using (var afterWrong = new AppDbContext(db.Path))
        {
            Assert.False(afterWrong.DbUsers.Single(u => u.UserKey == CurrentUser.Key).IsApproved);
            Assert.Equal(0, afterWrong.ChangeLog.Count(e => e.Action == "user.unlock"));
            Assert.Equal(baseline, afterWrong.ChangeLog.Count());
        }

        // Верный пароль: снапшот прав пересчитан ВНУТРИ TryUnlock — UI не обязан звать Reload.
        Assert.True(db.Permissions.TryUnlock("секрет-базы"));
        Assert.True(db.Permissions.IsApproved);
        Assert.True(db.Permissions.AccessGranted);

        using (var check = new AppDbContext(db.Path))
        {
            Assert.True(check.DbUsers.Single(u => u.UserKey == CurrentUser.Key).IsApproved);
            var unlock = Assert.Single(check.ChangeLog.Where(e => e.Action == "user.unlock").ToList());
            Assert.Equal("user", unlock.TargetType);
            Assert.Equal(CurrentUser.DisplayName, unlock.TargetName);
        }

        // Замок снят и на уровне репозитория, а не только в свойствах службы.
        SampleData.AddCollection(db.Constructor);
    }

    // ── 3. Управление доступом — только администратору ────────────────────────

    /// <summary>
    /// Допущенный, но рядовой пользователь работает с данными и НЕ управляет доступом:
    /// иначе любой сотрудник выдал бы себе права, снял пароль базы или стёр журнал —
    /// то есть замёл следы. Проверяем не только исключения, но и то, что до БД
    /// ничего не доехало (частично выполненная админская операция хуже отказа).
    /// </summary>
    [Fact]
    public void Non_admin_cannot_manage_access_password_or_journal()
    {
        using var db = new TempDatabase();
        var col          = SampleData.AddCollection(db.Constructor);
        var neighbourId  = JoinUser(db.Path, "sidorov@pc9", "Сидоров Семён", approved: false);
        var myId         = db.Permissions.CurrentUserId;

        int logBaseline;
        using (var before = new AppDbContext(db.Path))
        {
            logBaseline = before.ChangeLog.Count();
            Assert.Equal(0, before.DbPermissions.Count());
            Assert.False(before.DbMeta.Any(m => m.Key.StartsWith("password.")));
            Assert.False(before.DbMeta.Any(m => m.Key.StartsWith("changelog.retention.")));
        }

        Demote(db, admin: false, approved: true);

        // DemandAdmin — общий шлагбаум панели администратора и настроек базы.
        Assert.Throws<InvalidOperationException>(() => db.Permissions.SetApproved(neighbourId, true));
        Assert.Throws<InvalidOperationException>(() => db.Permissions.SetMask(myId, null, (int)PermAction.All));
        Assert.Throws<InvalidOperationException>(() => db.Permissions.SetPassword("любой"));
        Assert.Throws<InvalidOperationException>(() => db.Permissions.RemovePassword());
        Assert.Throws<InvalidOperationException>(() => db.Permissions.SetChangeLogRetention(true, 1));
        Assert.Throws<InvalidOperationException>(() => db.Constructor.PurgeChangeLog(olderThanUtc: null));

        using (var check = new AppDbContext(db.Path))
        {
            Assert.False(check.DbUsers.Single(u => u.Id == neighbourId).IsApproved);
            Assert.Equal(0, check.DbPermissions.Count());
            Assert.False(check.DbMeta.Any(m => m.Key.StartsWith("password.")));
            Assert.False(check.DbMeta.Any(m => m.Key.StartsWith("changelog.retention.")));
            // Ни одной строки user.* — отказ не должен даже выглядеть как действие над доступом.
            Assert.Equal(logBaseline, check.ChangeLog.Count());
            Assert.Equal(0, check.ChangeLog.Count(e => e.TargetType == "user"));
        }

        // Обычная работа при этом не пострадала — человек просто не админ, а не «заблокирован».
        SampleData.AddRecord(db.Constructor, col.Id!.Value, "Запись");
    }

    // ── 4. Правка прав соседом доезжает без перезапуска ───────────────────────

    /// <summary>
    /// Второй администратор с другой машины поменял мою маску — она обязана вступить в силу
    /// по одному Reload (ровно то, что делает поллер, увидев чужую строку журнала user.*).
    /// Это регрессия ради AsNoTracking: общий контекст, однажды прочитавший db_permissions
    /// трекинг-запросом, до перезапуска отдавал бы СТАРУЮ маску, и человек либо остался бы
    /// без прав, либо продолжал делать запрещённое.
    /// </summary>
    [Fact]
    public void Mask_written_by_neighbour_takes_effect_without_restart()
    {
        using var db = new TempDatabase();
        var repo  = db.Constructor;
        var col   = SampleData.AddCollection(repo);
        var field = SampleData.AddField(repo, col.Id!.Value, "Текст");
        var myId  = db.Permissions.CurrentUserId;

        // Рядовой допущенный: строк прав нет — по умолчанию разрешено всё.
        Demote(db, admin: false, approved: true);
        Assert.True(db.Permissions.Can(PermAction.EditRecord));

        using var neighbour = new AppDbContext(db.Path);
        var granted = new DbPermissionEntity
        {
            UserId       = myId,
            CollectionId = null,
            ActionsMask  = (int)(PermAction.ViewCollection | PermAction.CreateRecord),
        };
        neighbour.DbPermissions.Add(granted);
        neighbour.SaveChanges();

        db.Permissions.Reload(notify: false);

        Assert.True(db.Permissions.Can(PermAction.ViewCollection));
        Assert.False(db.Permissions.Can(PermAction.EditRecord));
        Assert.False(db.Permissions.Can(PermAction.DeleteRecord));
        // Панель администратора (чекбоксы) читает ту же свежую маску, что и проверки.
        Assert.Equal((int)(PermAction.ViewCollection | PermAction.CreateRecord),
                     db.Permissions.GetMask(myId, null));

        // Репозиторий страхует UI: скрытая кнопка — не защита, отказ обязан быть в модели.
        var rec = SampleData.AddRecord(repo, col.Id!.Value, "R");
        Assert.Throws<PermissionDeniedException>(() => repo.SetFieldValue(rec.Id!.Value, field.Id!.Value, "x"));
        Assert.Throws<PermissionDeniedException>(() => repo.DeleteRecord(rec.Id!.Value));

        using (var check = new AppDbContext(db.Path))
        {
            Assert.False(check.DbFieldValues.Any(v => v.RecordId == rec.Id!.Value && v.Value == "x"));
            Assert.True(check.DbRecords.Any(r => r.Id == rec.Id!.Value));
        }

        // Права вернули — тем же путём (чужая правка → Reload), запрет снимается сразу.
        granted.ActionsMask = (int)PermAction.All;
        neighbour.SaveChanges();
        db.Permissions.Reload(notify: false);

        Assert.True(db.Permissions.Can(PermAction.EditRecord));
        repo.SetFieldValue(rec.Id!.Value, field.Id!.Value, "x");

        using (var check = new AppDbContext(db.Path))
            Assert.Equal("x", check.DbFieldValues
                .Single(v => v.RecordId == rec.Id!.Value && v.FieldId == field.Id!.Value).Value);
    }

    // ── 5. Соседи удаляют строки под открытой панелью ─────────────────────────

    /// <summary>
    /// Панель администратора и профиль держат загруженные строки в общем контексте, а сосед
    /// в это время их удаляет (выгнал пользователя, снял права, вычистил db_users). Каждая
    /// правка после этого обязана быть безопасной: EF на удалённой строке иначе бросает
    /// DbUpdateConcurrencyException и роняет экран. Три блока — три Detached-гварда службы.
    /// </summary>
    [Fact]
    public void Rows_deleted_by_neighbour_do_not_break_admin_panel()
    {
        using var db = new TempDatabase();
        var myId = db.Permissions.CurrentUserId;

        // ── Блок А: пользователя выгнали, пока админ жал «Допустить» ────────────
        var petrovId = JoinUser(db.Path, "petrov@pc7", "Петров Пётр");
        Assert.NotNull(db.Context.DbUsers.Find(petrovId));   // строку держит открытая панель

        using (var neighbour = new AppDbContext(db.Path))
        {
            neighbour.DbUsers.Remove(neighbour.DbUsers.First(u => u.Id == petrovId));
            neighbour.SaveChanges();
        }

        int logBeforeA;
        using (var before = new AppDbContext(db.Path)) logBeforeA = before.ChangeLog.Count();

        db.Permissions.SetApproved(petrovId, true);   // не должно бросить

        using (var check = new AppDbContext(db.Path))
        {
            Assert.False(check.DbUsers.Any(u => u.Id == petrovId));
            // Пользователя нет — сообщать соседям нечего, лишних строк в журнале быть не должно.
            Assert.Equal(logBeforeA, check.ChangeLog.Count());
        }

        // ── Блок Б: строку прав снесли между двумя галками в панели ─────────────
        db.Permissions.SetMask(myId, null, (int)PermAction.All);
        // Строку прав держит открытая панель (чекбоксы уже прочитаны в общий контекст).
        Assert.Equal((int)PermAction.All,
                     db.Context.DbPermissions.First(p => p.UserId == myId && p.CollectionId == null).ActionsMask);

        using (var neighbour = new AppDbContext(db.Path))
        {
            neighbour.DbPermissions.RemoveRange(
                neighbour.DbPermissions.Where(p => p.UserId == myId && p.CollectionId == null));
            neighbour.SaveChanges();
        }

        db.Permissions.SetMask(myId, null, (int)PermAction.ViewCollection);   // не должно бросить

        using (var check = new AppDbContext(db.Path))
        {
            // Админ поставил галку — маска записалась заново (одной строкой, без дублей).
            var row = Assert.Single(check.DbPermissions
                .Where(p => p.UserId == myId && p.CollectionId == null).ToList());
            Assert.Equal((int)PermAction.ViewCollection, row.ActionsMask);
        }

        // ── Блок В: меня самого удалили из базы, а я правлю профиль ─────────────
        using (var neighbour = new AppDbContext(db.Path))
        {
            neighbour.DbUsers.Remove(neighbour.DbUsers.First(u => u.UserKey == CurrentUser.Key));
            neighbour.SaveChanges();
        }

        var original = CurrentUser.LastName;
        try
        {
            CurrentUser.LastName = "Тестов";
            db.Permissions.SyncDisplayName();   // не должно бросить и не должно ничего писать
        }
        finally { CurrentUser.LastName = original; }

        using (var check = new AppDbContext(db.Path))
        {
            Assert.False(check.DbUsers.Any(u => u.UserKey == CurrentUser.Key));
            Assert.Equal(0, check.ChangeLog.Count(e => e.Action == "user.rename"));
        }

        // Итог для человека: база просто становится недоступной, приложение живо.
        db.Permissions.Reload(notify: false);
        Assert.False(db.Permissions.AccessGranted);
    }

    // ── 6. След админских действий над доступом ───────────────────────────────

    /// <summary>
    /// Каждое действие над доступом уходит в change_log с TargetType=="user": по этому
    /// признаку (и только по нему) поллер соседа понимает, что надо перечитать права
    /// и состав пользователей. Он же — запись в «Истории изменений»: кто кому выдал/отозвал
    /// допуск и права. Имя в TargetName — ЗАТРОНУТОГО пользователя (иначе в истории
    /// невозможно понять, над кем действие), UserKey — того, кто действие совершил.
    /// </summary>
    [Fact]
    public void Access_actions_are_written_to_change_log()
    {
        using var db = new TempDatabase();
        var id = JoinUser(db.Path, "ivanov@pc2", "Иванов Иван");

        db.Permissions.SetApproved(id, true);
        db.Permissions.SetApproved(id, false);
        db.Permissions.SetMask(id, null, (int)PermAction.ViewCollection);

        string oldName;
        using (var before = new AppDbContext(db.Path))
            oldName = before.DbUsers.Single(u => u.UserKey == CurrentUser.Key).DisplayName;

        var original = CurrentUser.LastName;
        string newName;
        try
        {
            CurrentUser.LastName = "Тестов";
            newName = CurrentUser.DisplayName;
            db.Permissions.SyncDisplayName();
        }
        finally { CurrentUser.LastName = original; }

        using var check = new AppDbContext(db.Path);
        var userRows = check.ChangeLog.Where(e => e.Action.StartsWith("user.")).ToList();

        var approve = Assert.Single(userRows.Where(e => e.Action == "user.approve"));
        Assert.Equal("Иванов Иван", approve.TargetName);

        var revoke = Assert.Single(userRows.Where(e => e.Action == "user.revoke"));
        Assert.Equal("Иванов Иван", revoke.TargetName);

        var rights = Assert.Single(userRows.Where(e => e.Action == "user.rights"));
        Assert.Equal("Иванов Иван", rights.TargetName);

        var rename = Assert.Single(userRows.Where(e => e.Action == "user.rename"));
        Assert.Equal(newName, rename.TargetName);
        Assert.NotNull(rename.Detail);
        Assert.Contains(oldName, rename.Detail);   // «было → стало» читается в истории целиком
        Assert.Contains(newName, rename.Detail);

        foreach (var row in new[] { approve, revoke, rights, rename })
        {
            Assert.Equal("user", row.TargetType);     // признак «перечитай права» для соседа
            Assert.Equal(CurrentUser.Key, row.UserKey);
        }
    }

    // ── 7. Событие Changed — живое обновление внутри процесса ─────────────────

    /// <summary>
    /// PermissionService.Changed — то, на что подписан MainWindow (HookPermissionEvents →
    /// OnDbUsersChanged): по нему перестраиваются доступность кнопок, панель администратора
    /// и опции полей «пользователь». Событие обязано подниматься на КАЖДОМ изменении доступа
    /// и молчать там, где состояние лишь перечитано «для себя» (Reload(notify:false)) или где
    /// идёт монтирование базы — иначе UI перестраивался бы на каждом старте и на каждом тике
    /// фонового опроса. Считаем дельтой, чтобы падение указывало на конкретную операцию.
    /// </summary>
    [Fact]
    public void Changed_is_raised_on_access_changes_and_stays_silent_on_mount()
    {
        using var db = new TempDatabase();
        var id = JoinUser(db.Path, "ivanov@pc3", "Иванов Иван");

        var fires = 0;
        db.Permissions.Changed += (_, _) => fires++;

        void Raises(string what, Action act)
        {
            var before = fires;
            act();
            Assert.True(fires > before, $"{what}: UI не узнает об изменении доступа");
        }

        void Silent(string what, Action act)
        {
            var before = fires;
            act();
            Assert.True(fires == before, $"{what}: лишняя перестройка UI");
        }

        Raises("выдача допуска",        () => db.Permissions.SetApproved(id, true));
        Raises("правка прав",           () => db.Permissions.SetMask(id, null, (int)PermAction.All));
        Raises("срок хранения журнала", () => db.Permissions.SetChangeLogRetention(true, 3));
        Raises("установка пароля",      () => db.Permissions.SetPassword("пароль-1"));
        Raises("снятие пароля",         () => db.Permissions.RemovePassword());
        Raises("явный Reload",          () => db.Permissions.Reload());

        Silent("Reload(notify:false)",  () => db.Permissions.Reload(notify: false));
        Silent("монтирование базы",     () => db.Permissions.EnsureCurrentUserRegistered());

        // Вход паролем — тоже изменение доступа: замок в UI обязан сняться сам.
        db.Permissions.SetPassword("пароль-2");
        Demote(db, admin: false, approved: false);
        fires = 0;
        Raises("вход паролем", () => Assert.True(db.Permissions.TryUnlock("пароль-2")));
    }

    // ── 8. Политика базы (пароль, автоочистка) — общая для всех машин ─────────

    /// <summary>
    /// Пароль и срок хранения журнала — это политика САМОЙ базы (db_meta), она путешествует
    /// с файлом: «их видят все машины, открывающие файл». Живой сигнал для них не предусмотрен
    /// (журнал разносит только user.*-события допуска и прав, автоочистка применяется при
    /// монтировании) — значит, соседу обязано хватать одного Reload, чтобы увидеть текущую
    /// политику. Ломается это ровно там же, где допуск: общий контекст живёт весь сеанс, и
    /// без AsNoTracking сосед до перезапуска считал бы, что пароля нет (замок предлагал бы
    /// «ждите администратора» вместо ввода пароля) и чистил бы журнал по старому сроку.
    /// </summary>
    [Fact]
    public void Database_policy_from_db_meta_is_read_fresh_by_neighbours()
    {
        using var db = new TempDatabase();

        // Сосед — второе подключение к тому же файлу со своим долгоживущим контекстом.
        using var nctx = new AppDbContext(db.Path);
        var neighbour = new PermissionService(nctx);
        neighbour.Reload(notify: false);
        Assert.False(neighbour.HasPassword);
        Assert.False(neighbour.RetentionEnabled);
        Assert.Equal(PermissionService.DefaultRetentionMonths, neighbour.RetentionMonths);

        // Администратор поставил пароль базы и включил автоочистку.
        db.Permissions.SetPassword("секрет");
        db.Permissions.SetChangeLogRetention(true, 6);

        neighbour.Reload(notify: false);
        Assert.True(neighbour.HasPassword);
        Assert.True(neighbour.RetentionEnabled);
        Assert.Equal(6, neighbour.RetentionMonths);

        // Пароль верен для соседа так же, как для админа: хеш лежит в файле, а не в машине.
        Assert.False(neighbour.TryUnlock("не тот пароль"));
        Assert.True(neighbour.TryUnlock("секрет"));

        // Снятие пароля и отмена автоочистки доезжают тем же одним Reload.
        db.Permissions.RemovePassword();
        db.Permissions.SetChangeLogRetention(false, 6);

        neighbour.Reload(notify: false);
        Assert.False(neighbour.HasPassword);
        Assert.False(neighbour.RetentionEnabled);
        Assert.False(neighbour.TryUnlock("секрет"));   // хеша нет — открывать нечем

        using var check = new AppDbContext(db.Path);
        Assert.False(check.DbMeta.Any(m => m.Key.StartsWith("password.")));
    }
}
