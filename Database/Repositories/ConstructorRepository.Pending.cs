using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SmartBase.Database.Entities;
using SmartBase.Utils;

namespace SmartBase.Database.Repositories;

// ── Маркеры незавершенных массовых копий (дубль, перенос между базами) ─────────────────
//
// Пакетная копия коммитит по частям, и выход из программы посреди нее оставляет в базе копию с
// половиной данных. От этого — строка db_meta на ОПЕРАЦИЮ: ключ «pending.<вид>.<номер>», значение —
// что копируется (коллекция или ветка группы), кто владелец (компьютер, учетка, процесс и время
// его старта) и когда владелец последний раз отметился. Монтирование базы убирает только
// БРОШЕННЫЕ копии: процесс владельца на этом компьютере умер, а отметка чужого компьютера давно не
// обновлялась.
//
// Прежде маркер был один на базу и без владельца: коллега, открывший общую базу посреди чужого
// дубля, убирал копию как брошенную; две операции затирали маркер друг друга; а уборка, совпавшая
// с концом переноса, удаляла копию, пока владелец удалял исходник, — данные пропадали в обеих
// базах. Поэтому и владелец, и уборка меняют маркер только сравнением с прочитанным значением
// («сравнить и записать», UPDATE/DELETE … WHERE Value = прочитанное): отметка владельца в каждом
// пакете и снятие маркера — в транзакции последней записи копии; не совпало — маркер забрала
// уборка, владелец прерывается и исходник не трогает. Уборка сначала забирает маркер так же и
// только потом удаляет копию. Ошибка в оценке «брошена ли» поэтому стоит только неудачной
// операции, а не данных.
public partial class ConstructorRepository
{
    internal const string PendingPrefix = "pending.";

    /// <summary>Виды операций в ключе маркера.</summary>
    internal static class PendingKind
    {
        public const string DuplicateCollection = "duplicate.collection";
        public const string DuplicateGroup      = "duplicate.group";
        public const string MoveCollection      = "move.collection";
        public const string MoveGroup           = "move.group";

        // Маркер ИСТОЧНИКА переноса: копия в приемнике готова, исходник удаляется. Уборка его
        // доудаляет — иначе рядом с полной копией осталась бы часть исходных записей.
        public const string MovedCollection     = "moved.collection";
        public const string MovedGroup          = "moved.group";
    }

    // Отметка чужого компьютера старше этого — копия брошена. С запасом на расхождение часов
    // компьютеров и на самую долгую транзакцию между отметками (первая вставка всех записей).
    internal static readonly TimeSpan PendingStaleAfter = TimeSpan.FromMinutes(30);

    // Копия одной транзакцией (дубль на PostgreSQL) отметиться посреди работы не может —
    // ее маркер считается брошенным только после этого срока.
    internal static readonly TimeSpan PendingStaleAfterSingleTx = TimeSpan.FromHours(6);

    // Свой компьютер, процесс владельца не найден: маркер свежее этого не трогаем — тезка-компьютер
    // (имя обрезано до 15 знаков, клон виртуальной машины) мог бы оказаться живым владельцем.
    internal static readonly TimeSpan PendingDeadOwnerGrace = TimeSpan.FromMinutes(2);

    // Операции ЭТОГО процесса, которые сейчас идут: повторное монтирование той же базы (монитор
    // доступности после потери связи) не должно убрать копию, которая еще заполняется.
    private static readonly ConcurrentDictionary<string, byte> ActivePending = new(StringComparer.Ordinal);

    internal static readonly string ProcessStart =
        Process.GetCurrentProcess().StartTime.ToUniversalTime().ToString("o");

    /// <summary>Маркер операции у ее владельца: ключ и последнее записанное значение.</summary>
    internal sealed class PendingCopy
    {
        public required string Key   { get; init; }
        public required int    Target { get; init; }
        public bool   SingleTx { get; init; }
        public string Value { get; set; } = "";
    }

    // Значение маркера (JSON). Поля короткие: строка живет в db_meta общей базы.
    internal sealed record PendingValue(
        int V, string Kind, int Target, string Machine, string User, int Pid, string PStart,
        string Started, string Beat, long Seq, bool Single = false, string? ClaimedBy = null);

    private static readonly JsonSerializerOptions PendingJson = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    internal static string SerializePending(PendingValue v) => JsonSerializer.Serialize(v, PendingJson);

    /// <summary>Поставить маркер операции — в транзакции, которая создает первое из копируемого
    /// (шелл коллекции, корень ветки). Без транзакции маркер и копия расходились бы при сбое.</summary>
    internal static PendingCopy BeginPending(AppDbContext ctx, string kind, int target, bool singleTx = false)
    {
        var p = new PendingCopy
        {
            Key = $"{PendingPrefix}{kind}.{Guid.NewGuid():N}", Target = target, SingleTx = singleTx,
        };
        var now = DateTime.UtcNow.ToString("o");
        p.Value = JsonSerializer.Serialize(new PendingValue(1, kind, target, Environment.MachineName,
            CurrentUser.OwnKey, Environment.ProcessId, ProcessStart, now, now, 0, singleTx), PendingJson);
        ctx.DbMeta.Add(new DbMetaEntity { Key = p.Key, Value = p.Value });
        ctx.SaveChanges();
        ctx.ChangeTracker.Clear();
        ActivePending[p.Key] = 0;
        return p;
    }

    /// <summary>Отметка владельца — в транзакции каждого пакета. Маркер уже не тот (его забрала уборка
    /// соседа) — <see cref="PendingLostException"/>: операция прерывается, исходник не трогается.</summary>
    internal static void BeatPending(AppDbContext ctx, PendingCopy p)
    {
        var old = p.Value;
        var v = JsonSerializer.Deserialize<PendingValue>(old, PendingJson)!;
        var next = JsonSerializer.Serialize(v with { Beat = DateTime.UtcNow.ToString("o"), Seq = v.Seq + 1 }, PendingJson);
        var n = ctx.DbMeta.Where(m => m.Key == p.Key && m.Value == old)
                          .ExecuteUpdate(s => s.SetProperty(m => m.Value, next));
        if (n != 1) throw new PendingLostException();
        p.Value = next;
    }

    /// <summary>Снять маркер — в транзакции последней записи копии; дальше копия постоянная.</summary>
    internal static void FinishPending(AppDbContext ctx, PendingCopy p)
    {
        var old = p.Value;
        var n = ctx.DbMeta.Where(m => m.Key == p.Key && m.Value == old).ExecuteDelete();
        if (n != 1) throw new PendingLostException();
        ActivePending.TryRemove(p.Key, out _);
    }

    /// <summary>Сбой операции: маркер снимается, если он еще наш (копию к этому моменту убрал сам
    /// владелец); не вышло — его доберет уборка. Никогда не бросает.</summary>
    internal static void DropPending(AppDbContext ctx, PendingCopy? p)
    {
        if (p is null) return;
        ActivePending.TryRemove(p.Key, out _);
        try
        {
            var old = p.Value;
            ctx.DbMeta.Where(m => m.Key == p.Key && m.Value == old).ExecuteDelete();
        }
        catch { /* маркер остался — уберет монтирование */ }
    }

    /// <summary>Операция потеряла маркер: его забрала уборка другого подключения (сочла брошенной).</summary>
    internal sealed class PendingLostException()
        : InvalidOperationException("Копию сочли брошенной и убрали, пока она заполнялась: операция прервана, исходные данные не тронуты.");

    // ── Уборка при монтировании ───────────────────────────────────────────────

    // Брошена ли операция по маркеру (см. шапку). Свой процесс — по реестру идущих операций, свой
    // компьютер — по процессу владельца, чужой — по давности отметки.
    internal static bool IsAbandoned(PendingValue v, string key, DateTime nowUtc)
    {
        var beat  = DateTime.TryParse(v.Beat, null, System.Globalization.DateTimeStyles.RoundtripKind, out var b) ? b.ToUniversalTime() : DateTime.MinValue;
        var age   = nowUtc - beat;
        var stale = age > (v.Single ? PendingStaleAfterSingleTx : PendingStaleAfter);
        if (!string.Equals(v.Machine, Environment.MachineName, StringComparison.OrdinalIgnoreCase)) return stale;
        if (v.Pid == Environment.ProcessId && v.PStart == ProcessStart) return !ActivePending.ContainsKey(key);
        return OwnerProcessGone(v) ? age > PendingDeadOwnerGrace : stale;
    }

    private static bool OwnerProcessGone(PendingValue v)
    {
        try
        {
            using var proc = Process.GetProcessById(v.Pid);
            if (!DateTime.TryParse(v.PStart, null, System.Globalization.DateTimeStyles.RoundtripKind, out var started)) return false;
            return Math.Abs((proc.StartTime.ToUniversalTime() - started.ToUniversalTime()).TotalSeconds) > 2;   // номер отдан другому процессу
        }
        catch (ArgumentException) { return true; }    // такого процесса нет
        catch { return false; }                       // нет доступа к чужому процессу — считаем живым
    }

    // Убрать брошенные операции нового вида: забрать маркер (сравнить и записать), удалить копию,
    // снять забранный маркер.
    private void SweepPendingCopies()
    {
        var rows = db.DbMeta.AsNoTracking().Where(m => m.Key.StartsWith(PendingPrefix)).ToList();
        foreach (var row in rows)
        {
            PendingValue? v;
            try { v = JsonSerializer.Deserialize<PendingValue>(row.Value, PendingJson); }
            catch (JsonException) { v = null; }
            if (v is null || v.ClaimedBy is null && !IsAbandoned(v, row.Key, DateTime.UtcNow))
                continue;
            // Забранный другим уборщиком маркер, чей уборщик сам пропал (отметка устарела), берем себе.
            if (v.ClaimedBy is not null && DateTime.UtcNow - (DateTime.TryParse(v.Beat, null,
                    System.Globalization.DateTimeStyles.RoundtripKind, out var cb) ? cb.ToUniversalTime() : DateTime.MinValue) <= PendingStaleAfter)
                continue;

            var claim = JsonSerializer.Serialize(v with
            {
                ClaimedBy = $"{CurrentUser.OwnKey}#{Environment.ProcessId}", Beat = DateTime.UtcNow.ToString("o"),
            }, PendingJson);
            var observed = row.Value;
            if (db.DbMeta.Where(m => m.Key == row.Key && m.Value == observed)
                         .ExecuteUpdate(s => s.SetProperty(m => m.Value, claim)) != 1)
                continue;   // владелец отметился или маркер забрал другой уборщик

            // Маркер источника переноса (Moved*) — копия в приемнике полная, доудаляется исходник.
            Serilog.Log.Warning(v.Kind is PendingKind.MovedCollection or PendingKind.MovedGroup
                    ? "Прерванное удаление перенесенного в {Db:l} ({Kind:l} {Target}, начал {Owner:l}, последняя отметка {Beat:l}) — остаток удаляется"
                    : "Незавершенная копия в {Db:l} ({Kind:l} {Target}, начал {Owner:l}, последняя отметка {Beat:l}) — брошена, удаляется",
                db.DisplayPath, v.Kind, v.Target, $"{v.User} ({v.Machine})", v.Beat);
            if (v.Kind is PendingKind.DuplicateGroup or PendingKind.MoveGroup or PendingKind.MovedGroup)
                DeleteGroupData(v.Target, log: false, enforceRestrict: false);
            else
                DeleteCollectionData(v.Target, log: false, enforceRestrict: false);
            db.DbMeta.Where(m => m.Key == row.Key && m.Value == claim).ExecuteDelete();
            Serilog.Log.Information("Маркер {Kind:l} {Target} в {Db:l}: удалено", v.Kind, v.Target, db.DisplayPath);
        }
    }
}
