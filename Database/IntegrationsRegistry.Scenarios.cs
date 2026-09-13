using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SmartBase.Database.Entities;
using SmartBase.Integrations;

namespace SmartBase.Database;

/// <summary>
/// Сценарии в системной app.db — та же кладовая, что у соединений и запросов, и по той же
/// причине: сценарий, лежащий в файле базы данных, сработал бы на каждой машине, где эта база
/// открыта (docs/ROADMAP-API.md, раздел 3).
///
/// Шаги хранятся строкой JSON, как привязка у запроса. Битая строка читается как «шагов нет», а
/// не как исключение: сценарий с пустым списком шагов виден на экране и правится, а падение при
/// открытии настроек из-за одной испорченной строки не оставляет человеку выхода вовсе.
/// </summary>
public sealed partial class IntegrationsRegistry
{
    /// <summary>Все сценарии по имени.</summary>
    public IReadOnlyList<Scenario> Scenarios() =>
        [.. db.IntegrationScenarios.AsNoTracking()
             .OrderBy(s => s.Name)
             .AsEnumerable()
             .Select(ToModel)];

    /// <summary>Сценарий по ключу; <c>null</c> — такого нет.</summary>
    public Scenario? FindScenario(int id)
    {
        var entity = db.IntegrationScenarios.AsNoTracking().FirstOrDefault(s => s.Id == id);
        return entity is null ? null : ToModel(entity);
    }

    /// <summary>
    /// Сценарии, у которых есть шаг с этим запросом. Нужно при удалении запроса: унести с собой
    /// чужой сценарий молча хуже, чем отказать.
    ///
    /// Отбор идет в памяти, а не запросом: шаги лежат строкой JSON, и искать в ней средствами
    /// SQLite значило бы завести зависимость от формата хранения ради десятка строк.
    /// </summary>
    public IReadOnlyList<Scenario> ScenariosUsing(int requestId) =>
        [.. Scenarios().Where(s => s.Steps.Any(
            step => step.Kind == ScenarioStepKind.Request && step.RequestId == requestId))];

    /// <summary>
    /// Создать или обновить сценарий. Возвращает сохраненный — с ключом, если он новый.
    ///
    /// Проверяется форма и существование запросов, но НЕ выполнимость: есть ли у запроса
    /// привязка к коллекции и открыта ли нужная база — вопросы момента запуска. База откроется
    /// завтра, а сценарий должен сохраниться сегодня.
    ///
    /// Сохранение ставит ЯКОРЬ, если сценарий сохраняется включенным (см. <see cref="Anchor"/>):
    /// новый сценарий заводят окном правки, а тумблер в списке трогают уже после — и без якоря
    /// он выглядел бы включенным, не срабатывая ни разу.
    /// </summary>
    public Scenario SaveScenario(Scenario scenario, DateTimeOffset? now = null)
    {
        var problems = scenario.Problems();
        if (problems.Count > 0)
            throw new InvalidOperationException("Сценарий не сохранен: " + string.Join("; ", problems));

        foreach (var step in scenario.Steps)
            if (step.Kind == ScenarioStepKind.Request
                && !db.IntegrationRequests.Any(r => r.Id == step.RequestId))
                throw new InvalidOperationException("Сценарий не сохранен: запрос шага не найден");

        var entity = scenario.Id > 0
            ? db.IntegrationScenarios.FirstOrDefault(s => s.Id == scenario.Id)
            : null;

        if (entity is null)
        {
            entity = new IntegrationScenarioEntity();
            db.IntegrationScenarios.Add(entity);
        }

        entity.Name      = scenario.Name.Trim();
        entity.Enabled   = scenario.Enabled;
        entity.Trigger   = scenario.Trigger.ToString();
        entity.StepsJson = JsonSerializer.Serialize(scenario.Steps);

        Anchor(entity, now ?? DateTimeOffset.Now);

        db.SaveChanges();
        return ToModel(entity);
    }

    /// <summary>
    /// Включить или выключить сценарий, не открывая карточку: тумблер в списке.
    /// </summary>
    public void EnableScenario(int id, bool enabled, DateTimeOffset now)
    {
        var entity = db.IntegrationScenarios.FirstOrDefault(s => s.Id == id);
        if (entity is null) return;

        entity.Enabled = enabled;
        Anchor(entity, now);

        db.SaveChanges();
    }

    /// <summary>
    /// ЯКОРЬ — момент, от которого считается первый срок у включенного сценария. Без него
    /// <see cref="Scenario.IsDue"/> отвечает «не пора» навсегда: пустая отметка о прошлом запуске
    /// означает «еще ни разу», и ближайшего срока от нее не отсчитать.
    ///
    /// Якорь нужен именно на включении, а не при заведении: сценарий «ежедневно в 07:00»,
    /// включенный вечером, иначе сработал бы немедленно — ровно того, чего человек не просил.
    ///
    /// <b>Правило живет здесь, а не у тумблера,</b> потому что включенным сценарий становится
    /// тремя путями: тумблером в списке, окном правки (им заводят новый) и файлом переноса. Пока
    /// якорь ставил один тумблер, сценарий, созданный включенным в окне, не срабатывал НИ РАЗУ и
    /// выглядел при этом исправным — ни строки в журнале, ни пометки «что мешает».
    /// </summary>
    private static void Anchor(IntegrationScenarioEntity entity, DateTimeOffset now)
    {
        if (entity.Enabled && string.IsNullOrEmpty(entity.LastRunAt))
            entity.LastRunAt = now.ToString("o", CultureInfo.InvariantCulture);
    }

    /// <summary>Удалить сценарий. Запросы и соединения остаются: они живут сами по себе.</summary>
    public void DeleteScenario(int id)
    {
        var entity = db.IntegrationScenarios.FirstOrDefault(s => s.Id == id);
        if (entity is null) return;

        db.IntegrationScenarios.Remove(entity);
        db.SaveChanges();
    }

    // ── Отметка о запуске ─────────────────────────────────────────────────────

    /// <summary>
    /// Когда сценарий отработал в прошлый раз; <c>null</c> — еще ни разу, и по расписанию он не
    /// пойдет, пока не проставлен якорь (см. <see cref="Anchor"/>).
    /// </summary>
    public DateTimeOffset? ScenarioLastRun(int id)
    {
        var stored = db.IntegrationScenarios.AsNoTracking()
                       .FirstOrDefault(s => s.Id == id)?.LastRunAt;

        return DateTimeOffset.TryParse(stored, CultureInfo.InvariantCulture,
                                       DateTimeStyles.RoundtripKind, out var moment)
            ? moment
            : null;
    }

    /// <summary>
    /// Отметить запуск сценария. Отмечается ЛЮБОЙ состоявшийся запуск, удачный и нет, — в
    /// отличие от отметки запроса, от которой отсчитывается «отдай изменения с прошлого раза».
    /// Здесь смысл другой: это точка, от которой считается следующий срок. Не отмечай неудачу —
    /// и сломавшийся сценарий пошел бы по кругу на каждом тике таймера.
    /// </summary>
    public void MarkScenarioRun(int id, DateTimeOffset moment)
    {
        var entity = db.IntegrationScenarios.FirstOrDefault(s => s.Id == id);
        if (entity is null) return;

        entity.LastRunAt = moment.ToString("o", CultureInfo.InvariantCulture);
        db.SaveChanges();
    }

    // ── Перевод строки в модель ───────────────────────────────────────────────

    private static Scenario ToModel(IntegrationScenarioEntity entity) =>
        new(entity.Id,
            entity.Name,
            entity.Enabled,
            ScenarioTrigger.Parse(entity.Trigger),
            ParseSteps(entity.StepsJson));

    /// <summary>
    /// Шаги из строки хранения. Мусор — пустой список: сценарий откроется, и человек соберет
    /// шаги заново. Сработать он при этом не сможет — проверка формы не пропустит сценарий без
    /// шагов, и это правильный исход: молча выполнить половину разобравшихся шагов было бы хуже.
    /// </summary>
    private static IReadOnlyList<ScenarioStep> ParseSteps(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];

        try
        {
            return JsonSerializer.Deserialize<List<ScenarioStep>>(json) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }
}
