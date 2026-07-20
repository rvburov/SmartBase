#!/usr/bin/env bash
# Прогон автотестов SmartBase.
#   ./Tests/run-all.sh              — Unit (быстрый гейт) + Integration (SQLite + PG, если поднят sb-pg)
#   ./Tests/run-all.sh --with-bench — плюс быстрый прогон бенчмарков (маленькие объёмы, смоук)
set -euo pipefail
cd "$(dirname "$0")/.."   # корень репозитория

echo "== Unit (быстрый гейт) =="
dotnet test Tests/Unit

echo
echo "== Integration (SQLite + PostgreSQL, если поднят sb-pg) =="
dotnet test Tests/Integration

if [[ "${1:-}" == "--with-bench" ]]; then
  echo
  echo "== Бенчмарки (быстрый прогон, 50k строк) =="
  dotnet run -c Release --project Tests/Benchmarks -- import --rows 50000
  dotnet run -c Release --project Tests/Benchmarks -- export --rows 50000
  echo
  echo "Полные перф-замеры, сайдбар-стенд и нагрузка PG — см. Tests/README.md."
fi
