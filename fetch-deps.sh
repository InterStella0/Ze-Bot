#!/usr/bin/env bash
# Downloads BotControllerApi.dll, the shared CounterStrikeSharp API assembly of CS2-Bot-Controller
# (https://github.com/XBribo/CS2-Bot-Controller, AGPL-3.0), into deps/ so ZeBots can be compiled
# against it. The server gets the same DLL from the BotController-CSS-API archive of the
# CS2-Bot-Controller release it runs, so the version here must match the server's closely enough
# to share the API (ABI).
set -euo pipefail

version="0.7.1"
sha256="c6abd6434e730e70e73133d812959e6eb8d66d3bea82c669ee92f92a125351ea"
here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
deps="$here/deps"
target="$deps/BotControllerApi.dll"
stamp="$deps/BotControllerApi.version"

if [ -s "$target" ] && [ "$(cat "$stamp" 2>/dev/null)" = "$version" ]; then
  exit 0
fi

mkdir -p "$deps"
temporary="$(mktemp -d)"
trap 'rm -rf -- "$temporary"' EXIT
archive="$temporary/api.zip"
curl --fail --silent --show-error --location --retry 3 -o "$archive" \
  "https://github.com/XBribo/CS2-Bot-Controller/releases/download/v${version}/BotController-CSS-API-${version}.zip"
printf '%s  %s\n' "$sha256" "$archive" | sha256sum --check --quiet
unzip -q -o -j "$archive" 'addons/counterstrikesharp/shared/BotControllerApi/BotControllerApi.dll' -d "$temporary"
mv "$temporary/BotControllerApi.dll" "$target"
printf '%s' "$version" > "$stamp"
echo "fetched BotControllerApi.dll $version"
