using Microsoft.EntityFrameworkCore;
using SmartBase.Core.Models;
using SmartBase.Database.Entities;

namespace SmartBase.Database.Repositories;

// ── Раздел «Библиотека документов» ────────────────────────────────────────────
// Третий агрегатный раздел того же устройства, что календарь и задачи. Источник
// документа — file-поле с флагом IsDocFile, документ = пара (RecordId, file-FieldId).
//
// Ключевое отличие от задач: роль НЕ эксклюзивна. У задач исполнитель один на
// коллекцию намеренно (иначе каждая запись давала бы карточку на каждое user-поле,
// и один и тот же пункт списка дублировался бы). Здесь дублирования нет по смыслу:
// два файловых столбца — это два РАЗНЫХ документа записи («Оригинал» и «Скан»),
// и показать нужно оба. Поэтому правило то же, что у календарных дат: сколько
// столбцов включено, столько документов запись и дает.
//
// Роль «Тип документа» (select/status, IsDocType) — обычное поле коллекции,
// авто-создается при первом включении тумблера. Ни проекции, ни своей таблицы у
// раздела нет: чтение идет от db_records по индексу (CollectionId, Position).
public partial class ConstructorRepository
{
    private const string FieldFile    = "Файл";
    private const string FieldDocType = "Тип документа";

    // Пунктов по умолчанию у «Типа документа» НЕТ — в отличие от «Статуса» задач с его тремя
    // стадиями. Три стадии задачи — это сам смысл раздела, они одинаковы у всех. Виды
    // документов у каждого свои («ГОСТ», «СП», «Чертеж», «Приказ»), и любой предложенный набор
    // был бы навязанным: человеку пришлось бы сперва вычистить чужие пункты, а лишний из них
    // еще и разошелся бы по фильтру раздела. Столбец заводится пустым, пункты заводит человек.

    // ── Включение библиотеки ──────────────────────────────────────────────────

    /// <summary>
    /// Включает/выключает участие file-поля в библиотеке документов (флаг <c>IsDocFile</c>).
    /// В отличие от исполнителя задач второй такой столбец РАЗРЕШЕН — см. заголовок файла.
    /// Включение — read-time признак (никаких проекций), плюс коллекция доводится до полного
    /// набора ролей раздела: «Тип документа». <paramref name="adoptByName"/> = false —
    /// одноименные столбцы не трогаем и заводим для ролей отдельные поля (ответ пользователя
    /// в окне подтверждения; план для него считает <see cref="PlanRoles"/>).
    /// Возвращает true, если состав или разметка полей коллекции изменились.
    ///
    /// Выключение НИЧЕГО не удаляет: снимается один флаг, а «Тип документа» и все значения
    /// остаются на месте — вернули тумблер, и прежняя разметка ожила сама (то же правило,
    /// что у задач и календаря).
    /// </summary>
    public bool SetFileFieldInLibrary(int fieldId, bool enabled, bool adoptByName = true)
    {
        Demand(PermAction.EditField, CollectionOfField(fieldId));
        var field = db.DbFields.Find(fieldId);
        if (field is null || field.FieldType != "file" || field.IsDocFile == enabled) return false;

        // Чужая роль раздела источником документов быть не может: два значения писались бы в
        // один FieldId и затирали друг друга. «Тип документа» отсекаем и по флагу, и по имени —
        // у поля, заведенного руками до включения раздела, флага еще нет, а разметка ролей
        // ниже нашла бы его ПО ИМЕНИ и повесила вторую роль на то же поле.
        if (enabled && (field.IsDocType || IsRoleName(field.Name, FieldDocType)))
            throw new InvalidOperationException(
                $"Свойство «{field.Name}» — это роль типа документа, источником документов оно быть не может. " +
                "Заведите для документа отдельное свойство типа «Файл».");

        // Авто-поля потребуют права создания — проверяем ДО записи флага, чтобы отказ не
        // оставил коллекцию наполовину включенной.
        if (enabled && DocRolesIncomplete(field.CollectionId, fieldId))
            Demand(PermAction.CreateField, field.CollectionId);

        field.IsDocFile = enabled;
        Log("update", "field", field.Name, CollectionNameOf(field.CollectionId));
        db.SaveChanges();

        return enabled && EnsureDocRoles(field.CollectionId, fieldId, adoptByName);
    }

    /// <summary>
    /// Поле «Тип документа» коллекции (null — его нет). Роль эксклюзивна, но порядок нужен
    /// старым базам и копиям коллекций, где флаг мог оказаться сразу у двух полей, — до того,
    /// как их починит <see cref="RepairDocFieldRoles"/>.
    /// </summary>
    public int? DocTypeFieldId(int collectionId) =>
        RoleOrder(db.DbFields.AsNoTracking()
                    .Where(f => f.CollectionId == collectionId && f.IsDocType &&
                                (f.FieldType == "select" || f.FieldType == "status")))
        .Select(f => (int?)f.Id).FirstOrDefault();

    /// <summary>
    /// Есть ли у коллекции хотя бы одно включенное файловое поле — плашка «привязано к разделу»
    /// в окне свойства и в конструкторе.
    /// </summary>
    public bool HasDocFileField(int collectionId) =>
        db.DbFields.AsNoTracking()
          .Any(f => f.CollectionId == collectionId && f.FieldType == "file" && f.IsDocFile);

    /// <summary>
    /// Коллекции с включенным файловым полем — те, что участвуют в библиотеке. Ими боковая
    /// панель раздела ограничивает список, когда включена настройка «Только подключенные
    /// коллекции». Один запрос на базу.
    /// </summary>
    public HashSet<int> GetDocCollectionIds() =>
        db.DbFields.AsNoTracking()
          .Where(f => f.FieldType == "file" && f.IsDocFile)
          .Select(f => f.CollectionId)
          .Distinct()
          .ToHashSet();

    // Списка «какие вообще бывают типы» у раздела нет намеренно. Выпадашка навбара строится по
    // РЕАЛЬНЫМ значениям записей со счетчиками (GetDocTypeCounts): пункт, заведенный в столбце,
    // но никому не проставленный, дал бы в фильтре строку «0», на которую незачем нажимать, —
    // ровно так же ведет себя фильтр колонки в таблице.

    /// <summary>
    /// Гарантирует коллекции участие в библиотеке: помечает существующее file-поле либо
    /// заводит «Файл», после чего доводит набор ролей. Возвращает id файлового поля.
    /// Для кнопки «+» боковой панели раздела.
    /// </summary>
    /// <param name="adoptExisting">
    /// Разрешено ли «усыновить» под документ уже существующее file-поле коллекции. Так
    /// работает подключение коллекции, которую пользователь для этого и открыл. Поле с ролью
    /// «Тип документа» кандидатом не бывает — оно другого типа.
    /// </param>
    public int EnsureDocCollection(int collectionId, bool adoptExisting = true)
    {
        var fields = RoleOrder(db.DbFields.Where(f => f.CollectionId == collectionId).ToList()).ToList();

        var docFile = fields.FirstOrDefault(f => f.FieldType == "file" && f.IsDocFile);
        if (docFile is not null)
        {
            EnsureDocRoles(collectionId, docFile.Id, adoptByName: true);
            return docFile.Id;
        }

        var candidate = adoptExisting
            ? fields.FirstOrDefault(f => f.FieldType == "file" && !f.IsDocType &&
                                         !IsRoleName(f.Name, FieldDocType))
            : null;
        if (candidate is not null)
        {
            Demand(PermAction.EditField, collectionId);
            if (DocRolesIncomplete(collectionId, candidate.Id))
                Demand(PermAction.CreateField, collectionId);
            candidate.IsDocFile = true;
            Log("update", "field", candidate.Name, CollectionNameOf(collectionId));
            db.SaveChanges();
            EnsureDocRoles(collectionId, candidate.Id, adoptByName: true);
            return candidate.Id;
        }

        var created = AddField(new DbField
        {
            CollectionId = collectionId,
            Name         = FreeFieldName(collectionId, FieldFile),
            FieldType    = "file",
            Position     = NextFieldPosition(collectionId),
            IsDocFile    = true,
        });
        EnsureDocRoles(collectionId, created.Id!.Value, adoptByName: true);
        return created.Id!.Value;
    }

    /// <summary>
    /// Что изменится в коллекции при подключении ее к библиотеке: файловое поле (существующее
    /// помечается либо заводится «Файл» — правило <see cref="EnsureDocCollection"/>) плюс
    /// «Тип документа». Ничего не меняет — только считает, чтобы UI успел спросить до записи.
    /// </summary>
    public RoleAdoptionPlan PlanDocSetup(int collectionId, bool adoptFile = true)
    {
        var fields  = RoleOrder(db.DbFields.AsNoTracking()
                        .Where(f => f.CollectionId == collectionId).ToList()).ToList();
        var docFile = fields.FirstOrDefault(f => f.FieldType == "file" && f.IsDocFile);

        var candidates     = new List<RoleCandidate>();
        var createDeclined = new List<string>();
        var createAdopted  = new List<string>();
        var fileId         = docFile?.Id ?? 0;

        if (docFile is null)
        {
            var candidate = adoptFile
                ? fields.FirstOrDefault(f => f.FieldType == "file" && !f.IsDocType &&
                                             !IsRoleName(f.Name, FieldDocType))
                : null;
            if (candidate is not null)
            {
                candidates.Add(new RoleCandidate(FieldRole.DocFile, FieldFile, candidate.Id, candidate.Name));
                fileId = candidate.Id;
            }
            else
            {
                var name = FreeFieldName(collectionId, FieldFile);
                createDeclined.Add(name);
                createAdopted.Add(name);
            }
        }

        var roles = PlanRoles(collectionId, DocRoles, fileId);
        return candidates.Count == 0 && createDeclined.Count == 0 && !roles.ChangesCollection &&
               roles.FieldsToCreateIfDeclined.Count == 0
            ? RoleAdoptionPlan.Empty
            : new RoleAdoptionPlan([..candidates, ..roles.Candidates],
                                   [..createDeclined, ..roles.FieldsToCreateIfDeclined],
                                   [..createAdopted,  ..roles.FieldsToCreateIfAdopted]);
    }

    // Набор ролей библиотеки неполон — включение потребует создания полей (проверка прав ДО
    // записи флага, чтобы отказ не оставил коллекцию наполовину подключенной).
    private bool DocRolesIncomplete(int collectionId, int fileFieldId) =>
        PlanRoles(collectionId, DocRoles, fileFieldId).FieldsToCreateIfDeclined.Count > 0;

    // Доводит коллекцию до полного набора ролей библиотеки. Само файловое поле исключается из
    // кандидатов: это поле с тумблером, как исполнитель у задач.
    private bool EnsureDocRoles(int collectionId, int fileFieldId, bool adoptByName) =>
        EnsureRoles(collectionId, DocRoles, adoptByName, fileFieldId);

    /// <summary>
    /// Разовая починка разметки ролей библиотеки при монтировании — два дефекта, которые
    /// запреты нового кода сами по себе не лечат (база уже в таком состоянии, а пути включения
    /// уходят в ранний возврат):
    ///
    /// 1. <b>Две роли на одном поле.</b> Копия коллекции или ручная правка: файловое поле
    ///    оказалось помечено и «Типом документа». Правило починки — «документ остается, чужая
    ///    роль снимается»: раздел построен вокруг файлового поля.
    /// 2. <b>Роль на несовместимом типе.</b> Текстовый «Тип документа», <c>IsDocFile</c> на
    ///    поле, которому сменили тип мимо <c>UpdateField</c>: раздел такие данные не читает
    ///    (<see cref="RoleAllowsType"/>), но флаг продолжал висеть и показывать плашку
    ///    «привязано к разделу» над неработающей привязкой.
    ///
    /// В отличие от задач, дефекта «несколько носителей» здесь нет: файловых полей у коллекции
    /// может быть сколько угодно, это штатный режим.
    ///
    /// Возвращает число вылеченных коллекций. Монтирование не срывает: у пользователя без прав
    /// на схему починка не доходит до конца (доведет следующий, у кого права есть), а отказ
    /// остается в журнале диагностики (<see cref="LogRepairNotDone"/>).
    /// </summary>
    public int RepairDocFieldRoles()
    {
        var collections = db.DbFields
            .Where(f => (f.IsDocFile && f.IsDocType) ||
                        (f.IsDocFile && f.FieldType != "file") ||
                        (f.IsDocType && f.FieldType != "select" && f.FieldType != "status"))
            .Select(f => f.CollectionId)
            .Distinct()
            .ToList();
        if (collections.Count == 0) return 0;

        var healed = 0;
        foreach (var collectionId in collections)
        {
            string? name = null;
            try
            {
                name = CollectionNameOf(collectionId);
                var fields  = CollectionFields(collectionId);
                var anyFile = false;
                var keepId  = 0;

                foreach (var f in fields)
                {
                    var changed = false;

                    // (1) Чужая роль на источнике документов.
                    if (f.IsDocFile && f.FieldType == "file" && f.IsDocType)
                    {
                        f.IsDocType = false;
                        changed = true;
                    }

                    // (2) Роли, которым не подходит тип поля.
                    foreach (var role in AllRoles)
                        if (RoleFlag(f, role) && !RoleAllowsType(role, f.FieldType))
                        {
                            SetRole(f, role, false);
                            changed = true;
                        }

                    if (f.IsDocFile && f.FieldType == "file" && !anyFile)
                    {
                        anyFile = true;
                        keepId  = f.Id;
                    }
                    if (changed) Log("update", "field", f.Name, CollectionNameOf(collectionId));
                }
                db.SaveChanges();

                // Освободившийся «Тип документа» заводится заново — обычным путем, со своими
                // правами и транзакцией; только там, где остался источник документов.
                if (anyFile) EnsureDocRoles(collectionId, keepId, adoptByName: true);
                healed++;
            }
            catch (Exception ex) when (ex is PermissionDeniedException or DbUpdateException)
            {
                db.ChangeTracker.Clear();   // несохраненные флаги не должны утечь в следующие чтения
                LogRepairNotDone("Библиотека документов", collectionId, name, ex);
            }
        }
        return healed;
    }

    // ── Пути документов ───────────────────────────────────────────────────────
    //
    // Своего читателя карточек здесь нет: ленту читают страницы (DocsPaging.cs) — и раздел «в
    // памяти» тоже, одной страницей во весь поток, — а поиск читает свою выдачу. Сплошной
    // читатель всей проекции с потолком в тысячу строк жил здесь для одних тестов и мог разойтись
    // с лентой, которую видит человек; тесты сверяются теперь с самой лентой.

    /// <summary>
    /// Все пути документов активных коллекций — перепись и очередь чтения сверяют их с кешем,
    /// чтобы понять, что еще не разобрано. Только пути, без остальной карточки: на сотнях тысяч
    /// документов это разница между одной колонкой и полной проекцией.
    ///
    /// <b>Потолка нет.</b> Раньше путей бралось не больше 100 000, и библиотека крупнее читалась
    /// частично и молча: перепись считала числа подвала без хвоста, «Прочитать документы» его не
    /// разбирало, а «все уже прочитано» звучало при непрочитанном хвосте. Перепись и так держит
    /// по строке на каждый путь — чипам карточек нужен каждый, — так что потолок не берег память, а
    /// только прятал хвост. Цена — замер <c>docfeed</c>: 1,1 с на 500 000 документов, в фоне.
    /// </summary>
    public List<string> GetDocPaths(ISet<int>? activeIds = null)
    {
        var result = new List<string>();
        if (activeIds is { Count: 0 }) return result;

        var colFilter = activeIds is null
            ? ""
            : $" AND f.CollectionId IN ({string.Join(",", activeIds)})";

        var conn = db.Database.GetDbConnection();
        var opened = conn.State != System.Data.ConnectionState.Open;
        if (opened) conn.Open();
        try
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = $@"
                SELECT DISTINCT v.Value
                FROM db_fields f
                JOIN db_field_values v ON v.FieldId = f.Id AND v.Value <> ''
                WHERE f.FieldType = 'file' AND f.IsDocFile = 1{colFilter}";
            using var rdr = cmd.ExecuteReader();
            while (rdr.Read())
                if (!rdr.IsDBNull(0)) result.Add(rdr.GetString(0));
        }
        finally { if (opened) conn.Close(); }
        return result;
    }

    /// <summary>Фоновое чтение путей на ОТДЕЛЬНОМ подключении.</summary>
    public Task<List<string>> LoadDocPathsAsync(ISet<int>? activeIds = null) =>
        Task.Run(() =>
        {
            using var ctx = new AppDbContext(db.DbPath);
            ctx.ApplyPerformancePragmas();
            return new ConstructorRepository(ctx).GetDocPaths(activeIds);
        });
}
