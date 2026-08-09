# Модель данных

## Назначение

Что и как хранится. Различаются **доменные модели** ([Core/Models/](../../../Core/Models/)) —
ими оперирует UI/логика — и **EF-сущности** ([Database/Entities/](../../../Database/Entities/)) —
то, что ложится в таблицы SQLite. Репозитории ([Database/Repositories/](../../../Database/Repositories/))
переводят одно в другое.

## Доменные модели (Core/Models)

| Модель | Назначение |
|---|---|
| `DbGroup` | группа коллекций (может быть вложенной) |
| `DbCollection` | коллекция/таблица: имя, иконка, цвет, группа |
| `DbField` | поле: тип (базовые `text/number/date/select/status/multiselect/checkbox/url/user` + вычисляемые `created_at/updated_at/created_by/updated_by`, см. [«Типы полей»](#типы-полей-свойств)), `ConfigJson`, флаги `IsCalStart` (= «показывать поле-дату в календаре»; может быть включён у нескольких полей-дат коллекции), `IsCalEnd` (не используется), `IsSystem/IsRequired` |
| `DbRecord` | запись: `Title`, `Description`, позиция; колонки `StartDt/EndDt/AllDay` — устаревший след (календарём больше не читаются, см. ниже) |
| `CalendarRecord` | одно событие календаря (`DbId`, `Id`, **`FieldId`** — из какого поля-даты получено событие, `Title`, `StartDt/EndDt`, `AllDay`, `Color`, `CollectionId`). Идентичность события — пара `(Id, FieldId)`: одна запись даёт по событию на каждое видимое поле-дату |
| `Category` | категория (имя + цвет) |
| `DateValue` | разбор/форматирование дат полей-`date` |
| `EventColors` | правило «дефолтный синий = без цвета → серый» |


## Типы полей (свойств)

Тип свойства — **сквозная** сущность: одни и те же поля видны в **таблице**, **панели записи**,
питают **календарь** (поля-даты) и **задачи** (user-поля). Полный реестр — обзор, правила
`ConfigJson` и разбор каждого типа отдельным файлом — в [data-types/](data-types/overview.md).
Единый источник классификации/подписей/формата — [`FieldTypeInfo`](../../../Core/Models/FieldTypeInfo.cs);
за рендер (редактор, read-only) отвечает каждый режим отдельно (таблица, панель записи).

| Категория | Типы |
|---|---|
| Базовые (значение правит пользователь) | `text` · `number` · `date` · `select` · `status` · `multiselect` · `checkbox` · `url` · `file` · `user` |
| Вычисляемые (read-only, из аудит-метаданных) | `created_at` · `updated_at` · `created_by` · `updated_by` |

Смена типа между «дата» и остальными приводит значения без потери (`ConvertValueForType`);
покрытие — `FieldTypeMatrixTests` (реестр типов) и `ConstructorRepositoryCrudTests` (метаданные).

## Хранилище database.db (пользовательские данные)

Контекст [AppDbContext](../../../Database/AppDbContext.cs). Ниже — смысловая карта;
поколоночный справочник всех таблиц (типы, ключи, индексы) — [db-schema.md](database/schema.md).
Таблицы:

- **Контент:** `categories`, `db_groups`, `db_collections`, `db_fields`, `db_records`, `db_field_values`, `db_record_links`.
- **Значения полей:** `db_field_values` — пара `(RecordId, FieldId) → Value`; это естественный **PK**, суррогатного Id нет, таблица `WITHOUT ROWID` (кластеризована по PK: значения одной записи лежат физически рядом, отдельный уникальный индекс пары не нужен — см. миграцию `SchemaCompact`). Так произвольные поля хранятся «вертикально», а не колонками. Все базовые типы, кроме `date`, хранятся как обычные строки; `date` — в ISO-каноне (`DateValue.ToStorage`). **Вычисляемые** типы (`created_*/updated_*`) значения здесь НЕ имеют — читаются из аудит-колонок `db_records` (см. [«Типы полей»](#типы-полей-свойств)). При **переносе записи в другую коллекцию** (таблица «Расположение» / смена коллекции в окне календаря) значения сопоставляются с полями цели **по имени** с приведением типов (`ConstructorRepository.CopyFieldValuesByName` → `ConvertValueForType`; нетривиально только пересечение границы «дата»).
- **Связи записей:** `db_record_links` — рёбра «из записи в запись по полю-связи» (тип свойства
  `relation`): `Id, FieldId, FromRecordId, ToRecordId, TargetDbId, Position`. Значения связи здесь, а
  НЕ в `db_field_values` — так работают FK-каскады (удаление поля-связи или записи-источника чистит
  рёбра) и запрос входящих по `ToRecordId` (окно «связи будут удалены» перед удалением/переносом).
  `TargetDbId` — база цели: `0` = эта же БД, `>0` = стабильный DbId другого `.db` из реестра
  (межбазовая связь); FK на `ToRecordId` поэтому **снят** — между файлами он невозможен, и удаление
  цели чистит прикладной код. Уникальность `(FieldId, FromRecordId, TargetDbId, ToRecordId)` —
  различает одинаковые id в разных целевых БД. Подробности —
  [data-types/relation.md](data-types/relation.md).
- **Проекция календаря:** `calendar_index` — денормализованное «оглавление» событий (строка на каждое видимое поле-дату записи: `RecordId, FieldId, CollectionId, StartIso, EndIso, AllDay`; PK `(RecordId, FieldId)`, тоже `WITHOUT ROWID`; индекс `(CollectionId, StartIso)`, FK-каскады на `db_records`/`db_fields`). Из неё календарь читает события индексным range-query (см. ниже).
- **UI-настройки коллекции:** `ui_col_widths`, `ui_row_heights`, `ui_collection_settings` (режим вида, ручной ресайз), `column_meta` — все метаданные колонки одной строкой на `(CollectionId, Key)`: аспекты `Position` (порядок), `Visible`/`Level` (окно «Отображение»), `DisplayName` (подпись «Названия»); аспект `NULL` = дефолт, строка живёт, пока задан хоть один аспект. `record_display` — то же для строк-записей.
- **Пользователи и права (модуль «Профиль»):** `db_users` (кто открывал базу: ключ «учётка@компьютер», снапшот ФИО, `IsAdmin/IsApproved`), `db_permissions` (битовые маски `PermAction` per-пользователь: строка с `CollectionId=NULL` — вся база, с коллекцией — переопределение), `db_meta` (ключ-значение базы; PBKDF2-хеш пароля), `change_log` (журнал фактов: кто/когда/что, `Count` у пакетных операций; без FK — переживает удаление объектов). Подробно — [users-permissions.md](users-permissions.md).
- Тип поля `user` хранится в `db_field_values` строкой-отображаемым именем (как `select`); опции — допущенные пользователи базы (`ConstructorRepository.GetUserOptions`), новая запись по умолчанию получает текущего пользователя.

Связи каскадные: удаление коллекции уносит её поля, записи и значения; группа/коллекция
при удалении родителя — `SetNull`.

### Жизненный цикл записей и свойств

Запись и свойство — две параллельные «единицы» с полным набором операций (репозиторий
`ConstructorRepository`; покрытие — `RecordLifecycleTests` / `FieldLifecycleTests`, ключевые
PG-пути — `PgProviderTests`):

| Операция | Запись | Свойство (поле) |
|---|---|---|
| Создать | `AddRecord` / `AddRecordAfter` (авто-стамп аудита) | `AddField` |
| Изменить | значение — `SetFieldValue`; шапка — `SetRecordTitle/Description` | `UpdateField` (+ приведение значений при смене типа) |
| Дублировать | `DuplicateRecord` (копия сразу после источника) / `DuplicateRecordsData` (пакет; **PG — отдельный путь** через `nextval`) | `DuplicateField` (bulk-копия значений во всех записях) |
| Удалить | `DeleteRecord` / `DeleteRecordsData` (пакет) | `DeleteField` |
| Переместить | `MoveRecord` (своп с соседом) / `ReorderRecords` | `MoveColumn` / `MoveColumnAfter` (порядок в `column_meta`) |
| Перенести в др. коллекцию/базу | `MoveCollectionIsolated` (ATTACH, см. [multi-database.md](database/multi-database.md)) | вместе с коллекцией |

Общее правило: удаление каскадит значения (`db_field_values` по FK `ON DELETE CASCADE`);
дубль записи копирует все её значения, дубль поля — значения поля во всех записях; перенос
между коллекциями сопоставляет поля **по имени** с приведением типов. Порядок записей —
`db_records.Position`, порядок колонок — `column_meta.Position` (не путать). Табличные
жесты/события — [tables.md](../features/constructor/table.md#взаимодействия-события-наверх).

### Где календарь

Источник правды для дат — **значения полей-дат** в `db_field_values`. Поле-дата с флагом
`IsCalStart=true` показывается в календарём; таких полей в коллекции может быть **несколько**
(включаются тумблером «Отображать в календаре» в окне свойства или из контекстного меню
коллекции в сайдбаре). Каждое непустое значение такого поля = одно событие, поэтому одна
запись может появляться в календаре несколько раз (по событию на каждую видимую дату).

Чтобы чтение масштабировалось (10М+ записей, как таблица), события материализуются в
индексную таблицу-проекцию `calendar_index` и читаются оттуда быстрым range-query по
`(CollectionId, StartIso)` (`GetCalendarRecordsByRange` → `ReadCalendarEvents`). Проекция
поддерживается инкрементально:
- запись значения (`SetFieldValue`/`SetCalendarDateValue`) → upsert/удаление одной строки
  (`SyncCalendarIndexValue`);
- тумблер видимости (`SetDateFieldVisible`) и смена типа поля (`UpdateField`) → пересбор поля
  (`RebuildCalendarIndexForField`);
- дубль/перенос коллекции/записи → пересбор затронутой коллекции/записи;
- удаления (запись/поле/коллекция/группа, в т.ч. bulk) — авто-очистка через FK `ON DELETE CASCADE`;
- однократный потоковый бэкафилл при первом запуске (`MigrateAndBackfillCalendarDates`).

### Двойная система дат (важно понимать)

Даты живут в двух местах, и авторитетно только одно:

1. **Поля-даты** (`db_field_values` + проекция `calendar_index`) — **источник правды**.
   Календарь, «Расписание» и «Задачи» читают только отсюда.
2. **Колонки `db_records.StartDt/EndDt/AllDay`** — след прежней модели «одна дата на
   запись». Они **пишутся** при правке даты (`ToRecordColumns`, бэкафилл в
   `MigrateAndBackfillCalendarDates`), но календарём **не читаются** и не авторитетны.
   Стратегически модель сводится к полям-датам; колонки остаются ради обратной
   совместимости старых БД и переноса записей.

## Хранилище app.db (системное)

Контекст [SystemDbContext](../../../Database/SystemDbContext.cs). Две таблицы:

- `databases` — реестр подключённых БД (`DatabaseRefEntity`: имя, путь, провайдер, `IsDefault`);
- `app_settings` — настройки приложения «ключ → значение» (`AppSettingEntity`); здесь же ФИО
  профиля пользователя (ключи `profile.*`, читает/пишет `Utils/CurrentUser`).

Подробнее про два файла и миграции — [databases.md](database/storage-and-migrations.md).

## Связанные документы

[[database/schema]], [[database/storage-and-migrations]], [[database/multi-database]], [[../features/constructor/overview]], [[../features/categories]]
