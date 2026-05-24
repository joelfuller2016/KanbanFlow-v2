#!/usr/bin/env pwsh
# ─────────────────────────────────────────────────────────────────────────────
# master health check — CI without GitHub Actions (issue #147).
#
# Builds + tests the default branch in a clean clone and keeps a single tracking
# issue in sync with reality:
#   • red   -> open a tracking issue (or comment on the existing open one)
#   • green -> if a tracking issue is open, comment "recovered" and close it
#
# Catches cross-PR breakage that per-PR review can't see (e.g. the #142 NU1605
# downgrade that broke the test build only once two individually-green PRs merged).
#
#   Examples:
#     ./scripts/master-health.ps1
#     ./scripts/master-health.ps1 -Branch main
#
# Requires: pwsh 7+, git, gh (authenticated), .NET 10 SDK.
# Exit: 0 = branch healthy; 1 = branch red (issue opened/updated); 2 = script error.
# ─────────────────────────────────────────────────────────────────────────────
#requires -Version 7.0
[CmdletBinding()]
param(
    [ValidateNotNullOrEmpty()][string]$Repo = 'joelfuller2016/KanbanFlow-v2',
    [ValidateNotNullOrEmpty()][string]$Branch = 'master',
    [ValidateNotNullOrEmpty()][string]$IssueLabel = 'bug',
    [string]$WorkRoot = (Join-Path ([System.IO.Path]::GetTempPath()) 'kf-master-health')
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot '_ci-common.ps1')

$title = "Broken ${Branch}: build/test failing (automated)"
$fence = [string]::new([char]96, 3)
$bt = [char]96

try {
    Test-CiTool git; Test-CiTool gh; Test-CiTool dotnet
    $cloneDir = Join-Path $WorkRoot ($Repo -replace '[/\\]', '_')
    Initialize-CiClone -Repo $Repo -Dir $cloneDir
    git -C $cloneDir -c advice.detachedHead=false checkout --quiet --force "origin/$Branch"
    if ($LASTEXITCODE -ne 0) { throw "checkout origin/$Branch failed" }
    git -C $cloneDir clean -xdf --quiet | Out-Null
    $sha = (git -C $cloneDir rev-parse --short HEAD).Trim()

    $result = Invoke-CiBuildTest -Root $cloneDir
    $icon = $result.Ok ? '✅' : '❌'
    Write-Host "$icon $Branch @ $sha : $($result.Summary)" -ForegroundColor ($result.Ok ? 'Green' : 'Red')

    # Is there already an open tracking issue? Quote the title so search is exact
    # (unquoted, GitHub ANDs the words and can match unrelated issues).
    $search = '"' + $title + '" in:title'
    $existing = gh issue list -R $Repo --state open --search $search --json number --jq '.[0].number // empty'
    if ($LASTEXITCODE -ne 0) { throw "failed to query tracking issues for $Repo" }

    if (-not $result.Ok) {
        $safeDetail = Protect-CiLog -Text $result.Detail
        $body = "Automated **$Branch** health check failed at $bt$sha$bt.`n`n**$($result.Summary)**`n`n" +
                "<details><summary>last output lines</summary>`n`n$fence`n$safeDetail`n$fence`n</details>`n`n" +
                "<sub>Posted by scripts/master-health.ps1 — CI without GitHub Actions (#147).</sub>"
        if ($existing) {
            gh issue comment $existing -R $Repo --body $body | Out-Null
            if ($LASTEXITCODE -ne 0) { throw "failed to comment on tracking issue #$existing" }
            Write-Host "Updated tracking issue #$existing" -ForegroundColor Yellow
        } else {
            gh issue create -R $Repo --title $title --label $IssueLabel --body $body | Out-Null
            if ($LASTEXITCODE -ne 0) { throw "failed to create tracking issue for $Branch" }
            Write-Host 'Opened a new tracking issue' -ForegroundColor Yellow
        }
        exit 1
    }

    if ($existing) {
        gh issue comment $existing -R $Repo --body "✅ $Branch is green again at $bt$sha$bt ($($result.Summary)). Auto-closing." | Out-Null
        if ($LASTEXITCODE -ne 0) { throw "failed to post recovery comment on issue #$existing" }
        gh issue close $existing -R $Repo | Out-Null
        if ($LASTEXITCODE -ne 0) { throw "failed to close issue #$existing" }
        Write-Host "Closed recovered tracking issue #$existing" -ForegroundColor Green
    }
    Write-Host "$Branch healthy."
}
catch {
    Write-Host "`nmaster-health ERROR: $($_.Exception.Message)" -ForegroundColor Red
    exit 2
}
