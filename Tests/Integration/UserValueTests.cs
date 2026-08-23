using System.Globalization;
using Microsoft.EntityFrameworkCore;
using SmartBase.Core.Models;
using SmartBase.Database;
using SmartBase.Database.Entities;
using SmartBase.Database.Repositories;
using SmartBase.Utils;

namespace SmartBase.Tests.Integration;

/// <summary>
/// Значение типа свойства «пользователь» В ДАННЫХ (docs/architecture/users-permissions.md):
/// в ячейке лежит СТРОКА-ИМЯ — исторический снапшот на момент выбора, а не ссылка на строку
/// db_users. Отсюда все поведение, которое здесь закрепляется:
///  • пользователь переименовался или ушел из базы — значение в ячейке НЕ меняется и НЕ пропадает
///    (иначе «кто исполнитель» задним числом переписывался бы у всех записей сразу);
///  • фильтр/счетчики/сортировка колонки работают по этой строке — включая имена, которых уже нет
///    среди допущенных (не-relation ветка фильтра ничем не покрыта, а именно она обслуживает
///    «пользователя», «список», «текст»);
///  • перенос записи (в т.ч. в ДРУГУЮ базу) везет значение строкой, и редактор ячейки в целевой базе
///    обязан показать исторического исполнителя, а не пустоту (ради этого в GetUserOptions появился
///    includeValue — но проверено это было только внутри одной базы);
///  • вычисляемые колонки автора (created_by/updated_by) живут НЕ в db_field_values, а в аудите
///    записи — и должны одинаково читаться сортировкой, фильтром и экспортом.
///
/// «Чужого автора» у записи ставим вторым подключением: все тесты идут от одного OS-пользователя,
/// иначе CreatedBy у любой записи — это мы сами.
/// </summary>
public sealed class UserValueTests
{
    // Дата создания «до нас» — чужой аудит, который перенос обязан сохранить, а дубль — перештамповать.
    private const string OldCreatedAt = "2024-01-15T08:00:00.0000000";

    // Регистрация соседа в базе — как PermissionService при монтировании на его машине
    // (тот же хелпер, что в UserOptionsTests).
    private static int JoinUser(string path, string key, string name, bool approved = false)
    {
        using var neighbour = new AppDbContext(path);
        var user = new DbUserEntity
        {
            UserKey     = key,
            DisplayName = name,
            IsApproved  = approved,
            CreatedAt   = DateTime.UtcNow.ToString("o"),
        };
        neighbour.DbUsers.Add(user);
        neighbour.SaveChanges();
        return user.Id;
    }

    // «Эту запись создал другой сотрудник»: аудит записи правим ВТОРЫМ подключением — своим
    // API мы автором можем стать только сами (CurrentUser.DisplayName).
    private static void StampAuthor(string path, int recordId, string author, string? createdAt = null)
    {
        using var neighbour = new AppDbContext(path);
        var row = neighbour.DbRecords.First(r => r.Id == recordId);
        row.CreatedBy = author;
        row.UpdatedBy = author;
        if (createdAt is not null) { row.CreatedAt = createdAt; row.UpdatedAt = createdAt; }
        neighbour.SaveChanges();
    }

    // Записи коллекции свежим подключением, в порядке таблицы (Position, Id). Свежим — потому что
    // общий контекст держит записи в трекере, и правки соседа мимо него не видны (GetRecord → Find).
    private static List<DbRecordEntity> ReadRecords(string path, int collectionId)
    {
        using var check = new AppDbContext(path);
        return check.DbRecords.AsNoTracking()
                    .Where(r => r.CollectionId == collectionId)
                    .OrderBy(r => r.Position).ThenBy(r => r.Id)
                    .ToList();
    }

    private static DateTime Ts(string value) =>
        DateTime.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

    [Fact]
    public void Renaming_user_does_not_rewrite_stored_cell_values()
    {
        using var db = new TempDatabase();
        var repo = db.Constructor;
        var col  = SampleData.AddCollection(repo, "Заявки").Id!.Value;
        var exec = SampleData.AddField(repo, col, "Исполнитель", "user", position: 10).Id!.Value;

        JoinUser(db.Path, "sidorov@pc4", "Сидоров С.", approved: true);

        var rec = SampleData.AddRecord(repo, col, "Заявка", 10).Id!.Value;
        repo.SetFieldValue(rec, exec, "Сидоров С.");   // выбрали исполнителя тем именем, что было в базе

        // Сосед заполнил ФИО в профиле — снапшот имени в db_users обновился с его машины.
        using (var neighbour = new AppDbContext(db.Path))
        {
            neighbour.DbUsers.First(u => u.UserKey == "sidorov@pc4").DisplayName = "Сидоров Семен Семенович";
            neighbour.SaveChanges();
        }

        // 1. Данные не мигрируют вслед за профилем: в ячейке — имя на момент выбора. Иначе одна
        //    правка ФИО задним числом переписала бы историю во всех записях всех коллекций.
        Assert.Equal("Сидоров С.", repo.GetFieldValuesBatch([rec])[rec][exec]);

        // 2. Живые опции — только актуальные имена допущенных: выбрать можно лишь «Сидоров Семен Семенович».
        var live = repo.GetUserOptions();
        Assert.Contains("Сидоров Семен Семенович", live);
        Assert.DoesNotContain("Сидоров С.", live);

        // 3. Редактор ячейки строится с текущим значением: историческое имя ОБЯЗАНО быть в списке и
        //    ПОСЛЕДНИМ (живые опции сверху). Не будет его в списке — первый же клик по ячейке
        //    подставит чужое имя и затрет снапшот.
        var forCell = repo.GetUserOptions("Сидоров С.");
        Assert.Contains("Сидоров Семен Семенович", forCell);
        Assert.Equal("Сидоров С.", forCell[^1]);

        // 4. Фильтр и счетчики колонки работают по СТРОКЕ значения, а не по составу db_users:
        //    запись со «старым» исполнителем продолжает находиться.
        Assert.Equal([rec], repo.GetFilteredRecordIds(col,
            new Dictionary<string, HashSet<string>> { [exec.ToString()] = ["Сидоров С."] }));
        Assert.Equal(("Сидоров С.", 1),
                     repo.GetFieldValueCounts(col, exec).Single(c => c.Value == "Сидоров С."));
    }

    [Fact]
    public async Task User_column_filter_counts_and_empty_bucket()
    {
        using var db = new TempDatabase();
        var repo = db.Constructor;
        var col  = SampleData.AddCollection(repo, "Заявки").Id!.Value;
        var exec = SampleData.AddField(repo, col, "Исполнитель", "user", position: 10).Id!.Value;

        // Новая запись автоматически получает в user-поле текущего пользователя (AddRecord) —
        // для теста перезаписываем значения явно, включая «ячейку очистили».
        var r1 = SampleData.AddRecord(repo, col, "З1", 10).Id!.Value;
        var r2 = SampleData.AddRecord(repo, col, "З2", 20).Id!.Value;
        var r3 = SampleData.AddRecord(repo, col, "З3", 30).Id!.Value;
        repo.SetFieldValue(r1, exec, "Иванов Иван");
        repo.SetFieldValue(r2, exec, "Петров Петр");
        repo.SetFieldValue(r3, exec, "");

        var key = exec.ToString();
        Dictionary<string, HashSet<string>> F(params string[] v) => new() { [key] = [.. v] };

        // Фильтр колонки: одно имя, несколько имен (ИЛИ), «(Пусто)» — отдельным ведром.
        Assert.Equal([r1],     repo.GetFilteredRecordIds(col, F("Иванов Иван")));
        Assert.Equal([r1, r2], repo.GetFilteredRecordIds(col, F("Иванов Иван", "Петров Петр")));
        Assert.Equal([r3],     repo.GetFilteredRecordIds(col, F("")));

        // Список значений в окне фильтра: непустые — в порядке таблицы, «(Пусто)» последним.
        var counts = repo.GetFieldValueCounts(col, exec);
        Assert.Equal(("Иванов Иван", 1), counts[0]);
        Assert.Equal(("Петров Петр", 1), counts[1]);
        Assert.Equal(("", 1),            counts[^1]);

        // Окно фильтра считает то же самое в фоне (отдельное подключение) — расхождение означало бы
        // «в списке одно, после применения другое».
        Assert.Equal(counts, await repo.GetColumnCountsAsync(col, key));

        // Сортировка колонки берет значения отсюда: имена должны быть видны как обычные строки.
        var vals = repo.GetColumnValues(col, key);
        Assert.Equal("Иванов Иван", vals[r1]);
        Assert.Equal("Петров Петр", vals[r2]);
        Assert.Equal("",            vals[r3]);
    }

    // Готовит перенос одной записи с чужим автором из «Заявок» в «Архив» (та же база) и
    // возвращает id новой записи вместе с id user-поля в целевой коллекции.
    private static (int MovedId, int ExecFieldB) MoveRecordWithForeignAuthor(TempDatabase db)
    {
        var repo = db.Constructor;
        var colA = SampleData.AddCollection(repo, "Заявки").Id!.Value;
        var exec = SampleData.AddField(repo, colA, "Исполнитель", "user", position: 10).Id!.Value;
        var colB = SampleData.AddCollection(repo, "Архив").Id!.Value;

        var rec = SampleData.AddRecord(repo, colA, "Заявка", 10).Id!.Value;
        repo.SetFieldValue(rec, exec, "Иванов Иван");

        // Запись создал другой сотрудник — и «Расположение» ее только ПЕРЕКЛАДЫВАЕТ, а не создает заново.
        StampAuthor(db.Path, rec, "Сидоров Семен", OldCreatedAt);

        var newIds = repo.MoveRecordsToCollectionIsolated(db.Path, colA, [rec], colB,
                                                          createMissingFields: true, sourceDbId: 1, targetDbId: 1);
        var moved = Assert.Single(newIds);
        return (moved, repo.GetFields(colB).Single(f => f.Name == "Исполнитель").Id!.Value);
    }

    [Fact]
    public void Moving_record_to_another_collection_keeps_user_value()
    {
        using var db = new TempDatabase();
        var (moved, execB) = MoveRecordWithForeignAuthor(db);

        // Значение user-поля переезжает строкой: в целевой коллекции создалось одноименное
        // свойство, и исторический исполнитель приехал в него как есть.
        Assert.Equal("Иванов Иван", db.Constructor.GetFieldValuesBatch([moved])[moved][execB]);
    }

    [Fact]
    public void Moving_record_to_another_collection_keeps_original_author()
    {
        using var db = new TempDatabase();
        var (moved, _) = MoveRecordWithForeignAuthor(db);

        // MoveRecordsCore переносит все четыре поля аудита: «Расположение» перекладывает
        // запись, а не создает новую. Раньше строка создавалась пустой, StampNewRecordAudit
        // (AppDbContext) штамповал переносящего и «сейчас» — запись коллеги выглядела созданной
        // вами сегодня, колонки «Создание пользователем»/«Дата создания» врали, а журнал при
        // этом честно писал «перенес». Перенос КОЛЛЕКЦИИ аудит сохранял всегда
        // (CrossMoveTests.Move_copies_to_target_with_values_and_clears_source) — приложение
        // не должно противоречить само себе.
        var header = db.Constructor.GetRecord(moved)!;
        Assert.Equal("Сидоров Семен", header.CreatedBy);
        Assert.Equal(OldCreatedAt,    header.CreatedAt);
    }

    [Fact]
    public void Moving_event_to_another_collection_in_calendar_keeps_original_author()
    {
        using var db = new TempDatabase();
        var repo = db.Constructor;

        var colA = SampleData.AddCollection(repo, "Заявки").Id!.Value;
        var colB = SampleData.AddCollection(repo, "Архив").Id!.Value;

        var ev = repo.AddCalendarRecord(new CalendarRecord
        {
            CollectionId = colA, Title = "Осмотр насосной",
            StartDt = new DateTime(2026, 4, 1, 9, 0, 0),
            EndDt   = new DateTime(2026, 4, 1, 10, 0, 0),
        });

        StampAuthor(db.Path, ev.Id!.Value, "Сидоров Семен", OldCreatedAt);
        // Правку соседа наш контекст не видит — запись он держит в трекере с момента создания.
        // Чистим, как это делает новый сеанс: иначе тест мерил бы кеш, а не поведение переноса.
        db.Context.ChangeTracker.Clear();

        // Смена коллекции у события ПЕРЕСОЗДАЕТ запись (AddCalendarRecord + копия значений и
        // связей + удаление исходной) — тот же перенос, что «Расположение» в таблице, и аудит
        // он обязан сохранять так же. Раньше CreatedAt = «сейчас», а StampNewRecordAudit и
        // SetFieldValue проставляли переносящего: событие коллеги выглядело созданным вами.
        ev.CollectionId = colB;
        repo.UpdateCalendarRecord(ev);

        var moved = Assert.Single(ReadRecords(db.Path, colB));
        Assert.Equal("Сидоров Семен", moved.CreatedBy);
        Assert.Equal("Сидоров Семен", moved.UpdatedBy);
        Assert.Equal(OldCreatedAt,    moved.CreatedAt);
        Assert.Empty(ReadRecords(db.Path, colA));
    }

    [Fact]
    public void Cross_db_moved_record_keeps_foreign_user_name_selectable()
    {
        using var dbA = new TempDatabase(dbId: 1, name: "A");
        using var dbB = new TempDatabase(dbId: 2, name: "B");

        // База A: исполнитель — допущенный пользователь ИМЕННО ЭТОЙ базы.
        var colA = SampleData.AddCollection(dbA.Constructor, "Задачи").Id!.Value;
        var exec = SampleData.AddField(dbA.Constructor, colA, "Исполнитель", "user", position: 10).Id!.Value;
        JoinUser(dbA.Path, "ivanov@pc2", "Иванов Иван", approved: true);
        var rec = SampleData.AddRecord(dbA.Constructor, colA, "Задача", 10).Id!.Value;
        dbA.Constructor.SetFieldValue(rec, exec, "Иванов Иван");

        // База B: своя коллекция, и «Иванова» среди ее пользователей нет и не будет.
        var colB = SampleData.AddCollection(dbB.Constructor, "Задачи B").Id!.Value;

        var newIds = dbB.Constructor.MoveRecordsToCollectionIsolated(dbA.Path, colA, [rec], colB,
                                                                     createMissingFields: true,
                                                                     sourceDbId: 1, targetDbId: 2);
        var moved = Assert.Single(newIds);

        // Значение приехало строкой — межбазовый перенос значение user-поля не теряет.
        using (var check = new AppDbContext(dbB.Path))
        {
            var repoB = new ConstructorRepository(check);
            var execB = repoB.GetFields(colB).Single(f => f.Name == "Исполнитель").Id!.Value;
            Assert.Equal("Иванов Иван", repoB.GetFieldValuesBatch([moved])[moved][execB]);
        }

        // Живые опции целевой базы — только ее собственные пользователи: «Иванова» выбрать нельзя.
        var live = dbB.Constructor.GetUserOptions();
        Assert.DoesNotContain("Иванов Иван", live);
        Assert.Contains(CurrentUser.DisplayName, live);

        // Но редактор ячейки в целевой базе обязан показать исторического исполнителя (последним
        // пунктом, под живыми) — иначе после переезда ячейка выглядит пустой, а первый клик ее затрет.
        var forCell = dbB.Constructor.GetUserOptions("Иванов Иван");
        Assert.Equal("Иванов Иван", forCell[^1]);
    }

    [Fact(Skip = "дефект продукта: вычисляемые колонки (created_by/updated_by/created_at/updated_at) " +
                 "не поддержаны в GetColumnValues/GetFieldValueCounts/GetFilteredRecordIds/" +
                 "ReadExportRowsIsolated — сортировка, фильтр и экспорт видят их пустыми")]
    public async Task Author_column_is_filterable_sortable_and_exported()
    {
        using var db = new TempDatabase();
        var repo = db.Constructor;
        var col    = SampleData.AddCollection(repo, "Заявки").Id!.Value;
        var author = SampleData.AddField(repo, col, "Создал", "created_by", position: 10).Id!.Value;
        SampleData.AddField(repo, col, "Комментарий", "text", position: 20);

        var r1 = SampleData.AddRecord(repo, col, "З1", 10).Id!.Value;
        var r2 = SampleData.AddRecord(repo, col, "З2", 20).Id!.Value;
        StampAuthor(db.Path, r1, "Иванов Иван");
        StampAuthor(db.Path, r2, "Петров Петр");

        var key = author.ToString();

        // КРАСНЫЙ ТЕСТ. Значения вычисляемых типов (created_by/updated_by/created_at/updated_at)
        // хранятся в аудите записи (db_records), а сортировка (GetColumnValues), счетчики фильтра
        // (ColumnCounts → GetFieldValueCounts), фильтр (GetFilteredRecordIds) и экспорт
        // (ReadExportRowsIsolated) читают db_field_values, где строк у таких полей нет вовсе.
        // Для пользователя это выглядит так: колонка «Создал» в таблице заполнена, но сортировка
        // по ней ничего не делает, окно фильтра предлагает одно «(Пусто)» на все записи, а в
        // выгрузке столбец пустой. Правку делать ОБЩЕЙ для всех вычисляемых типов, тест — на created_by.

        // 1. Сортировка колонки.
        var vals = repo.GetColumnValues(col, key);
        Assert.Equal("Иванов Иван", vals.GetValueOrDefault(r1, ""));
        Assert.Equal("Петров Петр", vals.GetValueOrDefault(r2, ""));

        // 2. Окно фильтра: пункты — авторы, а не одно общее «(Пусто)».
        var counts = await repo.GetColumnCountsAsync(col, key);
        Assert.Contains(counts, c => c.Value == "Иванов Иван" && c.Count == 1);
        Assert.Contains(counts, c => c.Value == "Петров Петр" && c.Count == 1);
        Assert.DoesNotContain(counts, c => c.Value == "");

        // 3. Применение фильтра по автору.
        Assert.Equal([r1], repo.GetFilteredRecordIds(col,
            new Dictionary<string, HashSet<string>> { [key] = ["Иванов Иван"] }));

        // 4. Экспорт: колонка есть в списке, но ее значения в строках пустые.
        var cols = repo.GetExportColumnsIsolated(col, includeHidden: true);
        var idx  = cols.FindIndex(c => c.Header == "Создал");
        Assert.True(idx >= 0, "колонка «Создал» обязана попадать в экспорт как обычный столбец таблицы");

        var rows = repo.ReadExportRowsIsolated(col, cols, false, CancellationToken.None).ToList();
        Assert.Equal(2, rows.Count);
        Assert.Equal("Иванов Иван", rows[0][idx]);
        Assert.Equal("Петров Петр", rows[1][idx]);
    }

    [Fact]
    public void Duplicated_collection_keeps_user_values_but_restamps_author()
    {
        using var db = new TempDatabase();
        var repo = db.Constructor;
        var col  = SampleData.AddCollection(repo, "Заявки").Id!.Value;
        var exec = SampleData.AddField(repo, col, "Исполнитель", "user", position: 10).Id!.Value;

        var r1 = SampleData.AddRecord(repo, col, "З1", 10).Id!.Value;
        var r2 = SampleData.AddRecord(repo, col, "З2", 20).Id!.Value;
        repo.SetFieldValue(r1, exec, "Иванов Иван");
        repo.SetFieldValue(r2, exec, "Петров Петр");

        // Обе записи создал другой сотрудник и давно.
        StampAuthor(db.Path, r1, "Сидоров Семен", OldCreatedAt);
        StampAuthor(db.Path, r2, "Сидоров Семен", OldCreatedAt);

        var copyId = repo.DuplicateCollection(col).Id!.Value;

        // Дубль — это НОВЫЕ записи: значения полей копируются 1:1 (исполнителей терять нельзя),
        // а аудит перештамповывается осознанно (CopyCollectionData: «дубль коллекции = новые записи»).
        // Асимметрия «значение переносится, авторство — нет» легко ломается при рефакторинге,
        // поэтому закрепляем обе половины сразу.
        var execCopy = repo.GetFields(copyId).Single(f => f.Name == "Исполнитель").Id!.Value;
        var copies   = ReadRecords(db.Path, copyId);
        Assert.Equal(2, copies.Count);

        var values = repo.GetFieldValuesBatch(copies.Select(r => r.Id).ToList());
        Assert.Equal("Иванов Иван", values[copies[0].Id][execCopy]);
        Assert.Equal("Петров Петр", values[copies[1].Id][execCopy]);

        Assert.All(copies, r => Assert.Equal(CurrentUser.DisplayName, r.CreatedBy));
        Assert.All(copies, r => Assert.Equal(CurrentUser.DisplayName, r.UpdatedBy));
        Assert.All(copies, r => Assert.True(Ts(r.CreatedAt) > Ts(OldCreatedAt),
                                            "копия создана сейчас — дата создания источника не наследуется"));

        // Источник дубль не трогает вообще: ни значений, ни аудита.
        var originals = ReadRecords(db.Path, col);
        Assert.All(originals, r => Assert.Equal("Сидоров Семен", r.CreatedBy));
        Assert.All(originals, r => Assert.Equal(OldCreatedAt, r.CreatedAt));
    }
}
