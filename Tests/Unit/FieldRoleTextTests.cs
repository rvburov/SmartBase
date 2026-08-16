using SmartBase.Core.Models;
using SmartBase.Database.Repositories;
using SmartBase.Utils;

namespace SmartBase.Tests.Unit;

/// <summary>
/// Тексты привязки поля к разделу (Utils/FieldRoleText.cs) — плашка «привязано к разделу» в окне
/// свойства, предупреждение при удалении столбца и при смене его типа.
///
/// Главное правило: заявлять привязку можно только там, где раздел ДЕЙСТВИТЕЛЬНО читает данные.
/// Роль-призрак (флаг остался, а читать некому) — то, из-за чего пользователь видел плашку
/// «привязано к разделу «Задачи» — статус задачи» на текстовом столбце в коллекции, где
/// поля-исполнителя уже не было.
/// </summary>
public sealed class FieldRoleTextTests
{
    private static DbField Field(string type, bool cal = false, bool exec = false, bool assigner = false,
                                 bool status = false, bool description = false) =>
        new()
        {
            Id = 1, CollectionId = 1, Name = "Свойство", FieldType = type,
            IsCalStart = cal, IsTaskExec = exec, IsTaskAssigner = assigner,
            IsTaskStatus = status, IsDescription = description,
        };

    // ── Роль работает только на своём типе ────────────────────────────────────

    [Fact]
    public void Role_on_a_wrong_type_is_not_announced()
    {
        // Ровно случай со скриншота: «Статус» переключили на «Текст», флаг остался.
        Assert.Null(FieldRoleText.Binding(Field("text", status: true), hasTasks: true));
        // И «Описание» на чекбоксе — вторая половина того же дефекта.
        Assert.Null(FieldRoleText.Binding(Field("checkbox", description: true), hasTasks: true));
    }

    [Fact]
    public void Role_on_its_own_type_is_announced()
    {
        var binding = FieldRoleText.Binding(Field("status", status: true), hasTasks: true);
        Assert.Equal("к разделу «Задачи» — статус задачи", binding);
    }

    // ── Раздел, который никого не читает, привязку не заявляет ────────────────

    [Fact]
    public void Task_roles_are_silent_without_an_executor_field()
    {
        var status = Field("status", status: true);

        // Исполнителя в коллекции нет — раздел не покажет ни одной записи, и «Статус» читать
        // некому. Флаг при этом остаётся: вернули исполнителя — привязка ожила сама.
        Assert.Null(FieldRoleText.Binding(status, hasTasks: false));
        Assert.NotNull(FieldRoleText.Binding(status, hasTasks: true));

        // То же для предупреждения при удалении: терять нечего, пока раздел молчит.
        Assert.Null(FieldRoleText.DeletionImpact(status, hasTasks: false));
        Assert.Equal("раздел задач потеряет статусы",
                     FieldRoleText.DeletionImpact(status, hasTasks: true));
    }

    [Fact]
    public void Executor_speaks_for_itself()
    {
        // Поле-исполнитель само включает коллекцию в раздел, поэтому hasTasks для него не условие.
        var exec = Field("user", exec: true);
        Assert.Equal("к разделу «Задачи» — исполнитель задачи",
                     FieldRoleText.Binding(exec, hasTasks: true));
    }

    [Fact]
    public void Description_names_both_sections_when_both_read_it()
    {
        var desc = Field("text", description: true);

        Assert.Equal("к разделам «Календарь» и «Задачи» — описание записи",
                     FieldRoleText.Binding(desc, hasTasks: true, hasCalendar: true));
        Assert.Equal("к разделу «Календарь» — описание записи",
                     FieldRoleText.Binding(desc, hasTasks: false, hasCalendar: true));
        // Ни календаря, ни задач — обычный текстовый столбец.
        Assert.Null(FieldRoleText.Binding(desc, hasTasks: false, hasCalendar: false));
    }

    [Fact]
    public void Date_is_a_deadline_too_when_tasks_are_on()
    {
        var date = Field("date", cal: true);

        Assert.Equal("к разделам «Календарь» и «Задачи» — дата события и срок задачи",
                     FieldRoleText.Binding(date, hasTasks: true));
        Assert.Equal("к разделу «Календарь» — дата события",
                     FieldRoleText.Binding(date, hasTasks: false));
    }

    // ── Смена типа = потеря привязки ──────────────────────────────────────────

    [Fact]
    public void Type_change_impact_reads_like_deletion_except_for_description()
    {
        // Столбец удалили — тексты уходят вместе с ним; тип сменили — тексты на месте,
        // просто раздел перестаёт их читать. Формулировки обязаны различаться.
        Assert.Equal("тексты описаний удалятся вместе со столбцом",
                     FieldRoleText.DeletionImpact(Field("text", description: true)));
        Assert.Equal("описания перестанут показываться в календаре и задачах",
                     FieldRoleText.TypeChangeImpact([FieldRole.Description]));

        Assert.Equal("раздел задач потеряет статусы",
                     FieldRoleText.TypeChangeImpact([FieldRole.TaskStatus]));
        Assert.Equal("записи коллекции перестанут отображаться в задачах и " +
                     "события пропадут из календаря, а у задач исчезнет срок",
                     FieldRoleText.TypeChangeImpact([FieldRole.TaskExec, FieldRole.CalendarDate]));
    }

    // ── Реестр «роль ↔ тип» ───────────────────────────────────────────────────

    [Theory]
    [InlineData(FieldRole.CalendarDate, "date",   true)]
    [InlineData(FieldRole.CalendarDate, "text",   false)]
    [InlineData(FieldRole.TaskExec,     "user",   true)]
    [InlineData(FieldRole.TaskExec,     "select", false)]
    [InlineData(FieldRole.TaskAssigner, "user",   true)]
    [InlineData(FieldRole.TaskAssigner, "text",   false)]
    [InlineData(FieldRole.TaskStatus,   "status", true)]
    [InlineData(FieldRole.TaskStatus,   "select", true)]   // свой список статусов тоже годится
    [InlineData(FieldRole.TaskStatus,   "text",   false)]
    [InlineData(FieldRole.Description,  "text",   true)]
    [InlineData(FieldRole.Description,  "checkbox", false)]
    public void Role_allows_only_its_own_types(FieldRole role, string type, bool allowed) =>
        Assert.Equal(allowed, ConstructorRepository.RoleAllowsType(role, type));
}
