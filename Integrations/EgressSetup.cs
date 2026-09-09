using Serilog;

namespace SmartBase.Integrations;

/// <summary>
/// Подключение политики организации на старте: собрать источники, разобрать, отдать шлюзу и
/// записать в журнал, что откуда взялось. Одна строка в <c>App.axaml.cs</c> — по образцу
/// <c>Autostart.Sync</c> и <c>CalendarSettings.Init</c>.
///
/// Зовется сразу после <c>AppSettings.AttachStore</c> и до всего остального: политика читается
/// из <c>app.db</c> (снимок последней известной), а шлюз обязан быть заряжен раньше, чем
/// появится первый желающий выйти наружу. До этого вызова дверь закрыта — состояние по
/// умолчанию у <see cref="EgressGate"/> самое строгое, поэтому даже сбой загрузки не открывает
/// ничего.
/// </summary>
public static class EgressSetup
{
    /// <summary>
    /// Итог последнего чтения — для экрана «Безопасность». Не запас на будущее: экран обязан
    /// показывать то, чем шлюз заряжен СЕЙЧАС, а не то, что прочитается при повторном обходе
    /// источников. Сетевая папка могла с тех пор пропасть, и второе чтение дало бы другую
    /// картину, чем действующие ограничения.
    /// </summary>
    public static PolicyLoadResult Last { get; private set; } = new(EgressPolicySet.Closed, []);

    /// <summary>Прочитать политику и отдать ее шлюзу. Возвращает отчет для экрана «Безопасность».</summary>
    public static PolicyLoadResult Init()
    {
        PolicyLoadResult result;
        try
        {
            result = PolicyLoader.Load(PolicySources.Collect());
        }
        catch (Exception ex)
        {
            // Сюда попадаем только на неожиданном сбое: ожидаемые беды (нет папки, нет прав,
            // битый файл) уже разобраны источниками и загрузчиком. Дверь при этом остается
            // закрытой — шлюзу ничего не подключено.
            Log.Error(ex, "Политика интеграций не прочиталась: исходящие обращения запрещены");
            EgressGate.Close();
            return Last = new PolicyLoadResult(EgressPolicySet.Closed, []);
        }

        EgressGate.AttachPolicy(result.Set);
        Report(result);
        return Last = result;
    }

    private static void Report(PolicyLoadResult result)
    {
        foreach (var read in result.Reads)
        {
            if (read.Accepted)
                Log.Information("Политика интеграций принята: {Source:l} — {Note:l}", read.Source, read.Note);
            else
                Log.Warning("Политика интеграций отклонена: {Source:l} — {Note:l}", read.Source, read.Note);
        }

        if (!result.Set.Any)
        {
            Log.Information("Политика интеграций не найдена: исходящие обращения запрещены");
            return;
        }

        Log.Information(
            "Контур интеграций: режим {Mode}, источников {Sources}, действует до {Until:l}",
            result.Set.Mode,
            result.Reads.Count,
            result.Set.ValidUntil?.ToString("dd.MM.yyyy") ?? "без срока");
    }
}
