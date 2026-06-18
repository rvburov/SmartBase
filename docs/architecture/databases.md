# Хранилище, миграции, пути

## Назначение

Как и где физически лежат данные, как обновляется схема без потери данных и как
читаются/пишутся настройки.

## Два файла SQLite

| Файл | Контекст | Что внутри |
|---|---|---|
| `database.db` | [AppDbContext](../../Database/AppDbContext.cs) | пользовательские данные (коллекции, поля, записи, события календаря) |
| `app.db` | [SystemDbContext](../../Database/SystemDbContext.cs) | системное: реестр БД (`databases`) + настройки (`app_settings`) |

`app.db` — независимое хранилище: никогда не показывается в сайдбаре и не удаляется.

## Пути ([AppSettings](../../Utils/AppSettings.cs))

- **Release:** `%AppData%\Roaming\DataBase\` (Windows); на Linux/macOS .NET
  `ApplicationData` указывает на `~/.config/DataBase/`.
- **Debug:** папка `.localdata/` в корне проекта (находится по маркеру
  `DataBase.csproj`) — изолированно «как venv», переживает `dotnet clean` и снос
  `artifacts/`.

`database.db` → `AppSettings.DbPath`, `app.db` → `AppSettings.SystemDbPath`.

## Миграции схемы (database.db)

Накатываются автоматически при открытии каждой БД в
[DatabaseManager](../../Database/DatabaseManager.cs) (`ApplySchemaMigrations`):

- **Новый файл** — `Migrate()` создаёт БД и применяет все миграции по порядку.
- **Старый файл из времён `EnsureCreated()`** (нет таблицы `__EFMigrationsHistory`,
  но есть `db_records`) — вручную создаётся таблица истории и `InitialCreate`
  помечается как уже применённая, после чего `Migrate()` накатывает только
  недостающие миграции. Так схема обновляется, **не пересоздавая** существующие данные.

Новые правки сущностей → `dotnet ef migrations add ...` → применятся при следующем
запуске. См. также [ROADMAP.md](../ROADMAP.md) и инструменты EF в гайде разработчика.

> **Правило:** любая миграция должна сохранять данные — никаких drop/recreate и
> затёртых строк. То же для app.db: изменения только аддитивно
> (`CREATE TABLE IF NOT EXISTS` / `ALTER ADD`).

### app.db

Создаётся через `EnsureCreated()`; поскольку он не достраивает таблицы в уже
существующем файле, недостающая `app_settings` добавляется аддитивно при открытии
(`EnsureSettingsTable` в [DatabaseRegistry](../../Database/DatabaseRegistry.cs)).

## WAL

Для обеих БД включается `PRAGMA journal_mode=WAL` — параллельные чтения во время
записи и быстрые коммиты. Рядом с файлом появляются `*.db-wal` / `*.db-shm`. На
сетевой ФС SQLite режим тихо игнорирует.

## Настройки приложения

`DatabaseRegistry` реализует `IAppSettingsStore` (Get/Set по ключу поверх
`app_settings`). [AppSettings](../../Utils/AppSettings.cs) даёт типизированный доступ:
масштаб интерфейса (`scale`), состояние и ширина правой панели записи
(`recordPanelOpen`, `recordPanelWidth`). Контекст app.db живёт всё время работы
приложения, поэтому настройки читаются/пишутся через него.

## Провайдеры

Сейчас только SQLite. `DatabaseManager` спроектирован как единая точка открытия БД,
чтобы ветку под PostgreSQL добавить здесь же, не переписывая вызывающий код.

## Связанные документы

[[multi-database]], [[data-model]], [[../guides/developer-guide]]
