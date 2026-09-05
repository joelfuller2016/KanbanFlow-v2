#Requires -Version 7.0

param(
    [switch]$AllowGitHubWrites,
    [ValidateRange(1, 3600)]
    [int]$StartupTimeoutSeconds = 60,
    [switch]$SkipBuild
)

$ErrorActionPreference = "Stop"

# This is intentionally not configurable: the harness may leave several open issues behind and
# must never be pointed at a production repository by an accidental argument substitution.
$fixtureRepo = "joelfuller2016/test-mcp-github-repo-20250527"
$expectedRepoNodeId = "R_kgDOOxK3Dg"

if (-not $AllowGitHubWrites) {
    throw "Refusing real GitHub fixtures. Re-run with -AllowGitHubWrites after reading this script. It creates open issues and never closes or deletes them."
}

function Assert-Condition([bool]$Condition, [string]$Message) {
    if (-not $Condition) { throw $Message }
}

function Assert-WindowsHost(
    [bool]$RunningOnWindows = [System.Runtime.InteropServices.RuntimeInformation]::IsOSPlatform(
        [System.Runtime.InteropServices.OSPlatform]::Windows)
) {
    Assert-Condition $RunningOnWindows "The #336 real-GitHub verification harness is Windows-only because it loads the win-* native SQLite test artifact."
}

function ConvertTo-WindowsProcessArgument {
    param(
        [Parameter(Mandatory)]
        [AllowEmptyString()]
        [string]$Argument
    )

    if ($Argument.Length -gt 0 -and $Argument -notmatch '[\s"]') {
        return $Argument
    }

    # Start-Process joins ArgumentList before invoking CreateProcess on Windows. Apply the
    # CommandLineToArgvW/CRT escaping rules ourselves so quotes and trailing backslashes survive.
    $escaped = [System.Text.StringBuilder]::new()
    [void]$escaped.Append('"')
    $backslashCount = 0
    foreach ($character in $Argument.ToCharArray()) {
        if ($character -eq [char]92) {
            $backslashCount++
            continue
        }
        if ($character -eq [char]34) {
            [void]$escaped.Append(('\' * (($backslashCount * 2) + 1)))
            [void]$escaped.Append('"')
            $backslashCount = 0
            continue
        }
        if ($backslashCount -gt 0) {
            [void]$escaped.Append(('\' * $backslashCount))
            $backslashCount = 0
        }
        [void]$escaped.Append($character)
    }
    if ($backslashCount -gt 0) {
        [void]$escaped.Append(('\' * ($backslashCount * 2)))
    }
    [void]$escaped.Append('"')
    return $escaped.ToString()
}

function Join-WindowsProcessArguments {
    param(
        [Parameter(Mandatory)]
        [AllowEmptyCollection()]
        [object[]]$Arguments
    )

    $serialized = foreach ($argument in $Arguments) {
        if ($null -eq $argument) {
            throw "Native process arguments cannot contain null values."
        }
        ConvertTo-WindowsProcessArgument -Argument ([string]$argument)
    }
    return $serialized -join " "
}

function ConvertTo-GhCliVersion([string[]]$OutputLines) {
    $versionPattern = '^gh version (?<version>(?:0|[1-9]\d*)\.(?:0|[1-9]\d*)\.(?:0|[1-9]\d*))(?:\s|$)'
    foreach ($line in @($OutputLines)) {
        $match = [regex]::Match([string]$line, $versionPattern, [Text.RegularExpressions.RegexOptions]::CultureInvariant)
        if ($match.Success) {
            try {
                return [Version]::Parse($match.Groups["version"].Value)
            }
            catch {
                throw "Could not parse GitHub CLI version '$($match.Groups['version'].Value)': $($_.Exception.Message)"
            }
        }
    }

    $observed = (@($OutputLines | Select-Object -First 3) -join " | ").Trim()
    if ([string]::IsNullOrWhiteSpace($observed)) { $observed = "(no output)" }
    throw "Could not parse GitHub CLI version. Expected 'gh version X.Y.Z'; observed: $observed"
}

function Assert-GhCliSlurpSupport {
    $minimumVersion = [Version]::new(2, 48, 0)
    try {
        $versionOutput = @(& gh --version 2>&1 | ForEach-Object { $_.ToString() })
        $versionExitCode = $LASTEXITCODE
    }
    catch {
        throw "GitHub CLI >= $minimumVersion with 'gh api --paginate --slurp' support is required, but 'gh --version' failed: $($_.Exception.Message)"
    }
    if ($versionExitCode -ne 0) {
        throw "GitHub CLI >= $minimumVersion is required, but 'gh --version' exited $versionExitCode. $($versionOutput -join [Environment]::NewLine)"
    }

    $installedVersion = ConvertTo-GhCliVersion $versionOutput
    if ($installedVersion -lt $minimumVersion) {
        throw "GitHub CLI >= $minimumVersion is required for authoritative --paginate --slurp scans; found $installedVersion."
    }

    try {
        $apiHelpOutput = @(& gh api --help 2>&1 | ForEach-Object { $_.ToString() })
        $apiHelpExitCode = $LASTEXITCODE
    }
    catch {
        throw "GitHub CLI $installedVersion passed the minimum-version check, but 'gh api --help' failed: $($_.Exception.Message)"
    }
    if ($apiHelpExitCode -ne 0) {
        throw "GitHub CLI $installedVersion passed the minimum-version check, but 'gh api --help' exited $apiHelpExitCode."
    }
    if (($apiHelpOutput -join [Environment]::NewLine) -notmatch '(?m)^\s+--slurp(?:\s|$)') {
        throw "GitHub CLI $installedVersion does not advertise the required 'gh api --slurp' option; refusing authoritative pagination."
    }

    Write-Host "GitHub CLI preflight passed: $installedVersion (minimum $minimumVersion; api --slurp available)."
}

function Initialize-SqliteReadOnlyClient {
    Assert-WindowsHost
    $outputDirectory = Join-Path $script:repoRoot "src/KanbanFlow/bin/Debug/net10.0"
    $architecture = [System.Runtime.InteropServices.RuntimeInformation]::OSArchitecture.ToString().ToLowerInvariant()
    $runtimeId = switch ($architecture) {
        "arm64" { "win-arm64" }
        "x86" { "win-x86" }
        default { "win-x64" }
    }
    $nativeLibrary = Join-Path $outputDirectory "runtimes/$runtimeId/native/e_sqlite3.dll"
    foreach ($path in @(
        $nativeLibrary,
        (Join-Path $outputDirectory "SQLitePCLRaw.core.dll"),
        (Join-Path $outputDirectory "SQLitePCLRaw.provider.e_sqlite3.dll"),
        (Join-Path $outputDirectory "SQLitePCLRaw.batteries_v2.dll"),
        (Join-Path $outputDirectory "Microsoft.Data.Sqlite.dll")
    )) {
        Assert-Condition (Test-Path -LiteralPath $path) "SQLite read-only dependency is missing: $path. Run without -SkipBuild."
    }
    [void][System.Runtime.InteropServices.NativeLibrary]::Load($nativeLibrary)
    foreach ($assembly in @(
        "SQLitePCLRaw.core.dll",
        "SQLitePCLRaw.provider.e_sqlite3.dll",
        "SQLitePCLRaw.batteries_v2.dll",
        "Microsoft.Data.Sqlite.dll"
    )) {
        [void][System.Reflection.Assembly]::LoadFrom((Join-Path $outputDirectory $assembly))
    }
    [SQLitePCL.Batteries_V2]::Init()
}

function Get-SqliteRowCount([string]$TableName) {
    $allowedTables = @("ExternalWriteOperations", "ActivityEvents", "ExternalWorkItemLinks")
    Assert-Condition ($allowedTables -contains $TableName) "Refusing an unreviewed SQLite table name: $TableName"
    $connection = New-Object Microsoft.Data.Sqlite.SqliteConnection "Data Source=$script:dbPath;Mode=ReadOnly;Cache=Private"
    try {
        $connection.Open()
        $command = $connection.CreateCommand()
        $command.CommandText = "SELECT COUNT(*) FROM $TableName"
        return [long]$command.ExecuteScalar()
    }
    finally {
        if ($null -ne $connection) { $connection.Dispose() }
    }
}

function New-FreeLoopbackUrl {
    $listener = [System.Net.Sockets.TcpListener]::new([System.Net.IPAddress]::Loopback, 0)
    try {
        $listener.Start()
        return "http://127.0.0.1:$($listener.LocalEndpoint.Port)"
    }
    finally {
        $listener.Stop()
    }
}

function Invoke-KfJson([string]$Method, [string]$Path, $Body = $null) {
    $parameters = @{
        Method = $Method
        Uri = "$script:baseUrl$Path"
        TimeoutSec = 20
        ErrorAction = "Stop"
    }
    if ($null -ne $Body) {
        $parameters.ContentType = "application/json"
        $parameters.Body = $Body | ConvertTo-Json -Depth 20
    }
    return Invoke-RestMethod @parameters
}

function Get-KfItems([string]$Path) {
    # PowerShell 7 intentionally preserves a top-level JSON array from Invoke-RestMethod as one
    # pipeline object. Emit each member explicitly so filters/counts operate on records.
    $response = Invoke-KfJson "GET" $Path
    foreach ($item in $response) { Write-Output $item }
}

function Invoke-KfExpectedFailure([string]$Method, [string]$Path, $Body, [int]$ExpectedStatus) {
    try {
        Invoke-WebRequest `
            -Method $Method `
            -Uri "$script:baseUrl$Path" `
            -ContentType "application/json" `
            -Body ($Body | ConvertTo-Json -Depth 20) `
            -TimeoutSec 20 `
            -UseBasicParsing `
            -ErrorAction Stop | Out-Null
    }
    catch {
        $failure = $_
        $status = [int]$failure.Exception.Response.StatusCode
        $responseBody = [string]$failure.ErrorDetails.Message
        $responseExtractionErrors = [System.Collections.Generic.List[string]]::new()
        if ([string]::IsNullOrWhiteSpace($responseBody) -and $null -ne $failure.Exception.Response.Content) {
            try {
                $responseBody = $failure.Exception.Response.Content.ReadAsStringAsync().GetAwaiter().GetResult()
            }
            catch {
                $responseExtractionFailure = $_
                $responseExtractionErrors.Add(
                    "Response.Content.ReadAsStringAsync failed: $($responseExtractionFailure.Exception.GetType().FullName): $($responseExtractionFailure.Exception.Message)"
                )
            }
        }
        if ([string]::IsNullOrWhiteSpace($responseBody) -and $failure.Exception.Response.PSObject.Methods.Name -contains "GetResponseStream") {
            try {
                $reader = [System.IO.StreamReader]::new($failure.Exception.Response.GetResponseStream())
                try { $responseBody = $reader.ReadToEnd() } finally { $reader.Dispose() }
            }
            catch {
                $responseExtractionFailure = $_
                $responseExtractionErrors.Add(
                    "Response.GetResponseStream failed: $($responseExtractionFailure.Exception.GetType().FullName): $($responseExtractionFailure.Exception.Message)"
                )
            }
        }
        if ([string]::IsNullOrWhiteSpace($responseBody)) {
            if ($responseExtractionErrors.Count -eq 0) {
                $responseExtractionErrors.Add("ErrorDetails.Message and available response-body fallbacks returned no content.")
            }
            $responseBody = "[response body extraction failed: $($responseExtractionErrors -join ' | ')]"
        }
        if ($status -eq $ExpectedStatus) { return $responseBody }
        throw "Expected HTTP $ExpectedStatus from $Method $Path, got $status. $($_.Exception.Message)"
    }
    throw "Expected HTTP $ExpectedStatus from $Method $Path, but it succeeded."
}

function New-VerificationTask([string]$Name, [string]$Description = "") {
    return Invoke-KfJson "POST" "/api/features/$($script:feature.id)/tasks" @{
        name = $Name
        description = $Description
        status = "Todo"
        priority = "High"
        actor = $script:actor
    }
}

function Invoke-Portfolio([string[]]$Arguments, [bool]$ExpectSuccess = $true) {
    Push-Location $script:repoRoot
    try {
        $lines = @(& node $script:syncScript @Arguments 2>&1 | ForEach-Object { $_.ToString() })
        $exitCode = $LASTEXITCODE
    }
    finally {
        Pop-Location
    }
    $output = $lines -join [Environment]::NewLine
    if ($ExpectSuccess -and $exitCode -ne 0) {
        throw "portfolio-sync exited $exitCode.`n$output"
    }
    return [pscustomobject]@{ ExitCode = $exitCode; Output = $output }
}

function Start-BarrierPush(
    [int]$TaskId,
    [string]$Scenario,
    [ValidateSet("BeforePrepare", "AfterDispatch", "AfterManualResume", "AfterCreate")][string]$Phase = "AfterCreate",
    [string[]]$AdditionalArguments = @()
) {
    $signalPath = Join-Path $script:runDir "$Scenario.signal.json"
    $releasePath = Join-Path $script:runDir "$Scenario.release"
    $stdoutPath = Join-Path $script:runDir "$Scenario.node.out.log"
    $stderrPath = Join-Path $script:runDir "$Scenario.node.err.log"

    $savedBeforeSignal = $env:KANBANFLOW_336_BEFORE_PREPARE_SIGNAL
    $savedBeforeRelease = $env:KANBANFLOW_336_BEFORE_PREPARE_RELEASE
    $savedDispatchSignal = $env:KANBANFLOW_336_AFTER_DISPATCH_SIGNAL
    $savedDispatchRelease = $env:KANBANFLOW_336_AFTER_DISPATCH_RELEASE
    $savedManualResumeSignal = $env:KANBANFLOW_336_AFTER_MANUAL_RESUME_SIGNAL
    $savedManualResumeRelease = $env:KANBANFLOW_336_AFTER_MANUAL_RESUME_RELEASE
    $savedAfterSignal = $env:KANBANFLOW_336_AFTER_CREATE_SIGNAL
    $savedAfterRelease = $env:KANBANFLOW_336_AFTER_CREATE_RELEASE
    $savedTimeout = $env:KANBANFLOW_336_BARRIER_TIMEOUT_MS
    try {
        $env:KANBANFLOW_336_BEFORE_PREPARE_SIGNAL = $null
        $env:KANBANFLOW_336_BEFORE_PREPARE_RELEASE = $null
        $env:KANBANFLOW_336_AFTER_DISPATCH_SIGNAL = $null
        $env:KANBANFLOW_336_AFTER_DISPATCH_RELEASE = $null
        $env:KANBANFLOW_336_AFTER_MANUAL_RESUME_SIGNAL = $null
        $env:KANBANFLOW_336_AFTER_MANUAL_RESUME_RELEASE = $null
        $env:KANBANFLOW_336_AFTER_CREATE_SIGNAL = $null
        $env:KANBANFLOW_336_AFTER_CREATE_RELEASE = $null
        if ($Phase -eq "BeforePrepare") {
            $env:KANBANFLOW_336_BEFORE_PREPARE_SIGNAL = $signalPath
            $env:KANBANFLOW_336_BEFORE_PREPARE_RELEASE = $releasePath
        }
        elseif ($Phase -eq "AfterDispatch") {
            $env:KANBANFLOW_336_AFTER_DISPATCH_SIGNAL = $signalPath
            $env:KANBANFLOW_336_AFTER_DISPATCH_RELEASE = $releasePath
        }
        elseif ($Phase -eq "AfterManualResume") {
            $env:KANBANFLOW_336_AFTER_MANUAL_RESUME_SIGNAL = $signalPath
            $env:KANBANFLOW_336_AFTER_MANUAL_RESUME_RELEASE = $releasePath
        }
        else {
            $env:KANBANFLOW_336_AFTER_CREATE_SIGNAL = $signalPath
            $env:KANBANFLOW_336_AFTER_CREATE_RELEASE = $releasePath
        }
        $env:KANBANFLOW_336_BARRIER_TIMEOUT_MS = "600000"
        $nodeArguments = @(
            $script:syncScript,
            "push-issue", $fixtureRepo,
            "--allow-write", "--task", "$TaskId",
            "--actor", $script:actor
        )
        $nodeArguments += $AdditionalArguments
        $process = Start-Process `
            -FilePath "node" `
            -ArgumentList (Join-WindowsProcessArguments -Arguments $nodeArguments) `
            -WorkingDirectory $script:repoRoot `
            -RedirectStandardOutput $stdoutPath `
            -RedirectStandardError $stderrPath `
            -WindowStyle Hidden `
            -PassThru
    }
    finally {
        $env:KANBANFLOW_336_BEFORE_PREPARE_SIGNAL = $savedBeforeSignal
        $env:KANBANFLOW_336_BEFORE_PREPARE_RELEASE = $savedBeforeRelease
        $env:KANBANFLOW_336_AFTER_DISPATCH_SIGNAL = $savedDispatchSignal
        $env:KANBANFLOW_336_AFTER_DISPATCH_RELEASE = $savedDispatchRelease
        $env:KANBANFLOW_336_AFTER_MANUAL_RESUME_SIGNAL = $savedManualResumeSignal
        $env:KANBANFLOW_336_AFTER_MANUAL_RESUME_RELEASE = $savedManualResumeRelease
        $env:KANBANFLOW_336_AFTER_CREATE_SIGNAL = $savedAfterSignal
        $env:KANBANFLOW_336_AFTER_CREATE_RELEASE = $savedAfterRelease
        $env:KANBANFLOW_336_BARRIER_TIMEOUT_MS = $savedTimeout
    }

    $script:childProcesses.Add($process) | Out-Null
    return [pscustomobject]@{
        Process = $process
        SignalPath = $signalPath
        ReleasePath = $releasePath
        StdoutPath = $stdoutPath
        StderrPath = $stderrPath
        Phase = $Phase
    }
}

function Wait-BarrierSignal($Run, [int]$TimeoutSeconds = 120) {
    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    $lastObservation = "signal file was not present"
    do {
        if (Test-Path -LiteralPath $Run.SignalPath) {
            try {
                $rawSignal = Get-Content -LiteralPath $Run.SignalPath -Raw
                if ([string]::IsNullOrWhiteSpace($rawSignal)) {
                    $lastObservation = "signal file was empty"
                }
                else {
                    $signal = $rawSignal | ConvertFrom-Json -ErrorAction Stop
                    $requiredProperties = @("phase", "taskId")
                    if ($Run.Phase -ne "BeforePrepare") {
                        $requiredProperties += "operationId"
                    }
                    $missingProperties = @($requiredProperties | Where-Object {
                        $property = $signal.PSObject.Properties[$_]
                        $null -eq $property -or [string]::IsNullOrWhiteSpace([string]$property.Value)
                    })
                    if ($missingProperties.Count -eq 0) {
                        return $signal
                    }
                    $lastObservation = "signal JSON was missing required properties: $($missingProperties -join ', ')"
                }
            }
            catch {
                $lastObservation = "signal JSON was not ready: $($_.Exception.Message)"
            }
        }
        if ($Run.Process.HasExited) {
            $stdout = if (Test-Path -LiteralPath $Run.StdoutPath) { Get-Content -LiteralPath $Run.StdoutPath -Raw } else { "" }
            $stderr = if (Test-Path -LiteralPath $Run.StderrPath) { Get-Content -LiteralPath $Run.StderrPath -Raw } else { "" }
            throw "Node creator exited before barrier.`nSTDOUT:`n$stdout`nSTDERR:`n$stderr"
        }
        Start-Sleep -Milliseconds 100
    } while ((Get-Date) -lt $deadline)
    throw "Timed out waiting for barrier signal $($Run.SignalPath). Last observation: $lastObservation."
}

function New-BarrierRelease($Run) {
    New-Item -ItemType File -Path $Run.ReleasePath -ErrorAction Stop | Out-Null
}

function Stop-BarrierProcess($Run, [int]$TimeoutSeconds = 10) {
    if ($null -eq $Run) { throw "Stop-BarrierProcess: Run must not be null." }
    if ($TimeoutSeconds -le 0) { throw "Stop-BarrierProcess: TimeoutSeconds must be positive (got $TimeoutSeconds)." }
    $processId = $Run.Process.Id
    try {
        if (-not $Run.Process.HasExited) {
            Stop-Process -Id $processId -Force
        }
        if (-not $Run.Process.WaitForExit($TimeoutSeconds * 1000)) {
            throw "Killed Node creator (PID $processId) did not exit within $TimeoutSeconds second(s); refusing to assert against a live writer."
        }
    } catch {
        throw "Stop-BarrierProcess failed for PID $processId`: $_"
    }
}

function Wait-ProcessResult($Run, [int]$TimeoutSeconds = 120) {
    if (-not $Run.Process.WaitForExit($TimeoutSeconds * 1000)) {
        throw "Timed out waiting for Node creator $($Run.Process.Id)."
    }
    $Run.Process.WaitForExit()
    $stdout = if (Test-Path -LiteralPath $Run.StdoutPath) { Get-Content -LiteralPath $Run.StdoutPath -Raw } else { "" }
    $stderr = if (Test-Path -LiteralPath $Run.StderrPath) { Get-Content -LiteralPath $Run.StderrPath -Raw } else { "" }
    return [pscustomobject]@{
        ExitCode = $Run.Process.ExitCode
        Stdout = $stdout
        Stderr = $stderr
        Output = @($stdout, $stderr) -join [Environment]::NewLine
    }
}

function Wait-CheckedProcess($Run, [int]$TimeoutSeconds = 120) {
    $result = Wait-ProcessResult $Run $TimeoutSeconds
    if ($result.ExitCode -ne 0) {
        throw "Node creator exited $($result.ExitCode).`nSTDOUT:`n$($result.Stdout)`nSTDERR:`n$($result.Stderr)"
    }
    return $result
}

function ConvertTo-UtcInstant($Value, [string]$FieldName, [switch]$RequireUtcKind) {
    # PowerShell 7's JSON layer eagerly converts an ISO-8601 string to DateTime. Inspect Kind in
    # that case; converting it back with the default ToString() would discard the trailing Z and
    # recreate the very local-time bug this harness is meant to catch.
    if ($Value -is [DateTime]) {
        if ($RequireUtcKind) {
            Assert-Condition ($Value.Kind -eq [DateTimeKind]::Utc) "$FieldName was parsed without UTC kind: $($Value.ToString('O'))"
        }
        return [DateTimeOffset]::new($Value.ToUniversalTime())
    }
    if ($Value -is [DateTimeOffset]) {
        return $Value.ToUniversalTime()
    }

    $text = [string]$Value
    Assert-Condition ($text -match '(?:Z|[+-]\d{2}:\d{2})$') "$FieldName lacks a UTC/offset designator: $text"
    return [DateTimeOffset]::Parse($text).ToUniversalTime()
}

function Wait-OperationLeaseExpiry([string]$OperationId, [int]$TimeoutSeconds = 120) {
    $deadline = [DateTimeOffset]::UtcNow.AddSeconds($TimeoutSeconds)
    $lastLeaseExpiry = $null
    do {
        $operation = Invoke-KfJson "GET" "/api/external-write-operations/$OperationId"
        if (-not $operation.leaseExpiresAt) { return $operation }
        $expires = ConvertTo-UtcInstant $operation.leaseExpiresAt "API leaseExpiresAt" -RequireUtcKind
        $lastLeaseExpiry = $expires
        $remaining = $expires - [DateTimeOffset]::UtcNow
        if ($remaining.TotalMilliseconds -le 0) { return $operation }
        Assert-Condition ($remaining -le [TimeSpan]::FromMinutes(1)) "Scratch lease unexpectedly exceeds one minute: $($expires.ToString('O'))"
        Write-Host "  lease remains active for $([Math]::Ceiling($remaining.TotalMilliseconds)) ms"
        Start-Sleep -Milliseconds ([Math]::Min(250, [Math]::Max(25, [int]$remaining.TotalMilliseconds)))
    } while ([DateTimeOffset]::UtcNow -lt $deadline)

    $lastLeaseText = if ($null -eq $lastLeaseExpiry) { "not observed" } else { $lastLeaseExpiry.ToString('O') }
    throw "Timed out after $TimeoutSeconds second(s) waiting for operation $OperationId lease to expire. Last leaseExpiresAt: $lastLeaseText."
}

function Get-AllFixtureIssueRecords {
    $raw = & gh api --paginate --slurp "repos/$fixtureRepo/issues?state=all&per_page=100"
    if ($LASTEXITCODE -ne 0) { throw "Authoritative gh api pagination failed." }
    $pages = $raw | ConvertFrom-Json
    $records = @()
    foreach ($page in @($pages)) {
        foreach ($item in @($page)) {
            $records += $item
        }
    }
    return $records
}

function Get-AllFixtureIssues {
    return @(Get-AllFixtureIssueRecords | Where-Object {
        -not ($_.PSObject.Properties.Name -contains "pull_request")
    })
}

function Get-ExactMarkerIssue([string]$OperationId) {
    $marker = "<!-- kanbanflow-push:$($OperationId.ToLowerInvariant()) -->"
    $anyMarker = '<!--\s*kanbanflow-push:([0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12})\s*-->'
    return @(Get-AllFixtureIssues | Where-Object {
        $body = [string]$_.body
        $allOperationMarkers = @([regex]::Matches($body, $anyMarker))
        ([regex]::Matches($body, [regex]::Escape($marker))).Count -eq 1 -and
            $allOperationMarkers.Count -eq 1
    })
}

function Get-RawMarkerOccurrenceCount([string]$OperationId) {
    # Match the production negative-proof boundary: tolerate missing HTML-comment delimiters and
    # whitespace around the separator, and include PR records returned by the issues REST API.
    $rawTokenPattern = "kanbanflow-push\s*:\s*$([regex]::Escape($OperationId))"
    $count = 0
    foreach ($record in @(Get-AllFixtureIssueRecords)) {
        $count += ([regex]::Matches([string]$record.body, $rawTokenPattern, [Text.RegularExpressions.RegexOptions]::IgnoreCase)).Count
    }
    return $count
}

function Wait-RawMarkerOccurrenceCount([string]$OperationId, [int]$ExpectedCount, [int]$TimeoutSeconds = 30) {
    $deadline = [DateTimeOffset]::UtcNow.AddSeconds($TimeoutSeconds)
    do {
        $count = Get-RawMarkerOccurrenceCount $OperationId
        if ($count -eq $ExpectedCount) { return $count }
        if ($count -gt $ExpectedCount) {
            throw "Operation $OperationId exposed $count raw marker occurrences; expected $ExpectedCount."
        }
        Start-Sleep -Milliseconds 500
    } while ([DateTimeOffset]::UtcNow -lt $deadline)
    throw "Timed out waiting for $ExpectedCount raw marker occurrence(s) for operation $OperationId; last count was $count."
}

function Get-LinkForTask([int]$TaskId) {
    return @(Get-ProjectLinks |
        Where-Object { $_.taskId -eq $TaskId -and $_.provider -eq "GitHub" })
}

function Get-ProjectTasks {
    Get-KfItems "/api/projects/$($script:project.id)/tasks"
}

function Get-ProjectLinks {
    Get-KfItems "/api/projects/$($script:project.id)/external-work-item-links"
}

function Get-TaskIdFingerprint {
    return (@(Get-ProjectTasks | ForEach-Object { [int]$_.id } | Sort-Object) -join ",")
}

function Get-FixtureIssueFingerprint {
    return (@(Get-AllFixtureIssues | Sort-Object node_id | ForEach-Object {
        "$($_.node_id)|$($_.number)|$($_.updated_at)"
    }) -join "`n")
}

function Get-FixtureIssueForLink($Link) {
    $deadline = [DateTimeOffset]::UtcNow.AddSeconds(30)
    do {
        $issueMatches = @(Get-AllFixtureIssues | Where-Object {
            ($Link.externalNodeId -and $_.node_id -ceq $Link.externalNodeId) -or
            ($_.number -eq $Link.externalNumber)
        })
        if ($issueMatches.Count -eq 1) { break }
        if ($issueMatches.Count -gt 1) {
            throw "Expected one fixture issue for task link $($Link.id), found $($issueMatches.Count)."
        }
        Start-Sleep -Milliseconds 500
    } while ([DateTimeOffset]::UtcNow -lt $deadline)
    Assert-Condition ($issueMatches.Count -eq 1) "Timed out waiting for fixture issue for task link $($Link.id)."
    Assert-Condition ($issueMatches[0].node_id -ceq $Link.externalNodeId) "Fixture issue number matched but immutable node id did not."
    return $issueMatches[0]
}

function Get-OperationIdFromIssue($Issue) {
    $markerMatches = [regex]::Matches([string]$Issue.body, '<!--\s*kanbanflow-push:([0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12})\s*-->')
    Assert-Condition ($markerMatches.Count -eq 1) "Expected exactly one push marker in fixture issue $($Issue.html_url), found $($markerMatches.Count)."
    return $markerMatches[0].Groups[1].Value.ToLowerInvariant()
}

function Assert-OneExactMarker([string]$OperationId) {
    $deadline = [DateTimeOffset]::UtcNow.AddSeconds(30)
    do {
        $markerIssues = @(Get-ExactMarkerIssue $OperationId)
        $rawCount = Get-RawMarkerOccurrenceCount $OperationId
        if ($markerIssues.Count -eq 1 -and $rawCount -eq 1) { return $markerIssues[0] }
        if ($markerIssues.Count -gt 1 -or $rawCount -gt 1) {
            throw "Operation $OperationId has $($markerIssues.Count) exact issue(s) and $rawCount raw occurrence(s); expected one of each."
        }
        Start-Sleep -Milliseconds 500
    } while ([DateTimeOffset]::UtcNow -lt $deadline)
    throw "Timed out waiting for one authoritative issue for operation $OperationId; found $($markerIssues.Count) exact and $rawCount raw."
}

function New-NeedsReviewWithoutGitHubCreate([int]$TaskId, [string]$Scenario) {
    $issueFingerprintBefore = Get-FixtureIssueFingerprint
    $run = Start-BarrierPush $TaskId $Scenario "AfterDispatch"
    $signal = Wait-BarrierSignal $run
    Assert-Condition ($signal.phase -eq "after-dispatch-before-github-create") "$Scenario did not stop after dispatch and before GitHub create."
    Stop-BarrierProcess $run
    $operation = Invoke-KfJson "GET" "/api/external-write-operations/$($signal.operationId)"
    Assert-Condition ($operation.state -eq "Dispatched") "$Scenario expected Dispatched after kill, got $($operation.state)."
    Assert-Condition ((Get-RawMarkerOccurrenceCount $signal.operationId) -eq 0) "$Scenario unexpectedly reached GitHub before the post-dispatch kill."
    Wait-OperationLeaseExpiry $signal.operationId | Out-Null
    $normalRecovery = Invoke-Portfolio @(
        "push-issue", $fixtureRepo, "--allow-write", "--task", "$TaskId",
        "--resume", "$($signal.operationId)", "--actor", $script:actor
    ) $false
    Assert-Condition ($normalRecovery.ExitCode -ne 0) "$Scenario ordinary zero-match recovery unexpectedly authorized a create."
    Assert-Condition ($normalRecovery.Output -match "NeedsReview|zero|0 clean match") "$Scenario ordinary recovery did not report the fail-closed zero-match state.`n$($normalRecovery.Output)"
    $needsReview = Invoke-KfJson "GET" "/api/external-write-operations/$($signal.operationId)"
    Assert-Condition ($needsReview.state -eq "NeedsReview") "$Scenario ordinary recovery did not persist NeedsReview."
    Assert-Condition ($issueFingerprintBefore -ceq (Get-FixtureIssueFingerprint)) "$Scenario ordinary zero-match recovery changed GitHub."
    Assert-Condition ((Get-RawMarkerOccurrenceCount $signal.operationId) -eq 0) "$Scenario ordinary recovery created despite a zero-marker ambiguity."
    return $needsReview
}

function Invoke-AmbiguousRetryAuthorization([int]$TaskId, $Operation, [string]$Reason) {
    $issueFingerprintBefore = Get-FixtureIssueFingerprint
    $operationCountBefore = Get-SqliteRowCount "ExternalWriteOperations"
    $result = Invoke-Portfolio @(
        "push-issue", $fixtureRepo, "--allow-write", "--task", "$TaskId",
        "--resume", "$($Operation.operationId)",
        "--authorize-ambiguous-retry", "$($Operation.operationId)",
        "--actor", $script:actor, "--resolution-reason", $Reason
    )
    Assert-Condition ($result.Output -match "ManualRetryAuthorized|No GitHub create was attempted") "Manual retry authorization did not report its zero-write state.`n$($result.Output)"
    $authorized = Invoke-KfJson "GET" "/api/external-write-operations/$($Operation.operationId)"
    Assert-Condition ($authorized.state -eq "ManualRetryAuthorized") "Manual retry authorization returned state $($authorized.state)."
    Assert-Condition ($authorized.attemptCount -eq $Operation.attemptCount) "Authorization unexpectedly changed attemptCount."
    Assert-Condition ($operationCountBefore -eq (Get-SqliteRowCount "ExternalWriteOperations")) "Authorization created a second journal operation."
    Assert-Condition ($issueFingerprintBefore -ceq (Get-FixtureIssueFingerprint)) "Authorization wrote to GitHub despite its mandatory exit."
    Assert-Condition ((Get-RawMarkerOccurrenceCount $Operation.operationId) -eq 0) "Authorization did not preserve the exhaustive zero-occurrence result."
    return $authorized
}

Assert-WindowsHost

$script:repoRoot = (Resolve-Path (Join-Path (Split-Path -Parent $PSCommandPath) "..")).Path
$script:syncScript = (Resolve-Path (Join-Path $script:repoRoot "scripts/portfolio-sync.mjs")).Path
$projectPath = (Resolve-Path (Join-Path $script:repoRoot "src/KanbanFlow/KanbanFlow.csproj")).Path
$testProjectPath = (Resolve-Path (Join-Path $script:repoRoot "tests/KanbanFlow.Tests/KanbanFlow.Tests.csproj")).Path
$script:baseUrl = New-FreeLoopbackUrl
$runId = [Guid]::NewGuid().ToString("N")
$script:actor = "verify-336-$runId"
$script:runDir = Join-Path ([System.IO.Path]::GetTempPath()) "kanbanflow-336-$runId"
New-Item -ItemType Directory -Path $script:runDir | Out-Null
$appStdout = Join-Path $script:runDir "kanbanflow.out.log"
$appStderr = Join-Path $script:runDir "kanbanflow.err.log"
$dbPath = Join-Path $script:runDir "kanbanflow.db"
$script:dbPath = $dbPath
$script:childProcesses = [System.Collections.Generic.List[System.Diagnostics.Process]]::new()
$appProcess = $null
$results = [ordered]@{}
$fixtureUrls = [System.Collections.Generic.List[string]]::new()
$runCompletedSuccessfully = $false

$savedDbPath = $env:KANBANFLOW_DB_PATH
$savedApiUrl = $env:KANBANFLOW_API_URL
$savedLeaseSeconds = $env:KANBANFLOW_EXTERNAL_WRITE_LEASE_SECONDS

try {
    Write-Host "#336 real-GitHub verification run $runId"
    Write-Host "Artifacts will be preserved at $script:runDir"

    Assert-GhCliSlurpSupport
    & gh auth status | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "gh auth status failed." }
    $repoInfo = (& gh api "repos/$fixtureRepo") | ConvertFrom-Json
    if ($LASTEXITCODE -ne 0) { throw "Could not read fixture repository." }
    Assert-Condition ($repoInfo.node_id -ceq $expectedRepoNodeId) "Fixture repository node id mismatch; refusing writes."
    Assert-Condition ([bool]$repoInfo.private) "Fixture repository is no longer private; refusing writes."
    Assert-Condition (-not [bool]$repoInfo.archived) "Fixture repository is archived; refusing writes."
    Assert-Condition ([bool]$repoInfo.has_issues) "Fixture repository has issues disabled; refusing writes."
    Assert-Condition ([bool]$repoInfo.permissions.admin) "Authenticated viewer lacks admin permission on the fixture repository."

    # Exercise PowerShell's eager ISO-8601 conversion before any fixture mutation. A Z value must
    # retain UTC kind; an explicit-offset value may be materialized as Local but must round-trip to
    # the same UTC instant through the audit comparison helper.
    $instantProbe = '{"z":"2026-08-31T01:00:00Z","offset":"2026-08-31T01:00:00+00:00"}' | ConvertFrom-Json
    $probeUtc = ConvertTo-UtcInstant $instantProbe.z "timestamp parser Z probe" -RequireUtcKind
    $probeOffset = ConvertTo-UtcInstant $instantProbe.offset "timestamp parser offset probe"
    Assert-Condition ($probeUtc -eq $probeOffset) "Timestamp parser probes resolved to different instants."

    # S5 deliberately runs after the retained GitHub fixture scenarios and uses --no-restore.
    # Restore its exact project during preflight so a missing test-only package fails before any
    # scratch-board or GitHub fixture mutation, including when the caller elects to skip the build.
    & dotnet restore $testProjectPath --nologo
    if ($LASTEXITCODE -ne 0) { throw "dotnet restore for S5 focused tests failed." }

    if (-not $SkipBuild) {
        & dotnet build $projectPath --nologo
        if ($LASTEXITCODE -ne 0) { throw "dotnet build failed." }
    }

    # Fifteen seconds leaves headroom for the required post-Resume exhaustive GitHub scan. The
    # production default remains five minutes; intentional crash cases poll the recorded lease
    # expiry exactly and the harness never edits the SQLite journal.
    $env:KANBANFLOW_DB_PATH = $dbPath
    $env:KANBANFLOW_API_URL = $script:baseUrl
    $env:KANBANFLOW_EXTERNAL_WRITE_LEASE_SECONDS = "15"
    $appProcess = Start-Process `
        -FilePath "dotnet" `
        -ArgumentList (Join-WindowsProcessArguments -Arguments @("run", "--no-build", "--no-launch-profile", "--project", $projectPath, "--urls", $script:baseUrl)) `
        -WorkingDirectory $script:runDir `
        -RedirectStandardOutput $appStdout `
        -RedirectStandardError $appStderr `
        -WindowStyle Hidden `
        -PassThru

    $deadline = (Get-Date).AddSeconds($StartupTimeoutSeconds)
    $lastReadinessFailure = $null
    while ($true) {
        if ($appProcess.HasExited) {
            throw "KanbanFlow exited during startup. See $appStdout and $appStderr."
        }
        try {
            Invoke-KfJson "GET" "/api/projects" | Out-Null
            break
        }
        catch {
            $lastReadinessFailure = $_
        }
        if ((Get-Date) -ge $deadline) {
            $lastReadinessDetail = if ($null -eq $lastReadinessFailure) {
                "no readiness exception was captured"
            }
            else {
                "$($lastReadinessFailure.Exception.GetType().FullName): $($lastReadinessFailure.Exception.Message)"
            }
            throw "KanbanFlow did not become ready at $script:baseUrl within $StartupTimeoutSeconds second(s). Last readiness error: $lastReadinessDetail. Logs: stdout=$appStdout; stderr=$appStderr."
        }
        Start-Sleep -Milliseconds 250
    }
    Initialize-SqliteReadOnlyClient

    $script:project = Invoke-KfJson "POST" "/api/projects" @{
        name = "KF336 verification $runId"
        description = "<!-- github-node: $expectedRepoNodeId -->`n<!-- github: $fixtureRepo -->`n#336 disposable verification board"
    }
    $script:feature = Invoke-KfJson "POST" "/api/projects/$($script:project.id)/features" @{
        name = "Verification $runId"
    }

    # Establish a full baseline projection before opening the race windows. This makes a later
    # unchanged task-id assertion meaningful: every pre-existing fixture issue already had its
    # one opportunity to project into the scratch board.
    Invoke-Portfolio @("sync", "--repos", $fixtureRepo, "--no-comments", "--actor", $script:actor) | Out-Null

    Write-Host "Preflight: command-level --dry-run performs zero board or GitHub writes"
    $previewTask = New-VerificationTask "[KF336 $runId] dry-run zero-write proof" "This task must remain unlinked."
    $previewTaskBefore = Invoke-KfJson "GET" "/api/tasks/$($previewTask.id)"
    $previewTaskActivityBefore = @(Get-KfItems "/api/tasks/$($previewTask.id)/activity").Count
    $previewTaskIdsBefore = Get-TaskIdFingerprint
    $previewIssueFingerprintBefore = Get-FixtureIssueFingerprint
    $previewOperationCountBefore = Get-SqliteRowCount "ExternalWriteOperations"
    $previewActivityCountBefore = Get-SqliteRowCount "ActivityEvents"
    $previewLinkCountBefore = Get-SqliteRowCount "ExternalWorkItemLinks"
    $preview = Invoke-Portfolio @(
        "push-issue", $fixtureRepo, "--task", "$($previewTask.id)", "--dry-run", "--actor", $script:actor
    )
    Assert-Condition ($preview.Output -match "PREVIEW|zero writes|would create GitHub issue") "Dry-run output did not identify itself as a preview.`n$($preview.Output)"
    $previewTaskAfter = Invoke-KfJson "GET" "/api/tasks/$($previewTask.id)"
    Assert-Condition ($previewTaskIdsBefore -ceq (Get-TaskIdFingerprint)) "Dry-run changed the project task set."
    Assert-Condition ($previewTaskBefore.updatedAt -ceq $previewTaskAfter.updatedAt) "Dry-run mutated the candidate task."
    Assert-Condition ($previewTaskActivityBefore -eq @(Get-KfItems "/api/tasks/$($previewTask.id)/activity").Count) "Dry-run wrote task activity."
    Assert-Condition ((Get-LinkForTask $previewTask.id).Count -eq 0) "Dry-run registered or quarantined a link."
    Assert-Condition ($previewOperationCountBefore -eq (Get-SqliteRowCount "ExternalWriteOperations")) "Dry-run persisted an external write operation."
    Assert-Condition ($previewActivityCountBefore -eq (Get-SqliteRowCount "ActivityEvents")) "Dry-run persisted board activity."
    Assert-Condition ($previewLinkCountBefore -eq (Get-SqliteRowCount "ExternalWorkItemLinks")) "Dry-run changed the external link row count."
    Assert-Condition ($previewIssueFingerprintBefore -ceq (Get-FixtureIssueFingerprint)) "Dry-run created or updated a GitHub issue."
    $results.DryRun = "PASS: command preview left operations, activity, links, tasks, and GitHub issues byte-for-byte/count unchanged"

    Write-Host "Manual retry gate: post-dispatch kill, audited zero scans, and separate resume"
    $retryTask = New-VerificationTask "[KF336 $runId] manual retry after zero-marker ambiguity"
    $retryNeedsReview = New-NeedsReviewWithoutGitHubCreate $retryTask.id "manual-retry"
    $retryAuthorized = Invoke-AmbiguousRetryAuthorization `
        $retryTask.id `
        $retryNeedsReview `
        "Harness $runId reviewed the complete zero-marker scan; authorize one separate retry."
    $retryResume = Invoke-Portfolio @(
        "push-issue", $fixtureRepo, "--allow-write", "--task", "$($retryTask.id)",
        "--resume", "$($retryAuthorized.operationId)", "--actor", $script:actor
    )
    Assert-Condition ($retryResume.Output -match "leased attempt .* exhaustive scan found zero|created https://github.com") "Manual retry resume did not show the Resume-then-scan/create sequence.`n$($retryResume.Output)"
    $retryCompleted = Invoke-KfJson "GET" "/api/external-write-operations/$($retryAuthorized.operationId)"
    Assert-Condition ($retryCompleted.state -eq "Completed") "Separately resumed manual retry did not complete."
    $retryDispatchEvents = @(Get-KfItems "/api/tasks/$($retryTask.id)/activity?limit=500" | Where-Object {
        $_.eventType -eq "ExternalWriteDispatched" -and $_.afterJson -match $retryAuthorized.operationId
    })
    $manualDispatchEvidence = @($retryDispatchEvents | ForEach-Object { $_.afterJson | ConvertFrom-Json } | Where-Object {
        $_.manualRetryEvidenceVerified -eq $true
    })
    Assert-Condition ($manualDispatchEvidence.Count -eq 1) "Manual retry dispatch did not emit exactly one evidence-bearing activity event."
    Assert-Condition ($manualDispatchEvidence[0].manualRetryConfirmationOperationId -eq $retryAuthorized.operationId) "Dispatch audit confirmation UUID mismatch."
    Assert-Condition ($manualDispatchEvidence[0].manualRetryExpectedAttemptCount -eq $retryCompleted.attemptCount) "Dispatch audit expected-attempt count does not match the completed operation attempt."
    Assert-Condition ($manualDispatchEvidence[0].manualRetryObservedMatchCount -eq 0 -and $manualDispatchEvidence[0].manualRetryObservedRawTargetOccurrenceCount -eq 0) "Dispatch audit did not retain both zero counts."
    Assert-Condition (-not [string]::IsNullOrWhiteSpace([string]$manualDispatchEvidence[0].manualRetryScanCompletedAt)) "Dispatch audit omitted second-scan completion time."
    $manualScanCompletedAt = ConvertTo-UtcInstant $manualDispatchEvidence[0].manualRetryScanCompletedAt "dispatch audit manualRetryScanCompletedAt"
    $manualLastAttemptAt = ConvertTo-UtcInstant $retryCompleted.lastAttemptAt "operation lastAttemptAt" -RequireUtcKind
    Assert-Condition ($manualScanCompletedAt -ge $manualLastAttemptAt) "Dispatch audit scan time predates the leased attempt's LastAttemptAt."
    $retryIssue = Assert-OneExactMarker $retryAuthorized.operationId
    Assert-Condition ((Get-LinkForTask $retryTask.id).Count -eq 1) "Manual retry did not finalize exactly one link."
    $fixtureUrls.Add($retryIssue.html_url)

    Write-Host "Manual retry malformed-token denial subcase: raw occurrence blocks authorization"
    $malformedTask = New-VerificationTask "[KF336 $runId] malformed raw marker denial"
    $malformedNeedsReview = New-NeedsReviewWithoutGitHubCreate $malformedTask.id "manual-retry-malformed"
    $malformedBody = @"
Retained #336 negative-proof fixture. This is intentionally not a valid protocol comment.

kanbanflow-push : $($malformedNeedsReview.operationId)
"@
    $malformedUrlLines = @($malformedBody | & gh issue create --repo $fixtureRepo --title "[KF336 $runId] malformed raw marker fixture" --body-file - 2>&1 | ForEach-Object { $_.ToString() })
    if ($LASTEXITCODE -ne 0) { throw "Malformed-marker fixture create failed.`n$($malformedUrlLines -join [Environment]::NewLine)" }
    $malformedUrl = [string]($malformedUrlLines | Where-Object { $_ -match '^https://github\.com/.+/issues/\d+$' } | Select-Object -First 1)
    $malformedUrl = $malformedUrl.Trim()
    Assert-Condition (-not [string]::IsNullOrWhiteSpace($malformedUrl)) "Malformed-marker fixture create returned no issue URL."
    $fixtureUrls.Add($malformedUrl)
    Wait-RawMarkerOccurrenceCount $malformedNeedsReview.operationId 1 | Out-Null
    Assert-Condition (@(Get-ExactMarkerIssue $malformedNeedsReview.operationId).Count -eq 0) "Malformed fixture unexpectedly became an exact marker."
    $malformedFingerprint = Get-FixtureIssueFingerprint
    $malformedDenial = Invoke-Portfolio @(
        "push-issue", $fixtureRepo, "--allow-write", "--task", "$($malformedTask.id)",
        "--resume", "$($malformedNeedsReview.operationId)",
        "--authorize-ambiguous-retry", "$($malformedNeedsReview.operationId)",
        "--actor", $script:actor, "--resolution-reason", "Harness must reject malformed raw target evidence."
    ) $false
    Assert-Condition ($malformedDenial.ExitCode -ne 0) "Malformed raw target token unexpectedly authorized a retry."
    Assert-Condition ($malformedDenial.Output -match "ambiguous|raw target occurrence") "Malformed raw-token denial did not report the stronger negative-proof boundary.`n$($malformedDenial.Output)"
    Assert-Condition ($malformedFingerprint -ceq (Get-FixtureIssueFingerprint)) "Malformed-token authorization attempt mutated GitHub."
    $malformedAfter = Invoke-KfJson "GET" "/api/external-write-operations/$($malformedNeedsReview.operationId)"
    Assert-Condition ($malformedAfter.state -eq "NeedsReview") "Malformed-token denial changed operation state from NeedsReview."
    # A PR-contained token is covered by Node tests/analyzeMarkerRecords. Creating one here would
    # require an unrelated retained branch/commit mutation in addition to the approved issue fixtures.

    Write-Host "Manual retry late-marker subcase: second scan finalizes instead of duplicating"
    $lateTask = New-VerificationTask "[KF336 $runId] late marker after retry authorization"
    $lateNeedsReview = New-NeedsReviewWithoutGitHubCreate $lateTask.id "manual-retry-late"
    $lateAuthorized = Invoke-AmbiguousRetryAuthorization `
        $lateTask.id `
        $lateNeedsReview `
        "Harness $runId will crash after the authorized resume plus zero scan and verify the next invocation claims a new attempt and rescans."
    $lateResumeRun = Start-BarrierPush `
        -TaskId $lateTask.id `
        -Scenario "manual-retry-late-resumed" `
        -Phase "AfterManualResume" `
        -AdditionalArguments @("--resume", "$($lateAuthorized.operationId)")
    $lateResumeSignal = Wait-BarrierSignal $lateResumeRun
    Assert-Condition ($lateResumeSignal.phase -eq "after-manual-retry-resume-and-scan-before-dispatch") "Late-marker subcase did not stop after authorized Resume plus exhaustive zero scan and before dispatch."
    Stop-BarrierProcess $lateResumeRun
    $lateAfterResume = Invoke-KfJson "GET" "/api/external-write-operations/$($lateAuthorized.operationId)"
    Assert-Condition ($lateAfterResume.state -eq "ManualRetryAuthorized") "Authorized Resume should preserve ManualRetryAuthorized until evidence-bound dispatch; got $($lateAfterResume.state)."
    Assert-Condition ($lateAfterResume.attemptCount -gt $lateAuthorized.attemptCount) "Authorized Resume did not advance the attempt count."
    Assert-Condition ((Get-RawMarkerOccurrenceCount $lateAuthorized.operationId) -eq 0) "Late-marker subcase reached GitHub before dispatch."
    $lateOperation = Invoke-KfJson "GET" "/api/external-write-operations/$($lateAuthorized.operationId)"
    $lateTitle = "[KF336 $runId] delayed remote visibility fixture"
    $lateUrlLines = @($lateOperation.body | & gh issue create --repo $fixtureRepo --title $lateTitle --body-file - 2>&1 | ForEach-Object { $_.ToString() })
    if ($LASTEXITCODE -ne 0) { throw "Late-marker fixture create failed.`n$($lateUrlLines -join [Environment]::NewLine)" }
    $lateUrl = [string]($lateUrlLines | Where-Object { $_ -match '^https://github\.com/.+/issues/\d+$' } | Select-Object -First 1)
    $lateUrl = $lateUrl.Trim()
    Assert-Condition (-not [string]::IsNullOrWhiteSpace($lateUrl)) "Late-marker fixture create returned no issue URL."
    Wait-RawMarkerOccurrenceCount $lateAuthorized.operationId 1 | Out-Null
    Wait-OperationLeaseExpiry $lateAuthorized.operationId | Out-Null
    $lateFingerprintBeforeResume = Get-FixtureIssueFingerprint
    $lateResume = Invoke-Portfolio @(
        "push-issue", $fixtureRepo, "--allow-write", "--task", "$($lateTask.id)",
        "--resume", "$($lateAuthorized.operationId)", "--actor", $script:actor
    )
    Assert-Condition ($lateResume.Output -match "post-Resume exhaustive scan found late issue|finalized without creating") "Post-Resume scan did not report late-marker finalization.`n$($lateResume.Output)"
    Assert-Condition ($lateFingerprintBeforeResume -ceq (Get-FixtureIssueFingerprint)) "Late-marker resume wrote a duplicate or edited the fixture issue."
    Assert-Condition ((Get-RawMarkerOccurrenceCount $lateAuthorized.operationId) -eq 1) "Late-marker resume left other than one raw occurrence."
    $lateLink = @(Get-LinkForTask $lateTask.id | Where-Object { $_.state -eq "Active" })
    Assert-Condition ($lateLink.Count -eq 1 -and $lateLink[0].url -ceq $lateUrl) "Late-marker resume did not finalize the injected issue."
    $fixtureUrls.Add($lateUrl)
    $results.ManualRetry = "PASS: post-dispatch kill stayed NeedsReview; malformed raw token denied authorization; post-Resume evidence-bound create ran once; crash-after-scan then late marker was rescanned/finalized without duplicate"

    Write-Host "Scenario 1/6: crash after GitHub create, before finalize"
    $s1Task = New-VerificationTask "[KF336 $runId] S1 crash recovery" "Fixture remains open for audit."
    $s1Run = Start-BarrierPush $s1Task.id "s1"
    $s1Signal = Wait-BarrierSignal $s1Run
    Assert-Condition ($s1Signal.phase -eq "after-github-create-before-finalize") "S1 barrier did not land at the requested phase."
    Stop-BarrierProcess $s1Run
    $s1Issue = Assert-OneExactMarker $s1Signal.operationId
    $fixtureUrls.Add($s1Issue.html_url)
    $s1Before = Invoke-KfJson "GET" "/api/external-write-operations/$($s1Signal.operationId)"
    Assert-Condition ($s1Before.state -eq "Dispatched") "S1 expected Dispatched after process kill, got $($s1Before.state)."
    $s1Renamed = "#123 [KF336 $runId] renamed while create was in flight"
    Invoke-KfJson "PUT" "/api/tasks/$($s1Task.id)" @{ name = $s1Renamed; actor = $script:actor } | Out-Null
    Wait-OperationLeaseExpiry $s1Signal.operationId | Out-Null
    Invoke-Portfolio @(
        "push-issue", $fixtureRepo, "--allow-write", "--task", "$($s1Task.id)",
        "--resume", "$($s1Signal.operationId)", "--actor", $script:actor
    ) | Out-Null
    $s1After = Invoke-KfJson "GET" "/api/external-write-operations/$($s1Signal.operationId)"
    Assert-Condition ($s1After.state -eq "Completed") "S1 resume did not complete."
    Assert-Condition ((Get-LinkForTask $s1Task.id).Count -eq 1) "S1 did not finalize exactly one link."
    Assert-Condition ((Invoke-KfJson "GET" "/api/tasks/$($s1Task.id)").name -ceq $s1Renamed) "S1 operation-driven resume was diverted by the task's new #n prefix."
    Assert-OneExactMarker $s1Signal.operationId | Out-Null
    $results.S1 = "PASS: killed after create; explicit resume ignored a later #n rename, found one marker, and finalized one link"

    Write-Host "Scenario 2/6: concurrent pushes serialize before GitHub"
    $s2Task = New-VerificationTask "[KF336 $runId] S2 concurrent prepare"
    $s2OperationCountBefore = Get-SqliteRowCount "ExternalWriteOperations"
    $s2First = Start-BarrierPush $s2Task.id "s2-first" "BeforePrepare"
    $s2Second = Start-BarrierPush $s2Task.id "s2-second" "BeforePrepare"
    $s2FirstSignal = Wait-BarrierSignal $s2First
    $s2SecondSignal = Wait-BarrierSignal $s2Second
    Assert-Condition ($s2FirstSignal.phase -eq "before-prepare" -and $s2SecondSignal.phase -eq "before-prepare") "S2 creators did not both stop before prepare."
    # Both processes are demonstrably parked before either prepare request. Releasing both files
    # makes this a prepare race rather than a sequential replay of an already-created operation.
    New-BarrierRelease $s2First
    New-BarrierRelease $s2Second
    $s2FirstResult = Wait-ProcessResult $s2First
    $s2SecondResult = Wait-ProcessResult $s2Second
    $s2Runs = @(
        [pscustomobject]@{ Name = "first"; Result = $s2FirstResult },
        [pscustomobject]@{ Name = "second"; Result = $s2SecondResult }
    )
    $s2Winners = @($s2Runs | Where-Object { $_.Result.ExitCode -eq 0 })
    $s2Losers = @($s2Runs | Where-Object { $_.Result.ExitCode -ne 0 })
    Assert-Condition ($s2Winners.Count -eq 1 -and $s2Losers.Count -eq 1) "S2 expected one winner and one loser.`nfirst:`n$($s2FirstResult.Output)`nsecond:`n$($s2SecondResult.Output)"
    Assert-Condition ($s2Losers[0].Result.Output -match "409|external-operation-exists|external operation") "S2 loser did not expose the expected durable conflict.`n$($s2Losers[0].Result.Output)"
    Assert-Condition ((Get-SqliteRowCount "ExternalWriteOperations") -eq ($s2OperationCountBefore + 1)) "S2 prepare race persisted other than exactly one operation."
    $s2Links = @(Get-LinkForTask $s2Task.id | Where-Object { $_.state -eq "Active" })
    Assert-Condition ($s2Links.Count -eq 1) "S2 did not produce exactly one active link."
    $s2Issue = Get-FixtureIssueForLink $s2Links[0]
    $s2OperationId = Get-OperationIdFromIssue $s2Issue
    $s2Operation = Invoke-KfJson "GET" "/api/external-write-operations/$s2OperationId"
    Assert-Condition ($s2Operation.state -eq "Completed" -and $s2Operation.taskId -eq $s2Task.id) "S2 winning operation did not complete for the intended task."
    Assert-OneExactMarker $s2OperationId | Out-Null
    $fixtureUrls.Add($s2Issue.html_url)
    $results.S2 = "PASS: two pre-prepare calls raced; one operation, one issue, one link, and one 409 loser"

    Write-Host "Scenario 3/6: sync lands inside create/finalize window"
    $s3Task = New-VerificationTask "[KF336 $runId] S3 sync window"
    $s3TaskIdsBefore = Get-TaskIdFingerprint
    $s3Push = Start-BarrierPush $s3Task.id "s3"
    $s3Signal = Wait-BarrierSignal $s3Push
    $s3Issue = Assert-OneExactMarker $s3Signal.operationId
    $fixtureUrls.Add($s3Issue.html_url)
    Invoke-Portfolio @("sync", "--repos", $fixtureRepo, "--no-comments", "--actor", $script:actor) | Out-Null
    $s3Links = @(Get-ProjectLinks |
        Where-Object { $_.externalNodeId -ceq $s3Issue.node_id })
    Assert-Condition ($s3Links.Count -eq 1) "S3 sync did not converge to exactly one link."
    Assert-Condition ($s3Links[0].taskId -eq $s3Task.id) "S3 sync linked the issue to a duplicate task."
    $s3TasksAfterSync = Get-ProjectTasks
    Assert-Condition ($s3TaskIdsBefore -ceq (@($s3TasksAfterSync | ForEach-Object { [int]$_.id } | Sort-Object) -join ",")) "S3 sync changed the project task-id set during the create/finalize window."
    $s3Duplicates = @($s3TasksAfterSync | Where-Object {
        $_.id -ne $s3Task.id -and (
            ([string]$_.name -match "^#$($s3Issue.number)(?:\s|$)") -or
            ([string]$_.description).Contains([string]$s3Issue.html_url)
        )
    })
    Assert-Condition ($s3Duplicates.Count -eq 0) "S3 sync created an unlinked duplicate task for $($s3Issue.html_url)."
    New-BarrierRelease $s3Push
    Wait-CheckedProcess $s3Push
    Assert-OneExactMarker $s3Signal.operationId | Out-Null
    $results.S3 = "PASS: sync finalized the intended task during the barrier; no duplicate board task"

    Write-Host "Scenario 4/6: task and issue renames preserve immutable identity"
    $s4OriginalLink = (Get-LinkForTask $s3Task.id)[0]
    Invoke-KfJson "PUT" "/api/tasks/$($s3Task.id)" @{
        name = "[KF336 $runId] task-only rename"
        actor = $script:actor
    } | Out-Null
    $s4LinkAfterTaskRename = @(Get-LinkForTask $s3Task.id)
    Assert-Condition ($s4LinkAfterTaskRename.Count -eq 1) "S4 task rename changed the task's link count."
    Assert-Condition (
        $s4LinkAfterTaskRename[0].id -eq $s4OriginalLink.id -and
        $s4LinkAfterTaskRename[0].externalNodeId -ceq $s4OriginalLink.externalNodeId -and
        $s4LinkAfterTaskRename[0].url -ceq $s4OriginalLink.url
    ) "S4 task rename changed its link."

    $issueRename = "[KF336 $runId] issue-only rename"
    & gh issue edit $s3Issue.html_url --repo $fixtureRepo --title $issueRename | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "S4 GitHub issue rename failed." }
    Invoke-Portfolio @("sync", "--repos", $fixtureRepo, "--no-comments", "--actor", $script:actor) | Out-Null
    $afterIssueRename = Invoke-KfJson "GET" "/api/tasks/$($s3Task.id)"
    Assert-Condition ($afterIssueRename.name -eq $issueRename) "S4 issue rename did not reconcile the plain task name."

    Invoke-KfJson "PUT" "/api/tasks/$($s3Task.id)" @{ name = "[KF336 $runId] simultaneous task rename"; actor = $script:actor } | Out-Null
    $bothRename = "[KF336 $runId] both renamed, immutable link retained"
    & gh issue edit $s3Issue.html_url --repo $fixtureRepo --title $bothRename | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "S4 second GitHub issue rename failed." }
    Invoke-Portfolio @("sync", "--repos", $fixtureRepo, "--no-comments", "--actor", $script:actor) | Out-Null
    $s4FinalTask = Invoke-KfJson "GET" "/api/tasks/$($s3Task.id)"
    $s4FinalLink = (Get-LinkForTask $s3Task.id)[0]
    Assert-Condition ($s4FinalTask.name -eq $bothRename) "S4 final title did not reconcile."
    Assert-Condition ($s4FinalLink.id -eq $s4OriginalLink.id -and $s4FinalLink.externalNodeId -ceq $s4OriginalLink.externalNodeId) "S4 link identity changed across renames."
    $results.S4 = "PASS: task-only, issue-only, and both-side renames retained one node-id link"

    Write-Host "Scenario 5/6: uniqueness rejects a second task claiming one issue"
    $s5Task = New-VerificationTask "[KF336 $runId] S5 duplicate link claimant"
    $s5Failure = Invoke-KfExpectedFailure "PUT" "/api/tasks/$($s5Task.id)/external-work-item-links/github" @{
        provider = "GitHub"
        repositoryExternalId = $expectedRepoNodeId
        repositoryOwner = $repoInfo.owner.login
        repositoryName = $repoInfo.name
        externalNodeId = $s4FinalLink.externalNodeId
        externalNumber = $s4FinalLink.externalNumber
        url = $s4FinalLink.url
        actor = $script:actor
    } 409
    Assert-Condition ($s5Failure -match "external-identity-in-use|already") "S5 returned 409 without the expected identity conflict body: $s5Failure"
    $allS5Links = @(Get-ProjectLinks |
        Where-Object { $_.externalNodeId -ceq $s4FinalLink.externalNodeId -and $_.state -eq "Active" })
    Assert-Condition ($allS5Links.Count -eq 1) "S5 left more than one active claimant."
    & dotnet test $testProjectPath `
        --no-restore --filter "FullyQualifiedName~ExternalWorkItem" --nologo
    if ($LASTEXITCODE -ne 0) { throw "S5 focused real-SQLite uniqueness tests failed." }
    $results.S5 = "PASS: API conflict plus focused real-SQLite ExternalWorkItem tests"

    Write-Host "Scenario 6/6: title-only prefix is quarantined and rerun is idempotent"
    $s6Task = New-VerificationTask "#999999 [KF336 $runId] literal, not a verified link"
    Invoke-Portfolio @("backfill-links", $fixtureRepo, "--actor", $script:actor) | Out-Null
    $s6First = @(Get-LinkForTask $s6Task.id)
    Assert-Condition ($s6First.Count -eq 1 -and $s6First[0].state -eq "Quarantined") "S6 title-only candidate was not quarantined."
    $s6LinkId = $s6First[0].id
    Invoke-Portfolio @("backfill-links", $fixtureRepo, "--actor", $script:actor) | Out-Null
    $s6Second = @(Get-LinkForTask $s6Task.id)
    Assert-Condition ($s6Second.Count -eq 1 -and $s6Second[0].id -eq $s6LinkId) "S6 rerun was not idempotent."
    $s6TaskBeforeSuppression = Invoke-KfJson "GET" "/api/tasks/$($s6Task.id)"
    $s6TaskIdsBeforeSuppression = Get-TaskIdFingerprint
    $s6IssueFingerprintBefore = Get-FixtureIssueFingerprint
    $s6OperationCountBefore = Get-SqliteRowCount "ExternalWriteOperations"
    $s6ActivityCountBefore = Get-SqliteRowCount "ActivityEvents"
    Invoke-Portfolio @("sync", "--repos", $fixtureRepo, "--no-comments", "--actor", $script:actor) | Out-Null
    $s6Push = Invoke-Portfolio @(
        "push-issue", $fixtureRepo, "--allow-write", "--task", "$($s6Task.id)", "--actor", $script:actor
    )
    Assert-Condition ($s6Push.Output -match "quarantin") "S6 live push did not report quarantine suppression.`n$($s6Push.Output)"
    $s6TaskAfterSuppression = Invoke-KfJson "GET" "/api/tasks/$($s6Task.id)"
    $s6Final = @(Get-LinkForTask $s6Task.id)
    Assert-Condition ($s6Final.Count -eq 1 -and $s6Final[0].id -eq $s6LinkId -and $s6Final[0].state -eq "Quarantined") "S6 sync/push changed the persisted quarantine."
    Assert-Condition ($s6TaskBeforeSuppression.updatedAt -ceq $s6TaskAfterSuppression.updatedAt) "S6 sync/push mutated the quarantined task."
    Assert-Condition ($s6TaskIdsBeforeSuppression -ceq (Get-TaskIdFingerprint)) "S6 sync created another task for a quarantined candidate."
    Assert-Condition ($s6OperationCountBefore -eq (Get-SqliteRowCount "ExternalWriteOperations")) "S6 live push prepared an operation for a quarantined task."
    Assert-Condition ($s6ActivityCountBefore -eq (Get-SqliteRowCount "ActivityEvents")) "S6 sync/push emitted activity for a quarantined task."
    Assert-Condition ($s6IssueFingerprintBefore -ceq (Get-FixtureIssueFingerprint)) "S6 live push created or updated a GitHub issue."
    $results.S6 = "PASS: title-only quarantine is idempotent and suppresses subsequent sync mutation and live push creation"

    $comment = @"
<!-- kanbanflow-verify-336:$runId -->
## KanbanFlow #336 verification fixture

- Run: ``$runId``
- Dry run: $($results.DryRun)
- Manual retry gate: $($results.ManualRetry)
- S1: $($results.S1)
- S2: $($results.S2)
- S3: $($results.S3)
- S4: $($results.S4)
- S5: $($results.S5)
- S6: $($results.S6)

Fixtures are intentionally left open. This harness never closes or deletes issues.
"@
    $comment | & gh issue comment $s1Issue.html_url --repo $fixtureRepo --body-file - | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "Could not attach the audit summary to the S1 fixture issue." }

    $report = [ordered]@{
        runId = $runId
        fixtureRepository = $fixtureRepo
        repositoryNodeId = $expectedRepoNodeId
        scratchDatabase = $dbPath
        baseUrl = $script:baseUrl
        fixtureIssues = @($fixtureUrls)
        results = $results
        cleanup = "None. Issues remain open; run directory is preserved."
    }
    $report | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath (Join-Path $script:runDir "result.json") -Encoding utf8

    Write-Host ""
    Write-Host "The dry-run gate, manual-retry gate, and all six #336 scenarios passed."
    Write-Host "Fixture issues (left OPEN):"
    $fixtureUrls | ForEach-Object { Write-Host "  $_" }
    Write-Host "Preserved run artifacts: $script:runDir"
    $runCompletedSuccessfully = $true
}
finally {
    foreach ($process in $script:childProcesses) {
        if ($null -ne $process -and -not $process.HasExited) {
            Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue
        }
    }
    if ($null -ne $appProcess -and -not $appProcess.HasExited) {
        Stop-Process -Id $appProcess.Id -Force -ErrorAction SilentlyContinue
    }
    $env:KANBANFLOW_DB_PATH = $savedDbPath
    $env:KANBANFLOW_API_URL = $savedApiUrl
    $env:KANBANFLOW_EXTERNAL_WRITE_LEASE_SECONDS = $savedLeaseSeconds
    if (-not $runCompletedSuccessfully -and $fixtureUrls.Count -gt 0) {
        Write-Host "Fixture issues created before failure (left OPEN):"
        $fixtureUrls | ForEach-Object { Write-Host "  $_" }
    }
    Write-Host "Run directory preserved: $script:runDir"
}
