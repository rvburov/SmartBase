#!/bin/bash
#
# Сборка macOS-дистрибутива SmartBase одной короткой командой: publish + упаковка
# в .app с иконкой. Обычный publish бандл НЕ создаёт, а без бандла на маке нет
# иконки в Dock/Finder/Launchpad — поэтому здесь отдельный шаг сборки .app.
#
#   Scripts/macos-build.sh                         # osx-arm64, Release (Apple Silicon, по умолчанию)
#   Scripts/macos-build.sh osx-x64                 # Intel
#   Scripts/macos-build.sh osx-arm64 Debug         # другая конфигурация
#   Scripts/macos-build.sh osx-arm64 Release 1.2.3 # задать версию (запишется в SmartBase.csproj)
#
# Особенности macOS-сборки:
#   • self-contained       — .NET у пользователя не нужен;
#   • БЕЗ PublishSingleFile — содержимое publish кладётся в .app/Contents/MacOS
#                            целой папкой (так устроен бандл .app);
#   • .app-бандл           — icon.icns + Info.plist + сброс кэша иконок Finder (touch).
#
# Результат: dist/<RID>/SmartBase.app — раздаётся как есть.
# ── Будущие особенности macOS-сборки добавляйте в массив flags ниже. ──
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
RID="${1:-osx-arm64}"
CONFIG="${2:-Release}"
VERSION="${3:-}"
APP_NAME="SmartBase"
ICNS="$ROOT/Assets/Branding/icon.icns"
PLIST="$ROOT/Assets/Branding/Info.plist"

[ -f "$ICNS" ] || { echo "Нет $ICNS — запустите Scripts/generate-icons.sh" >&2; exit 1; }

# Целевая платформа: проект многотаргетный (см. TargetFrameworks в SmartBase.csproj), и
# publish требует явно выбрать один таргет. Всё, кроме Windows, собирается базовым net10.0 —
# Windows-таргет существует только ради WinRT-ассетов нативных тостов.
TFM="net10.0"

# Общие флаги publish — единая точка правки на будущее.
# Внимание: PublishSingleFile тут НЕ используется — бандл .app это папка, а не один файл.
flags=( -f "$TFM" --self-contained true )

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
OUTDIR="$ROOT/dist/Version $EFFVER/$RID"
PUBLISH="$OUTDIR/.publish-tmp"
echo "==> dotnet publish ($RID, $CONFIG, v$EFFVER)"
rm -rf "$PUBLISH"
dotnet publish "$csproj" -c "$CONFIG" -r "$RID" "${flags[@]}" -o "$PUBLISH"

APP="$OUTDIR/$APP_NAME.app"
echo "==> Сборка бандла $APP"
rm -rf "$APP"
mkdir -p "$APP/Contents/MacOS" "$APP/Contents/Resources"
cp -R "$PUBLISH/." "$APP/Contents/MacOS/"
cp "$ICNS" "$APP/Contents/Resources/icon.icns"
cp "$PLIST" "$APP/Contents/Info.plist"
# Проставить версию сборки в Info.plist (Finder «Версия»; приложение всё равно читает
# версию из управляемой сборки). PlistBuddy есть на любой macOS, где и идёт сборка.
PB="/usr/libexec/PlistBuddy"
if [ -x "$PB" ]; then
  "$PB" -c "Set :CFBundleShortVersionString $EFFVER" "$APP/Contents/Info.plist" 2>/dev/null || true
  "$PB" -c "Set :CFBundleVersion $EFFVER"            "$APP/Contents/Info.plist" 2>/dev/null || true
fi
chmod +x "$APP/Contents/MacOS/$APP_NAME"
rm -rf "$PUBLISH"   # промежуточный publish в бандле не нужен
touch "$APP"        # сбросить кэш иконок Finder

# Установщик (вариант A): тот же бандл под именем SmartBase-Setup.app — по имени «-Setup»
# он запускается в режиме установки (ставит в ~/Applications, lsregister, умеет --uninstall;
# внутренний бинарь остаётся SmartBase, Info.plist не меняется). См. docs/guides/auto-update.md.
SETUP="$OUTDIR/$APP_NAME-Setup.app"
rm -rf "$SETUP"
cp -R "$APP" "$SETUP"
touch "$SETUP"

echo "Готово:     $APP"
echo "Установщик: $SETUP"
echo "Запуск:     open \"$APP\""
