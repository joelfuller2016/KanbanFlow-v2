# PR #511 integration and CodeRabbit follow-up ? September 13, 2026

Request: review both open KanbanFlow PRs, ensure CodeRabbit has reviewed, repair actionable findings and commit. This follow-up does not authorize merging or deploying the review build. It continues TC-509 and maintenance issue #515.

Source inputs: PR head `516432fe4b8c83fa05e7bfe5f6a23e7c6262ce48`, current master `8cdd684322eec15485a400c81c3372999c1789fa`. The review uses an isolated worktree on `review/pr-511-coderabbit`, published to the existing `feat/unified-workflow-509` PR branch. The PR discussion records the resulting commit and CodeRabbit review.

The current running board does not contain historical task 227 or maintenance request `2a1272d9-1d11-49a8-b8f7-a1580d9705e1` (both returned 404). Project 2 task 189 records this follow-up and its verification; project 3 task 186 records PR #521. No old task data was reconstructed or overwritten.

## Findings and changes

- All 29 prior PR review threads were resolved before integration. CodeRabbit's newer replies confirm the `8cce41d` task-snapshot query repair; stale monitor comments are not new findings. Preserve the operation-local snapshot and constant-query regressions.
- Resolve the nine merge conflicts with current master. Preserve workflow history, complete source text, outline selection, title presentation, atomic external projection, durable links and nonterminal external-write deletion guards.
- Load external links before task/feature/project cascades and audit their deletion in the same transaction. Six regression cases cover deletion and forced audit-insert rollback at all three levels.
- Preserve outbound Markdown whitespace in both the script body builder and the persisted operation marker. Three service regressions plus the script case cover this behavior.
- Apply current immutable repository/task identity and quarantine checks to legacy content repair, including a fresh check before a write. Reject ambiguous source blocks without changing cards. Retain complete differing board text.
- Reconcile removed comment-clipping flags with the current write-journal CLI tests. Keep the explicit issue-create gates and all supported recovery options.

## Actual verification

Windows 11; PowerShell 7.6.6; .NET SDK 10.0.401; Node 24.19.0. Logs are ignored under `_sandbox/pr-review/` in this worktree.

| Command / observation | Result |
| --- | --- |
| New audit/body regression cases before production corrections | 9 failed, 0 passed; reproduced missing audit snapshots/rollback and trailing-whitespace loss. |
| `node --test scripts/portfolio-sync.test.mjs` | Exit 0; 94 passed. |
| `npm run test:scripts` | Exit 0; 314 passed, none failed/skipped. Includes real stdio wrapper processes and mock HTTP adapter coverage. |
| Early required-gate attempts with concurrent verification workloads | Build passed; existing race tests exceeded their 10-second watchdogs. First attempt: 1 failure; second: 2 failures. Failures are retained in logs, not suppressed. |
| `dotnet test tests/KanbanFlow.Tests --no-build --filter FullyQualifiedName~TaskDependencyConcurrencyTests -c Debug` | Exit 0; all 3 race cases passed separately. |
| Final `scripts/verify.ps1`, run without a competing verification suite | Build had 0 warnings/errors; all 1,541 .NET tests passed, 0 failed/skipped. Includes the nine new regressions and existing external-write/dependency concurrency cases. Gate exit 0. Formatting drift was reported as advisory, consistent with the repository policy. |

Automatic approval review rejected starting the isolated Kanban HTTP verification server, stating only **blocked by policy**. No launch workaround was attempted. Current external HTTP/native/stdio-to-app and browser walkthroughs remain unverified. Prior dated runs retain their original scope and source; they are not evidence for this integration. Actual human UI acceptance remains pending. No production restart, live database migration, deployment or PR merge was performed.
