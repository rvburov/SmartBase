# Руководство разработчика

## Требования

- **.NET SDK 10.0+** (см. [README](../../README.md) — установка под все ОС).
- Локальные инструменты (EF CLI) — из `dotnet-tools.json`: `dotnet tool restore`.

## Запуск и сборка

```bash
cd calendar_avalonia
dotnet run                     # запуск из исходников (Debug)
dotnet build -c Debug          # сборка
```

Полное описание — [build.md](build.md): dev-режим (`--no-build`, `watch`), сборка
готовых бинарников по ОС через `Scripts/*-build.sh` (RID, Config, версия), раскладка
вывода.

## Раскладка вывода

Проект использует нестандартные пути, чтобы не засорять корень и системный диск
(см. [databases.md](../architecture/databases.md), `Directory.Build.props`, `nuget.config`):

| Папка | Что | В git |
|---|---|---|
| `artifacts/` | вся сборка (`bin`/`obj`/`publish`), `UseArtifactsOutput` | нет |
| `packages/` | локальный кэш NuGet (аналог venv) | нет |
| `dist/` | готовые сборки для раздачи | нет |
| `.localdata/` | dev-данные при Debug (`database.db`, `app.db`) | нет |

## База данных при разработке

В Debug файлы БД лежат в `.localdata/` в корне проекта (путь вычисляется по маркеру
`SmartBase.csproj`, см. [AppSettings](../../Utils/AppSettings.cs)) — изолированно,
переживает `dotnet clean` и снос `artifacts/`. В Release — в папке данных
пользователя ОС. Подробнее — [databases.md](../architecture/databases.md).

## Миграции EF Core

Схема `database.db` управляется миграциями EF Core; они применяются автоматически
при старте ([DatabaseManager](../../Database/DatabaseManager.cs)). Добавление новой:

```bash
dotnet tool restore
dotnet ef migrations add ИмяМиграции --msbuildprojectextensionspath artifacts/obj/SmartBase
```

> Флаг `--msbuildprojectextensionspath` нужен из-за `UseArtifactsOutput`: `obj`
> лежит в `artifacts/obj/SmartBase`, и без указания пути EF CLI не находит проект.

**Правило:** миграции только сохраняют данные — без drop/recreate и потери строк
(то же для системной app.db — изменения аддитивно). См. [databases.md](../architecture/databases.md).

## Куда смотреть

- Общая архитектура и запуск — [architecture/overview.md](../architecture/overview.md).
- Модель данных — [architecture/data-model.md](../architecture/data-model.md).
- Как работают фичи — [features/](../features/).

## Связанные документы

[[../architecture/overview]], [[../architecture/databases]], [[user-guide]]
