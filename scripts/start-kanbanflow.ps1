<#
.SYNOPSIS
    Launch KanbanFlow locally for agent use (see KanbanFlow-v2#477).

.DESCRIPTION
    Two things this script exists to get right:

    1. The database lives OUTSIDE the repo. The app's default resolves the DB next to
       the binary, under src/, where `git clean -xdf` would silently delete real project
       data (*.db is gitignored, so clean removes it without a prompt). KANBANFLOW_DB_PATH
       takes precedence over that default (DbPathResolver.cs, #354), so we pin it.

    2. A brand-new machine still gets a populated board. If the live DB is missing, it is
       restored from the tracked seed at seed/kanbanflow.seed.db -- the committed starting
       point. Refresh that seed deliberately with scripts/snapshot-seed.ps1; nothing here
       ever writes back to it.

    Windows PowerShell 5.1 compatible -- no ternary, no null-coalescing.

.PARAMETER DbPath
    Live database path. Defaults to KANBANFLOW_DB_PATH, else ~/.kanbanflow/kanbanflow.db.

.PARAMETER Url
    Listen URL. Defaults to ASPNETCORE_URLS, else http://127.0.0.1:5123.

.PARAMETER NoSeed
    Start with an empty database instead of restoring the seed when none exists.
#>
[CmdletBinding()]
param(
    [string] $DbPath,
    [string] $Url,
    [switch] $NoSeed
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$RepoRoot = Split-Path -Parent $PSScriptRoot
$SeedPath = Join-Path $RepoRoot 'seed\kanbanflow.seed.db'

if (-not $DbPath) { $DbPath = $env:KANBANFLOW_DB_PATH }
if (-not $DbPath) { $DbPath = Join-Path $HOME '.kanbanflow\kanbanflow.db' }

if (-not $Url) { $Url = $env:ASPNETCORE_URLS }
if (-not $Url) { $Url = 'http://127.0.0.1:5123' }

$DbDir = Split-Path -Parent $DbPath
if (-not (Test-Path -LiteralPath $DbDir)) {
    New-Item -ItemType Directory -Path $DbDir -Force | Out-Null
}

# Restore the starting point. Guarded on absence, so this can never overwrite live data.
if (-not (Test-Path -LiteralPath $DbPath)) {
    if ($NoSeed) {
        Write-Host "[seed] no live DB; -NoSeed given, starting empty"
    }
    elseif (Test-Path -LiteralPath $SeedPath) {
        Copy-Item -LiteralPath $SeedPath -Destination $DbPath
        Write-Host "[seed] restored starting point: $SeedPath -> $DbPath"
    }
    else {
        Write-Host "[seed] no live DB and no seed at $SeedPath; starting empty"
    }
}

$env:KANBANFLOW_DB_PATH = $DbPath
$env:ASPNETCORE_URLS    = $Url

$Dll = Join-Path $RepoRoot 'src\KanbanFlow\bin\Release\net10.0\KanbanFlow.dll'
if (-not (Test-Path -LiteralPath $Dll)) {
    throw "Release build not found at $Dll. Run: dotnet build src/KanbanFlow/KanbanFlow.csproj -c Release"
}

Write-Host "[start] $Url  (db: $DbPath)"
Set-Location $RepoRoot
& dotnet $Dll
