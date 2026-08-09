using System.Text;
using System.Threading.Tasks;
using Avalonia.Controls;
using SmartBase.Database.Repositories;
using SmartBase.Utils;

namespace SmartBase.Controls.Dialogs;

/// <summary>
/// Подтверждение подготовки коллекции к разделу. Создание события или задачи молча доводило
/// коллекцию до нужного набора столбцов: окно закрывалось, а в чужой таблице появлялись четыре
/// новых столбца — узнать об этом можно было, только вернувшись в конструктор. Теперь UI сначала
/// считает план (<see cref="ConstructorRepository.PlanCalendarSetup"/> /
/// <see cref="ConstructorRepository.PlanTaskSetup"/>) и показывает, что именно изменится.
///
/// Спрашиваем ТОЛЬКО когда коллекция реально изменится: набор уже размечен — окна нет, запись
/// создаётся как прежде (иначе окно всплывало бы на каждое событие). «Отмена» = запись не
/// создаётся и не переносится, коллекция остаётся нетронутой.
/// </summary>
public static class SectionSetupConfirm
{
    /// <summary>Подготовка к календарю: поле-дата (если источника нет) и «Описание».</summary>
    public static Task<bool> ForCalendar(Window owner, ConstructorRepository repo, int? collectionId,
                                         int preferredDateFieldId = 0, bool moving = false) =>
        collectionId is int cid
            ? Ask(owner, repo, cid, repo.PlanCalendarSetup(cid, preferredDateFieldId),
                  FieldRoleText.SectionCalendar, moving)
            // Коллекции ещё нет (запись без цели — репозиторий заведёт её сам): спрашивать
            // про столбцы новой пустой коллекции не о чем.
            : Task.FromResult(true);

    /// <summary>
    /// Подготовка к задачам: исполнитель плюс «Назначил», «Статус», «Дата», «Описание».
    /// <paramref name="adoptExec"/> — можно ли отдать роль исполнителя существующему user-полю
    /// (перенос задачи в постороннюю коллекцию так не делает, см. <c>PlanTaskSetup</c>).
    /// </summary>
    public static Task<bool> ForTasks(Window owner, ConstructorRepository repo, int? collectionId,
                                      bool moving = false, bool adoptExec = true) =>
        collectionId is int cid
            ? Ask(owner, repo, cid, repo.PlanTaskSetup(cid, adoptExec), FieldRoleText.SectionTasks, moving)
            : Task.FromResult(true);

    private static async Task<bool> Ask(Window owner, ConstructorRepository repo, int collectionId,
                                        RoleAdoptionPlan plan, string section, bool moving)
    {
        if (!plan.ChangesCollection) return true;

        var verb = moving ? "перенесена" : "создана";
        var text = new StringBuilder()
            .Append($"Коллекция «{repo.CollectionName(collectionId)}» ещё не подготовлена ")
            .Append($"к разделу «{section}».");

        if (plan.FieldsToCreateIfAdopted.Count > 0)
            text.Append("\n\nБудут созданы столбцы:\n")
                .AppendJoin("\n", plan.FieldsToCreateIfAdopted.Select(n => $"    • «{n}»"));

        // Одноимённые столбцы не дублируем, а подтягиваем: данные из них поедут в раздел —
        // ровно та развилка, которую окно тумблера предлагает при включении раздела.
        if (plan.Candidates.Count > 0)
            text.Append("\n\nСуществующие столбцы станут полями раздела, и данные из них будут подтягиваться туда:\n")
                .AppendJoin("\n", plan.Candidates.Select(c => $"    • «{c.FieldName}» — {FieldRoleText.Title(c.Role)}"));

        text.Append($"\n\n«Подтвердить» — коллекция получит эти столбцы, и запись будет {verb}.")
            .Append($"\n«Отмена» — запись не будет {verb}, коллекция останется прежней.");

        return await new ConfirmDialog(text.ToString(), "Подтвердить", "Отмена").ShowDialog<bool>(owner);
    }
}
