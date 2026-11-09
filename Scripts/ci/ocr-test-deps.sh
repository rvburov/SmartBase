#!/usr/bin/env bash
# Компоненты для тестов распознавания плагина «Переименование PDF» в CI.
#   Linux   — системный Tesseract с русским и английским (как у пользователя: apt) и шрифт
#             DejaVu для синтетических сканов;
#   Windows — Tesseract плагин приносит сам, нужны только языковые данные: tessdata_fast 4.1.0
#             (те же файлы, что в пакете tesseract-ocr-rus Ubuntu), путь — в TESSDATA_PREFIX;
#             и шрифт DejaVu Sans 2.37 для синтетических сканов (как на Linux) — в SB_TEST_FONT;
#   macOS   — OCR-часть плагина не поставляется, тесты распознавания пропускаются сами.
set -euo pipefail

case "$(uname -s)" in
  Linux)
    sudo apt-get update -qq
    sudo apt-get install -y -qq tesseract-ocr tesseract-ocr-rus tesseract-ocr-eng fonts-dejavu-core
    tesseract --version | head -1
    ;;
  MINGW*|MSYS*|CYGWIN*)
    dir="${RUNNER_TEMP:-/tmp}/tessdata"
    mkdir -p "$dir"
    declare -A sums=(
      [rus]=e16e5e036cce1d9ec2b00063cf8b54472625b9e14d893a169e2b0dedeb4df225
      [eng]=7d4322bd2a7749724879683fc3912cb542f19906c83bcc1a52132556427170b2
    )
    for lang in rus eng; do
      curl -fsSL --retry 3 -o "$dir/$lang.traineddata" "https://raw.githubusercontent.com/tesseract-ocr/tessdata_fast/4.1.0/$lang.traineddata"
      # Через stdin: с путем в аргументе Git Bash печатает сумму с ведущим «\».
      actual=$(sha256sum < "$dir/$lang.traineddata" | cut -d' ' -f1)
      [[ "$actual" == "${sums[$lang]}" ]] || { echo "::error::контрольная сумма $lang.traineddata: $actual"; exit 1; }
    done
    echo "TESSDATA_PREFIX=$(cygpath -w "$dir")" >> "${GITHUB_ENV:-/dev/null}"

    fonts="${RUNNER_TEMP:-/tmp}/fonts"
    mkdir -p "$fonts"
    curl -fsSL --retry 3 -o "$fonts/dejavu.zip" \
      "https://github.com/dejavu-fonts/dejavu-fonts/releases/download/version_2_37/dejavu-fonts-ttf-2.37.zip"
    actual=$(sha256sum < "$fonts/dejavu.zip" | cut -d' ' -f1)
    [[ "$actual" == 7576310b219e04159d35ff61dd4a4ec4cdba4f35c00e002a136f00e96a908b0a ]] \
      || { echo "::error::контрольная сумма dejavu-fonts-ttf-2.37.zip: $actual"; exit 1; }
    python -c "import sys, zipfile; open(sys.argv[2], 'wb').write(zipfile.ZipFile(sys.argv[1]).read('dejavu-fonts-ttf-2.37/ttf/DejaVuSans.ttf'))" \
      "$(cygpath -w "$fonts/dejavu.zip")" "$(cygpath -w "$fonts/DejaVuSans.ttf")"
    echo "SB_TEST_FONT=$(cygpath -w "$fonts/DejaVuSans.ttf")" >> "${GITHUB_ENV:-/dev/null}"
    ;;
  *)
    echo "OCR-часть плагина на $(uname -s) не поставляется — тесты распознавания пропустятся"
    ;;
esac
