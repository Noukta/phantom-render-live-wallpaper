# Publishes the engine into publish\ and copies mpv next to it.
# Uses NativeAOT (small native exe) when the MSVC linker is available (Visual Studio "Desktop development
# with C++" or Build Tools, present on GitHub's windows runners); otherwise falls back to a regular
# framework-dependent build that needs the .NET 8 runtime.
param([switch]$NoAot)
$ErrorActionPreference = 'Stop'
$root = Resolve-Path (Join-Path $PSScriptRoot '..')
$out = Join-Path $root 'publish'
$proj = Join-Path $root 'src\Wpe.Engine\Wpe.Engine.csproj'

$vswhere = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe"
$hasLinker = (Test-Path $vswhere) -and (& $vswhere -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath)

if ($hasLinker -and -not $NoAot) {
    dotnet publish $proj -c Release -r win-x64 -o $out
} else {
    Write-Warning 'MSVC linker not found (or -NoAot): building without NativeAOT.'
    dotnet publish $proj -c Release -r win-x64 --self-contained false -p:PublishAot=false -o $out
}
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

$mpv = Join-Path $root 'tools\mpv\mpv.exe'
if (Test-Path $mpv) { Copy-Item $mpv $out -Force } else { Write-Warning 'tools\mpv\mpv.exe missing: run scripts\get-mpv.ps1' }
Get-ChildItem $out -Filter *.pdb | Remove-Item
"Published to $out"
