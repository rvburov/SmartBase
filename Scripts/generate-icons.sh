#!/bin/bash
#
# Собирает icon.ico (Windows) и icon.icns (macOS) из мастеров в Assets/Branding.
#
# Мастеров три — по одному на систему, потому что системы рисуют иконку по-разному:
#   icon-windows.png — во всю площадь холста, скругление ~10 % стороны;
#   icon-macos.png   — плашка 824×824 по центру холста 1024×1024 (сетка Apple), поля прозрачные;
#   icon-linux.png   — во всю площадь, скругление ~16 %; используется как есть (см. linux-install.sh),
#                      генерировать из него нечего.
#
# Скрипт проверяет мастера перед сборкой: 1024×1024, RGBA, без непрозрачной кромки по краю
# холста. Кромка — та самая «белая рамка», из-за которой иконка в Finder выглядела обведенной.
#
# Нужны только штатные macOS-утилиты: sips, iconutil, python3.
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
DIR="$ROOT/Assets/Branding"
WIN="$DIR/icon-windows.png"
MAC="$DIR/icon-macos.png"
LIN="$DIR/icon-linux.png"

for m in "$WIN" "$MAC" "$LIN"; do
  [ -f "$m" ] || { echo "Нет мастер-файла: $m" >&2; exit 1; }
done

TMP="$(mktemp -d)"
trap 'rm -rf "$TMP"' EXIT

echo "==> Проверка мастеров"
python3 - "$WIN" "$MAC" "$LIN" <<'PY'
import sys, zlib, struct

def rgba(path):
    """Читает PNG в RGBA без сторонних библиотек (нужен только 8 бит на канал)."""
    d = open(path, 'rb').read()
    if d[:8] != b'\x89PNG\r\n\x1a\n':
        sys.exit(f"  {path}: это не PNG")
    i, idat, pal, trns = 8, b'', None, None
    w = h = depth = ctype = None
    while i < len(d):
        ln = struct.unpack('>I', d[i:i+4])[0]
        typ, body = d[i+4:i+8], d[i+8:i+8+ln]
        if   typ == b'IHDR': w, h, depth, ctype = struct.unpack('>IIBB', body[:10])
        elif typ == b'IDAT': idat += body
        elif typ == b'PLTE': pal = body
        elif typ == b'tRNS': trns = body
        i += 12 + ln
    if depth != 8:
        sys.exit(f"  {path}: нужен PNG с 8 битами на канал, а тут {depth}")
    ch = {0: 1, 2: 3, 3: 1, 4: 2, 6: 4}[ctype]
    raw, stride = zlib.decompress(idat), w * ch
    out, prev, p = bytearray(w*h*ch), bytearray(stride), 0
    for y in range(h):
        f = raw[p]; p += 1
        line = bytearray(raw[p:p+stride]); p += stride
        if f == 1:
            for x in range(ch, stride): line[x] = (line[x] + line[x-ch]) & 255
        elif f == 2:
            for x in range(stride): line[x] = (line[x] + prev[x]) & 255
        elif f == 3:
            for x in range(stride):
                a = line[x-ch] if x >= ch else 0
                line[x] = (line[x] + ((a + prev[x]) >> 1)) & 255
        elif f == 4:
            for x in range(stride):
                a = line[x-ch] if x >= ch else 0
                b, c = prev[x], (prev[x-ch] if x >= ch else 0)
                pa, pb, pc = abs(b-c), abs(a-c), abs(a+b-2*c)
                line[x] = (line[x] + (a if pa <= pb and pa <= pc else b if pb <= pc else c)) & 255
        out[y*stride:(y+1)*stride] = line
        prev = line
    px = bytearray(w*h*4)
    for k in range(w*h):
        if   ctype == 6: px[k*4:k*4+4] = out[k*4:k*4+4]
        elif ctype == 2: px[k*4:k*4+3] = out[k*3:k*3+3]; px[k*4+3] = 255
        elif ctype == 4: g, a = out[k*2], out[k*2+1]; px[k*4:k*4+4] = bytes([g, g, g, a])
        elif ctype == 3:
            idx = out[k]
            px[k*4:k*4+3] = pal[idx*3:idx*3+3]
            px[k*4+3] = trns[idx] if trns and idx < len(trns) else 255
        else: g = out[k]; px[k*4:k*4+4] = bytes([g, g, g, 255])
    return w, h, px

def check(path, inset):
    w, h, px = rgba(path)
    if (w, h) != (1024, 1024):
        sys.exit(f"  {path}: нужен мастер 1024×1024, а тут {w}×{h}")
    if not any(px[k] < 255 for k in range(3, len(px), 4)):
        sys.exit(f"  {path}: нет прозрачности — углы скругления должны быть прозрачными")

    # Поля холста: у macOS плашка вписана в сетку Apple, у остальных иконка во всю площадь.
    edge = [(x, y) for y in range(h) for x in range(w)
            if (x < inset or y < inset or x >= w-inset or y >= h-inset) and px[(y*w+x)*4+3] > 8]
    if inset and edge:
        sys.exit(f"  {path}: поле {inset}px вокруг плашки должно быть прозрачным, "
                 f"а закрашенных точек в нем {len(edge)}")

    # Белая кромка: сплошная светлая полоса вдоль края плашки. Ищем по середине каждой стороны.
    def light(x, y):
        k = (y*w + x)*4
        return px[k+3] > 200 and px[k] >= 235 and px[k+1] >= 235 and px[k+2] >= 235
    cx = cy = w // 2
    runs = {
        'слева':  next((n for n in range(w) if not light(inset+n, cy)), w),
        'справа': next((n for n in range(w) if not light(w-1-inset-n, cy)), w),
        'снизу':  next((n for n in range(h) if not light(cx, h-1-inset-n)), h),
    }
    bad = {k: v for k, v in runs.items() if v > 2}
    if bad:
        sys.exit(f"  {path}: светлая кромка по краю плашки "
                 + ", ".join(f"{k} {v}px" for k, v in bad.items())
                 + " — уберите обводку/свечение в исходнике")
    print(f"  {path.split('/')[-1]}: 1024×1024 RGBA, поле {inset}px, кромки нет")

check(sys.argv[1], 0)     # Windows — во всю площадь
check(sys.argv[2], 100)   # macOS   — плашка 824×824 в холсте 1024×1024
check(sys.argv[3], 0)     # Linux   — во всю площадь
PY

echo "==> Windows icon.ico ← icon-windows.png"
ico=()
for s in 16 32 48 64 128 256; do
  sips -z "$s" "$s" "$WIN" --out "$TMP/$s.png" >/dev/null
  ico+=("$TMP/$s.png")
done
# Сборка .ico из PNG-кадров без сторонних зависимостей (формат поддерживают
# Windows Vista+ и Avalonia). Каждый кадр кладется в .ico как есть.
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

echo "==> macOS icon.icns ← icon-macos.png"
SET="$TMP/icon.iconset"
mkdir -p "$SET"
g() { sips -z "$1" "$1" "$MAC" --out "$SET/$2" >/dev/null; }
g 16   icon_16x16.png
g 32   icon_16x16@2x.png
g 32   icon_32x32.png
g 64   icon_32x32@2x.png
g 128  icon_128x128.png
g 256  icon_128x128@2x.png
g 256  icon_256x256.png
g 512  icon_256x256@2x.png
g 512  icon_512x512.png
g 1024 icon_512x512@2x.png   # retina-слот Dock: настоящий 1024, мастер как раз 1024×1024
iconutil -c icns "$SET" -o "$DIR/icon.icns"
echo "  $DIR/icon.icns"

echo "==> Linux icon-linux.png — берется как есть (см. Scripts/linux-install.sh)"
echo "Готово."
