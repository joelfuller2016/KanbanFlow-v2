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
    Write-Host "GET $Path"
    Invoke-RestMethod -Method Get -Uri "$BaseUrl$Path" -TimeoutSec 5
}

function Invoke-SmokeJson([string]$Method, [string]$Path, $Body) {
    Write-Host "$($Method.ToUpperInvariant()) $Path"
    Invoke-RestMethod -Method $Method -Uri "$BaseUrl$Path" -ContentType "application/json" -Body ($Body | ConvertTo-Json -Depth 10) -TimeoutSec 5
}

function Invoke-SmokeDelete([string]$Path) {
    Write-Host "DELETE $Path"
    Invoke-WebRequest -Method Delete -Uri "$BaseUrl$Path" -TimeoutSec 5 -UseBasicParsing
}

function Assert-SmokePostFailure([string]$Path, $Body, [int[]]$ExpectedStatusCodes, [string]$Message) {
    Write-Host "POST $Path (expecting failure)"
    try {
        Invoke-WebRequest -Method Post -Uri "$BaseUrl$Path" -ContentType "application/json" -Body ($Body | ConvertTo-Json -Depth 10) -TimeoutSec 5 -UseBasicParsing -ErrorAction Stop | Out-Null
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

    $tasks = Invoke-SmokeGet "/api/projects/$projectId/tasks"
    Assert-Truthy ($tasks.Count -gt 0) "GET /api/projects/$projectId/tasks returned no seeded tasks."
    $taskId = $tasks[0].id

    Assert-Truthy ($tasks.Count -ge 2) "Seeded project must have at least two tasks for dependency smoke coverage."
    $blockedTaskId = $tasks[0].id
    $blockingTaskId = $tasks[1].id

    $dependency = Invoke-SmokeJson "Post" "/api/tasks/$blockedTaskId/dependencies" @{ dependsOnTaskItemId = $blockingTaskId; type = "DependsOn" }
    Assert-Truthy $dependency.id "POST /api/tasks/$blockedTaskId/dependencies did not return an id."
    Assert-Truthy ($dependency.taskItemId -eq $blockedTaskId) "Created dependency has unexpected taskItemId."
    Assert-Truthy ($dependency.dependsOnTaskItemId -eq $blockingTaskId) "Created dependency has unexpected dependsOnTaskItemId."

    $dependencies = Invoke-SmokeGet "/api/tasks/$blockedTaskId/dependencies"
    Assert-Truthy (@($dependencies | Where-Object { $_.id -eq $dependency.id }).Count -eq 1) "GET /api/tasks/$blockedTaskId/dependencies did not include the created dependency."

    $dependencyById = Invoke-SmokeGet "/api/task-dependencies/$($dependency.id)"
    Assert-Truthy ($dependencyById.id -eq $dependency.id) "GET /api/task-dependencies/$($dependency.id) did not return the created dependency."

    Assert-SmokePostFailure "/api/tasks/$blockingTaskId/dependencies" @{ dependsOnTaskItemId = $blockedTaskId; type = "Blocks" } @(422) "Blocks payload using dependsOnTaskItemId should be rejected instead of inverted."

    $smokeFeatureId = $project.features[0].id
    $blocksBlockedTask = Invoke-SmokeJson "Post" "/api/features/$smokeFeatureId/tasks" @{ name = "Blocks smoke blocked task" }
    $blocksBlockingTask = Invoke-SmokeJson "Post" "/api/features/$smokeFeatureId/tasks" @{ name = "Blocks smoke blocking task" }
    $blocksDependency = Invoke-SmokeJson "Post" "/api/tasks/$($blocksBlockingTask.id)/dependencies" @{ blockedTaskId = $blocksBlockedTask.id; type = "Blocks" }
    Assert-Truthy ($blocksDependency.taskItemId -eq $blocksBlockedTask.id) "Blocks dependency has unexpected blocked task id."
    Assert-Truthy ($blocksDependency.dependsOnTaskItemId -eq $blocksBlockingTask.id) "Blocks dependency has unexpected blocking task id."
    Assert-Truthy ($blocksDependency.type -eq "Blocks") "Blocks dependency did not preserve type."

    Assert-SmokePostFailure "/api/tasks/$blockedTaskId/dependencies" @{ dependsOnTaskItemId = $blockedTaskId } @(422) "Self-dependency validation failed."
    Assert-SmokePostFailure "/api/tasks/$blockedTaskId/dependencies" @{ dependsOnTaskItemId = $blockingTaskId } @(409) "Duplicate dependency validation failed."
    Assert-SmokePostFailure "/api/tasks/$blockingTaskId/dependencies" @{ dependsOnTaskItemId = $blockedTaskId } @(422) "Dependency cycle validation failed."

    $otherProject = Invoke-SmokeJson "Post" "/api/projects" @{ name = "Dependency smoke cross-project" }
    $otherFeature = Invoke-SmokeJson "Post" "/api/projects/$($otherProject.id)/features" @{ name = "Other feature" }
    $otherTask = Invoke-SmokeJson "Post" "/api/features/$($otherFeature.id)/tasks" @{ name = "Other task" }
    Assert-SmokePostFailure "/api/tasks/$blockedTaskId/dependencies" @{ dependsOnTaskItemId = $otherTask.id } @(422) "Cross-project dependency validation failed."

    $deleteBlocksResponse = Invoke-SmokeDelete "/api/task-dependencies/$($blocksDependency.id)"
    Assert-Truthy ($deleteBlocksResponse.StatusCode -eq 204) "DELETE /api/task-dependencies/$($blocksDependency.id) did not return 204."

    $deleteResponse = Invoke-SmokeDelete "/api/task-dependencies/$($dependency.id)"
    Assert-Truthy ($deleteResponse.StatusCode -eq 204) "DELETE /api/task-dependencies/$($dependency.id) did not return 204."
    $dependenciesAfterDelete = Invoke-SmokeGet "/api/tasks/$blockedTaskId/dependencies"
    Assert-Truthy (@($dependenciesAfterDelete | Where-Object { $_.id -eq $dependency.id }).Count -eq 0) "Deleted dependency still appears in GET /api/tasks/$blockedTaskId/dependencies."

    $task = $context.tasks | Where-Object { $_.id -eq $taskId } | Select-Object -First 1
    if (-not $task) { $task = $context.tasks | Select-Object -First 1 }
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

    Invoke-SmokeDelete "/api/acceptance-criteria/$($createdCriterion.id)" | Out-Null
    $criteriaAfterDelete = Invoke-SmokeGet "/api/tasks/$($task.id)/acceptance-criteria"
    Assert-Truthy (@($criteriaAfterDelete | Where-Object { $_.id -eq $createdCriterion.id }).Count -eq 0) "DELETE /api/acceptance-criteria/$($createdCriterion.id) did not remove the criterion."

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
        Start-Sleep -Milliseconds 250
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
