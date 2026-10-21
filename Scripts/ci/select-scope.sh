#!/usr/bin/env bash
# Профиль единого workflow. Только вывод ключей GitHub Actions; нет сети и изменений git.
set -euo pipefail

if [[ "${WORKFLOW_FULL:-false}" == true || "${GITHUB_EVENT_NAME:-}" == workflow_dispatch ]]; then
  profile=full
elif [[ "${GITHUB_EVENT_NAME:-}" == push && "${GITHUB_REF:-}" == refs/heads/ci ]]; then
  profile=full
elif [[ "${GITHUB_EVENT_NAME:-}" == pull_request && "${GITHUB_BASE_REF:-}" == main ]]; then
  profile=quick
elif [[ "${GITHUB_EVENT_NAME:-}" == push && "${GITHUB_REF:-}" == refs/heads/* && "${GITHUB_REF}" != refs/heads/main ]]; then
  profile=quick
else
  echo '::error::Событие не относится к проверке ветки, PR или полному запуску' >&2
  exit 1
fi

printf 'profile=%s\n' "$profile"
if [[ "$profile" == full ]]; then printf 'full=true\n'; else printf 'full=false\n'; fi
