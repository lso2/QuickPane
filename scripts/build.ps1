# build.ps1
# Purpose: builds QuickPane.exe (Release or Debug, x64) with the MSBuild that vswhere reports.
# Reason: the .NET SDK cannot build the legacy WPF QuickPane.csproj.
# Defines: no functions or globals.
# Origin: from jprisant's fork, commit ed9964b (MIT, see NOTICE.md).
# Doc: docs/notes/scripts/build.ps1.md
# Parameters: -Configuration Release|Debug (default Release).
param([ValidateSet('Release', 'Debug')][string]$Configuration = 'Release')
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent

$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
$msbuild = $null
if (Test-Path $vswhere) {
    $msbuild = & $vswhere -latest -products * -requires Microsoft.Component.MSBuild -find 'MSBuild\**\Bin\MSBuild.exe' | Select-Object -First 1
}
if (-not $msbuild -or -not (Test-Path $msbuild)) { throw 'MSBuild.exe not found by vswhere.' }

$csproj = Join-Path $repo 'QuickPane\QuickPane.csproj'
& $msbuild $csproj "/p:Configuration=$Configuration" '/p:Platform=x64' /nologo /v:minimal
if ($LASTEXITCODE -ne 0) { throw "MSBuild failed with exit code $LASTEXITCODE" }

$exe = Join-Path $repo "QuickPane\bin\x64\$Configuration\QuickPane.exe"
"Built: $exe"
"Version: $((Get-Item $exe).VersionInfo.FileVersion)"
