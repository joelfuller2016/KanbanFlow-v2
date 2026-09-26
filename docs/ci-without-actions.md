# CI without GitHub Actions

KanbanFlow-v2 intentionally uses **no GitHub Actions**. The AI review bots
(CodeRabbit, Copilot, Gemini) read the *diff* but never compile the code or run
the tests — so a statically-clean PR can still break the build, and a set of
individually-green PRs can break `master` once merged (exactly what happened with
the #142 NU1605 downgrade). These scripts close that gap by letting an **external
scheduler** run the same gate the bots can't. See issue #147.

That gate also runs the **NuGet audit**. Advisory hits surface during `dotnet restore` as `NU1902`
(moderate) or `NU1903` (high), and are re-emitted at build time from the restore assets — so
`-warnaserror` promotes them to errors even though the build step passes `--no-restore`. Measured:
reverting to the vulnerable `AngleSharp` with the suppression deleted fails the build with
`error NU1902` in both projects (exit 1, 2 errors). Close those by **bumping the dependency**, never
by adding an audit suppression — see
[reviews/2026-08-02-anglesharp-cve-suppression-removal.md](reviews/2026-08-02-anglesharp-cve-suppression-removal.md) (#320).
This gate never runs `npm audit`, and Dependabot alerts and security updates are disabled for
this repository (`gh api repos/.../vulnerability-alerts` → 404; `gh api repos/.../dependabot/alerts`
→ 403 "Dependabot alerts are disabled for this repository"; `gh api repos/.../automated-security-fixes`
→ `{"enabled":false}`), so nothing reports npm advisories (Playwright or any future `scripts`/e2e
dependency). Dependabot's weekly npm version-update PRs still deliver newer releases — security
fixes included — but don't mark which bumps are security fixes. Enabling Dependabot alerts is an
owner-side repository setting that, like version updates, uses no Actions minutes.

## Verify the current checkout

Run `pwsh -NoProfile -File ./scripts/verify.ps1` from the repository before pushing. Restore,
build and tests are required; formatting drift remains advisory. The gate explicitly exits 0 after
successful required steps and 1 after a required-step failure. This also sets the correct
`$LASTEXITCODE` when another PowerShell script calls it with `& ./scripts/verify.ps1`.

The invocation regression runs with `node --test scripts/verify.test.mjs` and requires Node.js and
PowerShell 7+. It executes a copy of the real gate with a native command stand-in, checking exit
codes, step ordering, failure stops and restoration of the caller's working directory. It does not
replace the real application build and tests. See the
[exit-status review and measured results](reviews/2026-09-08-verify-exit-253.md).

## Scripts

| Script | Purpose |
|--------|---------|
| `scripts/_ci-common.ps1` | Shared helpers (dot-sourced; not run directly). |
| `scripts/pr-ci.ps1` | Build + test an open PR (or all of them) in an isolated clone; optionally post a **commit status** (`pr-build-test`) and an upserted **PR comment**. |
| `scripts/master-health.ps1` | Build + test the default branch; open/update/close a single tracking issue to mirror its health. |
| `scripts/resolve-threads.ps1` | Resolve all unresolved review threads on a PR with exponential backoff (so merges pass the conversation-resolution gate without `--admin`). |

All scripts are self-contained (they work in a dedicated clone under the temp dir,
never touching your working tree) and require: PowerShell 7+, `git`, `gh`
(authenticated), and the **.NET 10 SDK**.

## Usage

```powershell
# Dry run — build/test one PR, post nothing
./scripts/pr-ci.ps1 -Pr 146

# Gate one PR: post the commit status + an upserted comment
./scripts/pr-ci.ps1 -Pr 146 -PostStatus -PostComment

# Gate every open PR (what the scheduler runs)
./scripts/pr-ci.ps1 -PostStatus -PostComment

# Default-branch health (opens an issue if red, closes it when green again)
./scripts/master-health.ps1

# Resolve a PR's review threads before merging (no --admin)
./scripts/resolve-threads.ps1 -Pr 148
```

## Scheduling (pick one)

The scripts are scheduler-agnostic. Two supported options:

### A. Local — Windows Task Scheduler

The SDK is guaranteed (your dev box has .NET 10); runs only while the machine is on.

```powershell
$action  = New-ScheduledTaskAction -Execute 'pwsh' `
    -Argument '-NoProfile -File C:\path\to\KanbanFlow-v2\scripts\pr-ci.ps1 -PostStatus -PostComment'
$trigger = New-ScheduledTaskTrigger -Once -At (Get-Date) `
    -RepetitionInterval (New-TimeSpan -Hours 1)
Register-ScheduledTask -TaskName 'kbf2-pr-ci' -Action $action -Trigger $trigger
```

### B. Remote — Claude scheduled routine

Always-on (like the `dependabot-automerge-kanbanflow` routine). The cloud env must
provide `gh` (authenticated), `pwsh` 7+, and the .NET 10 SDK; bootstrap whatever
is missing at the top of the routine prompt:

```bash
command -v dotnet || curl -sSL https://dot.net/v1/dotnet-install.sh | bash -s -- --channel 10.0
gh repo clone joelfuller2016/KanbanFlow-v2 && cd KanbanFlow-v2   # gh auth handles the private repo
pwsh ./scripts/pr-ci.ps1 -PostStatus -PostComment
pwsh ./scripts/master-health.ps1
```

Minimum cron interval is 1 hour.

## Make it a hard merge gate

Once `pr-ci.ps1 -PostStatus` is posting the `pr-build-test` status, require it in
branch protection so red PRs can't merge — real CI, zero Actions:

```bash
# Pass a full required_status_checks body (strict + checks list) so the PATCH validates:
echo '{"strict":false,"checks":[{"context":"pr-build-test"}]}' \
  | gh api -X PATCH repos/joelfuller2016/KanbanFlow-v2/branches/master/protection/required_status_checks --input -
```

This also removes the need for `--admin` merges: `resolve-threads.ps1` clears the
conversation-resolution gate, and the status check becomes the real gate.
