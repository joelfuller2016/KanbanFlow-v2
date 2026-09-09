<#
.SYNOPSIS
    Register (or re-register) the scheduled task that keeps KanbanFlow running. See #477.

.DESCRIPTION
    #477 §8 step 3 asked for a task that survives a reboot and restarts on failure, so that
    `/api/admin/health` answers again with nothing started by hand. What actually got registered
    was a single one-shot time trigger with a 10-minute repetition, created by hand at a console —
    no reboot trigger, and no committed script, so the arrangement existed only as machine state
    that nobody could review, reproduce on another box, or restore after a mistake.

    This script is that arrangement, written down and re-runnable. It registers THREE triggers,
    deliberately:

      AtStartup      the one that actually survives an unattended reboot. A 3 a.m. update reboot
                     leaves nobody logged on, so a logon trigger alone cannot fire and the app
                     stays down until a human arrives -- exactly the "no manual start" case §9
                     rules out. This is why the principal is S4U (below) rather than Interactive.
      AtLogOn        covers the case AtStartup cannot: the machine was already up and the task was
                     registered, disabled or repaired during this session.
      Once + PT<n>M  a keepalive, and -- see below -- the REAL recovery path after a crash. With
                     -MultipleInstances IgnoreNew, a repeat that finds the app already running is
                     REFUSED by Task Scheduler with 0x800710E0 ("the operator or administrator has
                     refused the request"), so the repeat costs nothing while the app is healthy
                     and revives it after it exits. That refusal code is the system working as
                     designed and must not be "fixed".

    THE DATABASE PATH IS PINNED INTO THE ACTION, resolved here, at registration time. It used to be
    left to the launcher, which resolves `$HOME\.kanbanflow\kanbanflow.db` -- and `$HOME` is not a
    constant. Windows PowerShell builds it from HOMEDRIVE+HOMEPATH while pwsh uses USERPROFILE, and
    under an S4U logon both come from Task Scheduler's environment block rather than from an
    interactive logon. If that resolves anywhere else the launcher finds no database, RESTORES THE
    COMMITTED SEED (it is guarded on absence, so it cannot tell "missing" from "wrong path"), binds
    the port, and the health endpoint answers ok. The reboot criterion would pass with an empty
    board while the real one sat untouched on disk. Resolving the path in the operator's own
    context and passing it as -DbPath removes the logon context from the answer entirely.

    Health used to be blind to all of this -- it returned a literal and never opened the database.
    It now does (#498) and reports the file it opened, so the wrong-file start is at least VISIBLE:
    db.path names it. Pinning -DbPath is still the fix; the endpoint is the detector.

    A CONSEQUENCE WORTH STATING PLAINLY: `status: ok` from /api/admin/health used to prove only
    that the process was up and its DI graph wired -- the endpoint never opened the database. Since
    #498 it opens it, reads from it, and reports `db.path`, so it can now answer `degraded` or
    `error`. It still cannot know which database you MEANT -- this script pins that -- but the
    comparison is finally possible: read `db.path` and check it against the file you expect.

    REBOOT SURVIVAL NEEDS ADMINISTRATOR, and that is a measurement, not a guess. Registering the
    same trivial task as a non-elevated user on this box: Interactive principal with AtLogOn + Once
    => OK; add an AtStartup trigger => "Access is denied"; switch the principal to S4U (with no
    boot trigger at all) => "Access is denied"; LogonType Password => "Access is denied". Both
    halves of the fix are gated on the batch-logon right that an ordinary user does not hold.

    So this script ASKS for the full definition and, when Windows refuses it, falls back to the
    AtLogOn + keepalive arrangement, says loudly that reboot survival was NOT obtained, prints the
    elevated command that would obtain it, and EXITS 3. It does not quietly register a weaker task
    and report success -- that is the exact failure #477 was filed about, and an exit code of 0 is
    how that failure would reach a caller reading only the code. Pass -RequireStartupTrigger to
    turn the fallback into a hard failure (exit 2, with the previous definition restored) instead.

    IT ALSO REFUSES TO DOWNGRADE. If the task already registered carries a boot trigger or an S4U
    principal -- somebody ran this elevated once -- an unelevated re-run does NOT replace it with
    the weaker definition. It restores what was there and fails. Otherwise the routine unelevated
    run would silently undo the one elevated run that made the guarantee real, and verification
    would pass because the fallback meets its own requirements.

    AN UNELEVATED FIRST RUN THEREFORE YIELDS A LOGON-ONLY TASK AND RETURNS 3. That task is
    genuinely useful -- it keeps the app up for the whole time somebody is logged on, which is most
    of the time -- but it does NOT satisfy #477 §9 criterion 3. ONE elevated run is what buys the
    reboot guarantee, and it only has to happen once.

    Exit codes:
      0  the task is registered with all three triggers (or -Unregister removed it, or -WhatIf).
      2  failed; the message says exactly what state the task was left in.
      3  registered WITHOUT reboot survival because Windows refused the AtStartup + S4U definition.

    RESTART-ON-FAILURE DOES NOT RETRY HERE, and this docstring used to claim it did ("Task
    Scheduler retries it three times a minute apart -- faster than waiting for the next repeat").
    Measured on this box with exactly the settings below, on a task whose action appends a
    timestamp and exits 1: over 175 seconds -- long enough for all three configured retries -- the
    action ran ONCE. LastTaskResult settled at 0x00000001 (the action's own exit code) and the task
    returned to Ready. So the honest statement is that a crashed app is revived by the keepalive
    repetition, within KeepaliveMinutes, and not within a minute by RestartCount. The restart
    settings are left in place because they cost nothing and may apply to failure modes the
    scheduler classifies differently, but nothing here depends on them; the default keepalive is 5
    minutes rather than 10 so that the worst case is half as long. The price is twice as many
    refused repeats in the task history, which is the cheaper of the two.

    (The same measurement also showed the scheduler taking 45-75 seconds to launch a manually
    started task, which is why -StartTimeoutSeconds defaults generously.)

    Idempotent: -Force replaces any existing definition, so running it twice leaves exactly one
    task. The previous definition is EXPORTED first and restored if anything after that fails, so a
    failed run does not leave a half-registered task behind while reporting failure. It verifies its
    own work afterwards and exits non-zero if what landed is not what was asked for, so "it ran" and
    "it worked" are not the same claim.

    Windows PowerShell 5.1 compatible -- no ternary, no null-coalescing, no null-conditional. The
    repo's floor is 5.1 and pwsh 7 is not guaranteed on a fresh box; the shell that RUNS the task is
    resolved below rather than hardcoded, is never a version-stamped WindowsApps path, and is
    EXECUTED before it is trusted (see Resolve-LauncherShell).

    The task itself never runs elevated: -RunLevel Limited either way, and S4U stores no password.
    S4U runs without an interactive desktop, which is correct for a server only ever reached over
    HTTP. Elevation, where it is needed at all, is needed to REGISTER, not to run.

.PARAMETER TaskName
    Scheduled task name. Defaults to kanbanflow-app. A non-default name skips the worktree guard,
    so it is the supported way to exercise this script -- but it is NOT a sandbox on its own: the
    action it registers would still bind the production port and open the production database.
    A non-default name therefore REQUIRES both -HealthUrl and -DbPath pointing somewhere disposable.

.PARAMETER KeepaliveMinutes
    Interval for the keepalive repetition, which is the real crash-recovery path. Defaults to 5.

.PARAMETER HealthUrl
    Health endpoint to probe, and the address the task is told to listen on. Defaults to
    ASPNETCORE_URLS (first entry, with a wildcard bind rewritten to loopback) plus
    /api/admin/health, else http://127.0.0.1:5123/api/admin/health.

.PARAMETER DbPath
    Database the task is pinned to. Defaults to KANBANFLOW_DB_PATH, else ~/.kanbanflow/kanbanflow.db
    resolved HERE rather than inside the task's own logon context.

.PARAMETER StartTimeoutSeconds
    How long to wait for health after starting the task. Defaults to 180: the first run publishes
    the app, which takes about a minute on a cold checkout.

.PARAMETER RequireStartupTrigger
    Fail (exit 2, rolling back) rather than falling back to a logon-only task when Windows refuses
    the AtStartup + S4U definition. Without it the fallback is registered and the script exits 3.

.PARAMETER NoStart
    Register only. By default the task is started when KanbanFlow is not already answering.

.PARAMETER Unregister
    Remove the task instead of registering it.

.EXAMPLE
    # Unelevated. Registers a task that runs whenever you are logged on, warns that it will NOT
    # come back after an unattended reboot, and EXITS 3.
    powershell.exe -ExecutionPolicy Bypass -File scripts/register-kanbanflow-task.ps1

.EXAMPLE
    # Run ONCE from an elevated shell to get the reboot guarantee. Exits 0, or 2 if it still
    # cannot have it -- never a quiet 0 for a logon-only task.
    powershell.exe -ExecutionPolicy Bypass -File scripts/register-kanbanflow-task.ps1 -RequireStartupTrigger

.EXAMPLE
    # A throwaway task for testing. BOTH overrides are mandatory so it cannot touch the real board.
    pwsh -File scripts/register-kanbanflow-task.ps1 -TaskName kbf-scratch `
         -HealthUrl http://127.0.0.1:5199/api/admin/health -DbPath $env:TEMP\kbf-scratch.db

.EXAMPLE
    pwsh -File scripts/register-kanbanflow-task.ps1 -WhatIf
#>
#requires -Version 5.1
[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [ValidateNotNullOrEmpty()][string] $TaskName = 'kanbanflow-app',
    [ValidateRange(1, 1440)][int] $KeepaliveMinutes = 5,
    [ValidateNotNullOrEmpty()][string] $HealthUrl,
    [ValidateNotNullOrEmpty()][string] $DbPath,
    [ValidateRange(5, 3600)][int] $StartTimeoutSeconds = 180,
    [switch] $RequireStartupTrigger,
    [switch] $NoStart,
    [switch] $Unregister
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# Every ScheduledTasks call below ALSO carries an explicit -ErrorAction, rather than relying on the
# preference above. These are CIM cmdlets whose errors are non-terminating by default, and the
# ambient preference does not always reach them: measured, the same script invoked as
# `powershell.exe -File script.ps1` caught "Access is denied" from Register-ScheduledTask and fell
# back correctly (exit 3), while `powershell.exe -Command "& script.ps1"` did NOT -- the error was
# written to the stream, execution carried on into verification, and the operator got
# "Expected exactly one task ... found 0" instead of the real reason. Explicit beats ambient.

# 0 success, 2 failure (thrown), 3 registered but WITHOUT reboot survival. A degraded registration
# must not report success: a caller reading only the exit code would record "the reboot guarantee
# is in place" for a task that cannot start until somebody logs in.
$script:ExitCode = 0

# An MSIX install directory: C:\Program Files\WindowsApps\Microsoft.PowerShell_7.6.5.0_x64__<id>.
# The whole directory is DELETED when PowerShell updates, so a task holding a path inside it
# silently stops working days later. The architecture suffix is deliberately NOT part of this
# pattern: matching only `_x64__` would accept the arm64 and x86 package directories, and on an
# ARM64 box -- where the MSIX build is the NORMAL install -- that is precisely the path this design
# exists to avoid. The version-free alias under %LOCALAPPDATA%\Microsoft\WindowsApps is a different
# directory and is unaffected.
$script:WindowsAppsPackagePattern = '\\Program Files\\WindowsApps\\'

function Test-ShellCandidate {
    <#
        A candidate is usable only if it RUNS. Existence is not enough: a truncated or 0-byte
        pwsh.exe at the stable MSI path would win outright, ahead of every working candidate, and
        on the common path -- app already serving, so the action is never executed -- the dud would
        not be discovered until the next reboot, by nobody. A size heuristic cannot help, because
        the WindowsApps alias this legitimately prefers is ITSELF 0 bytes: it is an AppExecLink
        reparse point, not a binary.

        Measured in both hosts: Start-Process on a real shell returns 42; on a 0-byte .exe it
        throws "The specified executable is not a valid application for this OS platform".
    #>
    param([string] $Path)

    if ([string]::IsNullOrWhiteSpace($Path)) { return $false }
    if ($Path -match $script:WindowsAppsPackagePattern) { return $false }
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) { return $false }

    try {
        # Start-Process, not the call operator: it reports ExitCode the same way in both hosts and
        # is unaffected by $PSNativeCommandUseErrorActionPreference turning a non-zero exit into a
        # terminating error under pwsh 7.4+.
        $probe = Start-Process -FilePath $Path `
            -ArgumentList '-NoProfile', '-NonInteractive', '-Command', 'exit 42' `
            -Wait -PassThru -WindowStyle Hidden
        return ($probe.ExitCode -eq 42)
    }
    catch {
        return $false
    }
}

function Resolve-LauncherShell {
    <#
        Pick the host that will run the launcher, preferring paths that survive an update.

        `Get-Command -Name pwsh -CommandType Application` is NOT usable on its own for two
        independent reasons, both measured on a box with two pwsh entries on PATH:

          1. It returns EVERY match, not one. `$cmd.Source` then member-enumerates to an ARRAY,
             `Test-Path` passes because a non-empty array is truthy, and New-ScheduledTaskAction
             -Execute dies with "Cannot process argument transformation on parameter 'Execute'" --
             registering nothing, under both powershell.exe and pwsh. Set-StrictMode does not
             catch it. Hence the explicit single-value selection and the [string] assertion later.
          2. Its first match is the version-stamped WindowsApps path this docstring claims to
             avoid, which is the exact failure hardcoding was rejected for.

        So: stable MSI path first, then the version-free WindowsApps alias (a fixed forwarder that
        an update rewrites rather than deletes), then anything else pwsh on PATH points at that is
        not inside a WindowsApps package, then Windows PowerShell -- and each one has to actually
        run before it is accepted.
    #>
    $candidates = New-Object System.Collections.ArrayList

    if (-not [string]::IsNullOrWhiteSpace($env:ProgramFiles)) {
        [void]$candidates.Add((Join-Path $env:ProgramFiles 'PowerShell\7\pwsh.exe'))
    }
    if (-not [string]::IsNullOrWhiteSpace($env:LOCALAPPDATA)) {
        [void]$candidates.Add((Join-Path $env:LOCALAPPDATA 'Microsoft\WindowsApps\pwsh.exe'))
    }
    foreach ($found in @(Get-Command -Name pwsh -CommandType Application -ErrorAction SilentlyContinue)) {
        if ($found -and $found.Source) { [void]$candidates.Add([string]$found.Source) }
    }
    if (-not [string]::IsNullOrWhiteSpace($env:SystemRoot)) {
        [void]$candidates.Add((Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'))
    }

    foreach ($candidate in $candidates) {
        # ONE value, explicitly. @(...)[0] is deliberate belt-and-braces: every element added above
        # is already a single string, and this is the line that must never hand an array onward.
        $path = @($candidate)[0]
        if ($path -isnot [string]) { continue }
        if (Test-ShellCandidate -Path $path) { return $path }
    }
    return $null
}

function Test-LinkedWorktree {
    <#
        Ask git, not the path string.

        The original guard matched '\.worktrees\' in $PSScriptRoot, which any aliased path defeats:
        measured, reaching this same file through a `subst` drive walked straight past it (exit 0,
        and it would have registered the real task). `git rev-parse --git-dir` reports a path under
        .git/worktrees/ for a LINKED worktree and plain `.git` for the main checkout, and no alias,
        junction or subst drive can hide that because git resolves it from the repository, not from
        how the caller spelled the directory.

        There is no string-matching fallback, because a fallback that any junction defeats is the
        defect this replaced. The two failure modes are kept apart deliberately: git ANSWERING
        "this is not a repository" (exit 128) is a definitive NOT-a-worktree and is fine; git being
        absent means the question cannot be answered, and this refuses rather than guesses.
    #>
    param([string] $Root)

    if (-not (Get-Command -Name git -CommandType Application -ErrorAction SilentlyContinue)) {
        throw ("git is not on PATH, so this cannot tell whether $Root is a linked git worktree. " +
               "Registering the production task from a worktree stores a launcher path that " +
               "disappears when the worktree is removed, and the failure surfaces days later as an " +
               "app that stopped coming back. Install git, or run this from the main checkout.")
    }

    $gitDir = $null
    $exitCode = -1
    $previousPreference = $ErrorActionPreference
    # 'Continue' so the exit code is observable: under pwsh 7.4+ a non-zero native exit is a
    # terminating error when the preference is Stop, and `git rev-parse` OUTSIDE a repository exits
    # 128 -- which is an answer, not a failure. Measured in both hosts: threw=False, exit=128.
    $ErrorActionPreference = 'Continue'
    try {
        # $global:, not a bare assignment: a bare $LASTEXITCODE = 0 creates a FUNCTION-scoped
        # variable that shadows the one the native command updates, and the read below would then
        # always see 0 -- i.e. "git said this is the main checkout" for every failure.
        $global:LASTEXITCODE = 0
        $output = & git -C $Root rev-parse --git-dir 2>$null
        $exitCode = $global:LASTEXITCODE
        if ($exitCode -eq 0) { $gitDir = (@($output) -join '') }
    }
    catch {
        $exitCode = -1
    }
    finally {
        $ErrorActionPreference = $previousPreference
    }

    if ($exitCode -eq 0 -and -not [string]::IsNullOrWhiteSpace($gitDir)) {
        return (($gitDir -replace '/', '\') -match '\\\.git\\worktrees\\')
    }
    if ($exitCode -lt 0) {
        throw "git could not be run against $Root, so whether it is a linked worktree is unknown."
    }
    # git ran and reported this is not a repository at all: definitively not a linked worktree.
    return $false
}

function ConvertTo-ProbeUrl {
    <#
        Turn a Kestrel BIND specification into something a client can actually dial.

        `http://+:5123` and `http://*:5123` are the standard "listen on everything" forms and are
        NOT addresses: [uri] and Invoke-WebRequest both reject them with "Invalid URI: The hostname
        could not be parsed". Probing one is false forever, which sends this script down the
        start-and-poll path against a perfectly healthy app and out through exit 2.

        Returns $null when the input is not a URL at all, so the caller can say so rather than
        building a nonsense address out of it.

        -PreservePath keeps the path the caller supplied instead of dropping it. A BIND
        specification has no meaningful path, so the default discards it and the caller appends
        the probe path itself. An operator's -HealthUrl DOES carry one, and it must survive the
        same wildcard-host rewrite -- otherwise `-HealthUrl http://+:5199/api/admin/health` keeps a
        host Invoke-WebRequest cannot parse, Test-KanbanFlowHealthy is false forever, and the
        script polls out the whole -StartTimeoutSeconds and exits 2 against a healthy app. That is
        precisely the failure the first paragraph says this function exists to prevent, so the
        -HealthUrl path must not be the one branch that skips it.
    #>
    param(
        [string] $Url,
        [switch] $PreservePath
    )

    if ([string]::IsNullOrWhiteSpace($Url)) { return $null }
    $match = [regex]::Match($Url.Trim(), '^(?<scheme>https?)://(?<host>\[[^\]]+\]|[^/:]+)(?<port>:\d+)?(?<rest>/.*)?$')
    if (-not $match.Success) { return $null }

    $hostPart = $match.Groups['host'].Value
    if (@('+', '*', '0.0.0.0', '[::]', '[::0]') -contains $hostPart) { $hostPart = '127.0.0.1' }
    $path = if ($PreservePath) { $match.Groups['rest'].Value } else { '' }
    return ('{0}://{1}{2}{3}' -f $match.Groups['scheme'].Value, $hostPart, $match.Groups['port'].Value, $path)
}

function Test-KanbanFlowHealthy {
    <#
        True when the endpoint answers 200 AND its top-level status says the process is serving a
        database: `ok`, or `degraded` (it opened and read one, and is reporting something about it
        -- new, empty, or shadowed). Both mean RUNNING, which is what this readiness probe is for.
        `error` and anything else are false. §9's reboot criterion is stricter and lives there, not
        here: top-level `ok` AND `db.path` equal to the database the operator pinned (#498).

        The proxy handling is load-bearing, not defensive noise. Measured under Windows PowerShell
        5.1: the FIRST web request in a process pays a one-time proxy auto-detection cost -- 1886 ms
        of a 3-second budget on this box, and far more on a machine that has to time out a WPAD
        lookup -- so the old -TimeoutSec 3 reported the app DOWN while curl.exe answered the same
        URL in 4.6 ms. That false negative drove the caller into the start branch, which IgnoreNew
        then refused WITHOUT throwing (LastTaskResult 0x800710E0), and the script printed
        "[task] started ..." and exited 0 having done nothing at all.

        The two hosts need different treatment, and using only the 5.1 mechanism was itself a bug:
        pwsh 7's Invoke-WebRequest proxies through HttpClient.DefaultProxy, a completely different
        static from WebRequest.DefaultWebProxy, so assigning the latter under pwsh does nothing at
        all. -NoProxy is the PS6+ equivalent. The 5.1 assignment is process-wide, so the previous
        value is saved and put back rather than clobbered for whatever called this script.
    #>
    param(
        [Parameter(Mandatory = $true)][string] $Uri,
        [int] $TimeoutSec = 20
    )

    $params = @{ Uri = $Uri; UseBasicParsing = $true; TimeoutSec = $TimeoutSec }
    $savedProxy = $null
    $restoreProxy = $false

    if ($PSVersionTable.PSVersion.Major -ge 6) {
        $params['NoProxy'] = $true
    }
    else {
        try {
            $savedProxy = [System.Net.WebRequest]::DefaultWebProxy
            [System.Net.WebRequest]::DefaultWebProxy = $null
            $restoreProxy = $true
        }
        catch { }
    }

    try {
        $response = Invoke-WebRequest @params
        if ($response.StatusCode -ne 200) { return $false }
        $body = $response.Content
        if ($body -is [byte[]]) { $body = [System.Text.Encoding]::UTF8.GetString($body) }

        # Parse, do not regex (#498). The payload now carries a nested db.status, so the old
        # '"status"\s*:\s*"ok"' match would report the app healthy off the INNER field while the
        # top-level status said degraded or error -- a false positive in exactly the direction this
        # function must never fail.
        #
        # This is a readiness probe: "degraded" means the process is up, opened its database and
        # read from it, and is telling you something about that board (it is new, empty, or
        # shadowed by another file). That is running, so the caller must not restart it. §9's
        # reboot criterion is the STRICTER check -- top-level ok, db.path equal to the expected
        # file -- and belongs at the criterion, not here.
        $status = $null
        try { $status = ([string]$body | ConvertFrom-Json).status } catch { return $false }
        return ($status -eq 'ok' -or $status -eq 'degraded')
    }
    catch {
        return $false
    }
    finally {
        if ($restoreProxy) {
            try { [System.Net.WebRequest]::DefaultWebProxy = $savedProxy } catch { }
        }
    }
}

function Test-SurvivesReboot {
    <# Does this task definition come back without a human? Boot trigger, or an S4U principal. #>
    param($Task)
    if ($null -eq $Task) { return $false }
    $classes = @($Task.Triggers | ForEach-Object { $_.CimClass.CimClassName })
    if ($classes -contains 'MSFT_TaskBootTrigger') { return $true }
    return ("$($Task.Principal.LogonType)" -eq 'S4U')
}

function Undo-TaskRegistration {
    <#
        -Force has already replaced (and, when the replacement itself is refused, REMOVED) the
        previous definition by the time anything can fail, so an unrolled-back failure reports
        "error" while leaving a task the operator did not ask for -- or nothing at all.

        $HadPrevious is passed separately from $BackupXml on purpose. With only the XML this could
        not tell "there was nothing to save" from "the export failed", and it said the first while
        the second was true: for kanbanflow-app that reads as "nothing was lost" at the exact
        moment the task this script exists to protect has been deleted. Reproduced with fault
        injection: present before True, present after False, and the message claimed there had been
        no previous definition.

        Returns a sentence describing what happened, which the caller appends to the error. The
        operator must never have to guess the machine's state.
    #>
    param([string] $Name, $BackupXml, [bool] $HadPrevious)

    $present = [bool](Get-ScheduledTask -TaskName $Name -TaskPath '\' -ErrorAction SilentlyContinue)

    if (-not $HadPrevious) {
        if (-not $present) { return "Nothing was registered, and there was no previous definition." }
        try {
            Unregister-ScheduledTask -TaskName $Name -TaskPath '\' -Confirm:$false -ErrorAction Stop
            return "There was no previous definition, so the partially-registered '$Name' was removed."
        }
        catch {
            return ("There was no previous definition and the partially-registered '$Name' could NOT " +
                    "be removed ($($_.Exception.Message)); it is LEFT IN PLACE.")
        }
    }

    if (-not $BackupXml) {
        if ($present) {
            return ("A previous definition of '$Name' EXISTED but could not be exported, so it cannot " +
                    "be restored: '$Name' is LEFT IN PLACE as just written and DIFFERS from what was " +
                    "there before.")
        }
        return ("A previous definition of '$Name' EXISTED but could not be exported, and -Force " +
                "removed it: '$Name' is now ABSENT. Re-run this script to recreate it.")
    }

    try {
        Register-ScheduledTask -TaskName $Name -TaskPath '\' -Xml ([string]$BackupXml) -Force -ErrorAction Stop | Out-Null
        return "The previous definition of '$Name' was restored."
    }
    catch {
        if ($present) {
            return ("The previous definition of '$Name' could NOT be restored ($($_.Exception.Message)); " +
                    "the task is LEFT IN PLACE as just written.")
        }
        return ("The previous definition of '$Name' could NOT be restored ($($_.Exception.Message)); " +
                "'$Name' is now ABSENT.")
    }
}

try {
    if (-not (Get-Command -Name Register-ScheduledTask -ErrorAction SilentlyContinue)) {
        throw "The ScheduledTasks module is unavailable. This script needs Windows 8 / Server 2012 or newer."
    }

    $RepoRoot   = Split-Path -Parent $PSScriptRoot
    $LauncherPs = Join-Path $PSScriptRoot 'start-kanbanflow.ps1'
    if (-not (Test-Path -LiteralPath $LauncherPs)) {
        throw "Launcher not found at $LauncherPs"
    }

    $isDefaultTaskName = ($TaskName -eq 'kanbanflow-app')

    # -TaskPath '\' everywhere. Without it, a same-named task in ANOTHER folder makes $registered an
    # array, the trigger check member-enumerates across both, and a task genuinely missing its logon
    # trigger passes verification because the other one has it (measured: unpinned returned 2 tasks
    # and the check passed; pinned returned 1 and it failed). It also stops -Unregister removing
    # somebody else's task of the same name.
    #
    # The unregister branch runs BEFORE the worktree guard. Removing the real task from a worktree
    # is a legitimate thing to want -- it is the *registering* of a worktree path that is unsafe --
    # and the guard's message talks about registering, which is not what was asked for.
    if ($Unregister) {
        if (Get-ScheduledTask -TaskName $TaskName -TaskPath '\' -ErrorAction SilentlyContinue) {
            if ($PSCmdlet.ShouldProcess($TaskName, 'Unregister scheduled task')) {
                Unregister-ScheduledTask -TaskName $TaskName -TaskPath '\' -Confirm:$false -ErrorAction Stop
                Write-Host "[task] unregistered $TaskName"
            }
        }
        else {
            Write-Host "[task] $TaskName is not registered; nothing to remove"
        }
        exit 0
    }

    # A task records an ABSOLUTE path to the launcher, so registering from a git worktree points the
    # machine at a directory that `git worktree remove` will delete -- and the failure surfaces days
    # later as an app that stopped coming back, with a task that still looks correctly registered.
    # Refuse by default; a -Force-style override is deliberately NOT offered, because the only
    # honest fix is to run this from the main checkout.
    if ($isDefaultTaskName -and (Test-LinkedWorktree -Root $PSScriptRoot)) {
        throw ("Refusing to register '$TaskName' from a git worktree ($PSScriptRoot). The task " +
               "would store a launcher path that disappears when the worktree is removed, and the " +
               "failure would surface days later as an app that stopped coming back. Run this from " +
               "the main checkout -- or pass a throwaway -TaskName WITH -HealthUrl and -DbPath to " +
               "test from here.")
    }

    # A throwaway -TaskName is not a sandbox on its own. It skips the worktree guard, but the action
    # it registers still binds the production port and opens the production database, and its
    # keepalive keeps doing so every few minutes until somebody notices -- which is exactly how two
    # forgotten kbf-throwaway-* tasks were found still running days later. Make the isolation
    # explicit or refuse.
    if (-not $isDefaultTaskName) {
        $missing = @()
        if ([string]::IsNullOrWhiteSpace($HealthUrl)) { $missing += '-HealthUrl' }
        if ([string]::IsNullOrWhiteSpace($DbPath))    { $missing += '-DbPath' }
        if ($missing.Count -gt 0) {
            throw ("Refusing to register the throwaway task '$TaskName' without $($missing -join ' and '). " +
                   "A non-default -TaskName skips the worktree guard, but the action would still bind " +
                   "the production endpoint and open the production database, and the keepalive would " +
                   "keep restarting it. Point both somewhere disposable, e.g. " +
                   "-HealthUrl http://127.0.0.1:5199/api/admin/health -DbPath `$env:TEMP\$TaskName.db")
        }
    }

    $shell = Resolve-LauncherShell
    if ($null -eq $shell) {
        throw ("No usable PowerShell host found to run the launcher. Looked for " +
               "$env:ProgramFiles\PowerShell\7\pwsh.exe, $env:LOCALAPPDATA\Microsoft\WindowsApps\pwsh.exe, " +
               "any pwsh on PATH outside a WindowsApps package, then " +
               "$env:SystemRoot\System32\WindowsPowerShell\v1.0\powershell.exe. Each candidate must " +
               "exist AND run.")
    }
    # The assertion the original blocker earned. New-ScheduledTaskAction -Execute accepts only a
    # single string; an array reaches it as an unconvertible argument and registers nothing.
    if ($shell -isnot [string]) {
        throw ("Internal error: resolved shell is a [$($shell.GetType().Name)], not a single string. " +
               "New-ScheduledTaskAction -Execute would fail and nothing would be registered.")
    }

    # Where the app will listen, and where to probe it. start-kanbanflow.ps1 honours ASPNETCORE_URLS,
    # so hardcoding port 5123 probes an address the app may not be listening on -- and that false
    # negative lands in the same "claimed to start it, started nothing" branch as the proxy bug.
    if (-not [string]::IsNullOrWhiteSpace($HealthUrl)) {
        # NORMALIZED, not taken verbatim. The old code assigned $HealthUrl.Trim() straight to
        # $healthUri, so this was the one branch that bypassed ConvertTo-ProbeUrl's wildcard-host
        # rewrite: `-HealthUrl http://+:5199/api/admin/health` stayed a `+` host, which
        # Invoke-WebRequest cannot parse, so Test-KanbanFlowHealthy was false forever -- the script
        # then started the task, polled for the whole -StartTimeoutSeconds and threw, exiting 2
        # against a perfectly healthy app.
        $listenUrl = ConvertTo-ProbeUrl -Url $HealthUrl
        if ($null -eq $listenUrl) {
            throw "-HealthUrl '$HealthUrl' is not an http(s) URL."
        }
        # -PreservePath because an operator's health URL carries one and it is the whole point of
        # the argument; the host and port still go through the same rewrite as every other branch.
        $healthUri = ConvertTo-ProbeUrl -Url $HealthUrl -PreservePath
    }
    else {
        $listenCandidates = @()
        if (-not [string]::IsNullOrWhiteSpace($env:ASPNETCORE_URLS)) {
            $listenCandidates = @($env:ASPNETCORE_URLS -split ';' | ForEach-Object { $_.Trim() } | Where-Object { $_ })
        }
        if ($listenCandidates.Count -eq 0) { $listenCandidates = @('http://127.0.0.1:5123') }

        $listenUrl = $null
        foreach ($candidate in $listenCandidates) {
            $listenUrl = ConvertTo-ProbeUrl -Url $candidate
            if ($null -ne $listenUrl) { break }
        }
        if ($null -eq $listenUrl) {
            throw ("Could not derive a health URL from ASPNETCORE_URLS ('$env:ASPNETCORE_URLS'). " +
                   "Pass -HealthUrl explicitly.")
        }
        $healthUri = $listenUrl + '/api/admin/health'
    }

    # Resolve the database HERE, in the operator's context, and pin it into the action. See the
    # docstring: leaving this to the task's own $HOME is how the reboot criterion could pass against
    # a freshly seeded empty board while the real one sat untouched on disk.
    $resolvedDb = $DbPath
    if ([string]::IsNullOrWhiteSpace($resolvedDb)) { $resolvedDb = $env:KANBANFLOW_DB_PATH }
    if ([string]::IsNullOrWhiteSpace($resolvedDb)) { $resolvedDb = Join-Path (Join-Path $HOME '.kanbanflow') 'kanbanflow.db' }
    # GetUnresolvedProviderPathFromPSPath, not [Path]::GetFullPath: it honours PowerShell's own
    # location, which can differ from the process working directory.
    $resolvedDb = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($resolvedDb)

    # The path is interpolated INSIDE a double-quoted argument in the task action below, so a quote
    # character would close that argument and append whatever follows as further launcher
    # arguments -- persisted into the registered task, where it runs unattended at every boot.
    # The value does not have to be typed by the operator: it falls back to $env:KANBANFLOW_DB_PATH
    # above. Canonicalisation does not help, because GetUnresolvedProviderPathFromPSPath normalises
    # separators and leaves a quote intact. The post-registration check does not catch it either --
    # it looks for the resolved path as a SUBSTRING of the argument string, which a longer injected
    # string still satisfies. So refuse the value rather than trying to escape it: a legitimate
    # Windows path cannot contain a quote at all ('"' is not a valid NTFS filename character).
    if ($resolvedDb -match '"') {
        throw "The database path contains a double-quote character and cannot be pinned into the task action: $resolvedDb"
    }
    if ($listenUrl -match '"') {
        throw "The resolved health URL contains a double-quote character and cannot be pinned into the task action: $listenUrl"
    }

    # -ExecutionPolicy Bypass so the task does not die on a machine whose user policy is Restricted
    # -- a scheduled task has no console to prompt at, so a policy block there is a silent failure.
    $arguments = '-NoProfile -NonInteractive -ExecutionPolicy Bypass -WindowStyle Hidden -File "{0}" -DbPath "{1}" -Url "{2}"' -f `
        $LauncherPs, $resolvedDb, $listenUrl
    $action = New-ScheduledTaskAction -Execute $shell -Argument $arguments -WorkingDirectory $RepoRoot

    # Three triggers on one task. Building the repetition on its own -Once trigger avoids copying a
    # .Repetition onto a logon trigger, which is the fragile idiom this could otherwise have used.
    # Omitting -RepetitionDuration is what yields an indefinite repeat (verified on 5.1: Duration
    # comes back empty, matching the hand-registered task's PT10M-with-no-end).
    #
    # The start boundary is a FIXED instant, not (Get-Date). With "now" the repetition phase was
    # reset on every run, so re-running more often than KeepaliveMinutes meant the keepalive -- the
    # documented crash-recovery path -- never actually fired; and the definition differed on every
    # registration, which defeats diffing the task against itself to find configuration drift.
    #
    # Resolve the account from the real Windows identity: $env:USERDOMAIN can be empty or
    # overridden, which would hand the trigger and every principal below an invalid or unintended
    # account. One resolution, used by the logon trigger and both principal attempts alike.
    $userId = [Security.Principal.WindowsIdentity]::GetCurrent().Name
    if ([string]::IsNullOrWhiteSpace($userId)) {
        throw "Could not resolve the current Windows identity for the scheduled task."
    }

    $keepaliveAnchor = New-Object DateTime 2025, 1, 1, 0, 0, 0
    $startupTrigger = New-ScheduledTaskTrigger -AtStartup
    $logonTrigger = New-ScheduledTaskTrigger -AtLogOn -User $userId
    $keepaliveTrigger = New-ScheduledTaskTrigger -Once -At $keepaliveAnchor `
        -RepetitionInterval (New-TimeSpan -Minutes $KeepaliveMinutes)

    $settings = New-ScheduledTaskSettingsSet `
        -MultipleInstances IgnoreNew `
        -StartWhenAvailable `
        -AllowStartIfOnBatteries `
        -DontStopIfGoingOnBatteries `
        -RestartCount 3 `
        -RestartInterval (New-TimeSpan -Minutes 1) `
        -ExecutionTimeLimit ([TimeSpan]::Zero)
    # PT0S means "no limit". Without it the default three-day cap would kill a long-lived server
    # mid-week, and the symptom -- a board that stops answering after a few days of uptime -- would
    # look nothing like its cause. RestartCount/RestartInterval are kept because they cost nothing,
    # but see the docstring: over 175 s they retried ZERO times here, and the keepalive is what
    # actually brings a crashed app back.

    # Two definitions, tried in order. S4U ("service for user") runs whether or not anyone is logged
    # on, stores no password and grants no elevation; Interactive -- what this used to use -- cannot
    # run at all until a human logs in, which makes an AtStartup trigger useless. But BOTH the boot
    # trigger and the S4U principal were measured to need administrator on this box, so the weaker
    # definition is a real fallback and not a theoretical one. Which one landed is reported, loudly.
    # $userId is the resolved Windows identity from above, NOT $env:USERDOMAIN\$env:USERNAME.
    $attempts = @(
        (New-Object psobject -Property @{
            Label             = 'AtStartup + AtLogOn + keepalive, S4U'
            Triggers          = @($startupTrigger, $logonTrigger, $keepaliveTrigger)
            Principal         = (New-ScheduledTaskPrincipal -UserId $userId -LogonType S4U -RunLevel Limited)
            RequiredTriggers  = @('MSFT_TaskBootTrigger', 'MSFT_TaskLogonTrigger', 'MSFT_TaskTimeTrigger')
            RequiredLogonType = 'S4U'
            SurvivesReboot    = $true
        }),
        (New-Object psobject -Property @{
            Label             = 'AtLogOn + keepalive, Interactive'
            Triggers          = @($logonTrigger, $keepaliveTrigger)
            Principal         = (New-ScheduledTaskPrincipal -UserId $userId -LogonType Interactive -RunLevel Limited)
            RequiredTriggers  = @('MSFT_TaskLogonTrigger', 'MSFT_TaskTimeTrigger')
            RequiredLogonType = 'Interactive'
            SurvivesReboot    = $false
        })
    )

    if ($PSCmdlet.ShouldProcess($TaskName, 'Register scheduled task (AtStartup + AtLogOn + keepalive)')) {
        # Export BEFORE -Force overwrites it, so a failure can put back what was there.
        $existing = Get-ScheduledTask -TaskName $TaskName -TaskPath '\' -ErrorAction SilentlyContinue
        $hadPrevious = [bool]$existing
        $existingSurvivesReboot = Test-SurvivesReboot -Task $existing
        $backupXml = $null
        if ($hadPrevious) {
            try {
                $backupXml = Export-ScheduledTask -TaskName $TaskName -TaskPath '\' -ErrorAction Stop
            }
            catch {
                $backupXml = $null
                Write-Warning ("[task] could not export the existing '$TaskName' before replacing it " +
                               "($($_.Exception.Message)); a failed registration cannot be rolled back.")
            }
        }

        $refusal = '(none recorded)'
        for ($i = 0; $i -lt $attempts.Count; $i++) {
            $attempt = $attempts[$i]
            $isPreferred = ($i -eq 0)
            try {
                Register-ScheduledTask `
                    -TaskName $TaskName `
                    -TaskPath '\' `
                    -Action $action `
                    -Trigger $attempt.Triggers `
                    -Settings $settings `
                    -Principal $attempt.Principal `
                    -Description "Keep KanbanFlow serving at $healthUri for local agent use (KanbanFlow-v2#477)." `
                    -Force -ErrorAction Stop | Out-Null
            }
            catch {
                $refusal = $_.Exception.Message

                # NEVER downgrade a task that already survives a reboot. Somebody ran this elevated
                # once; a routine unelevated re-run must not quietly undo that. Verification would
                # not catch it either, because the fallback meets its own requirements.
                if ($isPreferred -and $existingSurvivesReboot) {
                    $rollback = Undo-TaskRegistration -Name $TaskName -BackupXml $backupXml -HadPrevious $hadPrevious
                    throw ("Windows refused the preferred definition for '$TaskName' ($refusal), and the " +
                           "definition already registered DOES survive a reboot. Falling back would have " +
                           "silently destroyed that, so nothing was downgraded. $rollback Re-run from an " +
                           "ELEVATED shell to update it.")
                }

                # Otherwise fall back on ANY refusal of the preferred definition, and quote the
                # refusal verbatim in the warning below rather than assuming it was a permission
                # problem -- the message is localised, so matching on its text would be a guess.
                if ($isPreferred -and -not $RequireStartupTrigger) { continue }

                $rollback = Undo-TaskRegistration -Name $TaskName -BackupXml $backupXml -HadPrevious $hadPrevious
                throw ("Could not register '$TaskName' as $($attempt.Label): $refusal $rollback")
            }

            try {
                # Verify what actually landed rather than trusting that the call returned. A task
                # missing its triggers is precisely the state #477 found, and it looked fine from
                # the outside.
                $registered = @(Get-ScheduledTask -TaskName $TaskName -TaskPath '\' -ErrorAction SilentlyContinue)
                if ($registered.Count -ne 1) {
                    throw "Expected exactly one task named '$TaskName' in \, found $($registered.Count)."
                }
                $triggerClasses = @($registered[0].Triggers | ForEach-Object { $_.CimClass.CimClassName })
                $actionExecutes = @($registered[0].Actions | ForEach-Object { $_.Execute })
                $actionArguments = @($registered[0].Actions | ForEach-Object { $_.Arguments })
                $logonType = "$($registered[0].Principal.LogonType)"

                foreach ($required in $attempt.RequiredTriggers) {
                    if ($triggerClasses -notcontains $required) {
                        throw ("Registered '$TaskName' is missing $required. Triggers present: " +
                               "$($triggerClasses -join ', ').")
                    }
                }
                if ($actionExecutes -notcontains $shell) {
                    throw "Registered '$TaskName' runs '$($actionExecutes -join ', ')' rather than the resolved shell '$shell'."
                }
                if (-not (@($actionArguments) -match [regex]::Escape($resolvedDb))) {
                    throw ("Registered '$TaskName' does not pin the database path '$resolvedDb'; it would " +
                           "resolve one from the task's own logon context and could seed an empty board.")
                }
                if ($logonType -ne $attempt.RequiredLogonType) {
                    throw "Registered '$TaskName' has LogonType '$logonType' rather than $($attempt.RequiredLogonType)."
                }

                Write-Host "[task] $TaskName registered"
                Write-Host "[task]   shell   : $shell"
                Write-Host "[task]   logon   : $logonType"
                Write-Host "[task]   triggers: $($triggerClasses -join ', ')"
                Write-Host "[task]   listen  : $listenUrl"
                Write-Host "[task]   health  : $healthUri"
                Write-Host "[task]   database: $resolvedDb"
                if (-not (Test-Path -LiteralPath $resolvedDb)) {
                    Write-Host "[task]   database: (does not exist yet -- the first run restores the committed seed)"
                }
            }
            catch {
                $rollback = Undo-TaskRegistration -Name $TaskName -BackupXml $backupXml -HadPrevious $hadPrevious
                throw ("$($_.Exception.Message) $rollback")
            }

            if (-not $attempt.SurvivesReboot) {
                # Exit 3, not 0. What landed is registered and working for a logged-on session, but
                # #477 §9 criterion 3 -- comes back after a reboot with nothing started by hand --
                # is NOT met, and the exit code is the only part of this a script will read.
                $script:ExitCode = 3
                Write-Warning ("[task] '$TaskName' will NOT come back after an unattended reboot. Windows " +
                               "refused the AtStartup + S4U definition: $refusal -- both the boot trigger " +
                               "and the S4U principal need administrator. It was registered as " +
                               "$($attempt.Label) instead, so it starts only once $userId logs on. For " +
                               "reboot survival, re-run from an ELEVATED shell: powershell.exe -NoProfile " +
                               "-ExecutionPolicy Bypass -File `"$PSCommandPath`" -RequireStartupTrigger")
            }
            break
        }
    }

    if (-not $NoStart) {
        # Only start when the app is not already answering. Starting unconditionally would be
        # harmless (IgnoreNew refuses the duplicate) but the refusal reads like an error in the
        # task history, and a clean history is worth more than one saved condition.
        # A -WhatIf preview must stay side-effect free, so the probe itself is SKIPPED under
        # -WhatIf: the short-circuit means Test-KanbanFlowHealthy is never called, and the preview
        # falls through to the ShouldProcess branch below, which only announces the start.
        if ((-not $WhatIfPreference) -and (Test-KanbanFlowHealthy -Uri $healthUri)) {
            Write-Host "[task] KanbanFlow is already serving at $healthUri; not starting a second instance"
        }
        elseif ($PSCmdlet.ShouldProcess($TaskName, 'Start scheduled task')) {
            Start-ScheduledTask -TaskName $TaskName -TaskPath '\' -ErrorAction Stop
            Write-Host "[task] started $TaskName; waiting up to ${StartTimeoutSeconds}s for $healthUri (the first run publishes the app)"

            # POLL. Start-ScheduledTask returning is not evidence of anything: IgnoreNew refuses a
            # duplicate start WITHOUT throwing (measured: LastTaskResult becomes 0x800710E0 and the
            # cmdlet is silent), and the previous version printed "[task] started" and exited 0 in
            # exactly that case. Health is the only claim worth making.
            $deadline = (Get-Date).AddSeconds($StartTimeoutSeconds)
            $healthy = $false
            while ((Get-Date) -lt $deadline) {
                if (Test-KanbanFlowHealthy -Uri $healthUri -TimeoutSec 10) { $healthy = $true; break }
                Start-Sleep -Seconds 3
            }

            if ($healthy) {
                # "is serving", not "is ok": Test-KanbanFlowHealthy accepts degraded as well,
                # because degraded means the app opened its database and read from it and is
                # reporting something about that board (#498). Saying "ok" here would name a
                # verdict the probe did not actually require.
                Write-Host "[task] $healthUri is serving a database (status ok or degraded)"
            }
            else {
                $lastResult = '(unknown)'
                try {
                    $info = Get-ScheduledTaskInfo -TaskName $TaskName -TaskPath '\' -ErrorAction Stop
                    $lastResult = ('0x{0:X8}' -f $info.LastTaskResult)
                }
                catch { }
                throw ("Started $TaskName but $healthUri did not report a served database " +
                       "(status ok or degraded) within " +
                       "${StartTimeoutSeconds}s. LastTaskResult=$lastResult (0x800710E0 means Task " +
                       "Scheduler REFUSED the start because an instance is already running, so " +
                       "nothing new was launched). The task is registered and left in place.")
            }
        }
    }

    if ($script:ExitCode -ne 0) {
        Write-Host ("[task] exit $($script:ExitCode): registered, but WITHOUT reboot survival. " +
                    "See the warning above; one elevated run fixes it permanently.")
    }
    exit $script:ExitCode
}
catch {
    Write-Host "`nregister-kanbanflow-task ERROR: $($_.Exception.Message)" -ForegroundColor Red
    exit 2
}
