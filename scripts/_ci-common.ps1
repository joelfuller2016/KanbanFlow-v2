#!/usr/bin/env pwsh
# ─────────────────────────────────────────────────────────────────────────────
# Shared helpers for the "CI without GitHub Actions" scripts (issue #147).
# Dot-sourced by pr-ci.ps1 and master-health.ps1 — NOT meant to run on its own.
#
# KanbanFlow uses NO GitHub Actions by design, so these helpers let an external
# scheduler (local Task Scheduler, cron, or a remote Claude routine) run the same
# build/test gate the bots can't: dotnet restore -> build -warnaserror -> test.
#
# Note: callers set `$ErrorActionPreference = 'Stop'` and wrap usage in try/catch;
# dot-sourced functions run in the caller scope and inherit that strict handling.
# ─────────────────────────────────────────────────────────────────────────────
#requires -Version 7.0

function Test-CiTool {
    # Fail fast and clearly when a required CLI is missing on PATH.
    [CmdletBinding()] param([Parameter(Mandatory)][ValidateNotNullOrEmpty()][string]$Name)
    if (-not (Get-Command $Name -ErrorAction SilentlyContinue)) {
        throw "Required tool not found on PATH: $Name"
    }
}

function Get-CiBuildTarget {
    # Prefer a solution file (.slnx then .sln); fall back to the web project.
    # Mirrors scripts/verify.ps1 so local and scheduled builds target the same thing.
    [CmdletBinding()] param([Parameter(Mandatory)][ValidateNotNullOrEmpty()][string]$Root)
    $slnx = Get-ChildItem -Path $Root -Filter '*.slnx' -File -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($slnx) { return $slnx.FullName }
    $sln = Get-ChildItem -Path $Root -Filter '*.sln' -File -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($sln) { return $sln.FullName }
    return (Join-Path $Root 'src/KanbanFlow/KanbanFlow.csproj')
}

function Initialize-CiClone {
    # Maintain a dedicated clone so we never disturb the caller's working tree.
    # First run clones (via gh, so private repos authenticate); later runs fetch.
    [CmdletBinding()] param(
        [Parameter(Mandatory)][ValidateNotNullOrEmpty()][string]$Repo,
        [Parameter(Mandatory)][ValidateNotNullOrEmpty()][string]$Dir
    )
    if (Test-Path $Dir) {
        git -C $Dir fetch --quiet --prune origin
        if ($LASTEXITCODE -ne 0) { throw "git fetch failed for $Repo" }
    } else {
        $parent = Split-Path -Parent $Dir
        if ($parent -and -not (Test-Path $parent)) { New-Item -ItemType Directory -Path $parent -Force | Out-Null }
        gh repo clone $Repo $Dir
        if ($LASTEXITCODE -ne 0) { throw "git clone failed for $Repo" }
    }
}

function Invoke-CiBuildTest {
    # Run restore -> build (warnings = errors) -> test in $Root.
    # Returns: [pscustomobject]@{ Ok=[bool]; Summary=[string]; Detail=[string] }
    # Detail carries the last lines of output on failure (for status/comment bodies).
    [CmdletBinding()] param([Parameter(Mandatory)][ValidateNotNullOrEmpty()][string]$Root)
    $target = Get-CiBuildTarget -Root $Root
    $steps = @(
        @{ Name = 'restore'; Cmd = { dotnet restore $target } },
        @{ Name = 'build';   Cmd = { dotnet build $target --no-restore -warnaserror -c Debug } }
    )
    $hasTests = Get-ChildItem -Path $Root -Recurse -Filter '*.Tests.csproj' -File -ErrorAction SilentlyContinue |
        Select-Object -First 1
    if ($hasTests) { $steps += @{ Name = 'test'; Cmd = { dotnet test $target --no-build -c Debug } } }

    $testSummary = ''
    foreach ($step in $steps) {
        $out = & $step.Cmd 2>&1 | Out-String
        if ($LASTEXITCODE -ne 0) {
            $tail = (($out -split "`r?`n") | Where-Object { $_ -ne '' } | Select-Object -Last 25) -join "`n"
            return [pscustomobject]@{ Ok = $false; Summary = "$($step.Name) FAILED (exit $LASTEXITCODE)"; Detail = $tail }
        }
        if ($step.Name -eq 'test') {
            $line = $out -split "`r?`n" | Where-Object { $_ -match 'Passed!|Passed:|Failed:' } | Select-Object -First 1
            if ($line) { $testSummary = $line.Trim() }
        }
    }
    $summary = 'build clean'
    if ($hasTests) {
        $summary += $testSummary ? " + $testSummary" : ' + tests passed'
    } else {
        $summary += ' (no test project)'
    }
    return [pscustomobject]@{ Ok = $true; Summary = $summary; Detail = '' }
}

function Protect-CiLog {
    # Redact common secret shapes before posting build/test output to GitHub
    # (PR comments / tracking issues). Defense-in-depth per the PowerShell
    # coding guideline "never store/expose plaintext secrets".
    [CmdletBinding()] param([Parameter(Mandatory)][AllowEmptyString()][string]$Text)
    if ([string]::IsNullOrEmpty($Text)) { return $Text }
    $patterns = @(
        'gh[pousr]_[A-Za-z0-9]{20,}',                                  # GitHub PAT/OAuth tokens
        'github_pat_[A-Za-z0-9_]{20,}',                                # fine-grained PAT
        '(?i)bearer\s+[A-Za-z0-9\-\._~\+\/]+=*',                       # Authorization: Bearer ...
        '(?i)(password|pwd|secret|token|api[_-]?key)\s*[:=]\s*\S+',    # key=value secrets
        'eyJ[A-Za-z0-9_\-]+\.[A-Za-z0-9_\-]+\.[A-Za-z0-9_\-]+'        # JWTs
    )
    $safe = $Text
    foreach ($p in $patterns) { $safe = [regex]::Replace($safe, $p, '[REDACTED]') }
    return $safe
}
