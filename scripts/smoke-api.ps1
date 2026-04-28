param(
    [string]$ProjectPath = "src/KanbanFlow/KanbanFlow.csproj",
    [string]$BaseUrl = "http://127.0.0.1:5124",
    [int]$StartupTimeoutSeconds = 30
)

$ErrorActionPreference = "Stop"

function Resolve-RepoRoot {
    $scriptDir = Split-Path -Parent $PSCommandPath
    return (Resolve-Path (Join-Path $scriptDir "..")).Path
}

function Invoke-SmokeGet([string]$Path) {
    Invoke-RestMethod -Method Get -Uri "$BaseUrl$Path" -TimeoutSec 5
}

function Invoke-SmokePost([string]$Path, $Body) {
    Invoke-RestMethod -Method Post -Uri "$BaseUrl$Path" -ContentType "application/json" -Body ($Body | ConvertTo-Json -Depth 10) -TimeoutSec 5
}

function Invoke-SmokePostExpectFailure([string]$Path, $Body, [int]$ExpectedStatusCode) {
    try {
        Invoke-SmokePost $Path $Body | Out-Null
        throw "POST $Path succeeded but expected HTTP $ExpectedStatusCode."
    }
    catch {
        $response = $_.Exception.Response
        if ($null -eq $response) { throw }
        $statusCode = [int]$response.StatusCode
        if ($statusCode -ne $ExpectedStatusCode) {
            throw "POST $Path returned HTTP $statusCode but expected HTTP $ExpectedStatusCode."
        }
    }
}

function Assert-Truthy($Value, [string]$Message) {
    if (-not $Value) { throw $Message }
}

$repoRoot = Resolve-RepoRoot
$resolvedProjectPath = (Resolve-Path (Join-Path $repoRoot $ProjectPath)).Path
$runDir = Join-Path ([System.IO.Path]::GetTempPath()) ("kanbanflow-smoke-" + [Guid]::NewGuid().ToString("N"))
New-Item -ItemType Directory -Path $runDir | Out-Null

$appProcess = $null
$stdoutPath = Join-Path $runDir "kanbanflow.out.log"
$stderrPath = Join-Path $runDir "kanbanflow.err.log"

try {
    Write-Host "Building KanbanFlow..."
    dotnet build $resolvedProjectPath --nologo

    Write-Host "Starting KanbanFlow with a fresh SQLite DB in $runDir..."
    $appProcess = Start-Process dotnet `
        -ArgumentList @("run", "--no-build", "--no-launch-profile", "--project", $resolvedProjectPath, "--urls", $BaseUrl) `
        -WorkingDirectory $runDir `
        -RedirectStandardOutput $stdoutPath `
        -RedirectStandardError $stderrPath `
        -PassThru

    $deadline = (Get-Date).AddSeconds($StartupTimeoutSeconds)
    do {
        if ($appProcess.HasExited) {
            $stdout = if (Test-Path $stdoutPath) { Get-Content $stdoutPath -Raw } else { "" }
            $stderr = if (Test-Path $stderrPath) { Get-Content $stderrPath -Raw } else { "" }
            throw "KanbanFlow exited before startup.`nSTDOUT:`n$stdout`nSTDERR:`n$stderr"
        }

        try {
            $projects = Invoke-SmokeGet "/api/projects"
            break
        }
        catch {
            Start-Sleep -Milliseconds 500
        }
    } while ((Get-Date) -lt $deadline)

    Assert-Truthy $projects "GET /api/projects did not return a response before timeout."
    Assert-Truthy ($projects.Count -gt 0) "GET /api/projects returned no seeded projects."

    $projectId = $projects[0].id
    Write-Host "Using seeded project id $projectId"

    $project = Invoke-SmokeGet "/api/projects/$projectId"
    Assert-Truthy $project.id "GET /api/projects/$projectId did not return a project id."
    Assert-Truthy $project.features "GET /api/projects/$projectId did not include features."

    $board = Invoke-SmokeGet "/api/projects/$projectId/board"
    foreach ($status in @("Backlog", "Todo", "InProgress", "Review", "Done")) {
        Assert-Truthy ($board.PSObject.Properties.Name -contains $status) "GET /api/projects/$projectId/board is missing $status."
    }

    $requirements = Invoke-SmokeGet "/api/projects/$projectId/requirements"
    Assert-Truthy ($null -ne $requirements) "GET /api/projects/$projectId/requirements returned null."

    $context = Invoke-SmokeGet "/api/projects/$projectId/context"
    Assert-Truthy $context.project "GET /api/projects/$projectId/context is missing project metadata."
    Assert-Truthy $context.summary "GET /api/projects/$projectId/context is missing summary."

    $nextTasks = Invoke-SmokeGet "/api/projects/$projectId/next-tasks"
    Assert-Truthy ($null -ne $nextTasks.recommendations) "GET /api/projects/$projectId/next-tasks is missing recommendations."

    $tasks = Invoke-SmokeGet "/api/projects/$projectId/tasks"
    Assert-Truthy ($tasks.Count -gt 0) "GET /api/projects/$projectId/tasks returned no seeded tasks."
    $taskId = $tasks[0].id

    $handoffText = "API smoke typed handoff $([Guid]::NewGuid().ToString("N"))"
    $handoff = Invoke-SmokePost "/api/tasks/$taskId/comments" @{ text = $handoffText; author = "api-smoke"; type = "Handoff" }
    Assert-Truthy ($handoff.type -eq "Handoff") "POST /api/tasks/$taskId/comments did not preserve type Handoff."

    $comments = Invoke-SmokeGet "/api/tasks/$taskId/comments"
    Assert-Truthy ($comments | Where-Object { $_.text -eq $handoffText -and $_.type -eq "Handoff" }) "GET /api/tasks/$taskId/comments did not return typed handoff comment."

    $defaultComment = Invoke-SmokePost "/api/tasks/$taskId/comments" @{ text = "API smoke default note $([Guid]::NewGuid().ToString("N"))"; author = "api-smoke" }
    Assert-Truthy ($defaultComment.type -eq "Note") "POST /api/tasks/$taskId/comments without type should default to Note."

    Invoke-SmokePostExpectFailure "/api/tasks/$taskId/comments" @{ text = "bad type"; type = "NotAType" } 422

    Write-Host "API smoke tests passed."
}
finally {
    if ($appProcess -and -not $appProcess.HasExited) {
        Stop-Process -Id $appProcess.Id -Force
        $appProcess.WaitForExit(5000) | Out-Null
    }

    if ($appProcess) {
        $appProcess.Dispose()
    }

    if (Test-Path $runDir) {
        Start-Sleep -Milliseconds 250
        Remove-Item $runDir -Recurse -Force -ErrorAction SilentlyContinue
    }
}
