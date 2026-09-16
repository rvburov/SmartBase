using Microsoft.EntityFrameworkCore;
using Serilog;
using SmartBase.Core.Models;
using SmartBase.Database.Entities;

namespace SmartBase.Database.Repositories;

/// <summary>
/// Что разрешено пользователю-роботу в этой базе.
/// </summary>
/// <param name="Known">Строка робота в базе есть. Нет — токен выдан к другой базе либо строку
/// удалили; в обоих случаях писать нечем.</param>
/// <param name="Approved">Администратор базы допустил робота. Недопущенный ждет так же, как
/// человек, и это не досадная мелочь: допуск — единственное место, где живой человек соглашается
/// пустить стороннюю программу в свою базу.</param>
/// <param name="Mask">Маска действий уровня базы.</param>
public sealed record RobotAccess(bool Known, bool Approved, int Mask)
{
    /// <summary>Робота нет вовсе.</summary>
    public static readonly RobotAccess Unknown = new(false, false, 0);

    /// <summary>Разрешено ли действие.</summary>
    public bool Can(PermAction action) =>
        Known && Approved && (Mask & (int)action) == (int)action;

    /// <summary>
    /// Почему нельзя; <c>null</c> — можно. Причина называется отправителю как есть: чужой
    /// программист, получивший «403», без слов будет считать, что сломана программа.
    /// </summary>
    public string? Trouble(PermAction action)
    {
        if (!Known)    return "робот этого токена в базе не заведен";
        if (!Approved) return "робот этого токена ждет допуска у администратора базы";

        return Can(action)
            ? null
            : $"роботу не выдано право «{PermActionLabels.Title(action)}»";
    }
}

/// <summary>
/// Пользователи-роботы — вторая половина правила «токен = пользователь» (docs/ROADMAP-API.md,
/// раздел 7 и 10.12).
///
/// Отдельной системы прав для входящего канала нет и не будет: робот живет в тех же
/// <c>db_users</c> и <c>db_permissions</c>, что и человек. Из этого следует все, ради чего так
/// и сделано — запись робота честно попадает в историю изменений с его именем, администратор
/// отзывает допуск той же панелью, а база, в которую робота не пустили, остается недоступной и
/// по сети.
///
/// <b>По умолчанию роботу выдается только запись.</b> Ни просмотра коллекций, ни чего-либо
/// другого: класть данные внутрь можно, вычитывать базу наружу — нет. Расширить маску может
/// администратор базы, обычной панелью допуска, — но не программа и не тот, кто выдает токен.
/// </summary>
public sealed partial class PermissionService
{
    /// <summary>
    /// Что роботу выдается при создании токена: завести запись и поправить уже заведенную.
    /// Просмотра здесь нет намеренно — см. описание класса.
    /// </summary>
    public const PermAction RobotDefaultMask = PermAction.CreateRecord | PermAction.EditRecord;

    /// <summary>
    /// Завести робота в этой базе или обновить его имя. Зовется в момент выдачи токена — то есть
    /// при живом человеке, за его столом, и потому без всяких гонок.
    ///
    /// Допуск робот получает СРАЗУ, если выдающий токен сам администратор базы: спрашивать у себя
    /// же разрешение, которое ты только что выдал, — обряд без смысла. Не администратор заводит
    /// робота ждущим допуска, как и человека.
    /// </summary>
    public void EnsureRobot(string key, string name, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(key)) return;

        var stamp = now.UtcDateTime.ToString("o");
        var robot = _db.DbUsers.FirstOrDefault(u => u.UserKey == key);

        if (robot is null)
        {
            robot = new DbUserEntity
            {
                UserKey     = key,
                DisplayName = name,
                IsAdmin     = false,   // роботу администратором быть незачем и опасно
                IsApproved  = IsAdmin,
                CreatedAt   = stamp,
                LastSeenAt  = stamp,
            };

            _db.DbUsers.Add(robot);
            LogUser("user.join", name);
        }
        else if (robot.DisplayName != name)
        {
            LogUser("user.rename", name, Utils.ChangeDetail.Rename(robot.DisplayName, name));
            robot.DisplayName = name;
        }

        _db.SaveChanges();

        // Маска заводится ОТДЕЛЬНОЙ строкой, а не оставляется пустой: у пользователя без строки
        // прав маска считается полной, и «робот по умолчанию только пишет» превратилось бы в
        // «робот по умолчанию может все».
        var rights = _db.DbPermissions
            .FirstOrDefault(p => p.UserId == robot.Id && p.CollectionId == null);

        if (rights is null)
        {
            _db.DbPermissions.Add(new DbPermissionEntity
            {
                UserId       = robot.Id,
                CollectionId = null,
                ActionsMask  = (int)RobotDefaultMask,
            });

            _db.SaveChanges();
        }
    }

    /// <summary>
    /// Права робота — читаются СВЕЖИМИ и на каждое обращение. Кеш тут был бы дырой: администратор
    /// отзывает допуск роботу ровно тогда, когда тот начал делать не то, и ждать перезапуска
    /// программы в этот момент никто не станет.
    /// </summary>
    public RobotAccess Robot(string key)
    {
        if (string.IsNullOrWhiteSpace(key)) return RobotAccess.Unknown;

        var robot = _db.DbUsers.AsNoTracking().FirstOrDefault(u => u.UserKey == key);
        if (robot is null) return RobotAccess.Unknown;

        // Администратор-робот маски не имеет — у него всегда все. Такого мы не заводим, но
        // строку могли поправить руками, и молча считать ее обычной нельзя.
        if (robot.IsAdmin) return new RobotAccess(true, true, (int)PermAction.All);

        var mask = _db.DbPermissions.AsNoTracking()
            .Where(p => p.UserId == robot.Id && p.CollectionId == null)
            .Select(p => (int?)p.ActionsMask)
            .FirstOrDefault();

        return new RobotAccess(true, robot.IsApproved, mask ?? (int)PermAction.All);
    }

    /// <summary>Отметить, что робот приходил, — панель администратора показывает это колонкой.</summary>
    public void MarkRobotSeen(string key, DateTimeOffset at)
    {
        var robot = _db.DbUsers.FirstOrDefault(u => u.UserKey == key);
        if (robot is null) return;

        robot.LastSeenAt = at.UtcDateTime.ToString("o");
        _db.SaveChanges();
    }

    // ── То же на отдельном подключении ────────────────────────────────────────
    //
    // Обращения обрабатываются в фоне, а общий контекст базы принадлежит интерфейсу: второе
    // обращение к нему кончается «A second operation was started on this context instance».
    // Прием ходит к правам только этим путем.

    /// <inheritdoc cref="Robot"/>
    public static RobotAccess RobotIsolated(string dbPath, string key)
    {
        try
        {
            using var ctx = new AppDbContext(dbPath);
            return new PermissionService(ctx).Robot(key);
        }
        catch (Exception failure)
        {
            // База сейчас недоступна (пропал сетевой диск, файл занят) — это «не знаем», а не
            // «можно»: пускать робота, не сумев прочитать его права, нельзя ни при каких бедах.
            Log.Warning(failure, "Прием по сети: права робота {Key:l} не прочитались", key);
            return RobotAccess.Unknown;
        }
    }

    /// <inheritdoc cref="MarkRobotSeen"/>
    public static void MarkRobotSeenIsolated(string dbPath, string key, DateTimeOffset at)
    {
        try
        {
            using var ctx = new AppDbContext(dbPath);
            new PermissionService(ctx).MarkRobotSeen(key, at);
        }
        catch (Exception failure)
        {
            // Отметка «приходил» — удобство панели администратора, а не часть обмена: из-за нее
            // принятое сообщение отклонять незачем.
            Log.Warning(failure, "Прием по сети: отметка о роботе {Key:l} не легла", key);
        }
    }
}
