param(
    [string]$ProjectPath = "src/KanbanFlow/KanbanFlow.csproj",
    [string]$BaseUrl = "http://127.0.0.1:$(Get-Random -Minimum 5200 -Maximum 5999)",
    [int]$StartupTimeoutSeconds = 30
)

$ErrorActionPreference = "Stop"

function Resolve-RepoRoot {
    $scriptDir = Split-Path -Parent $PSCommandPath
    return (Resolve-Path (Join-Path $scriptDir "..")).Path
}

function Invoke-SmokeGet([string]$Path) {
    Write-Host "GET $Path"
    Invoke-RestMethod -Method Get -Uri "$BaseUrl$Path" -TimeoutSec 5
}

function Invoke-SmokeJson([string]$Method, [string]$Path, $Body) {
    Write-Host "$($Method.ToUpperInvariant()) $Path"
    Invoke-RestMethod -Method $Method -Uri "$BaseUrl$Path" -ContentType "application/json" -Body ($Body | ConvertTo-Json -Depth 10) -TimeoutSec 5
}

function Invoke-SmokeDelete([string]$Path) {
    Write-Host "DELETE $Path"
    Invoke-RestMethod -Method Delete -Uri "$BaseUrl$Path" -TimeoutSec 5 | Out-Null
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

    Start-Sleep -Milliseconds 250
    if ($appProcess.HasExited) {
        $stdout = if (Test-Path $stdoutPath) { Get-Content $stdoutPath -Raw } else { "" }
        $stderr = if (Test-Path $stderrPath) { Get-Content $stderrPath -Raw } else { "" }
        throw "KanbanFlow exited after startup check.`nSTDOUT:`n$stdout`nSTDERR:`n$stderr"
    }

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

    $task = $context.tasks | Select-Object -First 1
    Assert-Truthy $task.id "Project context did not include a task to test acceptance criteria."
    $otherTask = $context.tasks | Where-Object { $_.id -ne $task.id } | Select-Object -First 1
    Assert-Truthy $otherTask.id "Project context did not include a second task for scoped acceptance criteria check."

    $initialCriteria = Invoke-SmokeGet "/api/tasks/$($task.id)/acceptance-criteria"
    Assert-Truthy ($null -ne $initialCriteria) "GET /api/tasks/$($task.id)/acceptance-criteria returned null."

    $createdCriterion = Invoke-SmokeJson "Post" "/api/tasks/$($task.id)/acceptance-criteria" @{ text = "User can complete the workflow" }
    Assert-Truthy $createdCriterion.id "POST /api/tasks/$($task.id)/acceptance-criteria did not return an id."
    Assert-Truthy ($createdCriterion.taskItemId -eq $task.id) "Created acceptance criterion was not scoped to the requested task."
    Assert-Truthy (-not $createdCriterion.completed) "Created acceptance criterion should default to incomplete."

    $criteria = Invoke-SmokeGet "/api/tasks/$($task.id)/acceptance-criteria"
    Assert-Truthy (@($criteria | Where-Object { $_.id -eq $createdCriterion.id }).Count -eq 1) "Created acceptance criterion was not returned for its task."

    $otherCriteria = Invoke-SmokeGet "/api/tasks/$($otherTask.id)/acceptance-criteria"
    Assert-Truthy (@($otherCriteria | Where-Object { $_.id -eq $createdCriterion.id }).Count -eq 0) "Acceptance criterion leaked into another task."

    $updatedCriterion = Invoke-SmokeJson "Put" "/api/acceptance-criteria/$($createdCriterion.id)" @{ text = "User can complete the workflow successfully"; completed = $true; sortOrder = 7 }
    Assert-Truthy $updatedCriterion.completed "PUT /api/acceptance-criteria/$($createdCriterion.id) did not update completed."
    Assert-Truthy ($updatedCriterion.sortOrder -eq 7) "PUT /api/acceptance-criteria/$($createdCriterion.id) did not update sortOrder."

    $taskDetail = Invoke-SmokeGet "/api/tasks/$($task.id)"
    Assert-Truthy (@($taskDetail.acceptanceCriteria | Where-Object { $_.id -eq $createdCriterion.id }).Count -eq 1) "GET /api/tasks/$($task.id) did not include acceptance criteria."

    $contextWithCriteria = Invoke-SmokeGet "/api/projects/$projectId/context"
    $contextTask = $contextWithCriteria.tasks | Where-Object { $_.id -eq $task.id } | Select-Object -First 1
    Assert-Truthy (@($contextTask.acceptanceCriteria | Where-Object { $_.id -eq $createdCriterion.id }).Count -eq 1) "GET /api/projects/$projectId/context did not include acceptance criteria."

    Invoke-SmokeDelete "/api/acceptance-criteria/$($createdCriterion.id)"
    $criteriaAfterDelete = Invoke-SmokeGet "/api/tasks/$($task.id)/acceptance-criteria"
    Assert-Truthy (@($criteriaAfterDelete | Where-Object { $_.id -eq $createdCriterion.id }).Count -eq 0) "DELETE /api/acceptance-criteria/$($createdCriterion.id) did not remove the criterion."

    Write-Host "API smoke tests passed."
}
finally {
    if ($appProcess -and -not $appProcess.HasExited) {
        Stop-Process -Id $appProcess.Id -Force
        $appProcess.WaitForExit(5000) | Out-Null
        Start-Sleep -Milliseconds 250
    }

    if (Test-Path $runDir) {
        try {
            Remove-Item $runDir -Recurse -Force
        }
        catch {
            Start-Sleep -Milliseconds 500
            Remove-Item $runDir -Recurse -Force
        }
    }
}
