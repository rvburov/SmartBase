#!/usr/bin/env bash
# Компоненты для тестов распознавания плагина «Переименование PDF» в CI.
#   Linux   — системный Tesseract с русским и английским (как у пользователя: apt) и шрифт
#             DejaVu для синтетических сканов;
#   Windows — Tesseract плагин приносит сам, нужны только языковые данные: tessdata_fast 4.1.0
#             (те же файлы, что в пакете tesseract-ocr-rus Ubuntu), путь — в TESSDATA_PREFIX;
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
      actual=$(sha256sum "$dir/$lang.traineddata" | cut -d' ' -f1)
      [[ "$actual" == "${sums[$lang]}" ]] || { echo "::error::контрольная сумма $lang.traineddata: $actual"; exit 1; }
    done
    echo "TESSDATA_PREFIX=$(cygpath -w "$dir")" >> "${GITHUB_ENV:-/dev/null}"
    ;;
  *)
    echo "OCR-часть плагина на $(uname -s) не поставляется — тесты распознавания пропустятся"
    ;;
esac
