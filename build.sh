#!/usr/bin/env bash
# Tests and publishes ZeBots, then packs it as dist/ZeBots.tar.gz with the layout to extract over
# csgo/: addons/counterstrikesharp/plugins/ZeBots/. Needs the .NET 10 SDK, curl and unzip.
set -euo pipefail

here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
cd "$here"
"$here/fetch-deps.sh"

dotnet test tests/ZeBots.Logic.Tests/ZeBots.Logic.Tests.csproj --configuration Release
publish="$(mktemp -d)"
trap 'rm -rf -- "$publish"' EXIT
dotnet publish src/ZeBots/ZeBots.csproj --configuration Release --output "$publish/addons/counterstrikesharp/plugins/ZeBots"
# Provided by the server (CounterStrikeSharp, and BotControllerApi in shared/); a second copy in
# the plugin directory would load as a different assembly and break the capability lookup.
rm -f "$publish/addons/counterstrikesharp/plugins/ZeBots/"{CounterStrikeSharp.API,BotControllerApi}.{dll,pdb,xml}

mkdir -p dist
tar -czf dist/ZeBots.tar.gz -C "$publish" addons
echo "built $here/dist/ZeBots.tar.gz"
tar -tzf dist/ZeBots.tar.gz
