using Microsoft.EntityFrameworkCore;
using SmartBase.Database;
using SmartBase.Integrations;

namespace SmartBase.Tests.Integration;

/// <summary>
/// Сценарии в app.db (<see cref="IntegrationsRegistry"/>): хранение, якорь расписания и связь с
/// запросами.
///
/// Проверяется то, чего не видно в модели: переживают ли шаги переоткрытие файла, не сработает
/// ли включенный сценарий немедленно, и что будет с запросом, на который ссылается шаг. Форма
/// сценария проверяется отдельно, юнит-тестами (<c>ScenarioTests</c>).
///
/// Временный app.db на диске, как в <see cref="IntegrationsRegistryTests"/>; PostgreSQL не нужен.
/// </summary>
public sealed class ScenariosRegistryTests
{
    private static readonly DateTimeOffset Now =
        new(new DateTime(2026, 9, 10, 19, 30, 0, DateTimeKind.Local));

    private static (string Dir, string AppDb, string DataDb) TempPaths()
    {
        var dir = Directory.CreateTempSubdirectory("sbscen_").FullName;
        return (dir, Path.Combine(dir, "app.db"), Path.Combine(dir, "database.db"));
    }

    private static void Cleanup(string dir, string appDb, string dataDb)
    {
        TempDatabase.Cleanup(appDb);
        TempDatabase.Cleanup(dataDb);
        try { Directory.Delete(dir, recursive: true); } catch { /* best-effort */ }
    }

    /// <summary>Соединение и запрос, на которые сценарию можно сослаться.</summary>
    private static int NewRequest(DatabaseRegistry registry, string name = "Заказы")
    {
        var connection = registry.Integrations.Save(IntegrationConnection.New() with
        {
            Name    = "Учетная система " + name,
            Address = "https://srv.zavod.local/exchange",
        });

        return registry.Integrations.SaveRequest(IntegrationRequest.New() with
        {
            Name         = name,
            ConnectionId = connection.Id,
            Path         = "/orders",
        }).Id;
    }

    private static Scenario Sample(int requestId) => Scenario.New() with
    {
        Name    = "Заказы каждые полчаса",
        Trigger = ScenarioTrigger.EveryMinutes(30),
        Steps   = [ScenarioStep.OfRequest(requestId), ScenarioStep.OfNotice("Заказы обновлены")],
    };

    // ── Хранение ──────────────────────────────────────────────────────────────

    [Fact]
    public void Scenario_is_saved_and_read_back()
    {
        var (dir, appDb, dataDb) = TempPaths();
        try
        {
            int id;
            int requestId;

            using (var registry = DatabaseRegistry.Open(dataDb, appDb))
            {
                requestId = NewRequest(registry);
                id        = registry.Integrations.SaveScenario(Sample(requestId)).Id;

                Assert.NotEqual(0, id);
            }

            // Переоткрытие: строка легла в app.db, а не осталась в памяти.
            using (var again = DatabaseRegistry.Open(dataDb, appDb))
            {
                var scenario = again.Integrations.FindScenario(id);

                Assert.NotNull(scenario);
                Assert.Equal("Заказы каждые полчаса", scenario!.Name);
                Assert.False(scenario.Enabled);
                Assert.Equal(ScenarioTriggerKind.Every, scenario.Trigger.Kind);
                Assert.Equal(30, scenario.Trigger.Minutes);

                // Шаги переживают переоткрытие в том же порядке: порядок и есть сценарий.
                Assert.Equal(2, scenario.Steps.Count);
                Assert.Equal(ScenarioStepKind.Request, scenario.Steps[0].Kind);
                Assert.Equal(requestId, scenario.Steps[0].RequestId);
                Assert.Equal("Заказы обновлены", scenario.Steps[1].Text);
            }
        }
        finally { Cleanup(dir, appDb, dataDb); }
    }

    /// <summary>
    /// Шаг-действие переживает переоткрытие целиком — вместе с выражениями полей. Они лежат в
    /// той же строке JSON, что и остальные шаги, и потерять их означало бы получить сценарий,
    /// который молча регистрирует документы с пустыми полями.
    /// </summary>
    [Fact]
    public void An_action_step_survives_a_reopen_with_its_field_expressions()
    {
        var (dir, appDb, dataDb) = TempPaths();
        try
        {
            int id;

            var step = ScenarioStep.OfAction("doc-registry", "register", 3, 7,
                                             new Dictionary<string, string>
                                             {
                                                 ["org"]  = "АБВГ",
                                                 ["name"] = "{{Название}}",
                                             });

            using (var registry = DatabaseRegistry.Open(dataDb, appDb))
                id = registry.Integrations.SaveScenario(Scenario.New() with
                {
                    Name  = "Заявки в журнал",
                    Steps = [step],
                }).Id;

            using (var again = DatabaseRegistry.Open(dataDb, appDb))
            {
                var saved = again.Integrations.FindScenario(id)!.Steps.Single();

                Assert.Equal(ScenarioStepKind.Action, saved.Kind);
                Assert.Equal("doc-registry", saved.Extension);
                Assert.Equal("register",     saved.Action);
                Assert.Equal(3,              saved.DbId);
                Assert.Equal(7,              saved.CollectionId);
                Assert.Equal("АБВГ",         saved.Fields["org"]);
                Assert.Equal("{{Название}}", saved.Fields["name"]);
            }
        }
        finally { Cleanup(dir, appDb, dataDb); }
    }

    /// <summary>
    /// Шаги «файл из папки» и «отбор» переживают переоткрытие вместе со своей начинкой. Потерять
    /// условия страшнее, чем потерять шаг целиком: отбор без условий не сохранится и будет
    /// виден, а отбор, пропускающий ВСЕ, выглядит работающим.
    /// </summary>
    [Fact]
    public void An_intake_and_a_filter_survive_a_reopen_whole()
    {
        var (dir, appDb, dataDb) = TempPaths();
        try
        {
            int id;

            using (var registry = DatabaseRegistry.Open(dataDb, appDb))
                id = registry.Integrations.SaveScenario(Scenario.New() with
                {
                    Name  = "Регистрация из папки",
                    Steps =
                    [
                        ScenarioStep.OfIntake(@"P:\Обмен\КД"),
                        ScenarioStep.OfFilter(new ScenarioCondition("Проект", ScenarioTest.Filled),
                                              new ScenarioCondition("Статус", ScenarioTest.Equals,
                                                                    "Оплачен")),
                    ],
                }).Id;

            using (var again = DatabaseRegistry.Open(dataDb, appDb))
            {
                var steps = again.Integrations.FindScenario(id)!.Steps;

                Assert.Equal(ScenarioStepKind.Intake, steps[0].Kind);
                Assert.Equal(@"P:\Обмен\КД",          steps[0].Folder);

                Assert.Equal(ScenarioStepKind.Filter, steps[1].Kind);
                Assert.Equal("Отбор: Проект заполнено и Статус равно Оплачен",
                             steps[1].Describe());
            }
        }
        finally { Cleanup(dir, appDb, dataDb); }
    }

    [Fact]
    public void A_scenario_of_a_wrong_shape_is_refused()
    {
        var (dir, appDb, dataDb) = TempPaths();
        try
        {
            using var registry = DatabaseRegistry.Open(dataDb, appDb);
            var requestId = NewRequest(registry);

            Assert.Throws<InvalidOperationException>(() =>
                registry.Integrations.SaveScenario(Sample(requestId) with { Name = "" }));

            Assert.Empty(registry.Integrations.Scenarios());
        }
        finally { Cleanup(dir, appDb, dataDb); }
    }

    /// <summary>
    /// Шаг ссылается на несуществующий запрос — сценарий не сохраняется. Иначе он молча
    /// останавливался бы при каждом запуске, а причину пришлось бы искать в журнале.
    /// </summary>
    [Fact]
    public void A_step_with_an_unknown_request_is_refused()
    {
        var (dir, appDb, dataDb) = TempPaths();
        try
        {
            using var registry = DatabaseRegistry.Open(dataDb, appDb);

            Assert.Throws<InvalidOperationException>(() =>
                registry.Integrations.SaveScenario(Sample(requestId: 4242)));
        }
        finally { Cleanup(dir, appDb, dataDb); }
    }

    /// <summary>
    /// Испорченная строка шагов читается как «шагов нет»: сценарий открывается и правится.
    /// Падение на открытии настроек из-за одной строки не оставляет человеку выхода вовсе.
    /// </summary>
    [Fact]
    public void Broken_steps_read_as_no_steps()
    {
        var (dir, appDb, dataDb) = TempPaths();
        try
        {
            int id;
            using (var registry = DatabaseRegistry.Open(dataDb, appDb))
                id = registry.Integrations.SaveScenario(Sample(NewRequest(registry))).Id;

            // Значение идет параметром: фигурные скобки в тексте запроса EF считает
            // подстановкой и спотыкается о них раньше, чем SQLite увидит строку.
            using (var ctx = new SystemDbContext(appDb))
                ctx.Database.ExecuteSqlRaw(
                    "UPDATE \"integration_scenarios\" SET \"StepsJson\" = {0};", "{это не список}");

            using (var again = DatabaseRegistry.Open(dataDb, appDb))
            {
                var scenario = again.Integrations.FindScenario(id);

                Assert.NotNull(scenario);
                Assert.Empty(scenario!.Steps);

                // И сработать он не сможет: проверка формы не пропустит сценарий без шагов.
                Assert.NotEmpty(scenario.Problems());
            }
        }
        finally { Cleanup(dir, appDb, dataDb); }
    }

    // ── Якорь расписания ──────────────────────────────────────────────────────

    /// <summary>
    /// Включение ставит якорь — момент, от которого считается первый запуск. Без него сценарий
    /// «ежедневно в 07:00», включенный вечером, сработал бы немедленно.
    /// </summary>
    [Fact]
    public void Enabling_anchors_the_first_run()
    {
        var (dir, appDb, dataDb) = TempPaths();
        try
        {
            using var registry = DatabaseRegistry.Open(dataDb, appDb);
            var id = registry.Integrations.SaveScenario(Sample(NewRequest(registry)) with
            {
                Trigger = ScenarioTrigger.DailyAt(new TimeOnly(7, 0)),
            }).Id;

            Assert.Null(registry.Integrations.ScenarioLastRun(id));

            registry.Integrations.EnableScenario(id, enabled: true, Now);

            var anchor = registry.Integrations.ScenarioLastRun(id);
            Assert.NotNull(anchor);
            Assert.True(registry.Integrations.FindScenario(id)!.Enabled);

            // От якоря ближайший запуск — завтрашнее утро, а не «сейчас».
            Assert.False(registry.Integrations.FindScenario(id)!.Trigger.IsDue(anchor, Now.AddHours(1)));
        }
        finally { Cleanup(dir, appDb, dataDb); }
    }

    /// <summary>
    /// Повторное включение якорь НЕ двигает: иначе сценарий, который выключили и включили
    /// обратно, потерял бы догон пропущенного запуска.
    /// </summary>
    [Fact]
    public void Re_enabling_keeps_the_old_mark()
    {
        var (dir, appDb, dataDb) = TempPaths();
        try
        {
            using var registry = DatabaseRegistry.Open(dataDb, appDb);
            var id = registry.Integrations.SaveScenario(Sample(NewRequest(registry))).Id;

            registry.Integrations.EnableScenario(id, enabled: true, Now);
            var first = registry.Integrations.ScenarioLastRun(id);

            registry.Integrations.EnableScenario(id, enabled: false, Now.AddHours(1));
            registry.Integrations.EnableScenario(id, enabled: true,  Now.AddHours(2));

            Assert.Equal(first, registry.Integrations.ScenarioLastRun(id));
        }
        finally { Cleanup(dir, appDb, dataDb); }
    }

    /// <summary>
    /// Сохранение включенным ставит якорь ТАК ЖЕ, как тумблер. Новый сценарий заводят окном
    /// правки, и тумблер в нем включают там же — пока якорь ставил один список, такой сценарий
    /// не срабатывал ни разу и выглядел при этом исправным.
    /// </summary>
    [Fact]
    public void Saving_an_enabled_scenario_anchors_it_too()
    {
        var (dir, appDb, dataDb) = TempPaths();
        try
        {
            using var registry = DatabaseRegistry.Open(dataDb, appDb);
            var id = registry.Integrations.SaveScenario(Sample(NewRequest(registry)) with
            {
                Enabled = true,
                Trigger = ScenarioTrigger.DailyAt(new TimeOnly(7, 0)),
            }, Now).Id;

            var anchor = registry.Integrations.ScenarioLastRun(id);

            Assert.NotNull(anchor);
            Assert.Equal(Now.ToUniversalTime(), anchor!.Value.ToUniversalTime());
        }
        finally { Cleanup(dir, appDb, dataDb); }
    }

    /// <summary>
    /// Ради чего якорь и заведен: сценарий, созданный включенным, СРАБАТЫВАЕТ в свой срок и не
    /// срабатывает раньше. Без якоря <c>IsDue</c> отвечал «не пора» навсегда.
    /// </summary>
    [Fact]
    public void A_scenario_created_enabled_becomes_due_at_its_hour()
    {
        var (dir, appDb, dataDb) = TempPaths();
        try
        {
            using var registry = DatabaseRegistry.Open(dataDb, appDb);
            var id = registry.Integrations.SaveScenario(Sample(NewRequest(registry)) with
            {
                Enabled = true,
                Trigger = ScenarioTrigger.DailyAt(new TimeOnly(7, 0)),
            }, Now).Id;

            var anchor  = registry.Integrations.ScenarioLastRun(id);
            var trigger = registry.Integrations.FindScenario(id)!.Trigger;

            // Якорь поставлен вечером 10-го: до утра 11-го не пора, а в 07:00 — пора.
            Assert.False(trigger.IsDue(anchor, Now.AddHours(1)));
            Assert.True (trigger.IsDue(anchor, Now.AddHours(12)));
        }
        finally { Cleanup(dir, appDb, dataDb); }
    }

    /// <summary>
    /// Сохранение ВЫКЛЮЧЕННЫМ якоря не ставит: отсчитывать нечего, пока сценарий не включен.
    /// Так же приезжают сценарии файлом переноса — они выключены нарочно.
    /// </summary>
    [Fact]
    public void Saving_a_disabled_scenario_leaves_no_anchor()
    {
        var (dir, appDb, dataDb) = TempPaths();
        try
        {
            using var registry = DatabaseRegistry.Open(dataDb, appDb);
            var id = registry.Integrations.SaveScenario(Sample(NewRequest(registry)), Now).Id;

            Assert.Null(registry.Integrations.ScenarioLastRun(id));
        }
        finally { Cleanup(dir, appDb, dataDb); }
    }

    /// <summary>
    /// Правка работающего сценария якорь НЕ двигает: иначе каждое открытие карточки отодвигало
    /// бы ближайший запуск, и «ежедневно в 07:00» уезжало бы на сутки от одного нажатия
    /// «Сохранить».
    /// </summary>
    [Fact]
    public void Editing_a_running_scenario_keeps_its_mark()
    {
        var (dir, appDb, dataDb) = TempPaths();
        try
        {
            using var registry = DatabaseRegistry.Open(dataDb, appDb);
            var saved = registry.Integrations.SaveScenario(
                Sample(NewRequest(registry)) with { Enabled = true }, Now);

            var first = registry.Integrations.ScenarioLastRun(saved.Id);

            registry.Integrations.SaveScenario(saved with { Name = "Заказы по-новому" },
                                               Now.AddHours(5));

            Assert.Equal(first, registry.Integrations.ScenarioLastRun(saved.Id));
        }
        finally { Cleanup(dir, appDb, dataDb); }
    }

    /// <summary>
    /// Отметка ставится и после НЕУДАЧНОГО запуска: она про срок, а не про успех. Не отмечай
    /// неудачу — и сломавшийся сценарий пошел бы по кругу на каждом тике таймера.
    /// </summary>
    [Fact]
    public void The_mark_moves_after_any_run()
    {
        var (dir, appDb, dataDb) = TempPaths();
        try
        {
            using var registry = DatabaseRegistry.Open(dataDb, appDb);
            var id = registry.Integrations.SaveScenario(Sample(NewRequest(registry))).Id;

            registry.Integrations.MarkScenarioRun(id, Now);
            registry.Integrations.MarkScenarioRun(id, Now.AddMinutes(30));

            var mark = registry.Integrations.ScenarioLastRun(id);

            Assert.NotNull(mark);
            Assert.Equal(Now.AddMinutes(30).ToUniversalTime(), mark!.Value.ToUniversalTime());
        }
        finally { Cleanup(dir, appDb, dataDb); }
    }

    // ── Связь с запросами ─────────────────────────────────────────────────────

    /// <summary>
    /// Запрос, который выполняет шаг сценария, не удаляется — то же правило, что у соединения с
    /// запросами: сценарий с дырой посреди списка шагов хуже отказа.
    /// </summary>
    [Fact]
    public void A_request_held_by_a_scenario_is_not_deleted()
    {
        var (dir, appDb, dataDb) = TempPaths();
        try
        {
            using var registry = DatabaseRegistry.Open(dataDb, appDb);
            var requestId = NewRequest(registry);
            registry.Integrations.SaveScenario(Sample(requestId));

            var holders = registry.Integrations.ScenariosUsing(requestId);
            Assert.Single(holders);
            Assert.Equal("Заказы каждые полчаса", holders[0].Name);

            Assert.Throws<InvalidOperationException>(() =>
                registry.Integrations.DeleteRequest(requestId));

            Assert.NotNull(registry.Integrations.FindRequest(requestId));
        }
        finally { Cleanup(dir, appDb, dataDb); }
    }

    /// <summary>Удаленный сценарий отпускает запрос: тот живет сам по себе.</summary>
    [Fact]
    public void Deleting_the_scenario_frees_the_request()
    {
        var (dir, appDb, dataDb) = TempPaths();
        try
        {
            using var registry = DatabaseRegistry.Open(dataDb, appDb);
            var requestId = NewRequest(registry);
            var id        = registry.Integrations.SaveScenario(Sample(requestId)).Id;

            registry.Integrations.DeleteScenario(id);

            Assert.Empty(registry.Integrations.Scenarios());
            Assert.Empty(registry.Integrations.ScenariosUsing(requestId));

            registry.Integrations.DeleteRequest(requestId);
            Assert.Null(registry.Integrations.FindRequest(requestId));
        }
        finally { Cleanup(dir, appDb, dataDb); }
    }

    /// <summary>Шаг-сообщение запрос не держит: искать в нем нечего.</summary>
    [Fact]
    public void A_notice_step_holds_nothing()
    {
        var (dir, appDb, dataDb) = TempPaths();
        try
        {
            using var registry = DatabaseRegistry.Open(dataDb, appDb);
            var requestId = NewRequest(registry);

            registry.Integrations.SaveScenario(Scenario.New() with
            {
                Name  = "Только сообщение",
                Steps = [ScenarioStep.OfNotice("Пора свести заказы")],
            });

            Assert.Empty(registry.Integrations.ScenariosUsing(requestId));
        }
        finally { Cleanup(dir, appDb, dataDb); }
    }

    /// <summary>
    /// Таблица достраивается в УЖЕ СУЩЕСТВУЮЩЕМ app.db: он никогда не пересоздается, иначе
    /// обновление программы стирало бы реестр баз и настройки.
    /// </summary>
    [Fact]
    public void The_table_appears_in_an_older_app_db()
    {
        var (dir, appDb, dataDb) = TempPaths();
        try
        {
            using (var first = DatabaseRegistry.Open(dataDb, appDb))
                Assert.Empty(first.Integrations.Scenarios());

            // app.db прежнего выпуска — сценариев в нем еще не было.
            using (var ctx = new SystemDbContext(appDb))
                ctx.Database.ExecuteSqlRaw("DROP TABLE \"integration_scenarios\";");

            using (var second = DatabaseRegistry.Open(dataDb, appDb))
            {
                var id = second.Integrations.SaveScenario(Sample(NewRequest(second))).Id;
                Assert.NotNull(second.Integrations.FindScenario(id));
            }
        }
        finally { Cleanup(dir, appDb, dataDb); }
    }
}
