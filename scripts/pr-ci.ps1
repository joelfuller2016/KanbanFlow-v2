#!/usr/bin/env pwsh
# ─────────────────────────────────────────────────────────────────────────────
# PR build/test gate — CI without GitHub Actions (issue #147).
#
# Builds and tests one (or every) open PR in an isolated clone, then optionally
# publishes the result two ways:
#   • a commit STATUS  — machine-enforceable merge gate (require it in branch
#                        protection and red PRs can't be merged)
#   • a PR COMMENT     — human-readable, upserted in place (no comment spam)
#
# Designed to be invoked by ANY external scheduler (local Task Scheduler, cron,
# or a remote Claude routine). Self-contained: never touches the caller's tree.
#
#   Examples:
#     ./scripts/pr-ci.ps1 -Pr 146                     # dry run (build/test only)
#     ./scripts/pr-ci.ps1 -Pr 146 -PostStatus -PostComment
#     ./scripts/pr-ci.ps1 -PostStatus -PostComment    # gate EVERY open PR
#
# Requires: pwsh 7+, git, gh (authenticated), .NET SDK (per global.json).
# Exit: 0 = all targeted PRs passed; 1 = a PR failed; 2 = script/setup error.
# ─────────────────────────────────────────────────────────────────────────────
#requires -Version 7.0
[CmdletBinding()]
param(
    [ValidateNotNullOrEmpty()][string]$Repo = 'joelfuller2016/KanbanFlow-v2',
    [int]$Pr = 0,                                   # 0 = all open PRs
    [switch]$PostStatus,
    [switch]$PostComment,
    [ValidateNotNullOrEmpty()][string]$StatusContext = 'pr-build-test',
    [string]$WorkRoot = (Join-Path ([System.IO.Path]::GetTempPath()) 'kf-pr-ci')
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot '_ci-common.ps1')

$marker = '<!-- pr-ci-bot -->'
$bt = [char]96                       # backtick — avoid literal `` in double-quoted strings
$fence = [string]::new([char]96, 3)  # ``` for collapsible log blocks

function Publish-CiStatus {
    [CmdletBinding()] param(
        [Parameter(Mandatory)][string]$Repo,
        [Parameter(Mandatory)][string]$Sha,
        [Parameter(Mandatory)][ValidateSet('pending','success','failure','error')][string]$State,
        [Parameter(Mandatory)][string]$Context,
        [Parameter(Mandatory)][string]$Description
    )
    $desc = $Description
    if ($desc.Length -gt 140) { $desc = $desc.Substring(0, 137) + '...' }  # GitHub caps status description length
    gh api -X POST "repos/$Repo/statuses/$Sha" -f "state=$State" -f "context=$Context" -f "description=$desc" | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "failed to post commit status ($State) for $Sha" }
}

function Set-CiPrComment {
    # Upsert: update our previous comment if present (find by hidden marker), else create.
    [CmdletBinding()] param(
        [Parameter(Mandatory)][string]$Repo,
        [Parameter(Mandatory)][int]$Pr,
        [Parameter(Mandatory)][string]$Body
    )
    $full = "$marker`n$Body"
    $jq = '[.[] | select(.body | contains("' + $marker + '")) | .id] | first // empty'
    $existing = gh api "repos/$Repo/issues/$Pr/comments" --paginate --jq $jq 2>$null
    if ($existing) {
        gh api -X PATCH "repos/$Repo/issues/comments/$existing" -f "body=$full" | Out-Null
    } else {
        gh api -X POST "repos/$Repo/issues/$Pr/comments" -f "body=$full" | Out-Null
    }
    if ($LASTEXITCODE -ne 0) { throw "failed to upsert PR comment on #$Pr" }
}

function Set-CiPrCheckout {
    [CmdletBinding()] param(
        [Parameter(Mandatory)][string]$Dir,
        [Parameter(Mandatory)][int]$Pr,
        [Parameter(Mandatory)][string]$Sha
    )
    git -C $Dir fetch --quiet origin "pull/$Pr/head"
    if ($LASTEXITCODE -ne 0) { throw "fetch pull/$Pr/head failed" }
    git -C $Dir -c advice.detachedHead=false checkout --quiet --force $Sha
    if ($LASTEXITCODE -ne 0) { throw "checkout $Sha failed" }
    git -C $Dir clean -xdf --quiet | Out-Null   # drop bin/obj/artifacts from a prior run
}

try {
    Test-CiTool git; Test-CiTool gh; Test-CiTool dotnet
    $cloneDir = Join-Path $WorkRoot ($Repo -replace '[/\\]', '_')
    Initialize-CiClone -Repo $Repo -Dir $cloneDir

    if ($Pr -gt 0) {
        $prNumbers = @($Pr)
    } else {
        $prNumbers = gh pr list -R $Repo --state open --json number --jq '.[].number' | ForEach-Object { [int]$_ }
    }
    if (-not $prNumbers) { Write-Host 'No open PRs to check.'; exit 0 }

    $anyFailed = $false
    foreach ($n in $prNumbers) {
        $meta = gh pr view $n -R $Repo --json headRefOid,headRefName,isCrossRepository | ConvertFrom-Json
        if ($meta.isCrossRepository) {
            Write-Host "PR #$n is from a fork — skipping (posting a status to a fork needs extra perms)." -ForegroundColor Yellow
            continue
        }
        $sha = $meta.headRefOid
        $short = $sha.Substring(0, 8)
        Write-Host "=== PR #$n ($($meta.headRefName)) @ $short ===" -ForegroundColor Cyan

        if ($PostStatus) { Publish-CiStatus -Repo $Repo -Sha $sha -State 'pending' -Context $StatusContext -Description 'build/test running' }

        Set-CiPrCheckout -Dir $cloneDir -Pr $n -Sha $sha
        $result = Invoke-CiBuildTest -Root $cloneDir
        $icon = $result.Ok ? '✅' : '❌'
        Write-Host "$icon $($result.Summary)" -ForegroundColor ($result.Ok ? 'Green' : 'Red')

        if ($PostStatus) {
            Publish-CiStatus -Repo $Repo -Sha $sha -State ($result.Ok ? 'success' : 'failure') `
                -Context $StatusContext -Description $result.Summary
        }
        if ($PostComment) {
            $body = "$icon **$StatusContext** for $bt$short$bt — $($result.Summary)"
            if (-not $result.Ok -and $result.Detail) {
                $body += "`n`n<details><summary>last output lines</summary>`n`n$fence`n$($result.Detail)`n$fence`n</details>"
            }
            $body += "`n`n<sub>Posted by scripts/pr-ci.ps1 — CI without GitHub Actions (#147).</sub>"
            Set-CiPrComment -Repo $Repo -Pr $n -Body $body
        }
        if (-not $result.Ok) { $anyFailed = $true }
    }

    if ($anyFailed) { Write-Host "`nOne or more PRs failed." -ForegroundColor Red; exit 1 }
    Write-Host "`nAll targeted PRs passed." -ForegroundColor Green
}
catch {
    Write-Host "`npr-ci ERROR: $($_.Exception.Message)" -ForegroundColor Red
    exit 2
}
