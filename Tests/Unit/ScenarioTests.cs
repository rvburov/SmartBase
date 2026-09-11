using SmartBase.Integrations;

namespace SmartBase.Tests.Unit;

/// <summary>
/// Сценарий и его триггер (<see cref="Scenario"/>, <see cref="ScenarioTrigger"/>) — форма и
/// расписание, без ввода-вывода.
///
/// Проверяется не «складывается ли запись», а обещания, на которые опирается служба: когда
/// наступает срок, что считается пропущенным, и какая настройка не имеет права сохраниться —
/// включенный сценарий, который никогда не сработает, хуже выключенного, потому что человек
/// считает, что обмен идет.
/// </summary>
public sealed class ScenarioTests
{
    /// <summary>
    /// Момент по МЕСТНЫМ часам: «в 07:00» человек понимает по своим, и тесты расписания не
    /// должны зависеть от пояса машины, на которой их запустили.
    /// </summary>
    private static DateTimeOffset Local(int day, int hour, int minute = 0) =>
        new(new DateTime(2026, 9, day, hour, minute, 0, DateTimeKind.Local));

    private static readonly DateTimeOffset Evening = Local(10, 19, 30);

    private static Scenario Sample(ScenarioTrigger? trigger = null, bool enabled = true) =>
        Scenario.New() with
        {
            Name    = "Заказы из учетной системы",
            Enabled = enabled,
            Trigger = trigger ?? ScenarioTrigger.EveryMinutes(30),
            Steps   = [ScenarioStep.OfRequest(7)],
        };

    // ── Хранение триггера строкой ─────────────────────────────────────────────

    [Theory]
    [InlineData("manual")]
    [InlineData("every 30")]
    [InlineData("daily 07:00")]
    [InlineData("changed 15 3 42")]
    public void Trigger_survives_a_round_trip(string stored) =>
        Assert.Equal(stored, ScenarioTrigger.Parse(stored).ToString());

    /// <summary>
    /// Мусор читается как «вручную» — самое безобидное из прочтений: сценарий не станет ходить
    /// сам по непонятному расписанию. Строку мог записать более новый выпуск программы, и падать
    /// на открытии экрана из-за одного сценария незачем.
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("каждые полчаса")]
    [InlineData("every")]
    [InlineData("every 0")]
    [InlineData("every 1")]           // чаще пяти минут — не расписание, а опрос
    [InlineData("every 2000")]        // реже суток — это уже «ежедневно»
    [InlineData("daily 25:00")]
    [InlineData("daily 7:00")]        // формат строгий: только HH:mm
    [InlineData("changed 15")]        // без коллекции следить не за чем
    [InlineData("changed 15 3")]
    [InlineData("changed 1 3 42")]    // тишины меньше пяти минут не бывает
    [InlineData("changed 15 0 42")]   // база не названа
    [InlineData("changed 15 3 0")]    // коллекция не названа
    public void Garbage_reads_as_manual(string? stored) =>
        Assert.Equal(ScenarioTriggerKind.Manual, ScenarioTrigger.Parse(stored).Kind);

    // ── Когда наступает срок ──────────────────────────────────────────────────

    [Fact]
    public void Interval_counts_from_the_previous_run()
    {
        var trigger = ScenarioTrigger.EveryMinutes(30);

        Assert.False(trigger.IsDue(Evening, Evening.AddMinutes(29)));
        Assert.True(trigger.IsDue(Evening, Evening.AddMinutes(30)));
    }

    [Fact]
    public void Daily_fires_at_the_appointed_hour()
    {
        var trigger = ScenarioTrigger.DailyAt(new TimeOnly(7, 0));
        var morning = Local(11, 7);

        Assert.False(trigger.IsDue(Evening, morning.AddMinutes(-1)));
        Assert.True(trigger.IsDue(Evening, morning));
    }

    /// <summary>
    /// Пропущенное догоняется, но ровно один раз: программа была выключена три дня — обмен нужен
    /// свежий, а не по разу за каждый пропущенный день.
    /// </summary>
    [Fact]
    public void A_missed_run_happens_once_after_the_program_starts()
    {
        var trigger = ScenarioTrigger.DailyAt(new TimeOnly(7, 0));
        var lastRun = Local(7, 7);
        var now     = Local(10, 9, 15);

        Assert.True(trigger.IsDue(lastRun, now));

        // После отметки о запуске срок снова наступит только завтра утром.
        Assert.False(trigger.IsDue(now, now.AddHours(6)));
        Assert.True(trigger.IsDue(now, Local(11, 7)));
    }

    /// <summary>
    /// «Еще ни разу» — не повод срываться с места. Включенный вечером сценарий «ежедневно в
    /// 07:00» иначе сработал бы немедленно: ближайший запуск от пустой отметки оказался бы в
    /// прошлом. Якорь ставит служба в момент включения.
    /// </summary>
    [Fact]
    public void Without_an_anchor_nothing_is_due()
    {
        Assert.False(ScenarioTrigger.DailyAt(new TimeOnly(7, 0)).IsDue(null, Evening));
        Assert.False(ScenarioTrigger.EveryMinutes(30).IsDue(null, Evening));
    }

    /// <summary>Ручной сценарий не наступает никогда — его заводит только человек.</summary>
    [Fact]
    public void Manual_never_becomes_due()
    {
        Assert.Null(ScenarioTrigger.Manual.NextRun(Evening));
        Assert.False(ScenarioTrigger.Manual.IsDue(Evening.AddYears(-1), Evening));
    }

    [Fact]
    public void Local_hour_is_what_the_person_means()
    {
        var trigger = ScenarioTrigger.DailyAt(new TimeOnly(7, 0));

        // Прошлый запуск пришел в UTC — сдвиг не должен сдвигать назначенный час.
        var lastRun = new DateTimeOffset(2026, 9, 10, 4, 0, 0, TimeSpan.Zero);
        var next    = trigger.NextRun(lastRun);

        Assert.NotNull(next);
        Assert.Equal(7, next!.Value.ToLocalTime().Hour);
    }

    // ── Как это выглядит на экране ────────────────────────────────────────────

    [Theory]
    [InlineData(30, "каждые 30 мин")]
    [InlineData(120, "каждые 2 ч")]
    public void Interval_is_named_the_way_people_say_it(int minutes, string expected) =>
        Assert.Equal(expected, ScenarioTrigger.EveryMinutes(minutes).Describe());

    [Fact]
    public void Daily_and_manual_are_named_too()
    {
        Assert.Equal("ежедневно в 07:00", ScenarioTrigger.DailyAt(new TimeOnly(7, 0)).Describe());
        Assert.Equal("вручную", ScenarioTrigger.Manual.Describe());
    }

    // ── Что не имеет права сохраниться ────────────────────────────────────────

    [Fact]
    public void A_filled_scenario_has_no_problems() =>
        Assert.Empty(Sample().Problems());

    [Fact]
    public void Empty_name_is_a_problem() =>
        Assert.Contains("Название", string.Join("; ", (Sample() with { Name = "  " }).Problems()),
                        StringComparison.Ordinal);

    [Fact]
    public void A_scenario_without_steps_is_a_problem() =>
        Assert.Contains("ни одного шага",
                        string.Join("; ", (Sample() with { Steps = [] }).Problems()),
                        StringComparison.Ordinal);

    /// <summary>
    /// Включенный сценарий без расписания не сработает никогда, и молчать об этом нельзя:
    /// человек будет считать, что обмен идет.
    /// </summary>
    [Fact]
    public void Enabled_without_a_schedule_is_a_problem()
    {
        var problems = (Sample(ScenarioTrigger.Manual)).Problems();

        Assert.Contains("только кнопкой", string.Join("; ", problems), StringComparison.Ordinal);

        // Тот же сценарий выключенным — законен: его запускают вручную.
        Assert.Empty(Sample(ScenarioTrigger.Manual, enabled: false).Problems());
    }

    [Fact]
    public void Too_many_steps_is_a_problem()
    {
        var many = Enumerable.Range(0, Scenario.MaxSteps + 1)
                             .Select(_ => ScenarioStep.OfRequest(7))
                             .ToList();

        Assert.Contains($"Шагов больше {Scenario.MaxSteps}",
                        string.Join("; ", (Sample() with { Steps = many }).Problems()),
                        StringComparison.Ordinal);
    }

    /// <summary>Проблема шага называет его номер: в списке из десятка шагов иначе не найти.</summary>
    [Fact]
    public void A_step_problem_names_the_step_number()
    {
        var broken = Sample() with
        {
            Steps = [ScenarioStep.OfRequest(7), ScenarioStep.OfNotice("")],
        };

        Assert.Contains("Шаг 2:", string.Join("; ", broken.Problems()), StringComparison.Ordinal);
    }

    [Fact]
    public void A_request_step_needs_a_request() =>
        Assert.Single(ScenarioStep.OfRequest(0).Problems());

    [Fact]
    public void A_notice_step_needs_text_and_not_too_much_of_it()
    {
        Assert.Single(ScenarioStep.OfNotice("   ").Problems());
        Assert.Single(ScenarioStep.OfNotice(new string('я', ScenarioStep.MaxTextLength + 1)).Problems());
        Assert.Empty(ScenarioStep.OfNotice("Заказы обновлены").Problems());
    }

    [Fact]
    public void A_step_says_what_it_does()
    {
        Assert.Equal("Запрос в коллекцию: Заказы", ScenarioStep.OfRequest(7).Describe("Заказы"));
        Assert.Equal("Сообщение: Готово", ScenarioStep.OfNotice("Готово").Describe());
        Assert.Equal("Действие расширения: Зарегистрировать",
                     ScenarioStep.OfAction("реестр", "register", 1, 2).Describe("Зарегистрировать"));
    }

    // ── Шаг «действие расширения» ─────────────────────────────────────────────

    /// <summary>
    /// Проверяется только сделанный выбор. Есть ли такое расширение, такое действие и открыта ли
    /// база — вопросы момента запуска: список расширений знает оболочка, а не модель.
    /// </summary>
    [Fact]
    public void An_action_step_needs_an_extension_and_an_action()
    {
        Assert.Equal(2, ScenarioStep.OfAction("", "", 0, 0).Problems().Count);
        Assert.Empty(ScenarioStep.OfAction("реестр", "register", 1, 5).Problems());
    }

    /// <summary>Выражения полей переживают дорогу и достаются по ключу поля.</summary>
    [Fact]
    public void An_action_step_carries_the_field_expressions()
    {
        var step = ScenarioStep.OfAction("реестр", "register", 1, 5, new Dictionary<string, string>
        {
            ["org"]     = "АБВГ",
            ["project"] = "{{Проект}}",
        });

        Assert.Equal("АБВГ",        step.Fields["org"]);
        Assert.Equal("{{Проект}}",  step.Fields["project"]);
    }

    /// <summary>Шаг без выражений — не поломка: проверка на пустоту не должна расползаться.</summary>
    [Fact]
    public void A_step_without_expressions_has_an_empty_set()
    {
        Assert.Empty(ScenarioStep.OfAction("реестр", "register", 1, 5).Fields);
        Assert.Empty(ScenarioStep.OfNotice("Готово").Fields);
    }

    /// <summary>
    /// Коллекция у шага-действия НЕ спрашивается формой: нужна она или нет, зависит от вида
    /// действия, а вид знает расширение. Преобразователю формата коллекция не нужна вовсе.
    /// </summary>
    [Fact]
    public void The_form_does_not_ask_an_action_step_for_a_collection()
    {
        Assert.Empty(ScenarioStep.OfAction("реестр", "parse", 0, 0).Problems());
    }

    // ── Шаг «файл из папки» ───────────────────────────────────────────────────

    /// <summary>
    /// Папка — все, что нужно шагу от человека. Разрешена ли она политикой и существует ли —
    /// вопросы момента запуска: сетевой диск бывает выключен, а сценарий должен сохраниться.
    /// </summary>
    [Fact]
    public void An_intake_step_needs_a_folder()
    {
        Assert.Single(ScenarioStep.OfIntake("   ").Problems());
        Assert.Empty(ScenarioStep.OfIntake(@"P:\Обмен\КД").Problems());
    }

    [Fact]
    public void An_intake_step_says_where_it_looks()
    {
        Assert.Equal(@"Файл из папки: P:\Обмен\КД",
                     ScenarioStep.OfIntake(@"P:\Обмен\КД").Describe());
    }

    // ── Шаг «отбор» ───────────────────────────────────────────────────────────

    [Fact]
    public void A_filter_without_conditions_is_a_problem()
    {
        Assert.Single(ScenarioStep.OfFilter().Problems());
    }

    /// <summary>Негодное условие называет свой номер: в отборе их бывает несколько.</summary>
    [Fact]
    public void A_bad_condition_names_its_place()
    {
        var step = ScenarioStep.OfFilter(
            new ScenarioCondition("Проект", ScenarioTest.Filled),
            new ScenarioCondition("Статус", ScenarioTest.Equals));

        Assert.Equal(["Условие 2: не заполнен образец"], step.Problems());
    }

    [Fact]
    public void Too_many_conditions_are_refused()
    {
        var many = Enumerable.Range(0, ScenarioStep.MaxConditions + 1)
                             .Select(i => new ScenarioCondition($"Поле {i}", ScenarioTest.Filled))
                             .ToArray();

        Assert.Contains(ScenarioStep.OfFilter(many).Problems(),
                        p => p.Contains("Условий больше", StringComparison.Ordinal));
    }

    /// <summary>
    /// Условия соединяются союзом «и»: «или» пришлось бы рисовать скобками, а скобки в строке
    /// настройки читаются хуже, чем два отбора подряд.
    /// </summary>
    [Fact]
    public void A_filter_reads_as_a_phrase()
    {
        var step = ScenarioStep.OfFilter(
            new ScenarioCondition("Проект", ScenarioTest.Filled),
            new ScenarioCondition("Статус", ScenarioTest.Equals, "Оплачен"));

        Assert.Equal("Отбор: Проект заполнено и Статус равно Оплачен", step.Describe());
    }

    /// <summary>«Ходит сам» — это и включен, и есть чем заводить.</summary>
    [Fact]
    public void Automatic_means_enabled_and_scheduled()
    {
        Assert.True(Sample().Automatic);
        Assert.False(Sample(enabled: false).Automatic);
        Assert.False((Sample(ScenarioTrigger.Manual, enabled: false)).Automatic);
    }

    // ── Триггер «изменение данных» ────────────────────────────────────────────

    /// <summary>
    /// Часов у этого триггера нет: «пора ли» решают правки, а не расписание. Поэтому и ближайший
    /// запуск у него не считается — вернуть тут время значило бы заставить службу ждать минуты,
    /// когда данные уже поменялись.
    /// </summary>
    [Fact]
    public void A_data_trigger_has_no_schedule_of_its_own()
    {
        var trigger = ScenarioTrigger.OnChange(dbId: 3, collectionId: 42, settle: 15);

        Assert.True(trigger.WatchesData);
        Assert.Null(trigger.NextRun(Evening));
        Assert.False(trigger.IsDue(Evening.AddYears(-1), Evening));
    }

    /// <summary>Ходит сам — значит, и он тоже: включенный сценарий по правкам идет без кнопки.</summary>
    [Fact]
    public void A_data_trigger_counts_as_automatic()
    {
        Assert.True(Sample(ScenarioTrigger.OnChange(3, 42, 15)).Automatic);
    }

    /// <summary>Не выбрана коллекция — следить не за чем, и сохранять такое незачем.</summary>
    [Fact]
    public void A_data_trigger_without_a_collection_is_refused()
    {
        var problems = ScenarioTrigger.OnChange(dbId: 0, collectionId: 0, settle: 15).Problems();

        Assert.Contains(problems, p => p.Contains("коллекция", StringComparison.Ordinal));
    }

    /// <summary>Тишины меньше пяти минут не бывает: правка каждой строки дала бы обмен на строку.</summary>
    [Theory]
    [InlineData(1)]
    [InlineData(0)]
    [InlineData(2000)]
    public void A_settle_time_outside_the_bounds_is_refused(int settle)
    {
        var problems = ScenarioTrigger.OnChange(3, 42, settle).Problems();

        Assert.Contains(problems, p => p.Contains("тишины", StringComparison.Ordinal));
    }

    /// <summary>Имя коллекции приходит извне; без него строка честно называет ключ.</summary>
    [Fact]
    public void A_data_trigger_describes_itself_by_the_collection()
    {
        var trigger = ScenarioTrigger.OnChange(3, 42, 15);

        Assert.Equal("при правках в «Заказы», через 15 мин тишины", trigger.Describe("Заказы"));
        Assert.Contains("№42", trigger.Describe());
    }

    /// <summary>
    /// Сценарий, который ПИШЕТ в ту же коллекцию, правки которой его заводят, не сохраняется.
    /// Такой запускал бы сам себя без конца — и, что хуже, выбрал бы часовой потолок политики,
    /// остановив ВЕСЬ обмен, а не только себя.
    /// </summary>
    [Fact]
    public void A_scenario_that_feeds_its_own_trigger_is_refused()
    {
        var scenario = Scenario.New() with
        {
            Name    = "Круг",
            Enabled = true,
            Trigger = ScenarioTrigger.OnChange(dbId: 1, collectionId: 10, settle: 15),
            Steps   = [ScenarioStep.OfAction("реестр", "register", dbId: 1, collectionId: 10)],
        };

        Assert.Contains(scenario.Problems(),
                        p => p.Contains("сам себя", StringComparison.Ordinal));
    }

    /// <summary>Запись в ДРУГУЮ коллекцию круга не образует — и запрещать ее незачем.</summary>
    [Fact]
    public void Writing_into_another_collection_is_allowed()
    {
        var scenario = Scenario.New() with
        {
            Name    = "Отдать итог",
            Enabled = true,
            Trigger = ScenarioTrigger.OnChange(dbId: 1, collectionId: 10, settle: 15),
            Steps   = [ScenarioStep.OfAction("реестр", "register", dbId: 1, collectionId: 11)],
        };

        Assert.Empty(scenario.Problems());
    }
}
