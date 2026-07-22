# Автотесты (Tests/Unit, Tests/Integration)

## Назначение

Набор автоматических тестов (xUnit) поверх **логики и слоя данных** приложения: Core
(разбор дат, валидаторы, планировщики импорта/экспорта, права) и репозитории
(`ConstructorRepository`, `CategoryRepository`, `PermissionService`) на временной SQLite-БД,
плюс опционально провайдер PostgreSQL. Фиксирует бизнес-инварианты из
[architecture](../architecture/) — keyset-пагинацию, консистентность календарной проекции,
перенос коллекций между БД, дельты настроек, проверки прав, «последний побеждает» при
многопользовательской записи.

UI/ViewModels в охват сознательно не входят — тестируется логика, стоящая за интерфейсом.

## Как запускать

Из корня репозитория:

```bash
dotnet test Tests/Unit                 # быстрый гейт: чистая логика, мс
dotnet test Tests/Integration          # SQLite + PostgreSQL (медленнее)
dotnet test SmartBase.sln              # оба набора
./Tests/run-all.sh                     # оба набора (+ --with-bench для бенчмарков)
dotnet test Tests/Integration --collect:"XPlat Code Coverage"   # покрытие (coverlet)
```

Граница между проектами — по характеру теста: `Unit` быстрый и без внешних зависимостей
(гейт на каждый коммит), `Integration` требует файловой БД и, для PG-тестов, стенда.

## Структура

Два тест-проекта, разделённых по характеру (скорость/зависимости):

| Проект | Что внутри |
|--------|-----------|
| **Tests/Unit** | Чистые юнит-тесты без БД и IO: `DateValueTests`, `CalendarRecordValidatorTests`, `PermissionsTests`, `PlannerTests`, `NotificationSchedulerTests` (ядро уведомлений), `NotificationSettingsTests` (дельты тумблера/метки), `AutostartTests` (построение записи автозапуска, «скрывать при закрытии?», дельты автозапуска/фона). Ноль внешних зависимостей, параллельные, миллисекунды. |
| **Tests/Integration** | На temp SQLite + PostgreSQL: CRUD (в т.ч. аудит-метаданные записи), `FieldTypeMatrixTests` (матрица типов свойств: round-trip / подпись / смена типа / user-дефолт), `KeysetPaginationTests`, `CalendarProjectionTests`, `ChangeLogTests`, `PermissionServiceTests`, `CrossMoveTests`, `ImportMatrixTests`/`ExportMatrixTests` (round-trip), `ContentionTests`, `PragmaTests`, `PgProviderTests`, `NotificationServiceTests` (служба уведомлений на календарной проекции), `SingleInstanceTests` (лок единственного экземпляра на temp-папке). Здесь же обвязка: `TempDatabase` (SQLite через настоящий `DatabaseManager.Open`), `SampleData`, `ImportExport`, `FakeSettingsDatabase`, `PgTestServer`. Прогон **последовательный** (см. ниже). |

Ключевые принципы обвязки:

- **Только файловые БД.** `TempDatabase` поднимает SQLite на временном файле — `:memory:`
  не годится (WAL и ATTACH требуют файла на диске). При `Dispose` файл и спутники
  `-wal/-shm` удаляются (пул соединений сбрасывается `SqliteConnection.ClearAllPools`).
- **Integration гоняется последовательно** (`[assembly: CollectionBehavior(DisableTestParallelization = true)]`):
  `ClearAllPools` — процесс-глобальная операция, при параллельных тестах задевала бы чужие
  подключения. Unit-тесты остаются параллельными (без общего состояния).
- **Настоящий путь инициализации.** База поднимается через `DatabaseManager.Open` — те же
  миграции, прагмы, сиды и регистрация текущего пользователя администратором, что и в
  приложении. Поэтому тесты идут ровно тем же кодом, а не его тестовым дублёром.
- **Импорт/экспорт** вызывают ровно те методы, что и UI:
  `ImportPlanner.Plan → ITableSource.Analyze → ReadRows → ImportStreamedCollectionIsolated`
  и `ExportPlanner.Plan → GetExportColumnsIsolated → ReadExportRowsIsolated → ITableWriter`.
  Функциональные матрицы прежних харнессов ImportPerf/ExportPerf/PgSmoke перенесены сюда
  как xUnit-тесты; перф-замеры — в [benchmarks.md](benchmarks.md).

## PostgreSQL

Тесты `PgProviderTests` — `[SkippableFact]`: без поднятого стенда они помечаются **Skipped**,
а не падают, поэтому локально и в CI без Docker набор остаётся зелёным. Стенд —
docker-контейнер `sb-pg` (порт **5455**), как в
[../guides/postgresql-setup.md](../guides/postgresql-setup.md); нужны базы `smartbase` и `smartbase2`:

```bash
docker run --name sb-pg -e POSTGRES_PASSWORD=postgres -p 5455:5432 -d postgres:16
docker exec sb-pg psql -U postgres -c "CREATE DATABASE smartbase;"
docker exec sb-pg psql -U postgres -c "CREATE DATABASE smartbase2;"
```

Перед прогоном каждый PG-тест сбрасывает схему (`DROP SCHEMA public CASCADE; CREATE SCHEMA
public;`), чтобы миграция `InitialCreatePg` накатилась начисто.

## Замечания по сборке

- Кэш NuGet перенаправлен внутрь репозитория в `packages/` (см. `nuget.config`), поэтому в
  `SmartBase.csproj` папка `packages/**` исключена из компиляции — иначе `.cs`-файлы из
  пакетов (например, `Microsoft.NET.Test.Sdk.Program.cs`) попадали бы в глоб `**/*.cs`
  основного проекта и ломали сборку.
- Перф-замеры (`Tests/Benchmarks`), генератор стенда (`Tests/DbGenerator`) и многопроцессный
  `Tests/MultiProcess` — консольные инструменты, не автотесты
  ([benchmarks.md](benchmarks.md), [multi-process.md](multi-process.md), [test-database.md](test-database.md)).

## Связанные документы

- [architecture/performance.md](../architecture/performance.md) — keyset-пагинация, календарная проекция
- [architecture/multi-database.md](../architecture/multi-database.md) — перенос между БД
- [architecture/users-permissions.md](../architecture/users-permissions.md) — права и допуск
- [features/import-export.md](../features/import-export.md) — инварианты round-trip
