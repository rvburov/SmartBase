#!/bin/bash
#
# Сборка Linux-дистрибутива SmartBase одной короткой командой вместо длинного
# `dotnet publish -c Release -r linux-x64 --self-contained true -p:PublishSingleFile=true -o ...`:
#
#   Scripts/linux-build.sh                         # linux-x64, Release (по умолчанию)
#   Scripts/linux-build.sh linux-arm64             # другая архитектура
#   Scripts/linux-build.sh linux-x64 Debug         # другая конфигурация
#   Scripts/linux-build.sh linux-x64 Release 1.2.3 # задать версию (запишется в SmartBase.csproj)
#
# Особенности Linux-сборки:
#   • self-contained      — .NET у пользователя не нужен;
#   • PublishSingleFile   — всё в одном файле (нативные .so вшиваются);
#   • chmod +x            — бинарю выставляется бит запуска;
#   • иконка/ярлык в меню — отдельным шагом на целевой машине через
#                           Scripts/linux-install.sh (использует icon.png из Assets/Branding).
#
# Результат: dist/<RID>/SmartBase — раздаётся как есть.
# ── Будущие особенности Linux-сборки добавляйте в массив flags ниже. ──
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
RID="${1:-linux-x64}"
CONFIG="${2:-Release}"
VERSION="${3:-}"

# Целевая платформа: проект многотаргетный (см. TargetFrameworks в SmartBase.csproj), и
# publish требует явно выбрать один таргет. Всё, кроме Windows, собирается базовым net10.0 —
# Windows-таргет существует только ради WinRT-ассетов нативных тостов.
TFM="net10.0"

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

# Эффективная версия сборки = аргумент или текущая из csproj. Задаёт папку вывода
# dist/Version <X.Y.Z>/<rid>: та же версия перезаписывается, новая — отдельная папка.
EFFVER="${VERSION:-$(sed -nE 's#.*<Version>([^<]*)</Version>.*#\1#p' "$csproj" | head -n1)}"
OUT="$ROOT/dist/Version $EFFVER/$RID"

echo "==> dotnet publish ($RID, $CONFIG, v$EFFVER)"
dotnet publish "$csproj" -c "$CONFIG" -r "$RID" "${flags[@]}" -o "$OUT"

chmod +x "$OUT/SmartBase"

# Установщик (вариант A): тот же бинарь под именем SmartBase-Setup — по имени «-Setup»
# он запускается в режиме установки (ставит в ~/.local/share/SmartBase, .desktop-ярлыки,
# умеет --uninstall). Раздаётся одним файлом. См. docs/guides/auto-update.md.
cp "$OUT/SmartBase" "$OUT/SmartBase-Setup"
chmod +x "$OUT/SmartBase-Setup"

echo "Готово:     $OUT/SmartBase"
echo "Установщик: $OUT/SmartBase-Setup"
echo "Иконку и пункт меню на целевой машине также добавит: Scripts/linux-install.sh"
