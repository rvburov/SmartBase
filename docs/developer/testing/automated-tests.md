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

**Тесты расширений живут не здесь.** У них свое дерево `Tests/Extensions/<Имя>/` — по проекту на
расширение, без единой связи с наборами ядра (в `Tests/Unit` и `Tests/Integration` не должно быть
ни одного `using SmartBase.Extensions.*`, это проверяется шагом CI). Зачем так и как устроено —
[extension-tests.md](extension-tests.md).

## Как запускать

Из корня репозитория:

```bash
dotnet test Tests/Unit                 # быстрый гейт: чистая логика, мс
dotnet test Tests/Integration          # SQLite + PostgreSQL (медленнее)
dotnet test SmartBase.sln              # оба набора ядра
./Tests/run-all.sh                     # оба набора ядра (+ --with-bench для бенчмарков)
./Tests/run-all.sh --with-extensions   # плюс тесты расширений
dotnet test Tests/Integration --collect:"XPlat Code Coverage"   # покрытие (coverlet)
```

Граница между проектами — по характеру теста: `Unit` быстрый и без внешних зависимостей
(гейт на каждый коммит), `Integration` требует файловой БД и, для PG-тестов, стенда.

## Структура

Два тест-проекта, разделенных по характеру (скорость/зависимости):

| Проект | Что внутри |
|--------|-----------|
| **Tests/Unit** | Чистые юнит-тесты без БД и IO: `DateValueTests`, `CalendarRecordValidatorTests`, `PermissionsTests`, `PlannerTests`, `NotificationSchedulerTests` (ядро календарных уведомлений), `TaskEventSchedulerTests` (ядро уведомлений по задачам: отбор по тумблерам, свертка повторных смен стадии в один честный переход «было → стало», сводка, тексты плашек и отсутствие в них буквы «е с точками»), `NotificationSettingsTests` (дельты тумблеров, метка, курсоры очередей задач), `AutostartTests` (построение записи автозапуска, «скрывать при закрытии?», дельты автозапуска/фона), `IconRegistryTests` (ключи иконок из C# существуют в `Assets/Styles/Icons.axaml` — опечатка в ключе иначе падает только при открытии экрана), `ScrollingConventionTests` (сторож стандарта прокрутки, [ui/scrolling.md](../ui/scrolling.md): у `ScrollViewer` нет своих отступов, markdown только виджетом `AppMarkdown`, у вынесенной полосы штатная спрятана — правила словесные, и новый экран легко пишется мимо них), `EtaEstimatorTests` (оценка оставшегося времени долгих операций), `ChangeLogLabelsTests` (подписи журнала и диагностики — общий источник для таблиц и выгрузки CSV), `TitleTextTests` (название для показа — одна строка, полное значение не тронуто), `DateFilterTreeTests` (дерево «год → месяц → день» в фильтре по дате: группировка сырых значений обоих форматов, суммы счетчиков, порядок под сортировку колонки, поиск по дню/месяцу/году/полной дате, галочка ветки только при полностью отмеченном поддереве, «(Пусто)» в конце), `TableViewportRangeTests` (навигация нижней панели таблицы: текст «10 – 50 из 125 437» с неразрывными разрядами, гашение кнопок у краев, нормализация диапазона от живой раскладки DataGrid, перевод слотов грида в номера строк — строка «+» последним слотом, «ничего не разложено» до первой раскладки, — шаг вертикальной полосы на экран и прыжки к краям), `DataGridInternalsTests` (имена internal-членов Avalonia DataGrid, на которых стоит навигация нижней панели таблицы: `DisplayData` с `FirstScrollingSlot`/`LastScrollingSlot`, `DataGridRow.IsRecycled`, `VerticalScrollBar` + `ProcessVerticalScroll` — рефлексия отказывает молча, после апгрейда Avalonia индикатор просто начинает врать), `TaskGroupingTests` (раскладка раздела задач по коллекциям: порядок групп «как в сайдбаре», ключ группы, индикатор выполненного) `TaskFiltersTests` (быстрые фильтры задач и их счетчики), `TaskFilterItemTests` (пункт фильтра: счетчик наблюдаемый — иначе свернутый бокс отстает от списка), `TaskSelectionTests` (выделение карточек задач: диапазон от якоря, ключ (база, запись, поле-исполнитель), переживание перезагрузки списка) и `SidebarChecksSettingsTests` (галочки коллекций в app.db: хранятся снятые, наборы календаря и задач раздельны, испорченное значение не роняет старт). Ноль внешних зависимостей, параллельные, миллисекунды. |
| **Tests/Integration** | На temp SQLite + PostgreSQL: CRUD (в т.ч. аудит-метаданные записи), `FieldTypeMatrixTests` (матрица типов свойств: round-trip / подпись / смена типа / user-дефолт), `KeysetPaginationTests`, `CalendarProjectionTests`, `CalendarWindowTests` (чтение окон там, где событие длиннее окна: в Диаграмму оно попадает на КАЖДОМ масштабе, который пересекает, — включая длинное timed, начавшееся до окна, а счетчик группы считает ровно то, что показано строками; в «Год» многодневные приходят отдельным запросом, однодневные — нет), `ChangeLogTests`, `PermissionServiceTests` (допуск, роли, удаление пользователя,
отметка активности), `PermissionMatrixTests` (матрица «право → операция»: каждый бит
`PermAction` снимается по одному и обязан отбить именно свое действие),
`PermissionMaskMigrationTests` (бэкфилл сохраненных масок при появлении нового бита,
SQLite + PG), `CsvExportTests` (выгрузка журнала и диагностики в CSV: экранирование и BOM, фильтры, keyset без пропусков, отмена не оставляет обрезанный файл), `CrossMoveTests`, `ImportMatrixTests`/`ExportMatrixTests` (round-trip; у Excel вдобавок схема пакета через `OpenXmlValidator` и проверка, что архив пишется потоком, а не копится в памяти), `DescriptionFieldTests` (описание как обычное поле с ролью) и `DescriptionMigrationTests` (переезд описаний из колонки в поле, SQLite + PG), `RoleAdoptionTests` (подтягивание ролей по имени столбца) и `SectionSetupPlanTests` (план подготовки коллекции к разделу — что покажет окно перед созданием записи), `ContentionTests`, `PragmaTests`, `PgProviderTests`, `NotificationServiceTests` (служба уведомлений на календарной проекции и на очереди задач: свой тумблер у каждого вида, значок общий, клик по метке `task:` не путается с календарным), `TaskEventWriteTests` (запись очереди `task_events` из `SetFieldValue`: свое назначение и первичная простановка стадии молчат, правки обычных свойств не стоят ни строки, дубль/перенос/отказ прав не оставляют следа), `TaskEventReaderTests` (курсоры по базам: первое знакомство историю не проигрывает, чужие события пропускаются, но курсор через них перешагивает, `Reseed` при включении тумблера), `SingleInstanceTests` (лок единственного экземпляра на temp-папке), `IntroSeedTests` (вводный чек-лист первого запуска: полнота задач, признак чистой установки, «завелся один раз и после удаления не вернулся»). Здесь же обвязка: `TempDatabase` (SQLite через настоящий `DatabaseManager.Open`), `SampleData`, `ImportExport`, `FakeSettingsDatabase`, `PgTestServer`. Прогон **последовательный** (см. ниже). |

Ключевые принципы обвязки:

- **Только файловые БД.** `TempDatabase` поднимает SQLite на временном файле — `:memory:`
  не годится (WAL и ATTACH требуют файла на диске). При `Dispose` файл и спутники
  `-wal/-shm` удаляются (пул соединений сбрасывается `SqliteConnection.ClearAllPools`).
- **Оба набора гоняются последовательно** (`[assembly: CollectionBehavior(DisableTestParallelization = true)]`
  в `AssemblyInfo.cs` каждого проекта). Integration — потому что `ClearAllPools` процесс-глобальна
  и при параллельных тестах задевала бы чужие подключения. Unit — потому что
  `AppSettings.AttachStore` кладет хранилище настроек в статическое поле, одно на процесс:
  параллельные классы подменяли его друг другу между Attach и проверкой. Полный прогон Unit —
  порядка сотни миллисекунд, так что последовательность ничего не стоит.
- **Настоящий путь инициализации.** База поднимается через `DatabaseManager.Open` — те же
  миграции, прагмы, сиды и регистрация текущего пользователя администратором, что и в
  приложении. Поэтому тесты идут ровно тем же кодом, а не его тестовым дублером.
- **Импорт/экспорт** вызывают ровно те методы, что и UI:
  `ImportPlanner.Plan → ITableSource.Analyze → ReadRows → ImportStreamedCollectionIsolated`
  и `ExportPlanner.Plan → GetExportColumnsIsolated → ReadExportRowsIsolated → ITableWriter`.
  Функциональные матрицы прежних харнессов ImportPerf/ExportPerf/PgSmoke перенесены сюда
  как xUnit-тесты; перф-замеры — в [benchmarks.md](benchmarks.md).

## PostgreSQL

Тесты `PgProviderTests` — `[SkippableFact]`: без поднятого стенда они помечаются **Skipped**,
а не падают, поэтому локально и в CI без Docker набор остается зеленым. Стенд —
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

- [architecture/performance.md](../architecture/performance/overview.md) — keyset-пагинация, календарная проекция
- [architecture/multi-database.md](../architecture/database/multi-database.md) — перенос между БД
- [architecture/users-permissions.md](../architecture/users-permissions.md) — права и допуск
- [features/import-export.md](../features/import-export/overview.md) — инварианты round-trip
