#!/usr/bin/env bash
# Build in a .NET 10 container. Output: ./dist/DeoVR_1.0.0.0/Jellyfin.Plugin.DeoVR.dll
set -euo pipefail
cd "$(dirname "$0")"
rm -rf dist
docker run --rm -v "$PWD":/src -w /src/Jellyfin.Plugin.DeoVR mcr.microsoft.com/dotnet/sdk:10.0 \
  dotnet publish -c Release -o /src/dist/DeoVR_1.0.0.0
# keep only the plugin DLL (Jellyfin provides its own assemblies)
find dist/DeoVR_1.0.0.0 -type f ! -name 'Jellyfin.Plugin.DeoVR.dll' -delete
ls -l dist/DeoVR_1.0.0.0
