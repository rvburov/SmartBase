#!/bin/bash
#
# Сборка Windows-дистрибутива SmartBase одной короткой командой вместо длинного
# `dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o ...`:
#
#   Scripts/windows-build.sh                       # win-x64, Release (по умолчанию)
#   Scripts/windows-build.sh win-arm64             # другая архитектура
#   Scripts/windows-build.sh win-x64 Debug         # другая конфигурация
#   Scripts/windows-build.sh win-x64 Release 1.2.3 # задать версию (запишется в SmartBase.csproj)
#
# Особенности Windows-сборки:
#   • self-contained     — .NET у пользователя не нужен;
#   • PublishSingleFile  — все в одном .exe (нативные .dll вшиваются и
#                          распаковываются при запуске, см.
#                          IncludeNativeLibrariesForSelfExtract в SmartBase.csproj);
#   • иконка вшита в .exe — через ApplicationIcon в SmartBase.csproj, отдельных
#                          действий не требует.
#
# dotnet publish одинаково работает в любом шелле (Git Bash / PowerShell / CMD),
# поэтому Windows-сборка — обычный bash-скрипт, как linux/macos.
#
# Результат: dist/<RID>/SmartBase.exe — раздается как есть.
# ── Будущие особенности Windows-сборки добавляйте в массив flags ниже. ──
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
RID="${1:-win-x64}"
CONFIG="${2:-Release}"
VERSION="${3:-}"

# Целевая платформа сборки. Проект многотаргетный (см. TargetFrameworks в SmartBase.csproj),
# и Windows собирается ТОЛЬКО этим таргетом: лишь под ним NuGet отдает WinRT-версии
# DesktopNotifications.Windows и Microsoft.Toolkit.Uwp.Notifications. Собранный под голым
# net10.0 дистрибутив выглядит рабочим, но нативных уведомлений в нем нет.
TFM="net10.0-windows10.0.17763"

# Общие флаги publish — единая точка правки на будущее.
flags=(
  -f "$TFM"
  --self-contained true
  -p:PublishSingleFile=true
)

# Номер версии: если передан 3-м аргументом — ЗАПИСЫВАЕМ его в <Version> в
# SmartBase.csproj (становится текущим и для последующих сборок) и в бейдж README.
# Через временный файл — портативно к GNU/BSD sed (без sed -i).
csproj="$ROOT/SmartBase.csproj"
if [ -n "$VERSION" ]; then
  tmp="$(mktemp)"
  sed -E "s#<Version>[^<]*</Version>#<Version>${VERSION}</Version>#" "$csproj" > "$tmp" && mv "$tmp" "$csproj"
  readme="$ROOT/README.md"; tmp="$(mktemp)"
  sed -E "s#version-[^-]*-blue#version-${VERSION}-blue#" "$readme" > "$tmp" && mv "$tmp" "$readme"
  echo "==> Версия обновлена на $VERSION (SmartBase.csproj + бейдж README.md)"
fi

# Эффективная версия сборки = аргумент или текущая из csproj. Задает папку вывода
# dist/Version <X.Y.Z>/<rid>: та же версия перезаписывается, новая — отдельная папка.
EFFVER="${VERSION:-$(sed -nE 's#.*<Version>([^<]*)</Version>.*#\1#p' "$csproj" | head -n1)}"
OUT="$ROOT/dist/Version $EFFVER/$RID"

echo "==> dotnet publish ($RID, $CONFIG, v$EFFVER)"
dotnet publish "$csproj" -c "$CONFIG" -r "$RID" "${flags[@]}" -o "$OUT"

# Установщик (вариант A): тот же бинарь под именем SmartBase-Setup.exe — по имени «-Setup»
# он запускается в режиме установки. Раздается одним файлом. См. docs/guides/auto-update.md.
# Копию иногда на миг блокирует антивирус (свежий .exe) — повтор с паузой.
for _ in 1 2 3 4 5; do
  cp "$OUT/SmartBase.exe" "$OUT/SmartBase-Setup.exe" 2>/dev/null && break
  sleep 1
done
[ -f "$OUT/SmartBase-Setup.exe" ] || { echo "Не удалось создать SmartBase-Setup.exe (файл занят)" >&2; exit 1; }

echo "Готово:     $OUT/SmartBase.exe"
echo "Установщик: $OUT/SmartBase-Setup.exe"
