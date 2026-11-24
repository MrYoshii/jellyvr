# Windows build. Uses a local .NET 10 SDK, falls back to Docker.
$ErrorActionPreference = "Stop"
Set-Location $PSScriptRoot
$out = Join-Path $PSScriptRoot "dist\DeoVR_1.0.0.0"
if (Test-Path "dist") { Remove-Item -Recurse -Force "dist" }

$sdk = $null
try { $sdk = (& dotnet --list-sdks 2>$null) | Where-Object { $_ -match '^1\d\.' } } catch {}

if ($sdk) {
    dotnet publish "Jellyfin.Plugin.DeoVR\Jellyfin.Plugin.DeoVR.csproj" -c Release -o $out
} else {
    docker run --rm -v "${PSScriptRoot}:/src" -w /src/Jellyfin.Plugin.DeoVR mcr.microsoft.com/dotnet/sdk:10.0 `
        dotnet publish -c Release -o /src/dist/DeoVR_1.0.0.0
}

if ($LASTEXITCODE -ne 0) { Write-Host "BUILD FAILED"; exit $LASTEXITCODE }

# keep only the plugin DLL (Jellyfin provides its own assemblies)
Get-ChildItem $out -File | Where-Object { $_.Name -ne "Jellyfin.Plugin.DeoVR.dll" } | Remove-Item -Force
Get-ChildItem $out
