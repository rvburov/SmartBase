#!/bin/bash
#
# Сборка Windows-дистрибутива DataBase одной короткой командой вместо длинного
# `dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o ...`:
#
#   Scripts/windows-build.sh                       # win-x64, Release (по умолчанию)
#   Scripts/windows-build.sh win-arm64             # другая архитектура
#   Scripts/windows-build.sh win-x64 Debug         # другая конфигурация
#   Scripts/windows-build.sh win-x64 Release 1.2.3 # задать версию (запишется в DataBase.csproj)
#
# Особенности Windows-сборки:
#   • self-contained     — .NET у пользователя не нужен;
#   • PublishSingleFile  — всё в одном .exe (нативные .dll вшиваются и
#                          распаковываются при запуске, см.
#                          IncludeNativeLibrariesForSelfExtract в DataBase.csproj);
#   • иконка вшита в .exe — через ApplicationIcon в DataBase.csproj, отдельных
#                          действий не требует.
#
# dotnet publish одинаково работает в любом шелле (Git Bash / PowerShell / CMD),
# поэтому Windows-сборка — обычный bash-скрипт, как linux/macos.
#
# Результат: dist/<RID>/DataBase.exe — раздаётся как есть.
# ── Будущие особенности Windows-сборки добавляйте в массив flags ниже. ──
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
RID="${1:-win-x64}"
CONFIG="${2:-Release}"
VERSION="${3:-}"

# Общие флаги publish — единая точка правки на будущее.
flags=(
  --self-contained true
  -p:PublishSingleFile=true
)

# Номер версии: если передан 3-м аргументом — ЗАПИСЫВАЕМ его в <Version> в
# DataBase.csproj (становится текущим и для последующих сборок) и в бейдж README.
# Через временный файл — портативно к GNU/BSD sed (без sed -i).
csproj="$ROOT/DataBase.csproj"
if [ -n "$VERSION" ]; then
  tmp="$(mktemp)"
  sed -E "s#<Version>[^<]*</Version>#<Version>${VERSION}</Version>#" "$csproj" > "$tmp" && mv "$tmp" "$csproj"
  readme="$ROOT/README.md"; tmp="$(mktemp)"
  sed -E "s#version-[^-]*-blue#version-${VERSION}-blue#" "$readme" > "$tmp" && mv "$tmp" "$readme"
  echo "==> Версия обновлена на $VERSION (DataBase.csproj + бейдж README.md)"
fi

# Эффективная версия сборки = аргумент или текущая из csproj. Задаёт папку вывода
# dist/Version <X.Y.Z>/<rid>: та же версия перезаписывается, новая — отдельная папка.
EFFVER="${VERSION:-$(sed -nE 's#.*<Version>([^<]*)</Version>.*#\1#p' "$csproj" | head -n1)}"
OUT="$ROOT/dist/Version $EFFVER/$RID"

echo "==> dotnet publish ($RID, $CONFIG, v$EFFVER)"
dotnet publish "$csproj" -c "$CONFIG" -r "$RID" "${flags[@]}" -o "$OUT"

# Установщик (вариант A): тот же бинарь под именем DataBase-Setup.exe — по имени «-Setup»
# он запускается в режиме установки. Раздаётся одним файлом. См. docs/guides/auto-update.md.
# Копию иногда на миг блокирует антивирус (свежий .exe) — повтор с паузой.
for _ in 1 2 3 4 5; do
  cp "$OUT/DataBase.exe" "$OUT/DataBase-Setup.exe" 2>/dev/null && break
  sleep 1
done
[ -f "$OUT/DataBase-Setup.exe" ] || { echo "Не удалось создать DataBase-Setup.exe (файл занят)" >&2; exit 1; }

echo "Готово:     $OUT/DataBase.exe"
echo "Установщик: $OUT/DataBase-Setup.exe"
