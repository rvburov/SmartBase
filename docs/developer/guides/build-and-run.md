# Сборка и запуск

Как запускать приложение при разработке и как собирать готовые бинарники для
раздачи пользователям. Краткий старт — в [README](../../../README.md); здесь полное
описание с особенностями каждого режима.

## Запуск для разработки

```bash
cd SmartBase
dotnet run -f net10.0-windows10.0.17763    # Windows
dotnet run -f net10.0                      # macOS, Linux
```

Флаг `-f` обязателен: проект многотаргетный (см. [«Два таргета»](#два-таргета-и-почему-их-два)),
и без явного выбора `dotnet run` не знает, что запускать. **На Windows запускайте только
windows-таргет** — под `net10.0` приложение работает, но нативных уведомлений в нем нет.

`dotnet run` каждый раз проверяет изменения и при необходимости пересобирает проект.
Первая сборка в сессии — «холодная» и долгая (стартует MSBuild-сервер, JIT компилятора,
компилятор XAML Avalonia и анализаторы EF). Чтобы **не пересобирать при повторных
запусках**, когда код не менялся:

```bash
dotnet run -f net10.0-windows10.0.17763 --no-build   # запустить уже собранное, без пересборки
artifacts/bin/SmartBase/debug_net10.0-windows10.0.17763/SmartBase.exe   # еще быстрее: прямой запуск
dotnet watch run -f net10.0-windows10.0.17763        # авто-пересборка и перезапуск при сохранении
```

Папка вывода теперь содержит таргет в имени: `artifacts/bin/SmartBase/debug_<TFM>/`.

> ⚠️ `--no-build` и прямой запуск `.exe` стартуют **последнюю собранную** версию.
> Менял код → сначала собери (`dotnet run` либо `dotnet build`), иначе запустится
> старый бинарь.

**Правило:** менял код → `dotnet run`; код не менялся, просто перезапустить →
`--no-build` или прямой `.exe`.

Старт приложения зависит от конфигурации (Debug медленнее Release из-за JIT) и от
состояния БД, а не от объема данных как такового — детали в
[performance.md](../architecture/performance/overview.md).

## Два таргета и почему их два

В [SmartBase.csproj](../../../SmartBase.csproj) объявлено `<TargetFrameworks>`:

| Таргет | Для чего |
|--------|----------|
| `net10.0` | macOS, Linux, а также все проекты `Tests/*` — они ссылаются на приложение и собираются под этот таргет |
| `net10.0-windows10.0.17763` | **только Windows** — единственный, под которым работают нативные уведомления |

Причина второго таргета одна, но существенная: NuGet выбирает файлы пакета по таргету, и
под голым `net10.0` от `DesktopNotifications.Windows` и `Microsoft.Toolkit.Uwp.Notifications`
приезжают сборки **без WinRT**. Приложение с ними собирается и запускается как обычно, но
менеджер уведомлений падает на старте с `TypeLoadException` (нет `ToastNotificationManagerCompat`),
исключение гасится, и баннеры Windows молча не показываются. Под windows-таргетом приезжают
WinRT-версии (`lib/net6.0-windows10.0.17763` и `lib/net5.0-windows10.0.17763`) — и тосты работают.

Отсюда правила:

- **Windows собирать и запускать только windows-таргетом.** `Scripts/windows-build.sh` передает
  `-f` сам; при ручном `dotnet run`/`build` флаг нужно указать.
- Проверить, что бэкенд поднялся, можно по логу: на старте пишется строка
  «Менеджер нативных уведомлений создан: WindowsNotificationManager», а при сбое —
  предупреждение (раздел «Профиль → Логи» или `logs.db`). Раньше сбой был бесследным.
- `EnableWindowsTargeting` в csproj разрешает собирать windows-таргет **с macOS/Linux** —
  кросс-сборка дистрибутивов с одной машины продолжает работать.

Устройство самой службы — [notifications.md](../features/notifications.md).

## Сборка для раздачи

Под каждую ОС — **один скрипт** в [Scripts/](../../../Scripts/) (имя = `<ОС>-build`).
Он прячет длинную команду `dotnet publish` и все особенности платформы. Результат
раскладывается по версиям — в папке `dist/Version <X.Y.Z>/<rid>/`: та же версия
перезаписывает свою папку, новая версия создает отдельную. Установка .NET пользователю
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
  [SmartBase.csproj](../../../SmartBase.csproj) и становится текущим для последующих сборок
  тоже (не только для этой). Без аргумента берется то, что уже указано в csproj.

> Все платформы собираются с одной машины (cross-compilation). Папка `dist/` —
> в `.gitignore`, пересоздается. Скрипты — bash, на Windows запускаются из Git Bash.

> **Windows: ошибка «Нет установленных дистрибутивов подсистемы Windows для Linux».**
> Значит `.sh` ушел в WSL-овый `C:\Windows\System32\bash.exe` (когда `System32` в PATH
> раньше Git). Скрипты используют шебанг `#!/bin/bash` и помечены исполняемыми — обычно
> хватает `Scripts/windows-build.sh`. Если ошибка все же появляется, запусти bash из Git
> по абсолютному пути: `/usr/bin/bash Scripts/windows-build.sh`.

### Windows

```bash
Scripts/windows-build.sh                        # win-x64, версия из csproj
Scripts/windows-build.sh win-x64 Release 1.2.3  # с номером версии 1.2.3
```

Результат: `dist/Version <X.Y.Z>/win-x64/SmartBase.exe`. Иконка вшита в `.exe` (через
`ApplicationIcon` в csproj), отдельных действий не требует. Рядом — `SmartBase-Setup.exe`
(тот же бинарь в режиме установщика, см. [auto-update.md](auto-update.md)).

### Linux

```bash
Scripts/linux-build.sh                          # linux-x64, версия из csproj
Scripts/linux-build.sh linux-x64 Release 1.2.3  # с номером версии 1.2.3
```

Результат: `dist/Version <X.Y.Z>/linux-x64/SmartBase` (бит запуска выставлен). Рядом —
`SmartBase-Setup` (тот же бинарь в режиме установщика, см. [auto-update.md](auto-update.md)):
запущенный на целевой машине, он ставит приложение в `~/.local/share/SmartBase`, сам пишет
`.desktop`-ярлыки и иконку в XDG-папки и умеет `--uninstall`. `Scripts/linux-install.sh` —
ручная альтернатива (только иконка + пункт меню для уже разложенного бинаря).

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

Результат: `dist/Version <X.Y.Z>/osx-arm64/SmartBase.app` и `…/osx-x64/SmartBase.app`.
Рядом — `SmartBase-Setup.app` (копия бандла в режиме установщика, имя бандла `-Setup`): ставит
приложение в `~/Applications`, регистрирует в Launchpad/Finder, умеет `--uninstall`
(см. [auto-update.md](auto-update.md)). Подробности про иконки и бандл — [icons.md](../features/icons.md).

## Версия и дата сборки

- **Версия** хранится в [SmartBase.csproj](../../../SmartBase.csproj) (`<Version>`).
  Третий аргумент скрипта **перезаписывает** ее в csproj — то есть номер версии меняется
  в одном месте и остается текущим для дальнейших сборок.
- **Дата сборки** определяется автоматически — это время файла бинаря
  (`Environment.ProcessPath`, работает и в single-file), указывать ее не нужно.
- И версия, и дата видны в приложении: **Настройки → О программе**
  ([SettingsView](../../../Views/Settings/SettingsView.axaml.cs)).

> Бейдж версии в [README](../../../README.md) — отдельный, обновляется вручную.

## Раскладка вывода

Проект использует нестандартные пути (`UseArtifactsOutput` в `Directory.Build.props`,
`nuget.config`), чтобы не засорять корень и системный диск:

| Папка | Что | В git |
|---|---|---|
| `artifacts/` | вся сборка (`bin`/`obj`/`publish`) | нет |
| `packages/` | локальный кэш NuGet (аналог venv) | нет |
| `dist/` | готовые сборки для раздачи, по версиям `Version <X.Y.Z>/<rid>/` | нет |
| `.localdata/` | dev-данные при Debug: `database.db`, `app.db`, `logs.db` + служебные `instance.lock`, `show.signal`, `crash.log` | нет |

Очистить только сборку — удалить `artifacts/` и `packages/`. Папку `.localdata/` при
очистке не трогают (там dev-база). Подробнее — [databases.md](../architecture/database/storage-and-migrations.md).

## Скрипты сборки ([Scripts/](../../../Scripts/))

| Скрипт | Назначение |
|---|---|
| `windows-build.sh` | сборка Windows → `dist/<rid>/SmartBase.exe` |
| `linux-build.sh` | сборка Linux → `dist/<rid>/SmartBase` |
| `macos-build.sh` | сборка macOS → `dist/<rid>/SmartBase.app` |
| `linux-install.sh` | установка иконки/ярлыка на Linux-машине (пост-установка) |
| `generate-icons.sh` | проверка мастеров и генерация `icon.ico`/`icon.icns` (только macOS) |

## Связанные документы

[[developer-guide]], [[../features/icons]], [[../architecture/database/storage-and-migrations]], [[../architecture/performance/overview]]
