# Модель данных

## Назначение

Что и как хранится. Различаются **доменные модели** ([Core/Models/](../../Core/Models/)) —
ими оперирует UI/логика — и **EF-сущности** ([Database/Entities/](../../Database/Entities/)) —
то, что ложится в таблицы SQLite. Репозитории ([Database/Repositories/](../../Database/Repositories/))
переводят одно в другое.

## Доменные модели (Core/Models)

| Модель | Назначение |
|---|---|
| `DbGroup` | группа коллекций (может быть вложенной) |
| `DbCollection` | коллекция/таблица: имя, иконка, цвет, группа |
| `DbField` | поле: тип (`text/number/date/select/checkbox/url`), `ConfigJson`, флаги `IsCalStart` (= «показывать поле-дату в календаре»; может быть включён у нескольких полей-дат коллекции), `IsCalEnd` (не используется), `IsSystem/IsRequired` |
| `DbRecord` | запись: `Title`, `Description`, позиция; колонки `StartDt/EndDt/AllDay` — устаревший след (календарём больше не читаются, см. ниже) |
| `CalendarRecord` | одно событие календаря (`DbId`, `Id`, **`FieldId`** — из какого поля-даты получено событие, `Title`, `StartDt/EndDt`, `AllDay`, `Color`, `CollectionId`). Идентичность события — пара `(Id, FieldId)`: одна запись даёт по событию на каждое видимое поле-дату |
| `Category` | категория (имя + цвет) |
| `DateValue` | разбор/форматирование дат полей-`date` |
| `EventColors` | правило «дефолтный синий = без цвета → серый» |

## Хранилище database.db (пользовательские данные)

Контекст [AppDbContext](../../Database/AppDbContext.cs). Таблицы:

- **Контент:** `categories`, `db_groups`, `db_collections`, `db_fields`, `db_records`, `db_field_values`.
- **Значения полей:** `db_field_values` — пара `(RecordId, FieldId) → Value` (уникальный индекс). Так произвольные поля хранятся «вертикально», а не колонками. Все типы, кроме `date`, хранятся как обычные строки; `date` — в ISO-каноне (`DateValue.ToStorage`). При **переносе записи в другую коллекцию** (таблица «Расположение» / смена коллекции в окне календаря) значения сопоставляются с полями цели **по имени** с приведением типов (`ConstructorRepository.CopyFieldValuesByName` → `ConvertValueForType`; нетривиально только пересечение границы «дата»).
- **Проекция календаря:** `calendar_index` — денормализованное «оглавление» событий (строка на каждое видимое поле-дату записи: `RecordId, FieldId, CollectionId, StartIso, EndIso, AllDay`; индекс `(CollectionId, StartIso)`, FK-каскады на `db_records`/`db_fields`). Из неё календарь читает события индексным range-query (см. ниже).
- **UI-настройки коллекции:** `ui_col_widths`, `ui_row_heights`, `ui_collection_settings` (режим вида, ручной ресайз), `system_field_meta` (подписи «Название»/«Описание»), `column_order`, `column_display`, `record_display`.

Связи каскадные: удаление коллекции уносит её поля, записи и значения; группа/коллекция
при удалении родителя — `SetNull`.

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

Колонки `db_records.StartDt/EndDt/AllDay` остались от прежней модели «одна дата на запись»
и календарём больше не читаются (пишутся, но не авторитетны).

## Хранилище app.db (системное)

Контекст [SystemDbContext](../../Database/SystemDbContext.cs). Две таблицы:

- `databases` — реестр подключённых БД (`DatabaseRefEntity`: имя, путь, провайдер, `IsDefault`);
- `app_settings` — настройки приложения «ключ → значение» (`AppSettingEntity`).

Подробнее про два файла и миграции — [databases.md](databases.md).

## Связанные документы

[[databases]], [[multi-database]], [[../features/constructor-db/overview]], [[../features/categories]]
