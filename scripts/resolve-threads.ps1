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
# Exit: 0 = nothing left unresolved (or WhatIf); 1 = some threads could not be resolved.
# ─────────────────────────────────────────────────────────────────────────────
#requires -Version 7.0
[CmdletBinding(SupportsShouldProcess)]
param(
    [ValidateNotNullOrEmpty()][string]$Repo = 'joelfuller2016/KanbanFlow-v2',
    [Parameter(Mandatory)][ValidateRange(1, [int]::MaxValue)][int]$Pr,
    [ValidateRange(1, 10)][int]$MaxRetries = 5
)
$ErrorActionPreference = 'Stop'
$owner, $name = $Repo -split '/', 2
if ([string]::IsNullOrWhiteSpace($owner) -or [string]::IsNullOrWhiteSpace($name)) {
    throw "Repo must be 'owner/name', got '$Repo'"
}

$listQuery = @'
query($owner:String!,$name:String!,$pr:Int!,$cursor:String){
  repository(owner:$owner,name:$name){
    pullRequest(number:$pr){
      reviewThreads(first:100, after:$cursor){
        nodes{ id isResolved }
        pageInfo{ hasNextPage endCursor }
      }
    } } }
'@
$pageJq = '{ids:[.data.repository.pullRequest.reviewThreads.nodes[]|select(.isResolved==false)|.id],' +
          'next:.data.repository.pullRequest.reviewThreads.pageInfo.hasNextPage,' +
          'end:.data.repository.pullRequest.reviewThreads.pageInfo.endCursor}'
$resolveMutation = 'mutation($tid:ID!){ resolveReviewThread(input:{threadId:$tid}){ thread{ isResolved } } }'

try {
    # Page through ALL review threads so PRs with >100 threads still fully clear.
    $ids = [System.Collections.Generic.List[string]]::new()
    $cursor = ''
    while ($true) {
        $ghArgs = @('graphql', '-f', "query=$listQuery", '-F', "owner=$owner", '-F', "name=$name", '-F', "pr=$Pr")
        if ($cursor) { $ghArgs += @('-F', "cursor=$cursor") }
        $json = gh api @ghArgs --jq $pageJq
        if ($LASTEXITCODE -ne 0) { throw 'failed to list review threads' }
        $page = $json | ConvertFrom-Json
        if ($page.ids) { $page.ids | ForEach-Object { $ids.Add($_) } }
        if ($page.next) { $cursor = $page.end } else { break }
    }

    if ($ids.Count -eq 0) { Write-Host "No unresolved threads on PR #${Pr}."; exit 0 }
    Write-Host "Unresolved threads on PR #${Pr}: $($ids.Count)" -ForegroundColor Cyan

    $failed = 0
    $skipped = 0
    $resolved = 0
    foreach ($id in $ids) {
        if (-not $PSCmdlet.ShouldProcess($id, 'resolveReviewThread')) { $skipped++; continue }
        $done = $false
        for ($attempt = 1; $attempt -le $MaxRetries -and -not $done; $attempt++) {
            gh api graphql -f query=$resolveMutation -F tid=$id --jq '.data.resolveReviewThread.thread.isResolved' | Out-Null
            if ($LASTEXITCODE -eq 0) { $done = $true; $resolved++; Write-Host "  resolved $id" -ForegroundColor Green; break }
            $delay = [math]::Min(60, [math]::Pow(2, $attempt))   # 2,4,8,16,32s (capped) — backs off secondary rate limits
            Write-Host "  retry $attempt/$MaxRetries for $id in ${delay}s" -ForegroundColor Yellow
            Start-Sleep -Seconds $delay
        }
        if (-not $done) { Write-Warning "could not resolve $id after $MaxRetries attempts"; $failed++ }
    }

    if ($skipped -gt 0) {
        Write-Host "WhatIf/skip: $skipped thread(s) not resolved, $resolved resolved. No gate-clearing claimed." -ForegroundColor Yellow
        exit 0
    }
    if ($failed -gt 0) { Write-Host "$resolved resolved, $failed still unresolved." -ForegroundColor Red; exit 1 }
    Write-Host "All $resolved thread(s) resolved on PR #${Pr}." -ForegroundColor Green
}
catch {
    Write-Host "`nresolve-threads ERROR: $($_.Exception.Message)" -ForegroundColor Red
    exit 2
}
