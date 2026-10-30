#!/usr/bin/env bash
# Сводный гейт не принимает skipped/cancelled вместо обязательной проверки.
set -euo pipefail

require_result() {
  if [[ "$2" != "$3" ]]; then
    printf '::error::%s: %s, ожидалось %s\n' "$1" "$2" "$3" >&2
    exit 1
  fi
}

require_result scope "${CI_SCOPE_RESULT:-missing}" success
require_result agent-tests "${CI_QUICK_RESULT:-missing}" success
if [[ "${CI_REQUIRE_FULL:-false}" == true ]]; then
  require_result 'запрошен полный профиль' "${CI_PROFILE:-missing}" full
fi
case "${CI_PROFILE:-}" in
  full) expected=success ;;
  quick) expected=skipped ;;
  *) echo '::error::Неизвестный профиль проверок' >&2; exit 1 ;;
esac
require_result test "${CI_MATRIX_RESULT:-missing}" "$expected"
require_result no-listener "${CI_LISTENER_RESULT:-missing}" "$expected"
require_result postgres "${CI_POSTGRES_RESULT:-missing}" "$expected"
require_result tools "${CI_TOOLS_RESULT:-missing}" "$expected"
printf 'Профиль %s: все обязательные проверки завершены\n' "$CI_PROFILE"
