#!/bin/bash
#
# Хук SessionStart облачных сессий Claude Code (claude.ai/code, приложение Claude, claude --cloud).
# Облачная машина приходит со своим автором git («Claude <noreply@anthropic.com>») и включенной
# подписью коммитов своим ключом — тогда на GitHub автором стоит ИИ со значком «Verified». AGENTS.md
# требует, чтобы автором и коммитером каждого коммита был пользователь, коммит — без подписи.
# Настройка ложится в .git/config репозитория и перекрывает глобальную.
#
# .claude/settings.json вызывает скрипт только при CLAUDE_CODE_REMOTE=true; на компьютере
# пользователя он ничего не делает. Все, что он печатает, попадает в контекст сессии.
set -euo pipefail

[[ "${CLAUDE_CODE_REMOTE:-}" == true ]] || exit 0
cd "$(dirname "${BASH_SOURCE[0]}")/.."

git config user.name  'Roman Burov'
git config user.email 'romanburov89@gmail.com'
git config commit.gpgsign false
git config tag.gpgsign false

# Переменные окружения сильнее git config: если среда их задала, коммит все равно уйдет от ее имени.
for name in GIT_AUTHOR_NAME GIT_AUTHOR_EMAIL GIT_COMMITTER_NAME GIT_COMMITTER_EMAIL; do
  if [[ -n "${!name:-}" ]]; then
    echo "В сессии задана $name, она перекрывает git config: коммитить через env -u $name git commit."
  fi
done
echo "Облачная копия репозитория: коммиты — от имени Roman Burov, без подписи. Как работа из облака"
echo "попадает в main — AGENTS.md, «Облачная копия»."
