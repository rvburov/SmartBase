#!/bin/bash
#
# Генерирует icon.ico (Windows) и icon.icns (macOS) из мастера
# Assets/Branding/icon.png. Linux использует сам icon.png (см. linux-install.sh),
# поэтому отдельные PNG ему не нужны.
#
# Нужны только штатные macOS-утилиты: sips, iconutil, python3.
# Перезапускайте после замены мастера (например, на версию 1024×1024).
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
DIR="$ROOT/Assets/Branding"
MASTER="$DIR/icon.png"
[ -f "$MASTER" ] || { echo "Нет мастер-файла: $MASTER" >&2; exit 1; }

TMP="$(mktemp -d)"
trap 'rm -rf "$TMP"' EXIT

echo "==> Windows icon.ico"
ico=()
for s in 16 32 48 64 128 256; do
  sips -z "$s" "$s" "$MASTER" --out "$TMP/$s.png" >/dev/null
  ico+=("$TMP/$s.png")
done
# Сборка .ico из PNG-кадров без сторонних зависимостей (формат поддерживают
# Windows Vista+ и Avalonia). Каждый кадр кладётся в .ico как есть.
python3 - "$DIR/icon.ico" "${ico[@]}" <<'PY'
import struct, sys
out, paths = sys.argv[1], sys.argv[2:]
imgs = []
for p in paths:
    d = open(p, "rb").read()
    w = int.from_bytes(d[16:20], "big")   # width/height из PNG IHDR
    h = int.from_bytes(d[20:24], "big")
    imgs.append((w, h, d))
imgs.sort(key=lambda x: x[0])
n = len(imgs)
off = 6 + 16 * n
entries = blobs = b""
for w, h, d in imgs:
    entries += struct.pack("<BBBBHHII",
                           0 if w >= 256 else w, 0 if h >= 256 else h,
                           0, 0, 1, 32, len(d), off)
    blobs += d
    off += len(d)
open(out, "wb").write(struct.pack("<HHH", 0, 1, n) + entries + blobs)
print(f"  {out} ({n} размеров)")
PY

echo "==> macOS icon.icns"
SET="$TMP/icon.iconset"
mkdir -p "$SET"
g() { sips -z "$1" "$1" "$MASTER" --out "$SET/$2" >/dev/null; }
g 16   icon_16x16.png
g 32   icon_16x16@2x.png
g 32   icon_32x32.png
g 64   icon_32x32@2x.png
g 128  icon_128x128.png
g 256  icon_128x128@2x.png
g 256  icon_256x256.png
g 512  icon_256x256@2x.png
g 512  icon_512x512.png
g 1024 icon_512x512@2x.png   # апскейл из 512 — единственный неидеальный слот
iconutil -c icns "$SET" -o "$DIR/icon.icns"
echo "  $DIR/icon.icns"

echo "Готово."
