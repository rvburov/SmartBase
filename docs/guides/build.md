# Сборка и запуск

Как запускать приложение при разработке и как собирать готовые бинарники для
раздачи пользователям. Краткий старт — в [README](../../README.md); здесь полное
описание с особенностями каждого режима.

## Запуск для разработки

```bash
cd calendar_avalonia
dotnet run                 # собрать при необходимости и запустить (Debug)
```

`dotnet run` каждый раз проверяет изменения и при необходимости пересобирает проект.
Первая сборка в сессии — «холодная» и долгая (стартует MSBuild-сервер, JIT компилятора,
компилятор XAML Avalonia и анализаторы EF). Чтобы **не пересобирать при повторных
запусках**, когда код не менялся:

```bash
dotnet run --no-build      # запустить уже собранное, без пересборки
artifacts/bin/DataBase/debug/DataBase.exe   # ещё быстрее: прямой запуск, без проверки сборки
dotnet watch run           # авто-пересборка и перезапуск при каждом сохранении
```

> ⚠️ `--no-build` и прямой запуск `.exe` стартуют **последнюю собранную** версию.
> Менял код → сначала собери (`dotnet run` либо `dotnet build`), иначе запустится
> старый бинарь.

**Правило:** менял код → `dotnet run`; код не менялся, просто перезапустить →
`--no-build` или прямой `.exe`.

Старт приложения зависит от конфигурации (Debug медленнее Release из-за JIT) и от
состояния БД, а не от объёма данных как такового — детали в
[performance.md](../architecture/performance.md).

## Сборка для раздачи

Под каждую ОС — **один скрипт** в [Scripts/](../../Scripts/) (имя = `<ОС>-build`).
Он прячет длинную команду `dotnet publish` и все особенности платформы. Результат
раскладывается по версиям — в папке `dist/Version <X.Y.Z>/<rid>/`: та же версия
перезаписывает свою папку, новая версия создаёт отдельную. Установка .NET пользователю
не нужна (**self-contained**), бинарь склеен в один файл (**single-file**, кроме
macOS — там бандл `.app`), размер ~100–150 МБ.

Сигнатура у всех скриптов одинаковая:

```
Scripts/<ОС>-build.sh [RID] [Config] [Version]
```

- **RID** — целевая платформа (`win-x64`, `win-arm64`, `linux-x64`, `linux-arm64`,
  `osx-arm64`, `osx-x64`). По умолчанию — основной x64/arm64 для своей ОС.
- **Config** — `Release` (по умолчанию) или `Debug`.
- **Version** — номер версии: **записывается в `<Version>`** в
  [DataBase.csproj](../../DataBase.csproj) и становится текущим для последующих сборок
  тоже (не только для этой). Без аргумента берётся то, что уже указано в csproj.

> Все платформы собираются с одной машины (cross-compilation). Папка `dist/` —
> в `.gitignore`, пересоздаётся. Скрипты — bash, на Windows запускаются из Git Bash.

### Windows

```bash
Scripts/windows-build.sh                        # win-x64, версия из csproj
Scripts/windows-build.sh win-x64 Release 1.2.3  # с номером версии 1.2.3
```

Результат: `dist/Version <X.Y.Z>/win-x64/DataBase.exe`. Иконка вшита в `.exe` (через
`ApplicationIcon` в csproj), отдельных действий не требует. Рядом — `DataBase-Setup.exe`
(тот же бинарь в режиме установщика, см. [auto-update.md](auto-update.md)).

### Linux

```bash
Scripts/linux-build.sh                          # linux-x64, версия из csproj
Scripts/linux-build.sh linux-x64 Release 1.2.3  # с номером версии 1.2.3
```

Результат: `dist/Version <X.Y.Z>/linux-x64/DataBase` (бит запуска выставлен). Иконка и пункт меню —
**отдельным шагом на целевой Linux-машине**: `Scripts/linux-install.sh` (пишет
`.desktop` + иконку в XDG-папки текущего пользователя). Это пост-установка, не сборка.

### macOS

Под Mac собирают **дважды** — отдельно Apple Silicon и Intel, с одной и той же версией.
Скрипт публикует бинарь **и** упаковывает в `.app` с иконкой (без бандла иконки в Dock нет).

```bash
# версия из csproj:
Scripts/macos-build.sh                          # Apple Silicon (osx-arm64)
Scripts/macos-build.sh osx-x64                  # Intel        (osx-x64)

# с номером версии 1.2.3 (одна и та же версия для обоих таргетов):
Scripts/macos-build.sh osx-arm64 Release 1.2.3  # Apple Silicon
Scripts/macos-build.sh osx-x64   Release 1.2.3  # Intel
```

Результат: `dist/Version <X.Y.Z>/osx-arm64/DataBase.app` и `…/osx-x64/DataBase.app`.
Подробности про иконки и бандл — [icons.md](../features/icons.md).

## Версия и дата сборки

- **Версия** хранится в [DataBase.csproj](../../DataBase.csproj) (`<Version>`).
  Третий аргумент скрипта **перезаписывает** её в csproj — то есть номер версии меняется
  в одном месте и остаётся текущим для дальнейших сборок.
- **Дата сборки** определяется автоматически — это время файла бинаря
  (`Environment.ProcessPath`, работает и в single-file), указывать её не нужно.
- И версия, и дата видны в приложении: **Настройки → О программе**
  ([SettingsView](../../Views/Settings/SettingsView.axaml.cs)).

> Бейдж версии в [README](../../README.md) — отдельный, обновляется вручную.

## Раскладка вывода

Проект использует нестандартные пути (`UseArtifactsOutput` в `Directory.Build.props`,
`nuget.config`), чтобы не засорять корень и системный диск:

| Папка | Что | В git |
|---|---|---|
| `artifacts/` | вся сборка (`bin`/`obj`/`publish`) | нет |
| `packages/` | локальный кэш NuGet (аналог venv) | нет |
| `dist/` | готовые сборки для раздачи, по версиям `Version <X.Y.Z>/<rid>/` | нет |
| `.localdata/` | dev-данные при Debug (`database.db`, `app.db`) | нет |

Очистить только сборку — удалить `artifacts/` и `packages/`. Папку `.localdata/` при
очистке не трогают (там dev-база). Подробнее — [databases.md](../architecture/databases.md).

## Скрипты сборки ([Scripts/](../../Scripts/))

| Скрипт | Назначение |
|---|---|
| `windows-build.sh` | сборка Windows → `dist/<rid>/DataBase.exe` |
| `linux-build.sh` | сборка Linux → `dist/<rid>/DataBase` |
| `macos-build.sh` | сборка macOS → `dist/<rid>/DataBase.app` |
| `linux-install.sh` | установка иконки/ярлыка на Linux-машине (пост-установка) |
| `generate-icons.sh` | генерация `icon.ico`/`icon.icns` из `icon.png` (только macOS) |

## Связанные документы

[[developer-guide]], [[../features/icons]], [[../architecture/databases]], [[../architecture/performance]]
