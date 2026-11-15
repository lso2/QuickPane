# test.ps1
# Purpose: runs the unit tests in tests/QuickPane.Tests with dotnet test, after building the app unless -NoBuild is given.
# Reason: the test project references the built QuickPane.exe.
# Defines: no functions or globals.
# Origin: from jprisant's fork, commit ed9964b (MIT, see NOTICE.md).
# Doc: docs/notes/scripts/test.ps1.md
# Parameters: -Configuration Release|Debug (default Release), and -NoBuild to skip build.ps1.
param([ValidateSet('Release', 'Debug')][string]$Configuration = 'Release', [switch]$NoBuild)
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent

if (-not $NoBuild) { & (Join-Path $PSScriptRoot 'build.ps1') -Configuration $Configuration }

$proj = Join-Path $repo 'tests\QuickPane.Tests\QuickPane.Tests.csproj'
dotnet test $proj -c $Configuration --nologo
if ($LASTEXITCODE -ne 0) { throw "Tests failed with exit code $LASTEXITCODE" }
