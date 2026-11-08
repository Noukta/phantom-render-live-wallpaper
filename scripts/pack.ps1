# Builds the Velopack installer (releases\PhantomRender-win-Setup.exe) plus update packages from publish\.
# Usage: scripts\pack.ps1 -Version 0.4.0
# Run scripts\build.ps1 -Version <same> first. The installer also installs the .NET 8 Desktop Runtime
# when it is missing (needed by the control panel; the engine is native).
param([Parameter(Mandatory)][string]$Version)
$ErrorActionPreference = 'Stop'
$root = Resolve-Path (Join-Path $PSScriptRoot '..')
$publish = Join-Path $root 'publish'
$out = Join-Path $root 'releases'
if (-not (Test-Path (Join-Path $publish 'PhantomRender.exe'))) { throw 'publish\PhantomRender.exe missing: run scripts\build.ps1 first.' }

vpk pack `
    --packId PhantomRender `
    --packVersion $Version `
    --packDir $publish `
    --mainExe PhantomRender.exe `
    --packTitle 'Phantom Render' `
    --packAuthors 'Ahmed Noukta' `
    --icon (Join-Path $root 'assets\phantom.ico') `
    --framework net8.0-x64-desktop `
    --shortcuts Desktop,StartMenuRoot `
    --exclude '(^|\\)mpv\.exe$' `
    --outputDir $out
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
Get-ChildItem $out | Select-Object Name, Length
