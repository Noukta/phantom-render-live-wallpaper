# Publishes the engine and the control panel into publish\ and copies mpv next to them.
# The engine uses NativeAOT (small native exe) when the MSVC linker is available (Visual Studio "Desktop
# development with C++" or Build Tools, present on GitHub's windows runners); otherwise it falls back to a
# regular framework-dependent build. The control panel (WPF) needs the .NET 8 Desktop Runtime.
param([switch]$NoAot)
$ErrorActionPreference = 'Stop'
$root = Resolve-Path (Join-Path $PSScriptRoot '..')
$out = Join-Path $root 'publish'
$engine = Join-Path $root 'src\Wpe.Engine\Wpe.Engine.csproj'
$ui = Join-Path $root 'src\Wpe.UI\Wpe.UI.csproj'

$vswhere = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe"
$hasLinker = (Test-Path $vswhere) -and (& $vswhere -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath)

if ($hasLinker -and -not $NoAot) {
    dotnet publish $engine -c Release -r win-x64 -o $out
} else {
    Write-Warning 'MSVC linker not found (or -NoAot): building the engine without NativeAOT.'
    dotnet publish $engine -c Release -r win-x64 --self-contained false -p:PublishAot=false -o $out
}
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

dotnet publish $ui -c Release -r win-x64 --self-contained false -o $out
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

$mpv = Join-Path $root 'tools\mpv\mpv.exe'
if (Test-Path $mpv) { Copy-Item $mpv $out -Force } else { Write-Warning 'tools\mpv\mpv.exe missing: run scripts\get-mpv.ps1' }
Get-ChildItem $out -Filter *.pdb | Remove-Item
"Published to $out"
