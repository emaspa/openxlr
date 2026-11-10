#!/usr/bin/env bash
set -euo pipefail

event=$1
before=$2
files=""
# A PR checks out its merge commit, whose first parent is the base.
# Include both paths of a rename so removed inputs still select their tests.
if [[ "$event" == pull_request ]] && git rev-parse --verify -q 'HEAD^1' >/dev/null; then
    files=$(git diff --no-renames --name-only HEAD^1 HEAD --)
elif [[ "$event" == push && -n "$before" ]] && git cat-file -e "$before^{commit}" 2>/dev/null; then
    files=$(git diff --no-renames --name-only "$before" HEAD --)
fi
if [[ -z "$files" ]]; then
    printf 'code=true\nunit=true\nomarchy=true\n'
    exit 0
fi

code=false
unit=false
omarchy=false
inert='\.md$|^docs/.*\.(png|svg)$|^plugin/com\.emaspa\.openxlr\.sdPlugin/.*\.(mjs|html|css|js)$|^plugin/tests/'
tested_docs='^docs/(skins\.md|manual\.md|install-from-source\.md|localization\.md|examples/skins/README\.md)$'
if grep -Eqv "$inert" <<< "$files"; then
    code=true
fi
if [[ "$code" == true ]] || grep -Eq "$tested_docs" <<< "$files"; then
    unit=true
fi
if grep -Eq '^packaging/omarchy/|^tools/check-omarchy\.py$|^tools/omarchy-qml-imports/|^\.github/workflows/ci\.yml$' <<< "$files"; then
    omarchy=true
fi
printf 'code=%s\nunit=%s\nomarchy=%s\n' "$code" "$unit" "$omarchy"
