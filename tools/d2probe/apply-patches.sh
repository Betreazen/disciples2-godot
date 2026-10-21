#!/usr/bin/env bash
# Накладывает локальные патчи сабмодулей tools/d2probe (см. NOTICE.md) —
# нужно после свежего `git submodule update --init --recursive`, иначе
# сборка падает на v142-тулсете / C4100 (см. README.md, раздел «Сборка»).
# Идемпотентно: если патч уже применён (совпадает с рабочим деревом
# сабмодуля), сообщает и пропускает.
set -euo pipefail
here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
patches_dir="$here/patches"

apply_one() {
  local submodule_dir="$1" patch_file="$2"
  if [ ! -d "$submodule_dir" ]; then
    echo "SKIP: $submodule_dir не найден (submodule не инициализирован?)" >&2
    return
  fi
  if git -C "$submodule_dir" apply --reverse --check "$patch_file" >/dev/null 2>&1; then
    echo "уже применён: $(basename "$patch_file") -> $submodule_dir"
    return
  fi
  if ! git -C "$submodule_dir" apply --check "$patch_file" >/dev/null 2>&1; then
    echo "ОШИБКА: $(basename "$patch_file") не накладывается на $submodule_dir (наложить руками по NOTICE.md)" >&2
    exit 1
  fi
  git -C "$submodule_dir" apply "$patch_file"
  echo "применён: $(basename "$patch_file") -> $submodule_dir"
}

apply_one "$here/SLikeNet" "$patches_dir/SLikeNet-0001-platformtoolset-v143-suppress-c4100.patch"
apply_one "$here/D2RSG" "$patches_dir/D2RSG-0001-platformtoolset-v143.patch"
