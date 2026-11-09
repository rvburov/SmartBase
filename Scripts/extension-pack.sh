#!/usr/bin/env bash
# Архив расширения-плагина для установки «Настройки → Расширения → Установить из файла…».
#
#   Scripts/extension-pack.sh pdf-rename linux-x64
#   Scripts/extension-pack.sh pdf-rename win-x64
#
# Результат: artifacts/extensions/<id>-<версия>-<платформа>.zip — внутри extension.json, сборка
# расширения, все его библиотеки (OCR, OpenCV, PDFium), нативные части для платформы и модели.
# Архив собирается на любой ОС (dotnet publish -r <платформа>), интернет нужен только здесь —
# для пакетов NuGet и языковых данных Tesseract. Установленное расширение работает без сети.
#
# Языковые данные Tesseract (rus, eng) — tessdata_fast 4.1.0, те же файлы, что ставит пакет
# tesseract-ocr-rus в Ubuntu 24.04; на них расширение проверено тестами. Контрольные суммы
# прибиты: подмененный файл в архив не попадет.
set -euo pipefail
cd "$(dirname "$0")/.."   # корень репозитория

id="${1:-}"
rid="${2:-}"
if [[ -z "$id" || -z "$rid" ]]; then
  echo "Использование: Scripts/extension-pack.sh <id> <win-x64|linux-x64>" >&2
  exit 2
fi

case "$id" in
  pdf-rename) project=Extensions/PdfRename/SmartBase.Extensions.PdfRename.csproj ;;
  *) echo "Неизвестное расширение-плагин: $id" >&2; exit 2 ;;
esac
case "$rid" in
  win-x64|linux-x64) ;;
  *) echo "Платформа $rid не поддерживается: OCR-библиотеки поставляются для win-x64 и linux-x64" >&2; exit 2 ;;
esac

out="artifacts/extensions/$id-$rid"
rm -rf "$out"
dotnet publish "$project" --configuration Release --runtime "$rid" --self-contained false --output "$out" -nologo

# Языковые данные Tesseract — в ocr/tessdata плагина.
declare -A sums=(
  [rus]=e16e5e036cce1d9ec2b00063cf8b54472625b9e14d893a169e2b0dedeb4df225
  [eng]=7d4322bd2a7749724879683fc3912cb542f19906c83bcc1a52132556427170b2
)
mkdir -p "$out/ocr/tessdata"
for lang in rus eng; do
  file="$out/ocr/tessdata/$lang.traineddata"
  curl -fsSL --retry 3 -o "$file" "https://raw.githubusercontent.com/tesseract-ocr/tessdata_fast/4.1.0/$lang.traineddata"
  # Через stdin: с путем в аргументе Git Bash на Windows печатает сумму с ведущим «\».
  actual=$( (sha256sum < "$file" 2>/dev/null || shasum -a 256 < "$file") | cut -d' ' -f1)
  if [[ "$actual" != "${sums[$lang]}" ]]; then
    echo "Контрольная сумма $lang.traineddata не совпала: $actual" >&2
    exit 1
  fi
done

version=$(sed -n 's/.*"version": *"\([^"]*\)".*/\1/p' "$out/extension.json")
zip="artifacts/extensions/$id-$version-$rid.zip"
rm -f "$zip"
python3 - "$out" "$zip" <<'PY'
import os, sys, zipfile
src, dst = sys.argv[1], sys.argv[2]
with zipfile.ZipFile(dst, "w", zipfile.ZIP_DEFLATED) as z:
    for root, _, files in os.walk(src):
        for name in sorted(files):
            path = os.path.join(root, name)
            z.write(path, os.path.relpath(path, src).replace(os.sep, "/"))
PY
echo "Готово: $zip ($(du -h "$zip" | cut -f1))"
