# Downloads a Windows mpv build into tools/mpv (not committed).
# Usage: scripts\get-mpv.ps1 [-From <path to an existing mpv.exe>]
param([string]$From)
$ErrorActionPreference = 'Stop'
$dest = Join-Path $PSScriptRoot '..\tools\mpv'
New-Item -ItemType Directory -Force $dest | Out-Null

if ($From) {
    Copy-Item $From (Join-Path $dest 'mpv.exe') -Force
    "Copied mpv from $From"
    exit 0
}

# Latest x86_64 build from the shinchiro builds (linked from mpv.io/installation).
$rel = Invoke-RestMethod 'https://api.github.com/repos/shinchiro/mpv-winbuild-cmake/releases/latest'
$asset = $rel.assets | Where-Object { $_.name -match '^mpv-x86_64-v3-\d.*\.7z$' } | Select-Object -First 1
if (-not $asset) { $asset = $rel.assets | Where-Object { $_.name -match '^mpv-x86_64-\d.*\.7z$' } | Select-Object -First 1 }
if (-not $asset) { throw 'No mpv build found in the latest release.' }

$tmp = Join-Path ([IO.Path]::GetTempPath()) ("mpv-" + [guid]::NewGuid())
New-Item -ItemType Directory $tmp | Out-Null
$archive = Join-Path $tmp $asset.name
"Downloading $($asset.name) ($([math]::Round($asset.size/1MB)) MB)"
Invoke-WebRequest $asset.browser_download_url -OutFile $archive
tar -xf $archive -C $tmp
Copy-Item (Join-Path $tmp 'mpv.exe') $dest -Force
Remove-Item $tmp -Recurse -Force
"mpv installed to $dest"
