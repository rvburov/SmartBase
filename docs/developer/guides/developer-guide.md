# Руководство разработчика

## Требования

- **.NET SDK 10.0+** (см. [README](../../../README.md) — установка под все ОС).
- Локальные инструменты (EF CLI) — из `dotnet-tools.json`: `dotnet tool restore`.

## Запуск и сборка

```bash
cd SmartBase
dotnet run -f net10.0-windows10.0.17763   # запуск из исходников (Debug), Windows
dotnet run -f net10.0                     # то же на macOS/Linux
dotnet build -c Debug                     # сборка (оба таргета)
```

Проект многотаргетный, поэтому `dotnet run` требует явного `-f`; на Windows берите
windows-таргет — только под ним работают нативные уведомления.

Полное описание — [build.md](build-and-run.md): dev-режим (`--no-build`, `watch`), два
таргета и зачем они, сборка готовых бинарников по ОС через `Scripts/*-build.sh`
(RID, Config, версия), раскладка вывода.

## Раскладка вывода

Проект использует нестандартные пути, чтобы не засорять корень и системный диск
(см. [databases.md](../architecture/database/storage-and-migrations.md), `Directory.Build.props`, `nuget.config`):

| Папка | Что | В git |
|---|---|---|
| `artifacts/` | вся сборка (`bin`/`obj`/`publish`), `UseArtifactsOutput` | нет |
| `packages/` | локальный кэш NuGet (аналог venv) | нет |
| `dist/` | готовые сборки для раздачи | нет |
| `.localdata/` | dev-данные при Debug: `database.db`, `app.db`, `logs.db` + служебные `instance.lock`, `show.signal`, `crash.log` | нет |

## База данных при разработке

В Debug файлы БД лежат в `.localdata/` в корне проекта (путь вычисляется по маркеру
`SmartBase.csproj`, см. [AppSettings](../../../Utils/AppSettings.cs)) — изолированно,
переживает `dotnet clean` и снос `artifacts/`. В Release — в папке данных
пользователя ОС. Подробнее — [databases.md](../architecture/database/storage-and-migrations.md).

## Миграции EF Core

Схема `database.db` управляется миграциями EF Core; они применяются автоматически
при старте ([DatabaseManager](../../../Database/DatabaseManager.cs)). Добавление новой:

```bash
dotnet tool restore
dotnet ef migrations add ИмяМиграции --context AppDbContext --framework net10.0 --msbuildprojectextensionspath artifacts/obj/SmartBase
```

> Флаг `--msbuildprojectextensionspath` нужен из-за `UseArtifactsOutput`: `obj`
> лежит в `artifacts/obj/SmartBase`, и без указания пути EF CLI не находит проект.
> Флаг `--framework` — из-за многотаргетности: EF CLI требует однозначный таргет.
> Берите базовый `net10.0` — миграции от платформы не зависят.

**Два набора миграций.** У пользовательской БД два провайдера: SQLite
(`AppDbContext`, `Database/Migrations/`) и PostgreSQL (`PgAppDbContext`,
`Database/Migrations/Pg/`). EF привязывает миграции к типу контекста, поэтому
каждое изменение сущностей = **два** запуска `migrations add` — по одному на контекст:

```bash
dotnet ef migrations add ИмяМиграции   --context AppDbContext   --framework net10.0 --msbuildprojectextensionspath artifacts/obj/SmartBase
dotnet ef migrations add ИмяМиграцииPg --context PgAppDbContext --framework net10.0 --output-dir Database/Migrations/Pg --msbuildprojectextensionspath artifacts/obj/SmartBase
```

**Правило:** миграции только сохраняют данные — без drop/recreate и потери строк
(то же для системной app.db — изменения аддитивно). См. [databases.md](../architecture/database/storage-and-migrations.md).

## Куда смотреть

- Общая архитектура и запуск — [architecture/overview.md](../architecture/overview.md).
- Модель данных — [architecture/data-model.md](../architecture/data-model.md).
- Как работают фичи — [features/](../features/).

## Диагностика сбоев (crash.log)

Необработанные исключения пишутся в `crash.log` рядом с данными приложения
(`AppSettings.AppDataDir`): глобальные предохранители в [Program.cs](../../../Program.cs)
ловят `AppDomain.UnhandledException`, упавшие fire-and-forget задачи
(`TaskScheduler.UnobservedTaskException` — паттерн `_ = Task.Run(...)` перестаёт
убивать процесс) и исключения, выбившие UI-цикл Avalonia (у 11.2.3 нет публичного
dispatcher-хука, поэтому это последний рубеж — основная защита остаётся за точечными
catch в UI, см. [Utils/DbErrors](../../../Utils/DbErrors.cs)). У пользователя вылет
больше не бесследный: просите файл `crash.log` из папки данных.

## Связанные документы

[[../architecture/overview]], [[../architecture/database/storage-and-migrations]], [[user-guide]]
