#!/usr/bin/env bash
# Прогон автотестов SmartBase.
#   ./Tests/run-all.sh                   — Unit (быстрый гейт) + Integration (SQLite + PG, если поднят sb-pg)
#                                          + стенды панели чтения и настроек копий (DocReader, BackupUi)
#   ./Tests/run-all.sh --with-extensions — плюс тесты расширений (Tests/Extensions/*)
#   ./Tests/run-all.sh --with-bench      — плюс быстрый прогон бенчмарков (маленькие объемы, смоук)
#
# По умолчанию здесь только ЯДРО: тесты расширений — отдельные сборки и отдельный прогон
# (./Tests/run-extensions.sh), см. docs/developer/testing/extension-tests.md.
set -euo pipefail
cd "$(dirname "$0")/.."   # корень репозитория

echo "== Unit (быстрый гейт) =="
dotnet test Tests/Unit

echo
echo "== Integration (SQLite + PostgreSQL, если поднят sb-pg) =="
dotnet test Tests/Integration

# Стенд — живая Avalonia без экрана: разметка панели, подсветка, переходы, страницы PDF, таблица
# книги. Отдельным проектом он в тесты не входит и, пока его не гонял общий прогон, мог стоять
# красным месяцами. Расхождение — код возврата 1, и прогон останавливается.
echo
echo "== Стенд панели чтения библиотеки =="
dotnet run --project Tests/DocReader -f net10.0

echo
echo "== Резервные копии: настройки и фоновые работы без экрана =="
dotnet run --project Tests/BackupUi -f net10.0

if [[ "${1:-}" == "--with-extensions" ]]; then
  echo
  echo "== Расширения (по проекту на расширение) =="
  ./Tests/run-extensions.sh
fi

if [[ "${1:-}" == "--with-bench" ]]; then
  echo
  echo "== Бенчмарки (быстрый прогон, 50k строк) =="
  dotnet run -c Release --project Tests/Benchmarks -- import --rows 50000
  dotnet run -c Release --project Tests/Benchmarks -- export --rows 50000
  echo
  echo "Полные перф-замеры, сайдбар-стенд и нагрузка PG — см. Tests/README.md."
fi
