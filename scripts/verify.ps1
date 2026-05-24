#!/usr/bin/env pwsh
# ─────────────────────────────────────────────────────────────────────────────
# Local quality gate — run before every push.
# KanbanFlow uses NO GitHub Actions / CI by design (single-trust-boundary tool),
# so this script is the gate: restore → build (warnings = errors) → test → format.
#
#   Usage:  ./scripts/verify.ps1
#
# Exits non-zero on the first failing step so humans and agents can gate on it.
# ─────────────────────────────────────────────────────────────────────────────
$ErrorActionPreference = 'Stop'   # Stop-action cmdlet failures become terminating errors -> caught below
$script:verifyFailed = $false
$repoRoot = Split-Path -Parent $PSScriptRoot
Push-Location $repoRoot
try {
    # Prefer a solution file if present (added with the test project in #134);
    # otherwise fall back to the single web project.
    $target =
        (Get-ChildItem -Path $repoRoot -Filter '*.slnx' -File | Select-Object -First 1).FullName ??
        (Get-ChildItem -Path $repoRoot -Filter '*.sln'  -File | Select-Object -First 1).FullName ??
        (Join-Path $repoRoot 'src/KanbanFlow/KanbanFlow.csproj')

    Write-Host "Verifying: $target`n" -ForegroundColor Cyan

    function Invoke-Step {
        param([string]$Name, [scriptblock]$Action)
        Write-Host "=== $Name ===" -ForegroundColor Cyan
        & $Action
        if ($LASTEXITCODE -ne 0) {
            # throw (not exit) so the finally block still runs and restores the
            # location stack — exit inside try would skip Pop-Location.
            throw "FAILED: $Name (exit $LASTEXITCODE)"
        }
        Write-Host ""
    }

    Invoke-Step 'restore' { dotnet restore $target }
    Invoke-Step 'build (warnings as errors)' { dotnet build $target --no-restore -warnaserror -c Debug }

    $hasTests = Get-ChildItem -Path $repoRoot -Recurse -Filter '*.Tests.csproj' -File -ErrorAction SilentlyContinue |
        Select-Object -First 1
    if ($hasTests) {
        Invoke-Step 'test' { dotnet test $target --no-build -c Debug }
    } else {
        Write-Host "=== test (skipped — no *.Tests.csproj yet; see issue #134) ===`n" -ForegroundColor Yellow
    }

    # Format is ADVISORY for now: the existing tree has pre-existing drift
    # (e.g. ApiEndpoints.cs). Build + test are the hard gates. Once the codebase
    # is format-clean, promote this to Invoke-Step so drift fails the gate.
    Write-Host "=== format (verify, advisory) ===" -ForegroundColor Cyan
    dotnet format $target --verify-no-changes --no-restore
    if ($LASTEXITCODE -ne 0) {
        Write-Host "WARNING: formatting drift detected. Run 'dotnet format' to fix (non-blocking).`n" -ForegroundColor Yellow
    } else {
        Write-Host ""
    }

    Write-Host "Build + tests passed. Safe to push." -ForegroundColor Green
}
catch {
    # A step threw, or a Stop-action cmdlet failed. Report, then fall through to
    # finally so the location stack is always restored before we exit non-zero.
    Write-Host "`n$($_.Exception.Message)`n" -ForegroundColor Red
    $script:verifyFailed = $true
}
finally {
    Pop-Location
}

if ($script:verifyFailed) { exit 1 }
