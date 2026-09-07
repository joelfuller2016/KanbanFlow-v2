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

function Assert-SmokeGetFailure(
    [ValidateNotNullOrEmpty()][string]$Path,
    [ValidateNotNullOrEmpty()][int[]]$ExpectedStatusCodes,
    [ValidateNotNullOrEmpty()][string]$Message,
    [ValidateNotNullOrEmpty()][string]$ExpectedMessage
) {
    Write-Host "GET $Path (expecting failure)"
    try {
        Invoke-WebRequest -Method Get -Uri "$BaseUrl$Path" -TimeoutSec 5 -UseBasicParsing -ErrorAction Stop | Out-Null
    }
    catch {
        $statusCode = [int]$_.Exception.Response.StatusCode
        if ($ExpectedStatusCodes -contains $statusCode) {
            if ($PSBoundParameters.ContainsKey("ExpectedMessage")) {
                $errorBody = $_.ErrorDetails.Message
                if ([string]::IsNullOrWhiteSpace($errorBody)) {
                    $response = $_.Exception.Response
                    if ($null -ne $response) {
                        if ($response.PSObject.Methods.Name -contains "GetResponseStream") {
                            $reader = New-Object System.IO.StreamReader($response.GetResponseStream())
                            try {
                                $errorBody = $reader.ReadToEnd()
                            }
                            finally {
                                $reader.Dispose()
                            }
                        }
                        elseif ($null -ne $response.Content) {
                            $errorBody = $response.Content.ReadAsStringAsync().GetAwaiter().GetResult()
                        }
                    }
                }

                try {
                    $actualMessage = ($errorBody | ConvertFrom-Json).message
                }
                catch {
                    throw "$Message Response body was not valid JSON: $errorBody"
                }
                if ($actualMessage -cne $ExpectedMessage) {
                    throw "$Message Expected error message '$ExpectedMessage', got '$actualMessage'."
                }
            }
            return
        }
        throw "$Message Expected HTTP $($ExpectedStatusCodes -join '/') but got $statusCode. $($_.Exception.Message)"
    }

    throw "$Message Expected HTTP failure but request succeeded."
}

function Assert-SmokePatchFailure([string]$Path, $Body, [int[]]$ExpectedStatusCodes, [string]$Message) {
    Write-Host "PATCH $Path (expecting failure)"
    try {
        Invoke-WebRequest -Method Patch -Uri "$BaseUrl$Path" -ContentType "application/json" -Body ($Body | ConvertTo-Json -Depth 10) -TimeoutSec 5 -UseBasicParsing -ErrorAction Stop | Out-Null
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

    # /api/admin/health must answer a question that CAN be wrong (#498). It previously returned a
    # hardcoded status with no database opened, so there was no state of the system in which it
    # said anything but "ok" and nothing here was worth asserting.
    #
    # This harness hands the app a database path that does not exist yet, so the app creates the
    # board it then serves -- and the CORRECT answer is "degraded", reason created-at-startup, even
    # though seeding leaves a project in it. Asserting that is what proves the endpoint is not a
    # constant; asserting db.path is what proves it opened the file this harness meant.
    $health = Invoke-SmokeGet "/api/admin/health"
    foreach ($field in @("status", "app", "startedAtUtc", "checkedAtUtc", "runtime", "openApiUrl")) {
        Assert-Truthy ($health.PSObject.Properties.Name -contains $field) "GET /api/admin/health lost the '$field' field."
    }
    Assert-Truthy ($null -ne $health.db) "GET /api/admin/health did not report a db object."
    Assert-Truthy ($health.db.status -eq "ok") "GET /api/admin/health could not read the database: $($health.db.error)"
    Assert-Truthy (
        [System.IO.Path]::GetFullPath([string]$health.db.path) -eq [System.IO.Path]::GetFullPath($dbPath)
    ) "GET /api/admin/health opened '$($health.db.path)', not the '$dbPath' this run pinned."
    Assert-Truthy ($health.db.projects -gt 0) "GET /api/admin/health reported an empty board after seeding."
    Assert-Truthy ($health.status -eq "degraded") "GET /api/admin/health said '$($health.status)' for a board this run created; it must not report ok."
    Assert-Truthy ($health.db.reasons -contains "created-at-startup") "GET /api/admin/health did not name created-at-startup as the reason."

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
    $handoff = Invoke-SmokeJson "Post" "/api/tasks/$taskId/comments" @{ text = $handoffText; author = "api-smoke"; type = "Handoff" }
    Assert-Truthy ($handoff.type -eq "Handoff") "POST /api/tasks/$taskId/comments did not preserve handoff type."

    $verification = Invoke-SmokeJson "Post" "/api/tasks/$taskId/comments" @{ text = "Verified in smoke"; author = "api-smoke"; type = "Verification" }
    Assert-Truthy ($verification.type -eq "Verification") "POST /api/tasks/$taskId/comments did not preserve verification type."

    $comments = Invoke-SmokeGet "/api/tasks/$taskId/comments"
    Assert-Truthy (@($comments | Where-Object { $_.id -eq $handoff.id -and $_.type -eq "Handoff" }).Count -eq 1) "GET /api/tasks/$taskId/comments did not return the handoff type."

    Assert-SmokePostFailure "/api/tasks/$taskId/comments" @{ text = "bad type"; type = "NotAType" } @(422) "Invalid comment type validation failed."

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

    $contextWithDependency = Invoke-SmokeGet "/api/projects/$projectId/context"
    $contextDependency = $contextWithDependency.dependencies | Where-Object { $_.id -eq $blocksDependency.id } | Select-Object -First 1
    Assert-Truthy $contextDependency.id "GET /api/projects/$projectId/context did not expose project-level dependency data."
    Assert-Truthy ($contextDependency.blockedTaskId -eq $blocksBlockedTask.id) "Project context dependency has unexpected blocked task id."
    Assert-Truthy ($contextDependency.blockingTaskId -eq $blocksBlockingTask.id) "Project context dependency has unexpected blocking task id."
    Assert-Truthy ($contextDependency.blockingTaskStatus -ne "Done") "Project context dependency should expose unfinished blocker status."
    $contextBlockedTask = $contextWithDependency.tasks | Where-Object { $_.id -eq $blocksBlockedTask.id } | Select-Object -First 1
    Assert-Truthy $contextBlockedTask.blockedByUnfinishedTasks "Project context task did not mark unfinished dependency blocker."
    Assert-Truthy (@($contextBlockedTask.unfinishedBlockers | Where-Object { $_.blockingTaskId -eq $blocksBlockingTask.id }).Count -eq 1) "Project context task did not expose unfinished blocker details."

    $nextTasksWithBlocked = Invoke-SmokeGet "/api/projects/$projectId/next-tasks?limit=25"
    Assert-Truthy (@($nextTasksWithBlocked.recommendations | Where-Object { $_.id -eq $blocksBlockedTask.id }).Count -eq 0) "Blocked task was recommended before its blocker was Done."
    Assert-Truthy (@($nextTasksWithBlocked.blockedTasks | Where-Object { $_.id -eq $blocksBlockedTask.id }).Count -eq 1) "Blocked task was not marked in next-task response."

    Invoke-SmokeJson "Patch" "/api/tasks/$($blocksBlockingTask.id)/status" @{ status = "Done" } | Out-Null
    $nextTasksAfterBlockerDone = Invoke-SmokeGet "/api/projects/$projectId/next-tasks?limit=25"
    Assert-Truthy (@($nextTasksAfterBlockerDone.recommendations | Where-Object { $_.id -eq $blocksBlockedTask.id }).Count -eq 1) "Blocked task was not recommended after its blocker was Done."

    Assert-SmokePostFailure "/api/tasks/$blockedTaskId/dependencies" @{ dependsOnTaskItemId = $blockedTaskId } @(422) "Self-dependency validation failed."
    Assert-SmokePostFailure "/api/tasks/$blockedTaskId/dependencies" @{ dependsOnTaskItemId = $blockingTaskId } @(409) "Duplicate dependency validation failed."
    Assert-SmokePostFailure "/api/tasks/$blockingTaskId/dependencies" @{ dependsOnTaskItemId = $blockedTaskId } @(422) "Dependency cycle validation failed."

    $otherProject = Invoke-SmokeJson "Post" "/api/projects" @{ name = "Dependency smoke cross-project" }
    $otherFeature = Invoke-SmokeJson "Post" "/api/projects/$($otherProject.id)/features" @{ name = "Other feature" }
    $otherTask = Invoke-SmokeJson "Post" "/api/features/$($otherFeature.id)/tasks" @{ name = "Other task" }
    Assert-SmokePostFailure "/api/tasks/$blockedTaskId/dependencies" @{ dependsOnTaskItemId = $otherTask.id } @(422) "Cross-project dependency validation failed."

    # Issue #83: PATCH /api/tasks/{id}/feature must reject reassignment when the target
    # feature lives in a different project. The same project pair from the dependency
    # cross-project case ($projectId vs $otherProject.id) gives us a known-good payload.
    Assert-SmokePatchFailure "/api/tasks/$blockedTaskId/feature" @{ featureId = $otherFeature.id } @(422) "Cross-project task reassignment was not rejected (issue #83)."

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

    # Status change goes through ITaskService (issue #88): the same code path runs whether
    # this comes from the API or a Blazor UI handler. The assertions below lock in the
    # service's contract — comment + activity event + actor propagation + snapshot completeness.
    Invoke-SmokeJson "Patch" "/api/tasks/$taskId/status" @{ status = "InProgress"; actor = "smoke-agent" } | Out-Null
    Invoke-SmokeJson "Post" "/api/tasks/$taskId/claim" @{ actor = "smoke-agent" } | Out-Null
    Invoke-SmokeJson "Post" "/api/tasks/$taskId/comments" @{ text = "Smoke test activity comment"; author = "smoke-agent" } | Out-Null

    # Audit-parity: the StatusChange comment is the user-visible audit signal.
    $taskComments = Invoke-SmokeGet "/api/tasks/$taskId/comments"
    $statusChangeComments = @($taskComments | Where-Object { $_.type -eq "StatusChange" })
    Assert-Truthy ($statusChangeComments.Count -ge 1) "PATCH /api/tasks/$taskId/status did not produce a StatusChange comment (issue #88 audit-trail contract)."

    $taskActivity = Invoke-SmokeGet "/api/tasks/$taskId/activity"
    Assert-Truthy ($taskActivity.Count -ge 3) "GET /api/tasks/$taskId/activity did not return expected workflow events."
    $eventTypes = @($taskActivity | ForEach-Object { $_.eventType })
    foreach ($eventType in @("TaskStatusChanged", "TaskClaimed", "TaskCommentAdded")) {
        Assert-Truthy ($eventTypes -contains $eventType) "Task activity is missing $eventType."
    }

    # Audit-parity: the TaskStatusChanged event must carry full snapshots and the actor.
    $statusEvent = $taskActivity | Where-Object { $_.eventType -eq "TaskStatusChanged" } | Select-Object -First 1
    Assert-Truthy ($null -ne $statusEvent) "Could not locate the TaskStatusChanged event for parity assertions."
    Assert-Truthy (-not [string]::IsNullOrWhiteSpace($statusEvent.beforeJson)) "TaskStatusChanged event has empty BeforeJson — service audit snapshot broken."
    Assert-Truthy (-not [string]::IsNullOrWhiteSpace($statusEvent.afterJson)) "TaskStatusChanged event has empty AfterJson — service audit snapshot broken."
    Assert-Truthy ($statusEvent.actor -eq "smoke-agent") "TaskStatusChanged event Actor was not propagated from request body (got '$($statusEvent.actor)')."

    Invoke-SmokeDelete "/api/tasks/$taskId" | Out-Null
    $deletedTaskActivity = Invoke-SmokeGet "/api/tasks/$taskId/activity"
    $deletedEventTypes = @($deletedTaskActivity | ForEach-Object { $_.eventType })
    Assert-Truthy ($deletedEventTypes -contains "TaskDeleted") "GET /api/tasks/$taskId/activity did not return deleted task history."

    $projectActivity = Invoke-SmokeGet "/api/projects/$projectId/activity"
    Assert-Truthy ($projectActivity.Count -ge $deletedTaskActivity.Count) "GET /api/projects/$projectId/activity did not include task events."

    Invoke-SmokeDelete "/api/projects/$projectId" | Out-Null

    # --- #203 regression: a partial PUT must preserve omitted fields; an explicit "" clears one ---
    $puProj = Invoke-SmokeJson "POST" "/api/projects" @{ name = "PU Test"; description = "keep-desc"; goals = "keep-goals"; readme = "keep-readme" }
    Invoke-SmokeJson "PUT" "/api/projects/$($puProj.id)" @{ name = "PU Renamed" } | Out-Null
    $puAfter = Invoke-SmokeGet "/api/projects/$($puProj.id)"
    Assert-Truthy ($puAfter.name -eq "PU Renamed") "#203: name-only PUT did not update name."
    Assert-Truthy ($puAfter.description -eq "keep-desc") "#203: name-only PUT wiped project description."
    Assert-Truthy ($puAfter.goals -eq "keep-goals") "#203: name-only PUT wiped project goals."
    Assert-Truthy ($puAfter.readme -eq "keep-readme") "#203: name-only PUT wiped project readme."
    Invoke-SmokeJson "PUT" "/api/projects/$($puProj.id)" @{ name = "PU Renamed"; description = "" } | Out-Null
    $puCleared = Invoke-SmokeGet "/api/projects/$($puProj.id)"
    Assert-Truthy ([string]::IsNullOrEmpty($puCleared.description)) "#203: explicit empty description did not clear."
    Assert-Truthy ($puCleared.goals -eq "keep-goals") "#203: clearing description also wiped goals."
    $puFeat = Invoke-SmokeJson "POST" "/api/projects/$($puProj.id)/features" @{ name = "PU Feature"; description = "feat-desc" }
    Invoke-SmokeJson "PUT" "/api/features/$($puFeat.id)" @{ name = "PU Feature Renamed" } | Out-Null
    $puFeatAfter = Invoke-SmokeGet "/api/features/$($puFeat.id)"
    Assert-Truthy ($puFeatAfter.description -eq "feat-desc") "#203: name-only feature PUT wiped description."
    Invoke-SmokeJson "PUT" "/api/features/$($puFeat.id)" @{ name = "PU Feature Renamed"; description = "" } | Out-Null
    $puFeatCleared = Invoke-SmokeGet "/api/features/$($puFeat.id)"
    Assert-Truthy ([string]::IsNullOrEmpty($puFeatCleared.description)) "#203: explicit empty feature description did not clear."
    Invoke-SmokeDelete "/api/projects/$($puProj.id)" | Out-Null

    # OpenAPI document generation exercises the Microsoft.OpenApi security pin (GHSA-v5pm-xwqc-g5wc,
    # pinned in KanbanFlow.csproj): the doc is only built on request, so probe it explicitly.
    $openapi = Invoke-SmokeGet "/openapi/v1.json"
    Assert-Truthy $openapi.paths "GET /openapi/v1.json did not return an OpenAPI document with paths."

    # --- #125: server-side progress at project and feature level -------------------
    # Self-contained: its own project, so it neither depends on nor disturbs the assertions
    # above. Shape: root -> child -> grandchild, so the recursive rollup is exercised over
    # the real endpoints rather than only in the calculator's unit tests.
    $pgProj = Invoke-SmokeJson "Post" "/api/projects" @{ name = "Progress smoke #125" }
    $pgRoot = Invoke-SmokeJson "Post" "/api/projects/$($pgProj.id)/features" @{ name = "PG root" }
    $pgChild = Invoke-SmokeJson "Post" "/api/projects/$($pgProj.id)/features" @{ name = "PG child"; parentFeatureId = $pgRoot.id }
    $pgGrand = Invoke-SmokeJson "Post" "/api/projects/$($pgProj.id)/features" @{ name = "PG grandchild"; parentFeatureId = $pgChild.id }
    # The blocker lives in the ROOT feature and the blocked task in the GRANDCHILD, so
    # `grandchild.progress.blocked == 1` also pins the dependency DIRECTION: were it inverted,
    # the root's task would be the blocked one and the grandchild would report 0.
    $pgRootTask = Invoke-SmokeJson "Post" "/api/features/$($pgRoot.id)/tasks" @{ name = "PG root blocking task" }
    Invoke-SmokeJson "Post" "/api/features/$($pgChild.id)/tasks" @{ name = "PG child task" } | Out-Null
    $pgGrandBlocked = Invoke-SmokeJson "Post" "/api/features/$($pgGrand.id)/tasks" @{ name = "PG grandchild blocked task" }
    Invoke-SmokeJson "Post" "/api/features/$($pgGrand.id)/tasks" @{ name = "PG grandchild plain task" } | Out-Null
    Invoke-SmokeJson "Post" "/api/tasks/$($pgGrandBlocked.id)/dependencies" @{ dependsOnTaskItemId = $pgRootTask.id; type = "DependsOn" } | Out-Null

    $pgProject = Invoke-SmokeGet "/api/projects/$($pgProj.id)"
    Assert-Truthy ($null -ne $pgProject.progress) "#125: GET /api/projects/{id} is missing the progress object."
    Assert-Truthy ($pgProject.progress.total -eq 4) "#125: GET /api/projects/{id} progress.total should count every task at any depth (got $($pgProject.progress.total))."
    Assert-Truthy ($pgProject.progress.completed -eq 0) "#125: progress.completed should be 0 before anything is Done."
    Assert-Truthy ($pgProject.progress.blocked -eq 1) "#125: progress.blocked should count the task with an open blocker (got $($pgProject.progress.blocked))."
    Assert-Truthy ($pgProject.progress.percentComplete -eq 0.0) "#125: progress.percentComplete should be 0.0 with nothing Done."
    # A JSON number round-trips as [long] or [double] depending on its value; the contract that
    # matters to an agent parsing it is that it is never a formatted string like "0%".
    Assert-Truthy ($pgProject.progress.percentComplete -isnot [string]) "#125: progress.percentComplete must be a number, not a formatted string."
    $pgRootRow = $pgProject.features | Where-Object { $_.id -eq $pgRoot.id } | Select-Object -First 1
    Assert-Truthy ($pgRootRow.progress.total -eq 4) "#125: GET /api/projects/{id} feature progress must include descendant features' tasks (got $($pgRootRow.progress.total))."

    $pgContext = Invoke-SmokeGet "/api/projects/$($pgProj.id)/context"
    Assert-Truthy ($null -ne $pgContext.progress) "#125: GET /api/projects/{id}/context is missing the progress object."
    Assert-Truthy ($pgContext.progress.total -eq $pgProject.progress.total) "#125: /context and /api/projects/{id} disagree on progress.total."
    Assert-Truthy ($pgContext.progress.blocked -eq 1) "#125: /context progress.blocked disagrees with the per-task isBlocked flags."
    # The two endpoints derive `blocked` by different routes (in-memory lookup vs SQL join), so
    # pin their agreement rather than only checking each against a literal.
    Assert-Truthy ($pgContext.progress.blocked -eq $pgProject.progress.blocked) "#125: /context and /api/projects/{id} disagree on progress.blocked."
    $pgNext = Invoke-SmokeGet "/api/projects/$($pgProj.id)/next-tasks?limit=25"
    Assert-Truthy ($pgNext.blockedTaskCount -eq $pgContext.progress.blocked) "#125: progress.blocked ($($pgContext.progress.blocked)) disagrees with next-tasks blockedTaskCount ($($pgNext.blockedTaskCount)) — one project, two numbers."
    # Goal 4: the project total is the sum of the TOP-LEVEL features' totals plus any task that
    # hangs off no top-level feature (none here), asserted rather than eyeballed.
    $pgTopLevelTotal = ($pgContext.features | ForEach-Object { $_.progress.total } | Measure-Object -Sum).Sum
    Assert-Truthy ($pgContext.progress.total -eq $pgTopLevelTotal) "#125: project progress.total ($($pgContext.progress.total)) != sum of top-level features[].progress.total ($pgTopLevelTotal)."
    $pgContextRoot = $pgContext.features | Where-Object { $_.id -eq $pgRoot.id } | Select-Object -First 1
    Assert-Truthy ($pgContextRoot.progress.total -eq 4) "#125: /context root feature progress.total should roll up the whole subtree."
    $pgContextChild = $pgContextRoot.children | Where-Object { $_.id -eq $pgChild.id } | Select-Object -First 1
    Assert-Truthy ($pgContextChild.progress.total -eq 3) "#125: /context child feature progress.total should be own 1 + grandchild 2 (got $($pgContextChild.progress.total))."
    $pgContextGrand = $pgContextChild.children | Where-Object { $_.id -eq $pgGrand.id } | Select-Object -First 1
    Assert-Truthy ($pgContextGrand.progress.total -eq 2) "#125: /context grandchild feature progress.total should be its own 2 tasks."
    Assert-Truthy ($pgContextGrand.progress.blocked -eq 1) "#125: /context grandchild progress.blocked should count its blocked task."

    # Moving a task to Done must raise percentComplete on BOTH endpoints, identically. The task
    # moved is the BLOCKED one and its blocker stays open, which also pins the rule that a
    # finished task is never counted as blocked (status change has no dependency gate, so this
    # state is reachable) — and that next-tasks agrees.
    Invoke-SmokeJson "Patch" "/api/tasks/$($pgGrandBlocked.id)/status" @{ status = "Done" } | Out-Null
    $pgContextAfter = Invoke-SmokeGet "/api/projects/$($pgProj.id)/context"
    $pgProjectAfter = Invoke-SmokeGet "/api/projects/$($pgProj.id)"
    $pgNextAfter = Invoke-SmokeGet "/api/projects/$($pgProj.id)/next-tasks?limit=25"
    Assert-Truthy ($pgContextAfter.progress.percentComplete -gt $pgContext.progress.percentComplete) "#125: percentComplete did not rise after a task moved to Done."
    Assert-Truthy ($pgContextAfter.progress.completed -eq 1) "#125: progress.completed did not count the newly Done task."
    Assert-Truthy ($pgContextAfter.progress.percentComplete -eq 25.0) "#125: 1 of 4 Done should be 25.0 (got $($pgContextAfter.progress.percentComplete))."
    Assert-Truthy ($pgProjectAfter.progress.percentComplete -eq $pgContextAfter.progress.percentComplete) "#125: /api/projects/{id} and /context report different percentComplete for the same project."
    Assert-Truthy ($pgContextAfter.progress.blocked -eq 0) "#125: a Done task with a still-open blocker must not be counted as blocked (got $($pgContextAfter.progress.blocked))."
    Assert-Truthy ($pgProjectAfter.progress.blocked -eq 0) "#125: /api/projects/{id} still counts a Done task as blocked."
    Assert-Truthy ($pgNextAfter.blockedTaskCount -eq $pgContextAfter.progress.blocked) "#125: progress.blocked and next-tasks blockedTaskCount disagree once the blocked task is Done."
    Invoke-SmokeDelete "/api/projects/$($pgProj.id)" | Out-Null

    # --- #224: mem0-style remember/recall over the memory store ---
    $memToken = "memtoken$([Guid]::NewGuid().ToString('N'))"
    $memNode = Invoke-SmokeJson "Post" "/api/memory" @{ content = "API smoke remembered fact $memToken"; type = "Semantic"; tags = "smoke"; source = "api-smoke"; confidence = 0.9; actor = "api-smoke" }
    Assert-Truthy $memNode.id "POST /api/memory did not return a node id."
    Assert-Truthy ($memNode.type -eq "Semantic") "POST /api/memory did not echo the type as a string."
    Assert-Truthy ($memNode.scope -eq "Agent") "POST /api/memory did not default scope to Agent."
    Assert-Truthy ($memNode.scopeKey -eq "knowledge-graph") "POST /api/memory did not default scopeKey to knowledge-graph."
    Assert-Truthy ($null -eq $memNode.validTo) "POST /api/memory returned a retired node."
    Assert-Truthy (-not $memNode.hasEmbedding) "POST /api/memory should start unembedded."

    # Remember writes its audit event through the service; a second node proves recall ranking has >1 candidate.
    Invoke-SmokeJson "Post" "/api/memory" @{ content = "API smoke unrelated fact $([Guid]::NewGuid().ToString('N'))" } | Out-Null

    Assert-SmokePostFailure "/api/memory" @{ content = "bad enum"; type = "NotAType" } @(422) "Invalid memory type validation failed."
    Assert-SmokePostFailure "/api/memory" @{ content = "bad enum"; scope = "NotAScope" } @(422) "Invalid memory scope validation failed."
    Assert-SmokePostFailure "/api/memory" @{ content = "   " } @(422) "Blank memory content validation failed."

    $recall = Invoke-SmokeGet "/api/memory/search?q=$memToken&k=5"
    Assert-Truthy ($recall.count -ge 1) "GET /api/memory/search did not recall the remembered fact."
    $recallHit = $recall.hits | Where-Object { $_.id -eq $memNode.id } | Select-Object -First 1
    Assert-Truthy $recallHit "GET /api/memory/search hits did not include the remembered node."
    Assert-Truthy ($null -ne $recallHit.fusedScore) "Recall hit is missing fusedScore."
    Assert-Truthy ($null -ne $recallHit.keywordRank) "Recall hit is missing its keyword rank (FTS dual-write broken?)."
    Assert-Truthy ($recall.hits[0].id -eq $memNode.id) "The keyword-matching node should outrank the unrelated one."

    Assert-SmokeGetFailure "/api/memory/search?q=" @(422) "Blank recall query validation failed."
    Assert-SmokeGetFailure "/api/memory/search?q=$memToken&k=0" @(422) "Non-positive recall k validation failed." "k (result count) must be greater than zero."
    Assert-SmokeGetFailure "/api/memory/search?q=$memToken&scope=NotAScope" @(422) "Invalid recall scope validation failed."
    Assert-SmokeGetFailure "/api/memory/search?q=$memToken&k=-5" @(422) "Negative recall k validation failed." "k (result count) must be greater than zero."

    # The UPPER clamp is load-bearing and must survive the #338 lower-clamp removal: k above the
    # ceiling is CAPPED (200, echoed k=100), not rejected. Dropping both bounds would make payload
    # size unbounded on this open endpoint.
    $recallCapped = Invoke-SmokeGet "/api/memory/search?q=$memToken&k=500"
    Assert-Truthy ($recallCapped.k -eq 100) "Recall k above the ceiling should be capped to 100, not rejected."

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
