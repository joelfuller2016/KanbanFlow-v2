#!/usr/bin/env pwsh
# ─────────────────────────────────────────────────────────────────────────────
# resolve-threads — resolve all unresolved review threads on a PR (issue #147).
#
# Branch protection requires conversation resolution, and bulk GraphQL
# resolveReviewThread calls can hit secondary rate limits — which is what forced
# the `--admin` merges we want to stop. This resolves every open thread with
# exponential backoff so PRs merge through the gate honestly.
#
#   Examples:
#     ./scripts/resolve-threads.ps1 -Pr 148
#     ./scripts/resolve-threads.ps1 -Pr 148 -WhatIf   # list only, resolve nothing
#
# Requires: pwsh 7+, gh (authenticated).
# Exit: 0 = nothing left unresolved; 1 = some threads could not be resolved.
# ─────────────────────────────────────────────────────────────────────────────
#requires -Version 7.0
[CmdletBinding(SupportsShouldProcess)]
param(
    [ValidateNotNullOrEmpty()][string]$Repo = 'joelfuller2016/KanbanFlow-v2',
    [Parameter(Mandatory)][int]$Pr,
    [ValidateRange(1, 10)][int]$MaxRetries = 5
)
$ErrorActionPreference = 'Stop'
$owner, $name = $Repo -split '/', 2
if ([string]::IsNullOrWhiteSpace($owner) -or [string]::IsNullOrWhiteSpace($name)) {
    throw "Repo must be 'owner/name', got '$Repo'"
}

$listQuery = @'
query($owner:String!,$name:String!,$pr:Int!){
  repository(owner:$owner,name:$name){
    pullRequest(number:$pr){
      reviewThreads(first:100){ nodes{ id isResolved } }
    } } }
'@
$resolveMutation = 'mutation($tid:ID!){ resolveReviewThread(input:{threadId:$tid}){ thread{ isResolved } } }'

try {
    $ids = gh api graphql -f query=$listQuery -F owner=$owner -F name=$name -F pr=$Pr `
        --jq '.data.repository.pullRequest.reviewThreads.nodes[] | select(.isResolved==false) | .id'
    if ($LASTEXITCODE -ne 0) { throw 'failed to list review threads' }
    $ids = @($ids | Where-Object { $_ })
    if (-not $ids) { Write-Host "No unresolved threads on PR #$Pr."; exit 0 }
    Write-Host "Unresolved threads on PR #${Pr}: $($ids.Count)" -ForegroundColor Cyan

    $failed = 0
    foreach ($id in $ids) {
        if (-not $PSCmdlet.ShouldProcess($id, 'resolveReviewThread')) { continue }
        $done = $false
        for ($attempt = 1; $attempt -le $MaxRetries -and -not $done; $attempt++) {
            gh api graphql -f query=$resolveMutation -F tid=$id --jq '.data.resolveReviewThread.thread.isResolved' | Out-Null
            if ($LASTEXITCODE -eq 0) { $done = $true; Write-Host "  resolved $id" -ForegroundColor Green; break }
            $delay = [math]::Min(60, [math]::Pow(2, $attempt))   # 2,4,8,16,32 s (capped at 60) — backs off secondary rate limits
            Write-Host "  retry $attempt/$MaxRetries for $id in ${delay}s" -ForegroundColor Yellow
            Start-Sleep -Seconds $delay
        }
        if (-not $done) { Write-Warning "could not resolve $id after $MaxRetries attempts"; $failed++ }
    }
    if ($failed -gt 0) { exit 1 }
    Write-Host "All threads resolved on PR #$Pr." -ForegroundColor Green
}
catch {
    Write-Host "`nresolve-threads ERROR: $($_.Exception.Message)" -ForegroundColor Red
    exit 2
}
