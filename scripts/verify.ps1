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
$ErrorActionPreference = 'Stop'
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
            Write-Host "`nFAILED: $Name (exit $LASTEXITCODE)`n" -ForegroundColor Red
            exit 1
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

    Invoke-Step 'format (verify, no changes)' { dotnet format $target --verify-no-changes --no-restore }

    Write-Host "All checks passed. Safe to push." -ForegroundColor Green
}
finally {
    Pop-Location
}
