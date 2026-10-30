using SmartBase.Integrations;

namespace SmartBase.Database;

public static partial class ScenarioRunner
{
    private static string? DownloadTrouble(ScenarioStep step, IntegrationsRegistry registry)
    {
        if (step.Problems() is { Count: > 0 } problems) return string.Join("; ", problems);
        if (registry.FindRequest(step.RequestId) is not { } request)
            return $"запрос получения файла не найден (№{step.RequestId})";
        if (registry.Find(request.ConnectionId) is null)
            return $"у запроса «{request.Name}» не найдено соединение";
        return request.Method != RequestMethod.Get || request.Paging != RequestPaging.None
            || !string.IsNullOrWhiteSpace(request.Body)
                ? "Для получения файла нужен запрос GET без тела и разбивки на страницы"
                : null;
    }

    private static async Task<StepOutcome> DownloadStepAsync(ScenarioStep step, IntegrationsRegistry registry,
        StepTable? incoming, string scenario, string mark, DateTimeOffset now, CancellationToken ct)
    {
        if (DownloadTrouble(step, registry) is { } problem) return StepOutcome.Bad(problem);
        var table = incoming ?? StepTable.Empty;
        if (table.Rows.Count != 1)
            return StepOutcome.Bad("Получение файла требует ровно одну строку события");
        var record = table.Rows[0];
        var name = ScenarioValues.Apply(step.Text, record, out var missing).Trim();
        // У обычного текста и подтверждения нет имени вложения. Такой шаг пропускается,
        // а исходное событие идет дальше к действию по прежнему пути.
        if (missing.Count > 0 || name.Length == 0) return new StepOutcome(true, "");
        if (name.Length > ScenarioStep.MaxTextLength || name is "." or ".."
            || name.Any(c => char.IsControl(c) || "/\\<>:\"|?*".Contains(c)))
            return StepOutcome.Bad("Имя получаемого файла должно быть именем без пути и недопустимых символов");

        var request = registry.FindRequest(step.RequestId)!;
        var connection = registry.Find(request.ConnectionId)!;
        var answer = await RequestLauncher.DownloadAsync(registry, connection, request, record, scenario, mark, now, ct);
        if (!answer.Ok || answer.Truncated || answer.Content is null)
        {
            var why = answer.Truncated ? "файл больше технического ограничения 32 МБ"
                : answer.Explanation.Length > 0 ? answer.Explanation : $"HTTP {answer.StatusCode}";
            return StepOutcome.Bad($"Не удалось получить файл: {why}");
        }
        return new StepOutcome(true, $"Файл «{name}» получен: {answer.Bytes:N0} байт",
            File: answer.Content, FileName: name);
    }
}
