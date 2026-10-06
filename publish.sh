#!/usr/bin/env bash
# Builds a self-contained copy for each platform into dist/.
#
#   ./publish.sh              all targets
#   ./publish.sh osx-arm64    just one
#
# Each zip holds a folder: a single executable with the .NET runtime inside it,
# wwwroot and appsettings.json. Nothing needs installing on the target machine.
#
# Native libraries are linked into the executable, never extracted at startup:
# extracting them crashed on macOS inside a socket call.
set -euo pipefail

cd "$(dirname "$0")"

VERSION=$(grep -o '<Version>[^<]*' DenonRemote.csproj | cut -d'>' -f2)
DEFAULT_TARGETS="osx-arm64 osx-x64 win-x64 win-arm64 linux-x64 linux-arm64"
read -ra TARGETS <<< "${*:-$DEFAULT_TARGETS}"

echo "==> self-test"
dotnet run -c Release --self-test

rm -rf dist
mkdir -p dist

for rid in "${TARGETS[@]}"; do
    echo "==> $rid"
    out="dist/DenonRemote-$rid"

    dotnet publish -c Release -r "$rid" -o "$out" --nologo --self-contained -p:PublishSingleFile=true

    rm -f "$out"/*.pdb

    ( cd dist && zip -qr "DenonRemote-$VERSION-$rid.zip" "$(basename "$out")" )
    rm -rf "$out"
    echo "    dist/DenonRemote-$VERSION-$rid.zip"
done

echo
echo "Done. These are unsigned:"
echo "  macOS   - right-click the executable and choose Open the first time, or"
echo "            xattr -dr com.apple.quarantine DenonRemote-osx-arm64"
echo "  Windows - SmartScreen warns; More info -> Run anyway."
