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
    Invoke-RestMethod -Method Post -Uri "$BaseUrl$Path" -ContentType "application/json" -Body ($Body | ConvertTo-Json -Depth 8) -TimeoutSec 5 -ErrorAction Stop
}

function Invoke-SmokeDelete([string]$Path) {
    Invoke-WebRequest -Method Delete -Uri "$BaseUrl$Path" -TimeoutSec 5 -UseBasicParsing -ErrorAction Stop
}

function Assert-SmokePostFailure([string]$Path, $Body, [int[]]$ExpectedStatusCodes, [string]$Message) {
    try {
        Invoke-WebRequest -Method Post -Uri "$BaseUrl$Path" -ContentType "application/json" -Body ($Body | ConvertTo-Json -Depth 8) -TimeoutSec 5 -UseBasicParsing -ErrorAction Stop | Out-Null
    }
    catch {
        $statusCode = [int]$_.Exception.Response.StatusCode
        if ($ExpectedStatusCodes -contains $statusCode) { return }
        throw "$Message Expected HTTP $($ExpectedStatusCodes -join '/') but got $statusCode. $($_.Exception.Message)"
    }

    throw "$Message Expected HTTP failure but request succeeded."
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
    $appDllPath = Join-Path (Split-Path -Parent $resolvedProjectPath) "bin/Debug/net10.0/KanbanFlow.dll"
    $appProcess = Start-Process dotnet `
        -ArgumentList @($appDllPath, "--urls", $BaseUrl) `
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
    Assert-Truthy ($tasks.Count -ge 2) "Seeded project must have at least two tasks for dependency smoke coverage."
    $blockedTaskId = $tasks[0].id
    $blockingTaskId = $tasks[1].id

    $dependency = Invoke-SmokePost "/api/tasks/$blockedTaskId/dependencies" @{ dependsOnTaskItemId = $blockingTaskId; type = "DependsOn" }
    Assert-Truthy $dependency.id "POST /api/tasks/$blockedTaskId/dependencies did not return an id."
    Assert-Truthy ($dependency.taskItemId -eq $blockedTaskId) "Created dependency has unexpected taskItemId."
    Assert-Truthy ($dependency.dependsOnTaskItemId -eq $blockingTaskId) "Created dependency has unexpected dependsOnTaskItemId."

    $dependencies = Invoke-SmokeGet "/api/tasks/$blockedTaskId/dependencies"
    Assert-Truthy (@($dependencies | Where-Object { $_.id -eq $dependency.id }).Count -eq 1) "GET /api/tasks/$blockedTaskId/dependencies did not include the created dependency."

    Assert-SmokePostFailure "/api/tasks/$blockedTaskId/dependencies" @{ dependsOnTaskItemId = $blockedTaskId } @(422) "Self-dependency validation failed."
    Assert-SmokePostFailure "/api/tasks/$blockedTaskId/dependencies" @{ dependsOnTaskItemId = $blockingTaskId } @(409) "Duplicate dependency validation failed."
    Assert-SmokePostFailure "/api/tasks/$blockingTaskId/dependencies" @{ dependsOnTaskItemId = $blockedTaskId } @(422) "Dependency cycle validation failed."

    $otherProject = Invoke-SmokePost "/api/projects" @{ name = "Dependency smoke cross-project" }
    $otherFeature = Invoke-SmokePost "/api/projects/$($otherProject.id)/features" @{ name = "Other feature" }
    $otherTask = Invoke-SmokePost "/api/features/$($otherFeature.id)/tasks" @{ name = "Other task" }
    Assert-SmokePostFailure "/api/tasks/$blockedTaskId/dependencies" @{ dependsOnTaskItemId = $otherTask.id } @(422) "Cross-project dependency validation failed."

    $deleteResponse = Invoke-SmokeDelete "/api/task-dependencies/$($dependency.id)"
    Assert-Truthy ($deleteResponse.StatusCode -eq 204) "DELETE /api/task-dependencies/$($dependency.id) did not return 204."
    $dependenciesAfterDelete = Invoke-SmokeGet "/api/tasks/$blockedTaskId/dependencies"
    Assert-Truthy (@($dependenciesAfterDelete | Where-Object { $_.id -eq $dependency.id }).Count -eq 0) "Deleted dependency still appears in GET /api/tasks/$blockedTaskId/dependencies."

    Write-Host "API smoke tests passed."
}
finally {
    if ($appProcess -and -not $appProcess.HasExited) {
        Stop-Process -Id $appProcess.Id -Force
        $appProcess.WaitForExit(5000) | Out-Null
    }

    if (Test-Path $runDir) {
        Remove-Item $runDir -Recurse -Force
    }
}
