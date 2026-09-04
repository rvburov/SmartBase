using Serilog;
using SmartBase.Logging;

namespace SmartBase.Core.Models;

/// <summary>
/// След в журнале диагностики о нечитаемом <c>ConfigJson</c> свойства. Разборщики настроек
/// (<see cref="RelationConfig"/>, <see cref="NumberFormat"/>, <see cref="FileAutoRule"/>) при
/// сломанном JSON тихо подставляют значение по умолчанию — колонка при этом ведет себя
/// странно (связь никуда не ведет, число без формата, файл не привязывается), и без записи
/// понять причину нечем. Разбор идет на каждую отрисовку ячейки, поэтому запись одна на
/// отличающийся конфиг за сеанс (<see cref="Diag.Once"/>).
/// </summary>
public static class ConfigJsonLog
{
    private const int MaxJsonInLog = 200;

    public static void Broken(string kind, string? configJson, Exception ex)
    {
        var json = configJson ?? "";
        if (!Diag.Once($"config:{kind}:{json}")) return;
        var shown = json.Length > MaxJsonInLog ? json[..MaxJsonInLog] + "…" : json;
        Log.Warning(ex, "Настройка свойства «{Kind:l}» не разобрана — действует значение по умолчанию: {Json:l}", kind, shown);
    }
}
