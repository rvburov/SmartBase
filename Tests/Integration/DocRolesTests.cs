using SmartBase.Core.Models;
using SmartBase.Database.Repositories;

namespace SmartBase.Tests.Integration;

/// <summary>
/// Разметка ролей раздела «Библиотека документов»: тумблер на file-поле
/// (<c>SetFileFieldInLibrary</c>), авто-создание и подтягивание «Типа документа»
/// (<c>PlanRoles</c>/<c>EnsureRoles</c>), а также уход коллекции из раздела при снятом тумблере,
/// удалении столбца и смене типа.
///
/// Ключевое отличие от задач проверяется отдельно: роль <b>не эксклюзивна</b> — файловых
/// столбцов у коллекции может быть сколько угодно, и каждый дает разделу свои документы.
/// У задач попытка назначить второго исполнителя — ошибка, здесь это штатный режим.
/// </summary>
public sealed class DocRolesTests
{
    private static int[] Ids(HashSet<int> ids) => [.. ids.Order()];

    private static DbField File(ConstructorRepository repo, int collectionId,
                               string name = "Файл", int position = 1000) =>
        SampleData.AddField(repo, collectionId, name, "file", position);

    // ── Включение и выключение ───────────────────────────────────────────────

    [Fact]
    public void Toggling_a_file_field_connects_the_collection_and_creates_the_type_column()
    {
        using var db = new TempDatabase();
        var repo = db.Constructor;

        var colId = SampleData.AddCollection(repo, "Документы").Id!.Value;
        var file  = File(repo, colId);

        Assert.Empty(repo.GetDocCollectionIds());

        Assert.True(repo.SetFileFieldInLibrary(file.Id!.Value, true));
        Assert.Equal(new[] { colId }, Ids(repo.GetDocCollectionIds()));

        // «Тип документа» завелся сам — списком и БЕЗ единого пункта: виды документов у каждого
        // свои, и любой предложенный набор пришлось бы сперва вычищать.
        var type = repo.GetFields(colId).Single(f => f.Name == "Тип документа");
        Assert.Equal("select", type.FieldType);
        Assert.True(type.IsDocType);
        Assert.Empty(ConstructorRepository.ReadOptions(type.ConfigJson));
        Assert.Equal(type.Id!.Value, repo.DocTypeFieldId(colId));
    }

    /// <summary>
    /// Выключение НИЧЕГО не удаляет: снимается один флаг, столбцы и значения остаются. Вернули
    /// тумблер — прежняя разметка ожила сама, второй «Тип документа 2» не завелся.
    /// </summary>
    [Fact]
    public void Turning_the_toggle_off_keeps_the_columns_and_the_values()
    {
        using var db = new TempDatabase();
        var repo = db.Constructor;

        var colId = SampleData.AddCollection(repo).Id!.Value;
        var file  = File(repo, colId);
        repo.SetFileFieldInLibrary(file.Id!.Value, true);

        var typeId  = repo.DocTypeFieldId(colId)!.Value;
        var record  = SampleData.AddRecord(repo, colId, "Документ").Id!.Value;
        repo.SetFieldValue(record, typeId, "ГОСТ");

        repo.SetFileFieldInLibrary(file.Id!.Value, false);
        Assert.Empty(repo.GetDocCollectionIds());
        // Столбец на месте, значение тоже.
        Assert.Contains(repo.GetFields(colId), f => f.Id == typeId);

        repo.SetFileFieldInLibrary(file.Id!.Value, true);
        Assert.Equal(new[] { colId }, Ids(repo.GetDocCollectionIds()));
        Assert.Single(repo.GetFields(colId), f => f.Name == "Тип документа");
        Assert.Equal(typeId, repo.DocTypeFieldId(colId));
    }

    // ── Роль НЕ эксклюзивна ──────────────────────────────────────────────────

    /// <summary>
    /// Два файловых столбца — это два РАЗНЫХ документа записи («Оригинал» и «Скан»), и показать
    /// раздел обязан оба. У задач второй исполнитель запрещен намеренно (одна запись — один
    /// пункт списка дел); здесь такого запрета быть не должно.
    /// </summary>
    [Fact]
    public void Several_file_fields_can_feed_the_section_at_once()
    {
        using var db = new TempDatabase();
        var repo = db.Constructor;

        var colId    = SampleData.AddCollection(repo).Id!.Value;
        var original = File(repo, colId, "Оригинал", 1000);
        var scan     = File(repo, colId, "Скан",     2000);

        repo.SetFileFieldInLibrary(original.Id!.Value, true);
        repo.SetFileFieldInLibrary(scan.Id!.Value,     true);

        Assert.Equal([original.Id!.Value, scan.Id!.Value], repo.DocFileFieldIds(colId));
        // «Тип документа» все равно один: эта роль эксклюзивна.
        Assert.Single(repo.GetFields(colId), f => f.IsDocType);
    }

    [Fact]
    public void A_record_with_two_file_fields_gives_two_documents()
    {
        using var db = new TempDatabase();
        var repo = db.Constructor;

        var colId    = SampleData.AddCollection(repo).Id!.Value;
        var original = File(repo, colId, "Оригинал", 1000);
        var scan     = File(repo, colId, "Скан",     2000);
        repo.SetFileFieldInLibrary(original.Id!.Value, true);
        repo.SetFileFieldInLibrary(scan.Id!.Value,     true);

        var record = SampleData.AddRecord(repo, colId, "ГОСТ Р 34.11").Id!.Value;
        repo.SetFieldValue(record, original.Id!.Value, "/docs/gost.pdf");
        repo.SetFieldValue(record, scan.Id!.Value,     "/docs/gost-scan.pdf");

        var docs = repo.GetDocRecords();
        Assert.Equal(2, docs.Count);
        Assert.Equal(new[] { "/docs/gost.pdf", "/docs/gost-scan.pdf" },
                     docs.Select(d => d.Path).ToArray());
        // Документ опознается парой (запись, файловое поле): id записи у обоих один.
        Assert.Single(docs.Select(d => d.Id).Distinct());
        Assert.Equal(2, docs.Select(d => d.FileFieldId).Distinct().Count());
    }

    /// <summary>Пустая ссылка документом не считается: показывать в библиотеке нечего.</summary>
    [Fact]
    public void An_empty_path_is_not_a_document()
    {
        using var db = new TempDatabase();
        var repo = db.Constructor;

        var colId = SampleData.AddCollection(repo).Id!.Value;
        var file  = File(repo, colId);
        repo.SetFileFieldInLibrary(file.Id!.Value, true);

        var filled = SampleData.AddRecord(repo, colId, "Есть файл", 1000).Id!.Value;
        SampleData.AddRecord(repo, colId, "Нет файла", 2000);
        repo.SetFieldValue(filled, file.Id!.Value, "/docs/a.pdf");

        Assert.Single(repo.GetDocRecords());
    }

    // ── Подтягивание одноименного столбца ────────────────────────────────────

    /// <summary>
    /// Существующий столбец «Тип документа» предлагается подтянуть, а не дублировать: это и есть
    /// развилка, которую показывает окно подтверждения при включении тумблера.
    /// </summary>
    [Fact]
    public void An_existing_type_column_shows_up_as_a_candidate()
    {
        using var db = new TempDatabase();
        var repo = db.Constructor;

        var colId = SampleData.AddCollection(repo).Id!.Value;
        var file  = File(repo, colId);
        var mine  = SampleData.AddField(repo, colId, "Тип документа", "select", 2000);

        var plan = repo.PlanRoles(colId, ConstructorRepository.DocRoles, file.Id!.Value);

        Assert.True(plan.NeedsConfirmation);
        var candidate = Assert.Single(plan.Candidates);
        Assert.Equal(FieldRole.DocType, candidate.Role);
        Assert.Equal(mine.Id!.Value, candidate.FieldId);
        // При отказе заведется отдельное поле с суффиксом — имя уже занято.
        Assert.Equal(["Тип документа 2"], plan.FieldsToCreateIfDeclined);
    }

    [Fact]
    public void Adopting_marks_the_existing_column_instead_of_creating_a_new_one()
    {
        using var db = new TempDatabase();
        var repo = db.Constructor;

        var colId = SampleData.AddCollection(repo).Id!.Value;
        var file  = File(repo, colId);
        var mine  = SampleData.AddField(repo, colId, "Тип документа", "select", 2000);

        repo.SetFileFieldInLibrary(file.Id!.Value, true, adoptByName: true);

        Assert.Equal(mine.Id!.Value, repo.DocTypeFieldId(colId));
        Assert.Single(repo.GetFields(colId), f => f.Name == "Тип документа");
    }

    [Fact]
    public void Declining_leaves_the_existing_column_alone_and_creates_its_own()
    {
        using var db = new TempDatabase();
        var repo = db.Constructor;

        var colId = SampleData.AddCollection(repo).Id!.Value;
        var file  = File(repo, colId);
        var mine  = SampleData.AddField(repo, colId, "Тип документа", "select", 2000);

        repo.SetFileFieldInLibrary(file.Id!.Value, true, adoptByName: false);

        var role = repo.DocTypeFieldId(colId)!.Value;
        Assert.NotEqual(mine.Id!.Value, role);
        Assert.Equal("Тип документа 2", repo.GetFields(colId).Single(f => f.Id == role).Name);
        // Чужой столбец роли не получил — он остался нетронутым.
        Assert.False(repo.GetFields(colId).Single(f => f.Id == mine.Id!.Value).IsDocType);
    }

    /// <summary>
    /// Подтягивание идет ТОЛЬКО по совпадению имени плюс подходящий тип. Текстовый «Тип
    /// документа» кандидатом не бывает: подтянутый столбец без списка значений дал бы
    /// неработающую привязку — то же правило, что у «Статуса» задач.
    /// </summary>
    [Fact]
    public void A_text_column_named_like_the_role_is_not_a_candidate()
    {
        using var db = new TempDatabase();
        var repo = db.Constructor;

        var colId = SampleData.AddCollection(repo).Id!.Value;
        var file  = File(repo, colId);
        SampleData.AddField(repo, colId, "Тип документа", "text", 2000);

        var plan = repo.PlanRoles(colId, ConstructorRepository.DocRoles, file.Id!.Value);
        Assert.False(plan.NeedsConfirmation);
    }

    /// <summary>
    /// Подтягивание берет ТОЛЬКО сам столбец: пункты чужого списка не дополняются и не
    /// переставляются. Раздел получает столбец с теми значениями, которые человек уже завел, —
    /// подмешанные виды документов он потом вычищал бы руками, а забытый разошелся бы по фильтру.
    /// </summary>
    [Fact]
    public void Adopting_does_not_touch_the_options_of_the_existing_list()
    {
        using var db = new TempDatabase();
        var repo = db.Constructor;

        var colId = SampleData.AddCollection(repo).Id!.Value;
        var file  = File(repo, colId);
        var mine  = repo.AddField(new DbField
        {
            CollectionId = colId, Name = "Тип документа", FieldType = "select", Position = 2000,
            ConfigJson   = """{"options":["Чертеж","Схема"]}""",
        });

        repo.SetFileFieldInLibrary(file.Id!.Value, true, adoptByName: true);

        var options = ConstructorRepository.ReadOptions(
            repo.GetFields(colId).Single(f => f.Id == mine.Id!.Value).ConfigJson);
        Assert.Equal(["Чертеж", "Схема"], options);
    }

    /// <summary>Пустой одноименный список так и остается пустым — заполнит его человек.</summary>
    [Fact]
    public void Adopting_an_empty_list_leaves_it_empty()
    {
        using var db = new TempDatabase();
        var repo = db.Constructor;

        var colId = SampleData.AddCollection(repo).Id!.Value;
        var file  = File(repo, colId);
        var mine  = SampleData.AddField(repo, colId, "Тип документа", "select", 2000);

        repo.SetFileFieldInLibrary(file.Id!.Value, true, adoptByName: true);

        Assert.Empty(ConstructorRepository.ReadOptions(
            repo.GetFields(colId).Single(f => f.Id == mine.Id!.Value).ConfigJson));
    }

    /// <summary>Роль «Статус» задач свои три стадии засевать не перестала — правило разное.</summary>
    [Fact]
    public void Task_status_still_gets_its_default_stages()
    {
        using var db = new TempDatabase();
        var repo = db.Constructor;

        var colId = SampleData.AddCollection(repo).Id!.Value;
        var exec  = SampleData.AddField(repo, colId, "Исполнитель", "user");
        repo.SetUserFieldTaskExec(exec.Id!.Value, true);

        var status = repo.GetFields(colId).Single(f => f.IsTaskStatus);
        Assert.Equal(ConstructorRepository.TaskStatusOptions,
                     ConstructorRepository.ReadOptions(status.ConfigJson));
    }

    // ── Чужая роль ───────────────────────────────────────────────────────────

    /// <summary>
    /// Две роли на одном поле запрещены: оба значения писались бы в один FieldId и затирали
    /// друг друга. Столбец с ролью «Тип документа» источником документов быть не может.
    /// </summary>
    [Fact]
    public void A_column_carrying_the_type_role_cannot_become_a_document_source()
    {
        using var db = new TempDatabase();
        var repo = db.Constructor;

        var colId = SampleData.AddCollection(repo).Id!.Value;

        // File-поле, названное как роль: флага на нем еще нет, но разметка ролей нашла бы его
        // ПО ИМЕНИ и повесила вторую роль на то же поле. Запрет обязан сработать и в этом случае.
        var named = File(repo, colId, "Тип документа");

        Assert.Throws<InvalidOperationException>(
            () => repo.SetFileFieldInLibrary(named.Id!.Value, true));
        Assert.Empty(repo.GetDocCollectionIds());
    }

    /// <summary>Тот же запрет по уже проставленному флагу, а не по имени.</summary>
    [Fact]
    public void A_column_flagged_as_the_type_role_cannot_become_a_document_source()
    {
        using var db = new TempDatabase();
        var repo = db.Constructor;

        var colId = SampleData.AddCollection(repo).Id!.Value;
        var ghost = repo.AddField(new DbField
        {
            CollectionId = colId, Name = "Вид", FieldType = "file",
            Position     = 1000,  IsDocType = true,
        });

        Assert.Throws<InvalidOperationException>(
            () => repo.SetFileFieldInLibrary(ghost.Id!.Value, true));
    }

    // ── Уход из раздела ──────────────────────────────────────────────────────

    /// <summary>
    /// Смена типа рвет привязку так же, как удаление столбца: раздел физически не сможет читать
    /// данные. Флаг снимается записью (<c>ClearRolesIncompatibleWithType</c>), а не остается
    /// ролью-призраком над неработающей привязкой.
    /// </summary>
    [Fact]
    public void Changing_the_type_away_from_file_drops_the_collection_from_the_section()
    {
        using var db = new TempDatabase();
        var repo = db.Constructor;

        var colId = SampleData.AddCollection(repo).Id!.Value;
        var file  = File(repo, colId);
        repo.SetFileFieldInLibrary(file.Id!.Value, true);
        Assert.Equal(new[] { colId }, Ids(repo.GetDocCollectionIds()));

        // Предупреждение считается ДО записи — именно так его показывает окно свойства.
        Assert.Contains(FieldRole.DocFile, repo.RolesLostByTypeChange(file.Id!.Value, "text"));

        repo.UpdateField(new DbField
        {
            Id = file.Id, CollectionId = colId, Name = file.Name,
            FieldType = "text", Position = file.Position, IsDocFile = true,
        });

        Assert.Empty(repo.GetDocCollectionIds());
        Assert.False(repo.HasDocFileField(colId));
        // Флаг снят записью, а не оставлен ролью-призраком над неработающей привязкой.
        Assert.False(repo.GetFields(colId).Single(f => f.Id == file.Id!.Value).IsDocFile);
    }

    [Fact]
    public void Deleting_the_file_column_drops_the_collection_from_the_section()
    {
        using var db = new TempDatabase();
        var repo = db.Constructor;

        var colId = SampleData.AddCollection(repo).Id!.Value;
        var file  = File(repo, colId);
        repo.SetFileFieldInLibrary(file.Id!.Value, true);

        repo.DeleteField(file.Id!.Value);

        Assert.Empty(repo.GetDocCollectionIds());
        Assert.False(repo.HasDocFileField(colId));
    }

    /// <summary>Одну из двух файловых колонок удалили — коллекция остается в разделе.</summary>
    [Fact]
    public void Deleting_one_of_two_file_columns_keeps_the_collection_connected()
    {
        using var db = new TempDatabase();
        var repo = db.Constructor;

        var colId    = SampleData.AddCollection(repo).Id!.Value;
        var original = File(repo, colId, "Оригинал", 1000);
        var scan     = File(repo, colId, "Скан",     2000);
        repo.SetFileFieldInLibrary(original.Id!.Value, true);
        repo.SetFileFieldInLibrary(scan.Id!.Value,     true);

        repo.DeleteField(scan.Id!.Value);

        Assert.Equal(new[] { colId }, Ids(repo.GetDocCollectionIds()));
        Assert.Equal([original.Id!.Value], repo.DocFileFieldIds(colId));
    }

    // ── Подключение коллекции из сайдбара ────────────────────────────────────

    [Fact]
    public void Connecting_a_collection_adopts_a_free_file_field()
    {
        using var db = new TempDatabase();
        var repo = db.Constructor;

        var colId = SampleData.AddCollection(repo).Id!.Value;
        var file  = File(repo, colId, "Скан");

        Assert.Equal(file.Id!.Value, repo.EnsureDocCollection(colId));
        Assert.Equal(new[] { colId }, Ids(repo.GetDocCollectionIds()));
        Assert.NotNull(repo.DocTypeFieldId(colId));
    }

    [Fact]
    public void Connecting_a_collection_without_file_fields_creates_one()
    {
        using var db = new TempDatabase();
        var repo = db.Constructor;

        var colId = SampleData.AddCollection(repo).Id!.Value;
        SampleData.AddField(repo, colId, "Название");

        var fileId = repo.EnsureDocCollection(colId);
        var field  = repo.GetFields(colId).Single(f => f.Id == fileId);

        Assert.Equal("Файл", field.Name);
        Assert.Equal("file", field.FieldType);
        Assert.True(field.IsDocFile);
    }

    // ── Починка при монтировании ─────────────────────────────────────────────

    [Fact]
    public void Repair_clears_the_role_left_on_an_incompatible_type()
    {
        using var db = new TempDatabase();
        var repo = db.Constructor;

        var colId = SampleData.AddCollection(repo).Id!.Value;
        // Поле с флагом на неподходящем типе — состояние старой базы или ручной правки.
        var ghost = repo.AddField(new DbField
        {
            CollectionId = colId, Name = "Призрак", FieldType = "text",
            Position     = 1000,  IsDocFile = true,
        });

        Assert.Equal(1, repo.RepairDocFieldRoles());
        Assert.False(repo.GetFields(colId).Single(f => f.Id == ghost.Id!.Value).IsDocFile);
    }

    [Fact]
    public void Repair_is_a_no_op_on_a_healthy_database()
    {
        using var db = new TempDatabase();
        var repo = db.Constructor;

        var colId = SampleData.AddCollection(repo).Id!.Value;
        var file  = File(repo, colId);
        repo.SetFileFieldInLibrary(file.Id!.Value, true);

        Assert.Equal(0, repo.RepairDocFieldRoles());
    }
}
