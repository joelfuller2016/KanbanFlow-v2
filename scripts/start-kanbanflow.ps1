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
       point. Refresh that seed deliberately with `npm run seed:snapshot`; nothing here
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
    # Validation runs only when a parameter is actually bound, so omitting these still
    # falls through to the environment/default resolution below -- but passing '' is
    # rejected outright rather than being silently treated as "not supplied".
    # ValidateNotNullOrEmpty, not ValidateNotNullOrWhiteSpace: the latter is PowerShell 7+
    # only and would break this script on the 5.1 it claims to support. Whitespace-only
    # input is rejected explicitly below instead.
    [ValidateNotNullOrEmpty()][string] $DbPath,
    [ValidateNotNullOrEmpty()][string] $Url,
    [switch] $NoSeed
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

foreach ($p in 'DbPath', 'Url') {
    if ($PSBoundParameters.ContainsKey($p) -and [string]::IsNullOrWhiteSpace($PSBoundParameters[$p])) {
        throw "-$p cannot be whitespace. Omit it to use the default, or give it a real value."
    }
}

try {
    $RepoRoot = Split-Path -Parent $PSScriptRoot
    $SeedPath = Join-Path (Join-Path $RepoRoot 'seed') 'kanbanflow.seed.db'

    # Resolve into fresh variables rather than reassigning the parameters. A validation
    # attribute stays attached to the *variable*, so PowerShell re-runs it on every later
    # assignment -- `$DbPath = $env:KANBANFLOW_DB_PATH` with that variable unset would trip
    # ValidateNotNullOrEmpty and kill the script on its most common invocation: no arguments
    # at all, which is exactly how the scheduled task runs it.
    $dbFile = $DbPath
    if (-not $dbFile) { $dbFile = $env:KANBANFLOW_DB_PATH }
    if (-not $dbFile) { $dbFile = Join-Path (Join-Path $HOME '.kanbanflow') 'kanbanflow.db' }

    $listenUrl = $Url
    if (-not $listenUrl) { $listenUrl = $env:ASPNETCORE_URLS }
    if (-not $listenUrl) { $listenUrl = 'http://127.0.0.1:5123' }

    # Canonicalize before touching the filesystem. A relative path would otherwise be resolved
    # twice against different roots: here against the caller's directory, and later by
    # DbPathResolver against $RepoRoot (we Set-Location there before launching). The seed would
    # land in one file and the app would open another. GetUnresolvedProviderPathFromPSPath is
    # used rather than [Path]::GetFullPath because it honours PowerShell's location, which can
    # differ from the process working directory.
    $dbFile = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($dbFile)

    $DbDir = Split-Path -Parent $dbFile
    if (-not (Test-Path -LiteralPath $DbDir)) {
        New-Item -ItemType Directory -Path $DbDir -Force | Out-Null
    }

    # Restore the starting point. Guarded on absence, so this can never overwrite live data.
    if (-not (Test-Path -LiteralPath $dbFile)) {
        if ($NoSeed) {
            Write-Host "[seed] no live DB; -NoSeed given, starting empty"
        }
        elseif (Test-Path -LiteralPath $SeedPath) {
            Copy-Item -LiteralPath $SeedPath -Destination $dbFile
            Write-Host "[seed] restored starting point: $SeedPath -> $dbFile"
        }
        else {
            Write-Host "[seed] no live DB and no seed at $SeedPath; starting empty"
        }
    }

    $env:KANBANFLOW_DB_PATH = $dbFile
    $env:ASPNETCORE_URLS    = $listenUrl

    $ProjectDir = Join-Path (Join-Path $RepoRoot 'src') 'KanbanFlow'
    $ProjectPath = Join-Path $ProjectDir 'KanbanFlow.csproj'
    $BinDir = Join-Path $ProjectDir 'bin'
    $ObjDir = Join-Path $ProjectDir 'obj'
    $ArtifactsDir = Join-Path $BinDir 'launcher-artifacts'
    $PublishDir = Join-Path (Join-Path (Join-Path $BinDir 'Release') 'net10.0') 'publish'
    $Dll = Join-Path $PublishDir 'KanbanFlow.dll'

    # A build output relies on the Development-only static-web-assets manifest to find
    # source files. A publish output contains wwwroot itself, so Production can serve the
    # complete UI. Avoid publishing on every launch by comparing the published DLL with
    # the newest project source file; generated bin/obj files are deliberately excluded.
    $publishNeeded = -not (Test-Path -LiteralPath $Dll)
    if (-not $publishNeeded) {
        $publishedAt = (Get-Item -LiteralPath $Dll).LastWriteTimeUtc
        $newestSource = Get-ChildItem -LiteralPath $ProjectDir -Recurse -File |
            Where-Object {
                -not $_.FullName.StartsWith($BinDir + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase) -and
                -not $_.FullName.StartsWith($ObjDir + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)
            } |
            Sort-Object LastWriteTimeUtc -Descending |
            Select-Object -First 1
        if ($newestSource -and $newestSource.LastWriteTimeUtc -gt $publishedAt) {
            $publishNeeded = $true
        }
    }

    if ($publishNeeded) {
        Write-Host "[publish] refreshing Release output: $PublishDir"
        # Isolated artifacts avoid colliding with an older build DLL that may already
        # be held open by a running local instance.
        & dotnet publish $ProjectPath -c Release -o $PublishDir --artifacts-path $ArtifactsDir
        if ($LASTEXITCODE -ne 0) {
            throw "dotnet publish failed with exit code $LASTEXITCODE"
        }
    }

    if (-not (Test-Path -LiteralPath $Dll)) {
        throw "Published application not found at $Dll"
    }

    Write-Host "[start] $listenUrl  (db: $dbFile)"
    Set-Location $PublishDir
    & dotnet $Dll

    # Without this the script exits 0 even when the app crashed, so a scheduled task or
    # supervisor sees a clean run and never reports the failure.
    exit $LASTEXITCODE
}
catch {
    Write-Host "`nstart-kanbanflow ERROR: $($_.Exception.Message)" -ForegroundColor Red
    exit 2
}
