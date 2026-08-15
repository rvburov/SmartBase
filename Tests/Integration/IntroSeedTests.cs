using SmartBase.Database;
using SmartBase.Database.Repositories;
using SmartBase.Utils;

namespace SmartBase.Tests.Integration;

/// <summary>
/// Вводный чек-лист первого запуска (Database/IntroSeed.cs).
///
/// Здесь проверяется НАПОЛНЕНИЕ (<see cref="IntroSeed.Seed"/>) и его отказ на непустой
/// базе. Политика «когда вообще сидить» — состояние установки: пустой реестр + отсутствие
/// файла основной БД (<see cref="DatabaseRegistry.IsFreshInstall"/>) плюс одноразовая
/// отметка <c>introSeedDone</c> в app.db; сам факт «повторно не заводим» держится на ней,
/// а не на содержимом базы — именно поэтому удаление задач их не возвращает.
///
/// Смысл проверок: чек-лист обязан быть заполнен ЦЕЛИКОМ. Задача с пустым исполнителем не
/// попадёт в раздел «Задачи», без срока — в календарь, без статуса и описания карточка
/// выглядит недоделанной. Молча потерять одно из полей легко: они пишутся разными путями
/// (шапка + поле-дата + три роли + описание).
/// </summary>
public sealed class IntroSeedTests
{
    // ── Наполнение ────────────────────────────────────────────────────────────

    [Fact]
    public void Seed_fills_every_task_completely()
    {
        using var db = new TempDatabase();
        var repo = db.Constructor;
        var now  = new DateTime(2026, 3, 10, 9, 0, 0);   // до 10:00 — первый срок ровно в 10:00

        Assert.True(IntroSeed.Seed(repo, now));

        var collection = Assert.Single(repo.GetAllCollections());
        Assert.Equal(IntroSeed.CollectionName, collection.Name);

        var tasks = repo.GetTaskRecords();
        Assert.NotEmpty(tasks);
        Assert.All(tasks, t =>
        {
            Assert.Equal(collection.Id, t.CollectionId);
            Assert.NotEqual("", t.Title);
            // Исполнитель и «Назначил» — тот, кто открыл программу (в тестах — тот же
            // пользователь, что регистрируется администратором при монтировании).
            Assert.Equal(CurrentUser.DisplayName, t.Executor);
            Assert.Equal(CurrentUser.DisplayName, t.Assigner);
            Assert.Equal(ConstructorRepository.TaskStatusPlanned, t.Status);
            Assert.NotNull(t.DueStart);
            Assert.NotEqual("", repo.GetRecordDescription(t.Id!.Value));
        });

        // Заголовки уникальны — чек-лист не должен дублировать разделы.
        Assert.Equal(tasks.Count, tasks.Select(t => t.Title).Distinct().Count());
    }

    /// <summary>Каждая задача — ещё и событие календаря: та же запись, поле-дата с IsCalStart.</summary>
    [Fact]
    public void Seeded_tasks_are_calendar_events_too()
    {
        using var db = new TempDatabase();
        var repo = db.Constructor;
        var now  = new DateTime(2026, 3, 10, 9, 0, 0);

        IntroSeed.Seed(repo, now);

        var tasks  = repo.GetTaskRecords();
        var events = repo.GetCalendarRecordsByRange(now.Date, now.Date.AddDays(30));

        Assert.Equal(tasks.Count, events.Count);
        Assert.Equal(tasks.Select(t => t.Title).OrderBy(x => x),
                     events.Select(e => e.Title).OrderBy(x => x));
        // Сроки разведены по дням: первая — сегодня, дальше по одной в день.
        Assert.Equal(events.Count, events.Select(e => e.StartDt.Date).Distinct().Count());
        Assert.Equal(now.Date, events.Min(e => e.StartDt).Date);
    }

    /// <summary>Коллекция получает полный набор ролей — иначе разделы не увидят задачи.</summary>
    [Fact]
    public void Seeded_collection_has_all_task_roles()
    {
        using var db = new TempDatabase();
        var repo = db.Constructor;

        IntroSeed.Seed(repo, new DateTime(2026, 3, 10, 9, 0, 0));

        var collectionId = repo.GetAllCollections().Single().Id!.Value;
        var roles        = repo.TaskRoleFields(collectionId);

        Assert.NotNull(roles.AssignerId);
        Assert.NotNull(roles.StatusId);
        Assert.NotNull(repo.DescriptionFieldId(collectionId));
        Assert.Contains(repo.GetFields(collectionId), f => f.FieldType == "user" && f.IsTaskExec);
        Assert.Contains(repo.GetFields(collectionId), f => f.FieldType == "date" && f.IsCalStart);
    }

    // ── Срок первой задачи ────────────────────────────────────────────────────

    /// <summary>
    /// Программу открывают и в середине дня: срок первой задачи не должен оказаться в
    /// прошлом, иначе чек-лист встречает пользователя карточкой «Просрочено».
    /// </summary>
    [Theory]
    [InlineData(9)]    // до 10:00 — срок ровно в 10:00
    [InlineData(15)]   // день — ближайший целый час
    [InlineData(23)]   // поздний вечер — сдвигать некуда, остаётся утро (единственное исключение)
    public void First_task_is_not_left_in_the_past(int hour)
    {
        using var db = new TempDatabase();
        var repo = db.Constructor;
        var now  = new DateTime(2026, 3, 10, hour, 20, 0);

        IntroSeed.Seed(repo, now);

        var first = repo.GetTaskRecords().Min(t => t.DueStart)!.Value;
        Assert.Equal(now.Date, first.Date);
        if (hour < 22) Assert.True(first >= now, $"срок первой задачи {first:t} уже прошёл в {now:t}");
    }

    // ── Отказ на непустой базе ────────────────────────────────────────────────

    /// <summary>
    /// В базе уже есть коллекции — чек-лист не лезет в чужие данные. Это вторая линия
    /// обороны после признака чистой установки: файл БД мог появиться в обход реестра.
    /// </summary>
    [Fact]
    public void Seed_skips_a_database_that_already_has_collections()
    {
        using var db = new TempDatabase();
        var repo = db.Constructor;
        SampleData.AddCollection(repo, "Моя коллекция");

        Assert.False(IntroSeed.Seed(repo, DateTime.Now));

        var collection = Assert.Single(repo.GetAllCollections());
        Assert.Equal("Моя коллекция", collection.Name);
        Assert.Empty(repo.GetTaskRecords());
    }

    /// <summary>Повторный прогон ничего не добавляет — даже если позвать напрямую.</summary>
    [Fact]
    public void Seed_is_not_repeatable()
    {
        using var db = new TempDatabase();
        var repo = db.Constructor;
        var now  = new DateTime(2026, 3, 10, 9, 0, 0);

        Assert.True(IntroSeed.Seed(repo, now));
        var after = repo.GetTaskRecords().Count;

        Assert.False(IntroSeed.Seed(repo, now));
        Assert.Equal(after, repo.GetTaskRecords().Count);
        Assert.Single(repo.GetAllCollections());
    }

    // ── Признак чистой установки ──────────────────────────────────────────────

    /// <summary>
    /// Пустая папка данных — чистая установка. Второй запуск (тот же app.db, дефолтная
    /// запись уже есть) — уже нет: иначе чек-лист возвращался бы каждый раз.
    /// </summary>
    [Fact]
    public void Fresh_install_is_true_only_on_the_very_first_open()
    {
        var dir     = Directory.CreateTempSubdirectory("sbreg_").FullName;
        var appDb   = Path.Combine(dir, "app.db");
        var dataDb  = Path.Combine(dir, "database.db");
        try
        {
            using (var first = DatabaseRegistry.Open(dataDb, appDb))
                Assert.True(first.IsFreshInstall);

            using var second = DatabaseRegistry.Open(dataDb, appDb);
            Assert.False(second.IsFreshInstall);
        }
        finally
        {
            TempDatabase.Cleanup(appDb);
            TempDatabase.Cleanup(dataDb);
            try { Directory.Delete(dir, recursive: true); } catch { /* best-effort */ }
        }
    }

    /// <summary>
    /// Реестр новый, а база данных на диске уже лежит (app.db удалили или он не пережил
    /// перенос) — это НЕ чистая установка: в базе данные пользователя.
    /// </summary>
    [Fact]
    public void Existing_database_file_without_registry_is_not_a_fresh_install()
    {
        var dir    = Directory.CreateTempSubdirectory("sbreg_").FullName;
        var appDb  = Path.Combine(dir, "app.db");
        var dataDb = Path.Combine(dir, "database.db");
        try
        {
            using (var existing = DatabaseManager.Open(1, "Основная", dataDb)) { }

            using var registry = DatabaseRegistry.Open(dataDb, appDb);
            Assert.False(registry.IsFreshInstall);
        }
        finally
        {
            TempDatabase.Cleanup(appDb);
            TempDatabase.Cleanup(dataDb);
            try { Directory.Delete(dir, recursive: true); } catch { /* best-effort */ }
        }
    }

    // ── Политика целиком: реестр + отметка + наполнение ───────────────────────

    /// <summary>
    /// Главный сценарий: на чистой папке данных чек-лист заводится, а после того как
    /// пользователь удалил его целиком (вместе с коллекцией), следующий запуск НИЧЕГО не
    /// создаёт — ни задач, ни коллекции. Именно этим новое поведение отличается от прежнего
    /// сидинга календаря, который возвращал задачи при каждом пустом календаре.
    /// </summary>
    [Fact]
    public void Fresh_install_seeds_once_and_never_again()
    {
        var dir    = Directory.CreateTempSubdirectory("sbseed_").FullName;
        var appDb  = Path.Combine(dir, "app.db");
        var dataDb = Path.Combine(dir, "database.db");
        AppSettings.AttachStore(new FakeSettingsDatabase());
        try
        {
            // Первый запуск: реестра нет, базы нет — порядок как в App (реестр до базы).
            using (var registry = DatabaseRegistry.Open(dataDb, appDb))
            using (var db = DatabaseManager.Open(1, "Основная", dataDb))
            {
                Assert.True(IntroSeed.RunOnFreshInstall(registry, db));
                Assert.NotEmpty(db.Constructor.GetTaskRecords());

                // Пользователь стирает чек-лист вместе с коллекцией.
                foreach (var c in db.Constructor.GetAllCollections())
                    db.Constructor.DeleteCollection(c.Id!.Value);
            }

            // Второй запуск: та же папка данных.
            using (var registry = DatabaseRegistry.Open(dataDb, appDb))
            using (var db = DatabaseManager.Open(1, "Основная", dataDb))
            {
                Assert.False(registry.IsFreshInstall);
                Assert.False(IntroSeed.RunOnFreshInstall(registry, db));
                Assert.Empty(db.Constructor.GetAllCollections());
            }
        }
        finally
        {
            AppSettings.AttachStore(new FakeSettingsDatabase());
            TempDatabase.Cleanup(appDb);
            TempDatabase.Cleanup(dataDb);
            try { Directory.Delete(dir, recursive: true); } catch { /* best-effort */ }
        }
    }

    // ── Одноразовая отметка в app.db ──────────────────────────────────────────

    /// <summary>
    /// Отметка — дельта: пока чек-лист не заводился, ключа в app.db нет; сброс в дефолт
    /// удаляет ключ (общий инвариант настроек).
    /// </summary>
    [Fact]
    public void Intro_seed_flag_is_stored_as_a_delta()
    {
        var store = new FakeSettingsDatabase();
        AppSettings.AttachStore(store);

        Assert.False(AppSettings.LoadIntroSeedDone());
        Assert.Equal(0, store.Count);

        AppSettings.SaveIntroSeedDone(true);
        Assert.True(AppSettings.LoadIntroSeedDone());
        Assert.Equal(1, store.Count);

        AppSettings.SaveIntroSeedDone(false);
        Assert.False(AppSettings.LoadIntroSeedDone());
        Assert.Equal(0, store.Count);
    }
}
