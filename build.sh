#!/usr/bin/env bash
# Builds the mod and packs it into dist/koppenflora_<version>.zip
#
# Set VINTAGE_STORY to your Vintage Story install if it is not at /opt/vintagestory, and
# TERRAIN_DIFFUSION to the Terrain Diffusion build if it is not next to this repository.
set -euo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
project="$root/src"
version="$(python3 -c "import json;print(json.load(open('$project/modinfo.json'))['version'])")"
archive="$root/dist/koppenflora_$version.zip"

dotnet build "$project" -c Release -v minimal -nologo
mkdir -p "$root/dist"
rm -f "$archive"
(cd "$project/bin/Release" && zip -q "$archive" KoppenFlora.dll)
(cd "$project" && zip -qr "$archive" modinfo.json assets)
echo "Done: $archive"
