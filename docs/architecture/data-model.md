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
| `DbField` | поле: тип (`text/number/date/select/checkbox/url`), `ConfigJson`, флаги `IsCalStart/IsCalEnd/IsSystem/IsRequired` |
| `DbRecord` | запись: `Title`, `Description`, `StartDt/EndDt/AllDay`, позиция |
| `CalendarRecord` | календарное представление записи с датами (`DbId`, `Title`, `StartDt/EndDt`, `AllDay`, `Color`, `CollectionId`) |
| `Category` | категория (имя + цвет) |
| `DateValue` | разбор/форматирование дат полей-`date` |
| `EventColors` | правило «дефолтный синий = без цвета → серый» |

## Хранилище database.db (пользовательские данные)

Контекст [AppDbContext](../../Database/AppDbContext.cs). Таблицы:

- **Контент:** `categories`, `db_groups`, `db_collections`, `db_fields`, `db_records`, `db_field_values`.
- **Значения полей:** `db_field_values` — пара `(RecordId, FieldId) → Value` (уникальный индекс). Так произвольные поля хранятся «вертикально», а не колонками.
- **UI-настройки коллекции:** `ui_col_widths`, `ui_row_heights`, `ui_collection_settings` (режим вида, ручной ресайз), `system_field_meta` (подписи «Название»/«Описание»), `column_order`, `column_display`, `record_display`.

Связи каскадные: удаление коллекции уносит её поля, записи и значения; группа/коллекция
при удалении родителя — `SetNull`.

### Где календарь

Календарное событие — это `db_record` с заполненными `StartDt/EndDt/AllDay`.
`ConstructorRepository` отдаёт их как `CalendarRecord` (`GetCalendarRecordsByRange`,
`AddCalendarRecord`, …). Поля `IsCalStart/IsCalEnd` на `DbField` помечают, какие поля
коллекции считаются датами календаря; `MigrateAndBackfillCalendarDates()` проставляет
их и заполняет `StartDt` из значения поля-даты для старых записей.

## Хранилище app.db (системное)

Контекст [SystemDbContext](../../Database/SystemDbContext.cs). Две таблицы:

- `databases` — реестр подключённых БД (`DatabaseRefEntity`: имя, путь, провайдер, `IsDefault`);
- `app_settings` — настройки приложения «ключ → значение» (`AppSettingEntity`).

Подробнее про два файла и миграции — [databases.md](databases.md).

## Связанные документы

[[databases]], [[multi-database]], [[../features/constructor-db/overview]], [[../features/categories]]
