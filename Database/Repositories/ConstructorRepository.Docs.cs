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
    /// Файловые поля коллекции, включенные в библиотеку, в порядке <see cref="RoleOrder"/>.
    /// Пустой список — коллекция в разделе не участвует.
    /// </summary>
    public List<int> DocFileFieldIds(int collectionId) =>
        RoleOrder(db.DbFields.AsNoTracking()
                    .Where(f => f.CollectionId == collectionId &&
                                f.FieldType == "file" && f.IsDocFile))
        .Select(f => f.Id).ToList();

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

    /// <summary>Есть ли у коллекции хотя бы одно включенное файловое поле.</summary>
    public bool HasDocFileField(int collectionId) => DocFileFieldIds(collectionId).Count > 0;

    /// <summary>Есть ли в базе хоть одна коллекция с документами — для пустого состояния раздела.</summary>
    public bool HasAnyDocFileField() =>
        db.DbFields.AsNoTracking().Any(f => f.FieldType == "file" && f.IsDocFile);

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
    /// на схему починка просто не происходит (ее сделает следующий, у кого права есть).
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
            try
            {
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
            }
        }
        return healed;
    }

    // ── Чтение документов ─────────────────────────────────────────────────────

    /// <summary>
    /// Документы по активным коллекциям (null — все коллекции базы, ПУСТОЙ набор — ни одной,
    /// как у календаря и задач). Документ = запись коллекции × включенное файловое поле с
    /// НЕПУСТЫМ значением: строка без пути — это не документ, показывать в библиотеке нечего
    /// (в отличие от задач, где пустой исполнитель — осмысленное «никому не роздано»).
    ///
    /// Чтение идет от db_records по покрывающему индексу (CollectionId, Position); путь
    /// приезжает JOIN'ом сиком по кластерному PK (RecordId, FieldId) таблицы db_field_values
    /// (она WITHOUT ROWID), «Тип документа» — коррелированным подзапросом, как «Статус» у задач.
    /// Сортировку и слияние баз делает контроллер.
    /// </summary>
    public List<DocRecord> GetDocRecords(ISet<int>? activeIds = null, int limit = 1000)
    {
        var result = new List<DocRecord>();
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
            // ORDER BY у роли «Тип документа» повторяет RoleOrder: при двух полях с одним
            // флагом читаем ровно то поле, в которое пишет разметка (без порядка SQLite отдавал
            // бы произвольное, и карточка спорила бы с фильтром типов).
            cmd.CommandText = $@"
                SELECT r.Id, f.Id, f.Name, f.CollectionId, c.Name, c.Color, r.Title, v.Value,
                       (SELECT vt.Value FROM db_field_values vt
                          JOIN db_fields ft ON ft.Id = vt.FieldId
                         WHERE vt.RecordId = r.Id AND ft.CollectionId = f.CollectionId
                           AND ft.IsDocType = 1
                         ORDER BY ft.Position, ft.Id LIMIT 1)
                FROM db_fields f
                JOIN db_collections c ON c.Id = f.CollectionId
                JOIN db_records r     ON r.CollectionId = f.CollectionId
                JOIN db_field_values v ON v.RecordId = r.Id AND v.FieldId = f.Id AND v.Value <> ''
                WHERE f.FieldType = 'file' AND f.IsDocFile = 1{colFilter}
                ORDER BY f.CollectionId, r.Position, r.Id, f.Position, f.Id
                LIMIT {Math.Max(1, limit)}";

            using var rdr = cmd.ExecuteReader();
            while (rdr.Read()) result.Add(ReadDocRow(rdr));
        }
        finally { if (opened) conn.Close(); }
        return result;
    }

    /// <summary>Строка карточки из читателя проекции документов. Вынесено, потому что ту же
    /// проекцию читает постраничный путь: разъехавшись, два режима раздела показывали бы
    /// разное.</summary>
    private static DocRecord ReadDocRow(System.Data.Common.DbDataReader rdr) => new()
    {
        Id             = rdr.GetInt32(0),
        FileFieldId    = rdr.GetInt32(1),
        FileFieldName  = rdr.IsDBNull(2) ? "" : rdr.GetString(2),
        CollectionId   = rdr.GetInt32(3),
        CollectionName = rdr.IsDBNull(4) ? "" : rdr.GetString(4),
        Color          = rdr.IsDBNull(5) ? "#007AFF" : rdr.GetString(5),
        Title          = rdr.IsDBNull(6) ? "" : rdr.GetString(6),
        Path           = rdr.IsDBNull(7) ? "" : rdr.GetString(7),
        DocType        = rdr.IsDBNull(8) ? "" : rdr.GetString(8),
    };

    /// <summary>Фоновая загрузка документов на ОТДЕЛЬНОМ подключении (общий контекст однопоточный).</summary>
    public Task<List<DocRecord>> LoadDocsAsync(ISet<int>? activeIds = null, int limit = 1000) =>
        Task.Run(() =>
        {
            using var ctx = new AppDbContext(db.DbPath);
            ctx.ApplyPerformancePragmas();
            return new ConstructorRepository(ctx).GetDocRecords(activeIds, limit);
        });

    /// <summary>
    /// Все пути документов активных коллекций — очередь индексации читает их, чтобы понять,
    /// что еще не разобрано. Только пути, без остальной карточки: на сотнях тысяч документов
    /// это разница между одной колонкой и полной проекцией.
    /// </summary>
    public List<string> GetDocPaths(ISet<int>? activeIds = null, int limit = 100_000)
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
                WHERE f.FieldType = 'file' AND f.IsDocFile = 1{colFilter}
                LIMIT {Math.Max(1, limit)}";
            using var rdr = cmd.ExecuteReader();
            while (rdr.Read())
                if (!rdr.IsDBNull(0)) result.Add(rdr.GetString(0));
        }
        finally { if (opened) conn.Close(); }
        return result;
    }

    /// <summary>Фоновое чтение путей на ОТДЕЛЬНОМ подключении.</summary>
    public Task<List<string>> LoadDocPathsAsync(ISet<int>? activeIds = null, int limit = 100_000) =>
        Task.Run(() =>
        {
            using var ctx = new AppDbContext(db.DbPath);
            ctx.ApplyPerformancePragmas();
            return new ConstructorRepository(ctx).GetDocPaths(activeIds, limit);
        });
}
