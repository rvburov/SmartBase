#!/usr/bin/env bash
#
# Собирает macOS-бандл DataBase.app с иконкой в Dock/Finder/Launchpad.
# Обычный `dotnet build` бандл НЕ создаёт — иконка на маке появляется только
# после этого скрипта. Результат (dist/) — артефакт, в git не попадает.
#
# Использование:  Scripts/build-macos-app.sh [RID] [CONFIG]
#   RID    — osx-arm64 (по умолчанию) или osx-x64
#   CONFIG — Release (по умолчанию) или Debug
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
RID="${1:-osx-arm64}"
CONFIG="${2:-Release}"
APP_NAME="DataBase"
ICNS="$ROOT/Assets/Branding/icon.icns"
PLIST="$ROOT/Assets/Branding/Info.plist"

[ -f "$ICNS" ] || { echo "Нет $ICNS — запустите Scripts/generate-icons.sh" >&2; exit 1; }

OUTDIR="$ROOT/dist/$RID"
PUBLISH="$OUTDIR/.publish-tmp"
echo "==> dotnet publish ($RID, $CONFIG)"
rm -rf "$PUBLISH"
dotnet publish "$ROOT/DataBase.csproj" -c "$CONFIG" -r "$RID" --self-contained true -o "$PUBLISH"

APP="$OUTDIR/$APP_NAME.app"
echo "==> Сборка бандла $APP"
rm -rf "$APP"
mkdir -p "$APP/Contents/MacOS" "$APP/Contents/Resources"
cp -R "$PUBLISH/." "$APP/Contents/MacOS/"
cp "$ICNS" "$APP/Contents/Resources/icon.icns"
cp "$PLIST" "$APP/Contents/Info.plist"
chmod +x "$APP/Contents/MacOS/$APP_NAME"
rm -rf "$PUBLISH"   # промежуточный publish в бандле не нужен
touch "$APP"        # сбросить кэш иконок Finder

echo "Готово: $APP"
echo "Запуск:  open \"$APP\""
