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

function Invoke-SmokeJson([string]$Method, [string]$Path, $Body) {
    Invoke-RestMethod -Method $Method -Uri "$BaseUrl$Path" -ContentType "application/json" -Body ($Body | ConvertTo-Json -Depth 10) -TimeoutSec 5
}

function Invoke-SmokeDelete([string]$Path) {
    Invoke-RestMethod -Method Delete -Uri "$BaseUrl$Path" -TimeoutSec 5
}

function Assert-Truthy($Value, [string]$Message) {
    if (-not $Value) { throw $Message }
}

function New-FreeLoopbackBaseUrl {
    $listener = [System.Net.Sockets.TcpListener]::new([System.Net.IPAddress]::Loopback, 0)
    try {
        $listener.Start()
        $port = $listener.LocalEndpoint.Port
    }
    finally {
        $listener.Stop()
    }

    return "http://127.0.0.1:$port"
}

if ($BaseUrl -eq "http://127.0.0.1:5124") {
    $BaseUrl = New-FreeLoopbackBaseUrl
}

$repoRoot = Resolve-RepoRoot
$resolvedProjectPath = (Resolve-Path (Join-Path $repoRoot $ProjectPath)).Path
$runDir = Join-Path ([System.IO.Path]::GetTempPath()) ("kanbanflow-smoke-" + [Guid]::NewGuid().ToString("N"))
New-Item -ItemType Directory -Path $runDir | Out-Null

$appProcess = $null
$stdoutPath = Join-Path $runDir "kanbanflow.out.log"
$stderrPath = Join-Path $runDir "kanbanflow.err.log"
$dbPath = Join-Path $runDir "kanbanflow.db"
$previousDbPath = $env:KANBANFLOW_DB_PATH

try {
    Write-Host "Building KanbanFlow..."
    dotnet build $resolvedProjectPath --nologo

    Write-Host "Starting KanbanFlow with a fresh SQLite DB in $runDir..."
    Write-Host "Using API smoke base URL $BaseUrl"
    $env:KANBANFLOW_DB_PATH = $dbPath
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

    Invoke-SmokeJson "Patch" "/api/tasks/$taskId/status" @{ status = "InProgress" } | Out-Null
    Invoke-SmokeJson "Post" "/api/tasks/$taskId/claim" @{ actor = "smoke-agent" } | Out-Null
    Invoke-SmokeJson "Post" "/api/tasks/$taskId/comments" @{ text = "Smoke test activity comment"; author = "smoke-agent" } | Out-Null

    $taskActivity = Invoke-SmokeGet "/api/tasks/$taskId/activity"
    Assert-Truthy ($taskActivity.Count -ge 3) "GET /api/tasks/$taskId/activity did not return expected workflow events."
    $eventTypes = @($taskActivity | ForEach-Object { $_.eventType })
    foreach ($eventType in @("TaskStatusChanged", "TaskClaimed", "TaskCommentAdded")) {
        Assert-Truthy ($eventTypes -contains $eventType) "Task activity is missing $eventType."
    }

    Invoke-SmokeDelete "/api/tasks/$taskId" | Out-Null
    $deletedTaskActivity = Invoke-SmokeGet "/api/tasks/$taskId/activity"
    $deletedEventTypes = @($deletedTaskActivity | ForEach-Object { $_.eventType })
    Assert-Truthy ($deletedEventTypes -contains "TaskDeleted") "GET /api/tasks/$taskId/activity did not return deleted task history."

    $projectActivity = Invoke-SmokeGet "/api/projects/$projectId/activity"
    Assert-Truthy ($projectActivity.Count -ge $deletedTaskActivity.Count) "GET /api/projects/$projectId/activity did not include task events."

    Invoke-SmokeDelete "/api/projects/$projectId" | Out-Null

    Write-Host "API smoke tests passed."
}
finally {
    if ($null -eq $previousDbPath) {
        Remove-Item Env:KANBANFLOW_DB_PATH -ErrorAction SilentlyContinue
    }
    else {
        $env:KANBANFLOW_DB_PATH = $previousDbPath
    }

    if ($appProcess -and -not $appProcess.HasExited) {
        Stop-Process -Id $appProcess.Id -Force
        $appProcess.WaitForExit(5000) | Out-Null
    }

    if (Test-Path $runDir) {
        for ($i = 0; $i -lt 5; $i++) {
            try {
                Remove-Item $runDir -Recurse -Force -ErrorAction Stop
                break
            }
            catch {
                if ($i -eq 4) { throw }
                Start-Sleep -Milliseconds 500
            }
        }
    }
}
