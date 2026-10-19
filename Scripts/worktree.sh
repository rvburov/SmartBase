#!/bin/bash
#
# Рабочие папки агентов (git worktree): создать, проверить, влить в main, убрать.
# Правила параллельной работы — AGENTS.md, раздел «Параллельная работа агентов».
#
#   Scripts/worktree.sh                 # то же, что check
#   Scripts/worktree.sh check           # все папки и ветки: что не закоммичено, что не в main
#   Scripts/worktree.sh new <имя>       # (другие ИИ) новая папка .agents/worktrees/<имя>
#                                       #   на ветке <имя> от локального main
#   Scripts/worktree.sh sync            # (в рабочей папке) перенести свою ветку на свежий main
#                                       #   (локальный или origin/main — какой новее)
#   Scripts/worktree.sh merge [<имя>]   # влить ветку в main перемоткой; без имени — ветку этой папки
#   Scripts/worktree.sh remove <имя>    # убрать папку и ветку — только когда все уже в main
#
# <имя> в merge и remove — имя папки или ветки. У Claude Code ветка зовется worktree-<имя>.
#
# Где лежат папки — внутри основной, в каталогах с точкой, которые сборка пропускает:
#   .claude/worktrees/<имя>  — Claude Code, его родное место (EnterWorktree, claude --worktree);
#   .agents/worktrees/<имя>  — остальные ИИ, папку создает этот скрипт.
# Путь считается от основной папки, правило одно на любой машине и ОС. В другом месте проекта
# рабочей папки быть не должно: каталог без точки в начале имени SmartBase.csproj обходит, и
# чужая копия исходников ломает сборку (проверено).
#
# Пакеты NuGet рабочая папка берет из кеша основной (Directory.Build.props), ссылок внутри
# рабочей папки нет: git worktree remove на Windows проходит по ссылке и стирает ее цель.
#
# «Свежий main» — локальный main или origin/main, смотря какой из них уже содержит другой:
# облачные копии и другие компьютеры отправляют работу на GitHub, а вливания здесь остаются
# локальными до push. Разошлись — решает пользователь, скрипт останавливается.
#
# В облачной копии (CLAUDE_CODE_REMOTE=true) рабочих папок нет: check не считает ветку основной
# папки ошибкой и показывает, что не отправлено на GitHub и не влито в main на GitHub.
#
# Ни одна команда не удаляет незакоммиченное и не влитое в main: такие случаи скрипт
# называет и останавливается. push сюда не входит — только по просьбе пользователя.
# На Windows запускать из Git Bash.
set -euo pipefail

BASE=main
REMOTE=origin
MAX_NAME=20
LONGEST_INSIDE=175   # самый длинный путь внутри папки проекта (пакеты, сборка) — с запасом

die()  { echo "Ошибка: $*" >&2; exit 1; }
note() { echo "  $*"; }

git rev-parse --git-dir > /dev/null 2>&1 || die "запускать внутри репозитория"

# Пути везде в записи git («C:/...» на Windows), а не bash («/c/...»), чтобы их можно было сравнивать.
MAIN=$(git worktree list --porcelain | sed -n '1s/^worktree //p')
CLAUDE_ROOT="$MAIN/.claude/worktrees"
AGENTS_ROOT="$MAIN/.agents/worktrees"
HERE=$(git rev-parse --show-toplevel)

# Список папок: путь, ветка, признаки (locked, prunable) — через знак-разделитель US (\037).
# Табуляция не годится: для read она пробельная, и пустая ветка (папка без ветки) схлопнулась бы.
SEP=$'\037'
worktrees() {
  git worktree list --porcelain | awk '
    /^worktree / { if (p != "") print p "\037" br "\037" fl; p = substr($0, 10); br = ""; fl = "" }
    /^branch /   { br = substr($0, 19) }
    /^locked/    { fl = fl "locked," }
    /^prunable/  { fl = fl "prunable," }
    END          { if (p != "") print p "\037" br "\037" fl }'
}

# Путь папки, где выложена ветка (пусто — нигде).
path_of_branch() {
  worktrees | awk -F '\037' -v b="$1" '$2 == b { print $1 }'
}

# Папка по имени ветки или по имени самой папки: «путь SEP ветка SEP признаки».
resolve() {
  local hit
  hit=$(worktrees | awk -F '\037' -v n="$1" '$2 == n')
  if [[ -z "$hit" ]]; then
    hit=$(worktrees | awk -F '\037' -v n="$1" '{ k = split($1, s, "/") } s[k] == n')
  fi
  if [[ $(printf '%s\n' "$hit" | awk 'NF { c++ } END { print c + 0 }') -gt 1 ]]; then
    die "имени $1 подходят несколько папок — укажите ветку"
  fi
  printf '%s' "$hit"
}

# Чье место: claude, agents, inside-plain (без точки — ломает сборку), inside-dot, outside.
place_of() {
  case "$1/" in
    "$CLAUDE_ROOT/"*) echo claude ;;
    "$AGENTS_ROOT/"*) echo agents ;;
    "$MAIN/".*)       echo inside-dot ;;
    "$MAIN/"*)        echo inside-plain ;;
    *)                echo outside ;;
  esac
}

# Сколько изменений в папке: tracked — только правки отслеживаемых файлов, all — и новые файлы.
changes() {
  if [[ "$2" == tracked ]]; then
    git -C "$1" status --porcelain --untracked-files=no | wc -l | tr -d ' '
  else
    git -C "$1" status --porcelain | wc -l | tr -d ' '
  fi
}

ahead()  { git rev-list --count "$BASE..$1"; }   # коммиты ветки, которых нет в main
behind() { git rev-list --count "$1..$BASE"; }   # коммиты main, которых нет в ветке

# Первые строки вывода с отступом; sed дочитывает поток до конца, git не получает SIGPIPE.
first() { sed -n "1,${1}s/^/      /p"; }

# Тихо обновить origin/main; без сети остается последнее полученное состояние.
fetch_origin() {
  git remote get-url "$REMOTE" > /dev/null 2>&1 || return 0
  if ! git fetch --quiet "$REMOTE" "$BASE" 2> /dev/null; then
    note "нет связи с $REMOTE — берется последний полученный $REMOTE/$BASE"
  fi
}

# Свежий main: локальный или origin/main — смотря какой из них уже содержит другой.
newest_base() {
  local remote="$REMOTE/$BASE"
  if ! git rev-parse -q --verify "$remote" > /dev/null; then
    echo "$BASE"
  elif git merge-base --is-ancestor "$remote" "$BASE"; then
    echo "$BASE"
  elif git merge-base --is-ancestor "$BASE" "$remote"; then
    echo "$remote"
  else
    die "локальный $BASE и $remote разошлись — как их соединить, решает пользователь"
  fi
}

cloud() { [[ "${CLAUDE_CODE_REMOTE:-}" == true ]]; }

# Облачная копия: одна папка, работа уходит только через GitHub (AGENTS.md, «Облачная копия»).
cmd_check_cloud() {
  local problems=0 branch dirty a u remote="$REMOTE/$BASE"
  fetch_origin
  branch=$(git branch --show-current)
  dirty=$(changes "$HERE" all)
  echo "Облачная копия: $HERE (рабочие папки не нужны)"
  echo "[копия] ветка ${branch:-(без ветки)}"
  if [[ "$dirty" != 0 ]]; then
    note "НЕ ЗАКОММИЧЕНО изменений: $dirty"
    git status --short | first 20
    problems=$((problems + 1))
  fi
  if [[ -n "$branch" ]]; then
    if git rev-parse -q --verify "$REMOTE/$branch" > /dev/null; then
      u=$(git rev-list --count "$REMOTE/$branch..HEAD")
    else
      u=$(git rev-list --count HEAD --not --remotes="$REMOTE")
    fi
    if [[ "$u" != 0 ]]; then
      note "НЕ ОТПРАВЛЕНО на GitHub коммитов: $u — пропадут вместе с машиной сессии (git push -u $REMOTE HEAD)"
      problems=$((problems + 1))
    fi
  fi
  if git rev-parse -q --verify "$remote" > /dev/null; then
    a=$(git rev-list --count "$remote..HEAD")
    if [[ "$a" != 0 ]]; then
      note "не в $BASE на GitHub коммитов: $a (в $BASE — только по просьбе пользователя)"
      git log -n 10 --format='%h %s' "$remote..HEAD" | first 10
    fi
    a=$(git rev-list --count "HEAD..$remote")
    if [[ "$a" != 0 ]]; then
      note "$BASE на GitHub ушел вперед на $a — перед отправкой в $BASE: git rebase $remote, тесты"
    fi
  fi
  echo
  if [[ "$problems" == 0 ]]; then
    echo "ИТОГ: все закоммичено и отправлено на GitHub."
  else
    echo "ИТОГ: требует внимания мест: $problems — показать пользователю."
  fi
}

cmd_check() {
  local problems=0 path branch flags dirty a b
  if cloud; then cmd_check_cloud; return; fi
  fetch_origin
  echo "Основная папка: $MAIN"
  echo "Рабочие папки:  Claude Code — $CLAUDE_ROOT/<имя>"
  echo "                другие ИИ   — $AGENTS_ROOT/<имя>"
  echo

  # Основная папка: на main и без правок — агенты в ней не работают.
  branch=$(git -C "$MAIN" branch --show-current)
  dirty=$(changes "$MAIN" all)
  echo "[основная] ветка ${branch:-(без ветки)}"
  if [[ "$branch" != "$BASE" ]]; then
    note "ВНИМАНИЕ: основная папка не на $BASE"
    problems=$((problems + 1))
  fi
  if [[ "$dirty" != 0 ]]; then
    note "ВНИМАНИЕ: в основной папке незакоммиченных изменений: $dirty"
    git -C "$MAIN" status --short | first 20
    problems=$((problems + 1))
  fi
  if git rev-parse -q --verify "$REMOTE/$BASE" > /dev/null; then
    a=$(git rev-list --count "$REMOTE/$BASE..$BASE")
    b=$(git rev-list --count "$BASE..$REMOTE/$BASE")
    if [[ "$a" != 0 && "$b" != 0 ]]; then
      note "ВНИМАНИЕ: локальный $BASE и $REMOTE/$BASE разошлись ($a и $b коммитов) — решает пользователь"
      problems=$((problems + 1))
    elif [[ "$a" != 0 ]]; then
      note "на GitHub не отправлено коммитов $BASE: $a (push — только по просьбе)"
    elif [[ "$b" != 0 ]]; then
      note "$BASE на GitHub впереди локального на $b — догнать: git pull --ff-only в основной папке"
      note "(sync рабочих папок и так ставит ветки на $REMOTE/$BASE)"
    fi
  fi

  # Рабочие папки, где бы они ни лежали.
  while IFS="$SEP" read -r path branch flags; do
    if [[ "$path" == "$MAIN" ]]; then continue; fi
    echo
    echo "[папка] $path"
    echo "  ветка ${branch:-(без ветки)}"
    if [[ "$flags" == *prunable* ]]; then
      note "ВНИМАНИЕ: папки на диске нет, а git ее помнит (запись уберет git worktree prune)"
      problems=$((problems + 1))
      continue
    fi
    case "$(place_of "$path")" in
      claude)  note "место: Claude Code" ;;
      agents)  note "место: другие ИИ" ;;
      outside) note "место: вне проекта — папку создала сама программа-агент; правила те же" ;;
      inside-plain)
        note "ВНИМАНИЕ: внутри основной папки в каталоге без точки — ее исходники попадут в"
        note "сборку SmartBase.csproj и сломают ее; перенести в .claude/worktrees или .agents/worktrees"
        problems=$((problems + 1)) ;;
      inside-dot)
        note "ВНИМАНИЕ: внутри основной папки не на своем месте — должна быть в .claude/worktrees"
        note "или .agents/worktrees"
        problems=$((problems + 1)) ;;
    esac
    if [[ "$flags" == *locked* ]]; then
      note "заблокирована (git worktree lock) — в ней сейчас работает программа-агент"
    fi
    dirty=$(changes "$path" all)
    if [[ "$dirty" != 0 ]]; then
      note "НЕ ЗАКОММИЧЕНО изменений: $dirty"
      git -C "$path" status --short | first 10
      problems=$((problems + 1))
    fi
    if [[ -n "$branch" ]]; then
      a=$(ahead "$branch")
      b=$(behind "$branch")
      if [[ "$a" != 0 ]]; then
        note "НЕ В MAIN коммитов: $a"
        git log -n 10 --format='%h %s' "$BASE..$branch" | first 10
        problems=$((problems + 1))
      fi
      if [[ "$b" != 0 ]]; then
        note "main ушел вперед на $b — перед слиянием sync"
      fi
      if [[ "$a" == 0 && "$dirty" == 0 ]]; then
        note "все в main — папку можно убрать (remove ${path##*/}), если пользователь согласен"
      fi
    fi
  done < <(worktrees)

  # Ветки без папки: их работу легко забыть.
  while read -r branch; do
    if [[ "$branch" == "$BASE" || -n "$(path_of_branch "$branch")" ]]; then continue; fi
    a=$(ahead "$branch")
    if [[ "$a" == 0 ]]; then continue; fi
    echo
    echo "[ветка без папки] $branch"
    note "НЕ В MAIN коммитов: $a"
    git log -n 10 --format='%h %s' "$BASE..$branch" | first 10
    problems=$((problems + 1))
  done < <(git for-each-ref refs/heads --format='%(refname:short)')

  echo
  if [[ "$problems" == 0 ]]; then
    echo "ИТОГ: все в main, незакоммиченного нет."
  else
    echo "ИТОГ: требует внимания мест: $problems — показать пользователю."
  fi
}

cmd_new() {
  local name="${1:-}" dest
  [[ -n "$name" ]] || die "укажите имя: Scripts/worktree.sh new <имя>"
  if [[ ! "$name" =~ ^[a-z0-9][a-z0-9-]*$ || ${#name} -gt $MAX_NAME ]]; then
    die "имя — латиница в нижнем регистре, цифры и дефис, не длиннее $MAX_NAME знаков"
  fi
  if git show-ref -q --verify "refs/heads/$name"; then
    die "ветка $name уже есть — другое имя или remove"
  fi
  dest="$AGENTS_ROOT/$name"
  if [[ -e "$dest" ]]; then die "папка $dest уже есть"; fi
  if (( ${#dest} + 1 + LONGEST_INSIDE > 259 )); then
    echo "Внимание: путь $dest длинный — на Windows без длинных путей сборка может упереться в 260 знаков." >&2
  fi

  mkdir -p "$AGENTS_ROOT"
  git worktree add -q -b "$name" "$dest" "$BASE"
  echo "Создана рабочая папка: $dest"
  echo "Ветка: $name (от локального $BASE, $(git rev-parse --short "$BASE"))"
  echo "Дальше работать только в ней. (Claude Code создает свою папку сам: EnterWorktree с именем.)"
}

cmd_sync() {
  local branch target
  ! cloud || die "в облачной копии рабочих папок нет: git fetch $REMOTE && git rebase $REMOTE/$BASE (AGENTS.md, «Облачная копия»)"
  [[ "$HERE" != "$MAIN" ]] || die "sync запускается в рабочей папке, а не в основной"
  branch=$(git branch --show-current)
  [[ -n "$branch" ]] || die "папка не на ветке"
  [[ "$(changes "$HERE" tracked)" == 0 ]] || die "есть незакоммиченные правки — сначала коммит"
  fetch_origin
  target=$(newest_base)
  if ! git rebase -q "$target"; then
    echo "Конфликт при переносе на $target. Разрешить и git rebase --continue, или отменить: git rebase --abort." >&2
    exit 1
  fi
  echo "Ветка $branch стоит на свежем $BASE ($target). Прогнать тесты, затем: Scripts/worktree.sh merge"
}

cmd_merge() {
  local name="${1:-}" hit path branch a
  ! cloud || die "в облачной копии в $BASE отправляют через GitHub, по просьбе пользователя (AGENTS.md, «Облачная копия»)"
  if [[ -z "$name" ]]; then
    [[ "$HERE" != "$MAIN" ]] || die "в основной папке укажите имя: Scripts/worktree.sh merge <имя>"
    path="$HERE"
    branch=$(git branch --show-current)
  else
    hit=$(resolve "$name")
    if [[ -n "$hit" ]]; then
      IFS="$SEP" read -r path branch _ <<< "$hit"
    elif git show-ref -q --verify "refs/heads/$name"; then
      path=""; branch="$name"   # ветка без папки
    else
      die "нет ни папки, ни ветки с именем $name"
    fi
  fi
  [[ -n "$branch" && "$branch" != "$BASE" ]] || die "нечего вливать: папка не на ветке или это $BASE"

  if [[ -n "$path" && "$(changes "$path" tracked)" != 0 ]]; then
    die "в папке $path есть незакоммиченные правки — сначала коммит"
  fi
  a=$(ahead "$branch")
  if [[ "$a" == 0 ]]; then
    echo "В ветке $branch нет коммитов вне $BASE — вливать нечего."
    return 0
  fi
  git merge-base --is-ancestor "$BASE" "$branch" \
    || die "$BASE ушел вперед — сначала Scripts/worktree.sh sync в папке ветки, тесты, потом merge"
  fetch_origin
  newest_base > /dev/null
  if git rev-parse -q --verify "$REMOTE/$BASE" > /dev/null; then
    git merge-base --is-ancestor "$REMOTE/$BASE" "$branch" \
      || die "$BASE на GitHub ушел вперед — сначала Scripts/worktree.sh sync в папке ветки, тесты, потом merge"
  fi
  [[ "$(git -C "$MAIN" branch --show-current)" == "$BASE" ]] || die "основная папка не на $BASE"
  [[ "$(changes "$MAIN" tracked)" == 0 ]] \
    || die "в основной папке незакоммиченные правки — разобрать их с пользователем"

  git -C "$MAIN" merge -q --ff-only "$branch"
  echo "В $BASE влито коммитов: $a (ветка $branch). На сервер не отправлено — push только по просьбе."
  echo "Папка и ветка остаются; убрать — Scripts/worktree.sh remove, когда пользователь согласен."
}

cmd_remove() {
  local name="${1:-}" hit path branch flags all
  [[ -n "$name" ]] || die "укажите имя: Scripts/worktree.sh remove <имя>"
  [[ "$name" != "$BASE" ]] || die "$BASE не удаляется"
  hit=$(resolve "$name")
  [[ -n "$hit" ]] || die "нет рабочей папки с именем или веткой $name (git worktree list)"
  IFS="$SEP" read -r path branch flags <<< "$hit"
  [[ "$path" != "$MAIN" ]] || die "это основная папка"
  [[ "$HERE" != "$path" ]] || die "вы внутри $path — запустите из другой папки"
  if [[ "$flags" == *locked* ]]; then
    die "папка $path заблокирована — в ней работает программа-агент; сначала закрыть тот чат"
  fi

  all=$(changes "$path" all)
  if [[ "$all" != 0 ]]; then
    echo "В $path есть незакоммиченное ($all) — папка не тронута:" >&2
    git -C "$path" status --short | first 20 >&2
    exit 1
  fi
  if [[ -n "$branch" && "$(ahead "$branch")" != 0 ]]; then
    echo "Ветка $branch содержит коммиты, которых нет в $BASE, — папка не тронута:" >&2
    git log --format='%h %s' "$BASE..$branch" | first 20 >&2
    exit 1
  fi

  git worktree remove "$path"
  if [[ -n "$branch" ]]; then
    git branch -q -D "$branch"   # все ее коммиты уже в main — проверено выше
  fi
  echo "Убраны папка $path${branch:+ и ветка $branch}."
}

case "${1:-check}" in
  check)  cmd_check ;;
  new)    cmd_new "${2:-}" ;;
  sync)   cmd_sync ;;
  merge)  cmd_merge "${2:-}" ;;
  remove) cmd_remove "${2:-}" ;;
  *)      die "неизвестная команда «$1»; есть: check, new, sync, merge, remove" ;;
esac
