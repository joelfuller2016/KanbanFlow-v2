<#
.SYNOPSIS
    Register (or re-register) the scheduled task that keeps KanbanFlow running. See #477.

.DESCRIPTION
    #477 §8 step 3 asked for a task that starts ON LOGON and restarts on failure, so that
    `/api/admin/health` answers after a reboot with nothing started by hand. What actually got
    registered was a single one-shot time trigger with a 10-minute repetition, created by hand at
    a console — no logon trigger, and no committed script, so the arrangement existed only as
    machine state that nobody could review, reproduce on another box, or restore after a mistake.

    This script is that arrangement, written down and re-runnable. It registers TWO triggers,
    deliberately:

      AtLogOn        the one #477 asked for. This is what survives a reboot.
      Once + PT10M   a keepalive. With -MultipleInstances IgnoreNew, a repeat that finds the app
                     already running is REFUSED by Task Scheduler with 0x800710E0 ("the operator
                     or administrator has refused the request"), so the repeat costs nothing while
                     the app is healthy and revives it within ten minutes if it ever exits cleanly.
                     That refusal code is the system working as designed and must not be "fixed".

    Restart-on-failure covers the other half: `start-kanbanflow.ps1` exits with the app's own exit
    code, so a crash is a task failure and Task Scheduler retries it three times a minute apart —
    faster than waiting for the next repeat.

    Idempotent: -Force replaces any existing definition, so running it twice leaves exactly one
    task. It verifies its own work afterwards and exits non-zero if the registered task does not
    carry both triggers, so "it ran" and "it worked" are not the same claim.

    Windows PowerShell 5.1 compatible -- no ternary, no null-coalescing, no null-conditional. The
    repo's floor is 5.1 and pwsh 7 is not guaranteed on a fresh box; the shell that RUNS the task
    is resolved below rather than hardcoded to a WindowsApps path for the same reason.

    No elevation required: the task is registered for the current user with an interactive
    principal, which is exactly the authority a local user already has.

.PARAMETER TaskName
    Scheduled task name. Defaults to kanbanflow-app.

.PARAMETER KeepaliveMinutes
    Interval for the keepalive repetition. Defaults to 10.

.PARAMETER NoStart
    Register only. By default the task is started when KanbanFlow is not already answering.

.PARAMETER Unregister
    Remove the task instead of registering it.

.EXAMPLE
    powershell.exe -ExecutionPolicy Bypass -File scripts/register-kanbanflow-task.ps1

.EXAMPLE
    pwsh -File scripts/register-kanbanflow-task.ps1 -WhatIf
#>
#requires -Version 5.1
[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [ValidateNotNullOrEmpty()][string] $TaskName = 'kanbanflow-app',
    [ValidateRange(1, 1440)][int] $KeepaliveMinutes = 10,
    [switch] $NoStart,
    [switch] $Unregister
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

try {
    if (-not (Get-Command -Name Register-ScheduledTask -ErrorAction SilentlyContinue)) {
        throw "The ScheduledTasks module is unavailable. This script needs Windows 8 / Server 2012 or newer."
    }

    $RepoRoot   = Split-Path -Parent $PSScriptRoot
    $LauncherPs = Join-Path $PSScriptRoot 'start-kanbanflow.ps1'
    if (-not (Test-Path -LiteralPath $LauncherPs)) {
        throw "Launcher not found at $LauncherPs"
    }

    # A task records an ABSOLUTE path to the launcher, so registering from a git worktree points
    # the machine at a directory that `git worktree remove` will delete -- and the failure surfaces
    # days later as an app that stopped coming back, with a task that still looks correctly
    # registered. Refuse by default; -Force-style override is deliberately NOT offered, because the
    # only honest fix is to run this from the main checkout. A DELIBERATE non-default -TaskName is
    # the escape hatch: that is a throwaway task being exercised in a sandbox, which is the
    # supported way to test this script and cannot clobber the real one.
    $isDefaultTaskName = ($TaskName -eq 'kanbanflow-app')
    if ($isDefaultTaskName -and $PSScriptRoot -match '[\\/]\.worktrees[\\/]') {
        throw ("Refusing to register '$TaskName' from a git worktree ($PSScriptRoot). The task " +
               "would store a launcher path that disappears when the worktree is removed, and the " +
               "failure would surface days later as an app that stopped coming back. Run this from " +
               "the main checkout -- or pass a throwaway -TaskName to test from here.")
    }

    if ($Unregister) {
        if (Get-ScheduledTask -TaskName $TaskName -ErrorAction SilentlyContinue) {
            if ($PSCmdlet.ShouldProcess($TaskName, 'Unregister scheduled task')) {
                Unregister-ScheduledTask -TaskName $TaskName -Confirm:$false
                Write-Host "[task] unregistered $TaskName"
            }
        }
        else {
            Write-Host "[task] $TaskName is not registered; nothing to remove"
        }
        exit 0
    }

    # Resolve the shell that will run the launcher. pwsh 7 is preferred, but the launcher is 5.1
    # compatible on purpose, so Windows PowerShell is a real fallback rather than a degradation.
    # Resolving beats hardcoding the WindowsApps path the hand-made task used: that path carries a
    # version number and an app-package id, both of which change under the operator on an update.
    $shell = $null
    $pwshCommand = Get-Command -Name pwsh -CommandType Application -ErrorAction SilentlyContinue
    if ($pwshCommand) {
        $shell = $pwshCommand.Source
    }
    else {
        $shell = Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'
    }
    if (-not (Test-Path -LiteralPath $shell)) {
        throw "No PowerShell host found to run the launcher (looked for pwsh, then $shell)."
    }

    # -ExecutionPolicy Bypass so the task does not die on a machine whose user policy is Restricted
    # -- a scheduled task has no console to prompt at, so a policy block there is a silent failure.
    $action = New-ScheduledTaskAction `
        -Execute $shell `
        -Argument ('-NoProfile -NonInteractive -ExecutionPolicy Bypass -WindowStyle Hidden -File "{0}"' -f $LauncherPs) `
        -WorkingDirectory $RepoRoot

    # Two triggers on one task. Building the repetition on its own -Once trigger avoids copying a
    # .Repetition onto a logon trigger, which is the fragile idiom this could otherwise have used.
    # Omitting -RepetitionDuration is what yields an indefinite repeat (verified on 5.1: Duration
    # comes back empty, matching the hand-registered task's PT10M-with-no-end).
    # Resolve the account from the real Windows identity: $env:USERDOMAIN can be empty or
    # overridden, which would hand both cmdlets an invalid or unintended account.
    $taskIdentity = [Security.Principal.WindowsIdentity]::GetCurrent().Name
    if ([string]::IsNullOrWhiteSpace($taskIdentity)) {
        throw "Could not resolve the current Windows identity for the scheduled task."
    }

    $logonTrigger = New-ScheduledTaskTrigger -AtLogOn -User $taskIdentity
    $keepaliveTrigger = New-ScheduledTaskTrigger -Once -At (Get-Date) `
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
    # look nothing like its cause.

    $principal = New-ScheduledTaskPrincipal `
        -UserId $taskIdentity `
        -LogonType Interactive `
        -RunLevel Limited

    if ($PSCmdlet.ShouldProcess($TaskName, 'Register scheduled task (AtLogOn + keepalive)')) {
        Register-ScheduledTask `
            -TaskName $TaskName `
            -Action $action `
            -Trigger @($logonTrigger, $keepaliveTrigger) `
            -Settings $settings `
            -Principal $principal `
            -Description "Keep KanbanFlow serving on http://127.0.0.1:5123 for local agent use (KanbanFlow-v2#477)." `
            -Force | Out-Null

        # Verify what actually landed rather than trusting that the call returned. A task missing
        # its logon trigger is precisely the state #477 found, and it looked fine from the outside.
        $registered = Get-ScheduledTask -TaskName $TaskName
        $triggerClasses = @($registered.Triggers | ForEach-Object { $_.CimClass.CimClassName })
        Write-Host "[task] $TaskName registered"
        Write-Host "[task]   shell   : $shell"
        Write-Host "[task]   triggers: $($triggerClasses -join ', ')"

        if ($triggerClasses -notcontains 'MSFT_TaskLogonTrigger') {
            throw "Registered $TaskName has no logon trigger -- it would NOT come back after a reboot."
        }
        if ($triggerClasses -notcontains 'MSFT_TaskTimeTrigger') {
            throw "Registered $TaskName has no keepalive trigger."
        }
    }

    if (-not $NoStart) {
        # Only start when the app is not already answering. Starting unconditionally would be
        # harmless (IgnoreNew refuses the duplicate) but the refusal reads like an error in the
        # task history, and a clean history is worth more than one saved condition.
        $healthy = $false
        try {
            # A -WhatIf preview must stay side-effect free, so the probe itself is skipped.
            # The StatusCode test lives INSIDE the gate too: $response is otherwise never
            # assigned, and Set-StrictMode would throw on reading it — caught below, but
            # relying on an exception for control flow is not the same as not doing it.
            if (-not $WhatIfPreference) {
                $response = Invoke-WebRequest -Uri 'http://127.0.0.1:5123/api/admin/health' -UseBasicParsing -TimeoutSec 3
                if ($response.StatusCode -eq 200) { $healthy = $true }
            }
        }
        catch {
            $healthy = $false
        }

        if ($healthy) {
            Write-Host "[task] KanbanFlow is already serving; not starting a second instance"
        }
        elseif ($PSCmdlet.ShouldProcess($TaskName, 'Start scheduled task')) {
            Start-ScheduledTask -TaskName $TaskName
            Write-Host "[task] started $TaskName (first run publishes the app, which can take a minute)"
        }
    }

    exit 0
}
catch {
    Write-Host "`nregister-kanbanflow-task ERROR: $($_.Exception.Message)" -ForegroundColor Red
    exit 2
}
