# PR #511 integration and CodeRabbit follow-up — September 13, 2026

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

## Integration verification at 6345d5d

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


## CodeRabbit review corrections after 6345d5d

CodeRabbit completed full review `a571c3a4-2dff-4b91-8c2c-485ca3afe5b5` on `6345d5d` and posted 30 actionable comments plus one duplicate. The following corrections were verified in the working tree based on that commit; the resulting commit and subsequent review are recorded on PR #511 and task 189.

- Batch current audit snapshots by entity type and parent context by identity. Both 12-task and 100-task graph deletions require at most five SELECTs inside the audited save. Fresh database values remain authoritative, including stale tracked deletes. Existing savepoint/rollback/generated-ID retry and semantic-event contracts remain covered.
- Retain canonical string `EntityKey` values for GUID and long IDs, while preserving legacy integer `EntityId`. Include keys in workflow semantic events, history filters, API results and the timeline. The idempotent schema patch recovers old keys without rewriting snapshot text. Tests cover two distinct request GUIDs, IDs above Int32, legacy integer filters, type-only queries and malformed historical JSON.
- Cancel superseded/disposed workflow and activity loads, invalidate pending request refreshes on selection, and pass request cancellation through changed API mutations. Add stable Razor keys and distinct accessible disclosure/subtask labels; keep creation outside outline navigation. These components rebuilt successfully; browser execution remains unverified.
- Emit MCP `structuredContent` only for object results; retain full JSON text for arrays, null and scalar values. Recover omitted GitHub headers using the durable issue number when a synced title has no legacy prefix. Preserve provenance/identity/quarantine checks. Browser negative assertions now observe a bounded settling interval.
- Batch dependency cleanup when publishing multiple changed tasks. Reject incomplete dependency graphs before hashing publication tokens, while keeping their older history readable with an unusable token. Native idea tools expose workflow conflicts as MCP errors and retain rollback behavior.
- Correct stale counts, run links, merge-scope notes, promotion contracts and test assertions. Strengthen below-cap archived ordering, exact before-values and the specific audit failure. Root-escape fixtures now contain real Git metadata so the boundary checks cannot pass merely because the path is missing.
- The draft-cardinality premise was incorrect: drafts are stored in `PlanVersion.DraftJson`; only publication creates a `PlannedTask` with a non-nullable `TaskItemId` and unique FK. Both diagrams now show the actual inverse optionality: an ordinary task can have zero or one planned mapping, and every planned mapping has exactly one task.

### Actual correction verification

Same Windows/PowerShell/.NET/Node environment as above. The required gate ran after all production changes, without a competing verification suite.

| Command / observation | Result |
| --- | --- |
| `dotnet build KanbanFlow.slnx -warnaserror --no-restore` | Exit 0, zero warnings/errors. An initial command naming the nonexistent `.sln` failed before building; corrected to the repository's `.slnx`. |
| Initial targeted .NET regression run | Exit 1: 432 passed, one failed. The new publication guard also blocked legacy history reads; corrected context to retain readable history and reject only invalid mutations. |
| Rebuilt targeted .NET regression run | Exit 0: all 438 cases passed. The final full suite additionally includes the semantic request-key regression. |
| Initial `npm run test:scripts` | Exit 1: durable-link lookup bypassed the existing provenance mismatch diagnostic. Corrected precedence so complete provenance remains checked and durable lookup replaces title fallback only. |
| Final `npm run test:scripts` | Exit 0: 321 passed, none failed/skipped. Real stdio child processes cover object/array/null/scalar output and workflow forwarding against fixture HTTP responses. |
| Final `scripts/verify.ps1` | Exit 0: build had zero warnings/errors; 1,554 .NET tests passed, none failed/skipped. Includes batch read limits, migration compatibility, complete identities, invalid publication and native tool-to-service conflicts. Formatting drift remains advisory. |
| `git diff --check` | Exit 0. |

Logs: `_sandbox/pr-review/build-511-findings.log`, `targeted-511-findings.log`, `targeted-511-findings-2.log`, `scripts-511-findings.log`, `scripts-511-findings-2.log`, and `verify-511-findings.log`.

Automatic approval review still blocks the temporary HTTP app launch. Actual external HTTP/native/stdio-to-app checks, browser walkthroughs and human acceptance remain pending. This review does not treat historical browser evidence as validation of the new cancellation/accessibility changes.
