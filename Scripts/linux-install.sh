#!/bin/bash
#
# Ставит иконку и пункт меню приложения для текущего пользователя Linux.
# Запускать НА Linux-машине после установки приложения.
# Использует напрямую Assets/Branding/icon.png — отдельные размеры не нужны,
# рабочие столы (GNOME/KDE и пр.) масштабируют его сами.
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
SRC="$ROOT/Assets/Branding"
DATA="${XDG_DATA_HOME:-$HOME/.local/share}"

install -Dm644 "$SRC/icon.png"        "$DATA/icons/hicolor/512x512/apps/smartbase.png"
install -Dm644 "$SRC/icon.png"        "$DATA/pixmaps/smartbase.png"
install -Dm644 "$SRC/SmartBase.desktop" "$DATA/applications/SmartBase.desktop"

command -v gtk-update-icon-cache >/dev/null && gtk-update-icon-cache -f "$DATA/icons/hicolor" || true
command -v update-desktop-database >/dev/null && update-desktop-database "$DATA/applications" || true

echo "Готово. Иконка и ярлык SmartBase установлены в меню приложений."
echo "ВАЖНО: строка Exec= в SmartBase.desktop должна указывать на бинарь SmartBase"
echo "       (полный путь или бинарь в PATH)."
