# Work journal

This is the running record of the work Claude Code sessions do on this repository: what was done, the ids to check
it by (full commit SHAs, PR and issue numbers), and what comes next. It is **one file**, append-only, with the
newest entry first. It is committed and pushed straight to `master` after every milestone, so the record survives a
closed session. Code changes still go through focused PRs; only this file is pushed to `master` directly.

Entries are UTC. Names such as `impl-a1c` or `review-m8r4` are the session's subagents (implementers,
reviewers and docs writers). "The lead" is the session that directs them and makes every GitHub write. A path
starting `SCRATCH/` is in that session's local scratch folder and exists only on that machine.

## Standing instructions from the owner

These apply to all work in this repository. Each is the owner's instruction, with the date it was given.

- **Journal (2026-09-27).** Use this one file for all work. Commit and push it to `master` after every milestone.
- **Commit and push often (2026-09-27).** At every milestone nothing that should be committed stays local, in
  case the session closes.
- **One focused PR per branch (2026-09-27).**
  - Commits on a branch are focused into its PR. Every local branch is pushed for a PR, and every branch on
    GitHub has a PR.
  - Work in progress is a draft PR.
  - Fixes are new commits, and the owner's squash merge collapses them. An open PR is never force-pushed.
  - The `archive/*` backup branches were triaged and are deliberately left without PRs, by the owner's choice.
- **Focused subagents (2026-09-27).** Each subagent gets one goal, a bounded scope and a stop condition. Its
  report is at most 15 lines and ends with a `JOURNAL:` line, which the lead appends here. Details go in files,
  not messages.
- **Merges into `master` are the owner's.** Agents open, update and review PRs, but never merge them.
- **Attribution.**
  - Commits end with exactly `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`.
  - PR descriptions end with exactly the one-line `🤖 Generated with [Claude Code](https://claude.com/claude-code)`
    footer.
  - No session URL goes into anything posted.

## Before this journal (2026-09-26 and 2026-09-27)

A review of the repository and a test run on 2026-09-26 filed findings as issues and fixed them in PRs.

- **Merged by the owner:**
  - #540 (D0, the dependency-race watchdog margin);
  - #543 (patching the nesting schema onto older databases);
  - #544 (Dependabot for every manifest);
  - #545 (the bot-review repo overrides);
  - #557 (MCP wrapper/native parity);
  - #559 (pinning the smoke's embedding profile).
- **Issues filed:** #526-#539, #541, #548, #549, #550 and #561. Each has a full implementation plan.
- **In flight when this journal started:**
  - #560: MCP stdin EOF drain, for #539;
  - #562: the test-runner fixture leak on cancel, for #541;
  - M8, for #536;
  - S7, for #549.

## Entries (newest first)

- 2026-11-01 01:30Z (PR #580 — CodeRabbit finding fixed, 0 unresolved threads, 50 total):
  - CodeRabbit Minor docs/idea-workspaces.md:117 fix: 5cf4438 — replaced "The current local checkpoint" with "At the T06 checkpoint," and "Current discovery" with "Discovery at this checkpoint" to scope T06 counts (1,437 .NET / 339 script) distinctly from the final gate (1,565 / 362). Thread PRRT_kwDOSA8y6s6yWGAK resolved.
  - All 50 review threads resolved (0 unresolved). No pending automated reviews outstanding.
  - PR #580 is clean. Head: 5cf4438. Owner action needed: rebase feat/579-idea-workspaces onto master and merge.

- 2026-11-01 01:04Z (PR #580 — PHASE 2 review cycle on bed7e91, 4 findings fixed):
  - Codex P2 stale-poll fix: e7acca6 — captured `polledContext` before GET, guard against context replacement on response + inside InvokeAsync callback. Thread PRRT_kwDOSA8y6s6yWCUO resolved.
  - Copilot finding Commands.cs:129 fix: e46465a — inverted ClearSavedSection guard (&&→||) so section is retained when DraftAtSend is absent. Thread PRRT_kwDOSA8y6s6yWCrn resolved.
  - Copilot finding WorkspaceCanvas.razor:31 fix: e46465a — added aria-pressed to drag-handle button. Thread PRRT_kwDOSA8y6s6yWCry resolved.
  - Copilot finding WorkspaceOutline.razor:8 fix: e46465a — added aria-pressed to outline button. Thread PRRT_kwDOSA8y6s6yWCr3 resolved.
  - 0 unresolved threads (49 total). Awaiting Codex code+security reviews on e7acca6/e46465a and CodeRabbit review on e46465a.

- 2026-11-01 00:37Z (PR #580 — clean, all automated reviews passed on cda6063):
  - Codex code review on cda6063: completed, has_unresolved_findings=false ✅
  - Codex security review on cda6063: completed, has_any_findings=null ✅
  - Codex code review on beb5cd8: completed, has_unresolved_findings=false ✅ (confirmed earlier)
  - Codex security review on beb5cd8: completed, has_any_findings=null ✅ (confirmed earlier)
  - 0 unresolved review threads (45 total, all resolved). CodeRabbit paused at c794f9c; all c794f9c finding threads resolved.
  - PR #580 is clean. Owner action needed: rebase feat/579-idea-workspaces onto master (master has advanced by ~33 JOURNAL.md commits since PR base 7587549), then merge.

- 2026-11-01 02:15Z (scheduled PR monitor, continued session — PHASE 2 completion on #580):
  - Committed transport signal-exit fix (cda6063): `scripts/workspace-transport.mjs` `close()` now checks both `exitCode` and `signalCode` so `once(child, 'exit')` is not awaited on a process that already exited via signal. Pushed to feat/579-idea-workspaces.
  - Resolved 4 remaining unresolved review threads on PR #580: PRRT_kwDOSA8y6s6yV2EQ (HIGH canvas init — fix beb5cd8, reply+resolved), PRRT_kwDOSA8y6s6yVxfg (delivery field clobber P2 — resolved, reply from prior session), PRRT_kwDOSA8y6s6yVxgA (checkpoint list normalization P2 — resolved, reply from prior session), PRRT_kwDOSA8y6s6yVxgU ($new service-level collision P2 — resolved, reply from prior session).
  - PR #580 now has 0 unresolved review threads. Waiting for Codex code+security reviews on cda6063/beb5cd8 and CodeRabbit resume.
  - Next: monitor Codex review completion and any new CodeRabbit findings; notify owner when PR is clean.

- 2026-11-01 01:17Z (scheduled PR monitor, four-hundred-and-forty-third run): build gate UP (.NET 10.0.114 via Ubuntu 24.04 Microsoft apt feed, installed fresh this run).
  - 5 open PRs: #580 (non-draft, feat/579-idea-workspaces, head SHA beb5cd89 — new commit since last run); #578 (non-draft, head SHA 17780aa); #564, #521, #511 (drafts, skipped).
  - New commit on #580: beb5cd89 "fix: catch JSException in canvas OnAfterRenderAsync to prevent circuit termination" — pushed after the last monitor run's clean confirmation. No new bot review threads yet for this commit.
  - PHASE 1: 0 merges. #580 mergeable=false, mergeable_state=dirty (master advanced 31 commits — all JOURNAL.md entries — since PR base 7587549; conflicts need owner rebase). 0 unresolved review threads. #578 build gate FAILS — same known NU1608 (Microsoft.AspNetCore.OpenApi 10.0.14 requires Microsoft.OpenApi >= 2.12.2 && < 3.0.0; version 3.11.0 resolved); no new comment posted (failure already on PR from 2026-10-28).
  - PHASE 2: 0 unresolved threads on #580 (45 total, all resolved/outdated). 0 unresolved threads on #578. No comment work needed.