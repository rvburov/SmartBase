# Автотесты (Tests/Unit, Tests/Integration)

## Назначение

Набор автоматических тестов (xUnit) поверх **логики и слоя данных** приложения: Core
(разбор дат, валидаторы, планировщики импорта/экспорта, вычисления) и репозитории
(`ConstructorRepository`, `CategoryRepository`, `UserDirectory`) на временной SQLite-БД,
плюс опционально провайдер PostgreSQL. Фиксирует бизнес-инварианты из
[architecture](../architecture/) — keyset-пагинацию, консистентность календарной проекции,
перенос коллекций между БД, дельты настроек, авторство, «последний побеждает» при
многопользовательской записи.

UI/ViewModels в охват сознательно не входят — тестируется логика, стоящая за интерфейсом.

**Тесты расширений живут не здесь.** У них свое дерево `Tests/Extensions/<Имя>/` — по проекту на
расширение, без единой связи с наборами ядра: в `Tests/Unit` и `Tests/Integration` не должно быть
ни одного обращения к конкретному расширению (`SmartBase.Extensions.<Имя>`) и к реестру
`ExtensionRegistry`, который их создает, — это проверяется шагом CI. Остальной общий механизм
расширений (`SmartBase.Extensions` — `IExtension`, `ExtensionAction`) принадлежит ядру, и тесты
сценариев работают с ним на стендовых расширениях. Зачем так и как устроено —
[extension-tests.md](extension-tests.md).

## Как запускать

Из корня репозитория:

```bash
dotnet test Tests/Unit                 # быстрый гейт: чистая логика, мс
dotnet test Tests/Integration          # SQLite + PostgreSQL (медленнее)
dotnet test SmartBase.sln              # оба набора ядра
./Tests/run-all.sh                     # оба набора ядра и стенд панели чтения (+ --with-bench)
./Tests/run-all.sh --with-extensions   # плюс тесты расширений
dotnet test Tests/Integration --collect:"XPlat Code Coverage"   # покрытие (coverlet)
```

Граница между проектами — по характеру теста: `Unit` быстрый и без внешних зависимостей
(гейт на каждый коммит), `Integration` требует файловой БД и, для PG-тестов, стенда.

## Структура

| Проект | Область |
|---|---|
| `Tests/Unit` | Чистая логика: даты, вычисления и периоды, планы импорта/экспорта, фильтры, раскладки, буфер, настройки, уведомления, формы сообщений, подстановки и технические пределы транспорта |
| `Tests/Integration` | Временные SQLite и PostgreSQL: CRUD, история, участники, проекции, связи, перенос, импорт, копии, сеть, фоновые службы, конкурентные изменения |
| `Tests/RecordPage`, `Tests/Sections`, `Tests/TasksQuickAdd`, `Tests/ServiceGrids` и другие стенды без экрана | Реальные контролы Avalonia.Headless, обработчики, геометрия и обновление интерфейса |
| `Tests/Extensions/*` | Изолированные проекты расширений, отдельно от ядра |

Текущий перечень сценариев и команды стендов — [Tests/README.md](../../../Tests/README.md).
Каждая регрессия проверяет поведение приложения и сохранность данных, а не только наличие метода.
Новые тесты проверяются мутантами: возврат исправленного дефекта должен дать красный результат.

### Снятие внутренних ограничений 30.10.2026

Тесты ролей, допусков, пароля SmartBase, политик, меток, мастер-пароля и аудита удалены
вместе с соответствующими механизмами. Вместо прежней матрицы действуют регрессии:

- `LegacySecurityMetadataTests` — старые запреты не применяются после повторного открытия,
  дублирования и переноса между провайдерами; метаданные сохраняются без уничтожения.
- `UserOptionsTests` — справочник имен без допуска, исторические значения и смена имени.
- `IngressHandlerTests`, `IngressReadTests`, `InboxModesTests` — анонимный API, выбор базы
  через `database`, чтение, сообщения, транзакции и повторная доставка.
- `OutgoingSourceTests`, `OutgoingBodySizeTests`, `EgressHttpTests` — выбранный состав,
  точный объем UTF-8, предел тела, отправка без политики, перенаправления и маскирование
  секретов внешнего сервиса.

Серверная авторизация PostgreSQL, ошибки файловой системы, TLS и технические ограничения
памяти продолжают проверяться. Удаление механизма SmartBase не превращает сбой сервера
или диска в успешную операцию.

## PostgreSQL

Тесты `PgProviderTests` (провайдер), `PgBulkWriteTests` (сырые массовые пути: заливка и
раскраска свода, prepared-правки значений при смене настроек поля, сортированный CSV
журнала, капнутый счет пилюль задач — SQLite такие ошибки прощает, ловит только живой PG),
`PgCreateDatabaseTests` (создание базы и
роли-владельца из приложения, `PgDatabaseCreator`), `PgLibraryTests` и `PgSearchTests`
(сверки: один сценарий «Библиотеки документов» или поиска по названию у задач, календаря и
окна связи идет на SQLite и на PostgreSQL, и ответы обязаны совпасть строка в строку),
`PgCalendarSummaryTests` (сводки календаря `calendar_day_counts` и `calendar_spans` на триггерах
целого запроса: правка, очистка и перенос даты, удаление записи и поля каскадом, дубль, пакетное
удаление и перенос между базами в обе стороны — сводка дней равна проекции, граница длительности не
меньше настоящей; перенос в PG откатывается, если исходник не удалить) —
`[SkippableFact]`: без поднятого
стенда они помечаются **Skipped**, а не падают, поэтому локально и в CI без Docker набор
остается зеленым. Стенд — docker-контейнер `sb-pg` (порт **5455**, о выборе порта — врезка
в [../guides/postgresql-setup.md](../guides/postgresql-setup.md)); нужны базы `smartbase`
и `smartbase2`:

```bash
docker run --name sb-pg -e POSTGRES_PASSWORD=postgres -p 5455:5432 -d postgres:16
docker exec sb-pg psql -U postgres -c "CREATE DATABASE smartbase;"
docker exec sb-pg psql -U postgres -c "CREATE DATABASE smartbase2;"
```

На macOS docker работает через colima: перед первым запуском `brew services start colima`.

Без докера — например, на Windows с установленным PostgreSQL — стенд поднимается отдельным
кластером из тех же программ; служба сервера на 5432 им не задевается:

```bash
PG="C:/Program Files/PostgreSQL/18/bin"   # там, где стоит сервер
"$PG/initdb.exe" -D <папка стенда> -U postgres --pwfile=<файл с паролем postgres> \
                 -A scram-sha-256 -E UTF8 --locale=en-US
# в postgresql.conf стенда дописать: port = 5455 и listen_addresses = 'localhost'
"$PG/pg_ctl.exe" -D <папка стенда> -l <журнал сервера> start
PGPASSWORD=postgres "$PG/psql.exe" -h localhost -p 5455 -U postgres \
    -c "CREATE DATABASE smartbase;" -c "CREATE DATABASE smartbase2;"
```

Локаль `en-US` — ради английских сообщений сервера, как у докер-образа. После перезагрузки такой
стенд сам не стартует: перед прогоном его запускают тем же `pg_ctl … start`, иначе тесты
PostgreSQL молча уйдут в Skipped.

Перед прогоном каждый PG-тест провайдера сбрасывает схему (`DROP SCHEMA public CASCADE;
CREATE SCHEMA public;`), чтобы миграция `InitialCreatePg` накатилась начисто.
`PgCreateDatabaseTests` работают иначе: создают базы/роли с уникальными именами
`sb_create_test_*` / `sb_create_role_*` и подчищают их в `finally`
(`DROP DATABASE … WITH (FORCE)`), так что стенд остается чистым. В CI все пять классов гоняет
job `postgres` (фильтр `PgProviderTests|PgCreateDatabaseTests|PgBulkWriteTests|PgLibraryTests|PgSearchTests|PgCalendarSummaryTests`)
с guard-проверкой «Skipped: 0» — см. [../guides/ci.md](../guides/ci.md).

## Замечания по сборке

- Кэш NuGet перенаправлен внутрь репозитория в `packages/` (см. `nuget.config`), поэтому в
  `SmartBase.csproj` папка `packages/**` исключена из компиляции — иначе `.cs`-файлы из
  пакетов (например, `Microsoft.NET.Test.Sdk.Program.cs`) попадали бы в глоб `**/*.cs`
  основного проекта и ломали сборку.
- Рабочие папки ИИ-агентов (`.claude/worktrees/`, `.agents/worktrees/`, см. AGENTS.md) — полные
  копии проекта внутри него, но в каталогах с точкой: их глоб `**/*.cs` пропускает сам (проверено —
  ни одного файла), а каталог без точки втянул бы чужую копию исходников. `ScreenReferenceTests`,
  который обходит весь корень, пропускает каталоги с точкой явно. Кэш NuGet рабочие папки берут
  из основной (`RestorePackagesPath` в `Directory.Build.props`).
- Перф-замеры (`Tests/Benchmarks`), генератор стенда (`Tests/DbGenerator`) и многопроцессный
  `Tests/MultiProcess` — консольные инструменты, не автотесты
  ([benchmarks.md](benchmarks.md), [multi-process.md](multi-process.md), [test-database.md](test-database.md)).

## Платформенные ловушки

Локально тесты идут на Windows, а в CI — еще на Linux и двух macOS, и между выпусками матрица
не запускается сама ([../guides/ci.md](../guides/ci.md)). На чем уже ловились тесты, прошедшие
на Windows:

- **Регистр.** Файловая система Linux различает регистр, и `StringComparison.Ordinal` для
  тождества путей там верен. Но признак формата — не тождество: расширение файла сравнивается
  без учета регистра на любой ОС (`VaultRules.IsMarkdown`).
- **UNC-путь.** `Path.GetFullPath` на Linux и macOS не знает записи `\\server\share`: обратный
  слеш там обычный знак имени, и к строке приклеивается текущая папка. UNC распознается по сырой
  строке до нормализации (`NetworkPath.IsUnc`).
- **Имя файла — 255 байт, а не знаков.** На ext4 кириллица занимает два байта на знак, и имя из
  170 русских букв не создать вовсе (`ENAMETOOLONG`); на Windows и APFS предел считается в
  знаках. Длинные имена в тестах набираются латиницей (`TimeCollectorRunTests`).
- **Пул потоков раннера занят.** xUnit гоняет классы параллельно, и задача из пула может ждать
  свободного потока дольше короткого предела вроде 300 мс: здоровый путь выглядит «не отвечает».
  Пробы пути идут на своей нити (`DbHealthMonitor.StartProbe`), а короткий предел в тесте
  ставится только на ту часть, где проверяется сам предел (`MountResilienceTests`).

## Связанные документы

- [architecture/performance.md](../architecture/performance/overview.md) — keyset-пагинация, календарная проекция
- [architecture/multi-database.md](../architecture/database/multi-database.md) — перенос между БД
- [architecture/users-permissions.md](../architecture/users-permissions.md) — права и допуск
- [features/import-export.md](../features/import-export/overview.md) — инварианты round-trip
