#!/usr/bin/env bash
# Последовательная матрица независимых сессий. Только новые тестовые БД и папки.
# ./Tests/run-concurrency.sh --clients 1000 --out /tmp/sb-load
set -euo pipefail
cd "$(dirname "$0")/.."

clients=8
rounds=1
timeout_seconds=180
provider=all
scenario=all
output=
p95=
usage() {
  echo "Tests/run-concurrency.sh [--clients 1..1000] [--rounds N] [--timeout-seconds N]"
  echo "  [--provider all|sqlite|postgres] [--scenario all|lifecycle|hotcell|rollback|planning|library|integrations|people|structure]"
  echo "  [--p95-ms N] [--out НОВАЯ_ПАПКА]"
}
while (($#)); do
  if [[ "$1" == --help ]]; then usage; exit 0; fi
  if (($# < 2)); then usage >&2; exit 2; fi
  case "$1" in
    --clients) clients=$2 ;;
    --rounds) rounds=$2 ;;
    --timeout-seconds) timeout_seconds=$2 ;;
    --provider) provider=$2 ;;
    --scenario) scenario=$2 ;;
    --out) output=$2 ;;
    --p95-ms) p95=$2 ;;
    *) usage >&2; exit 2 ;;
  esac
  shift 2
done
for number in "$clients" "$rounds" "$timeout_seconds"; do
  if [[ ! "$number" =~ ^[1-9][0-9]{0,4}$ ]]; then usage >&2; exit 2; fi
done
if ((clients > 1000 || rounds > 10000 || timeout_seconds > 3600)); then usage >&2; exit 2; fi
case "$provider" in all|sqlite|postgres) ;; *) usage >&2; exit 2 ;; esac
case "$scenario" in all|lifecycle|hotcell|rollback|planning|library|integrations|people|structure) ;; *) usage >&2; exit 2 ;; esac
if [[ -n "$p95" && ! "$p95" =~ ^[0-9]+([.][0-9]+)?$ ]]; then usage >&2; exit 2; fi
if [[ -n "$p95" && ! "$p95" =~ [1-9] ]]; then usage >&2; exit 2; fi
if [[ -z "$output" ]]; then
  output=$(mktemp -d "${TMPDIR:-/tmp}/sb-concurrency.XXXXXXXX")
else
  # mkdir отказывает и для существующей папки: предыдущие отчеты не перезаписываются.
  mkdir -- "$output"
fi
output=$(cd "$output" && pwd)
dotnet build Tests/Benchmarks --configuration Release > "$output/build.log" 2>&1 || {
  cat "$output/build.log" >&2
  exit 1
}
scenarios=(lifecycle hotcell rollback planning library integrations people structure)
if [[ "$scenario" != all ]]; then scenarios=("$scenario"); fi
providers=(sqlite postgres)
if [[ "$provider" != all ]]; then providers=("$provider"); fi
printf 'scenario\tprovider\tprofile\texit_code\treport\n' > "$output/matrix.tsv"
failed=0
for selected_scenario in "${scenarios[@]}"; do
  for selected_provider in "${providers[@]}"; do
    profiles=(server)
    if [[ "$selected_provider" == sqlite ]]; then profiles=(wal shared); fi
    for selected_profile in "${profiles[@]}"; do
      name="$selected_scenario-$selected_provider-$selected_profile"
      args=(multiuser --provider "$selected_provider" --profile "$selected_profile"
        --scenario "$selected_scenario" --clients "$clients" --rounds "$rounds"
        --timeout-seconds "$timeout_seconds" --out "$output/$name")
      if [[ -n "$p95" ]]; then args+=(--p95-ms "$p95"); fi
      status=0
      dotnet run --project Tests/Benchmarks --configuration Release --no-build -- "${args[@]}" \
        > "$output/$name.log" 2>&1 || status=$?
      if ((status != 0)); then failed=1; fi
      printf '%s\t%s\t%s\t%s\t%s\n' "$selected_scenario" "$selected_provider" \
        "$selected_profile" "$status" "$output/$name/report.json" >> "$output/matrix.tsv"
      printf '%s: exit=%s; %s\n' "$name" "$status" "$output/$name.log"
    done
  done
done
echo "Матрица: $output/matrix.tsv"
exit "$failed"
