# PR review and regression expansion

Date: 2026-11-01. Source: `origin/master` at `789c6b4743f42663365cc04d1bb1e5429788a9d4`.

The owner requested: “review all prs and fix and test what possible. Merge when green the run unit tests. build out testing as much as posisble”. The live request `9327981b-5625-4912-90ee-efdae8ad9928` in project 2 owns execution and result status. This is the single design/acceptance plan for this pass; root `JOURNAL.md` owns work history.

## Outcome and boundaries

Audit every current open PR, repair demonstrated failures and strengthen meaningful regression coverage. Publish a focused change, complete independent and configured bot review, merge when the exact head is green, and freshly run units on the actual merged primary. The initial GitHub queue is empty, so the executable increment is a testing/repair PR based on current primary.

The original dirty checkout, persistent app on 5123 and local UI review preview on 5199 remain available. The separate UI usability plan and human adoption gate remain authoritative; this testing pass supplies no invented human feedback. No dependency/framework upgrade, CI, Docker, authentication or permission-policy change is included.

Tests protect observable behavior rather than pursuing a count. Static pairing found 193 source files and 130 test files, with 155 paired and 38 unpaired sources. That heuristic is a discovery aid, not line/branch coverage or proof that the unpaired files lack runtime tests. Existing workflow rollback, competing-write and stale-evidence tests already have substantial assertions.

## Requirements

- R1. Read the actual PR queue, head SHAs, review findings and owning source before making readiness claims.
- R2. The production stdio wrapper accepts only a matching terminal native JSON-RPC response, preserving valid result/error content and business retry identity.
- R3. Complete authored result reports reach both human-visible copies, current public field limits remain exact, rejected writes remain atomic, and context graph/edit tokens share one coherent read.
- R4. Existing browser expectations reflect real source/audit behavior without weakening meaningful keyboard, rendering or data-integrity assertions.
- R5. Fresh focused and complete gates, independent review, and the configured PR bot loop cover the exact published head. Findings are reproduced or explicitly rejected with evidence.
- R6. Merge only an eligible green head without bypassing repository controls, then freshly build/run units on the actual merged primary. Collect available coverage without inventing an overall percentage.
- R7. Preserve complete content, audit semantics, existing work, runtime/configuration boundaries and unfinished human review. Keep raw evidence in ignored storage and publish the required journal only through its existing authorization.

## Phases and responsibilities

Phase 1 establishes the current queue, static pairing, observed baseline and concrete cases (T1). Phase 2 implements independently owned workflow, transport and browser-test changes (T2–T4). Phase 3 integrates exact-source checks, review, merge and post-merge units (T5–T6). The app owns actual task state; the local GSD progress file is a resumption pointer, not another tracker.

| Responsibility | Files | Boundary |
|---|---|---|
| Workflow test worker | `tests/KanbanFlow.Tests/Services/WorkspaceService.ResultsTests.cs`, `WorkspaceService.QueryTests.cs`, narrowly related test helpers | Existing real SQLite conventions; no production seam or dependency change |
| Transport worker | `scripts/mcp-server.mjs`, `scripts/workflow-tools.test.mjs` | Test the real spawned stdio wrapper; preserve existing tool schemas and successful values |
| Parent browser/integration | `scripts/ui-e2e.mjs`, this plan, test documentation, request results and journal | Preserve unrelated checks and actual audit behavior |
| Independent verifier/reviewer | Read-only diff and owned test executions/artifacts | Never repair assertions merely to get green |

Temporary controlled mutations of `WorkspaceService.Context.cs` and `WorkspaceService.Results.cs` may prove a new assertion can fail. They must be narrow, evidenced and restored byte-identically before another test or commit. A genuine product defect requires its own narrow repair and updated plan/evidence. No production state or other worker's edits may be reverted.

## Implementation units

### T1. Audit the queue and establish meaningful test targets

Read the complete current open PR queue and default branch; preserve original checkout changes. Execute the installed static source-pairing analyzer once and use existing tests to distinguish genuine assertion gaps from heuristic omissions. Capture a current-source existing UI baseline in a disposable process/database on an alternate port. Preserve actual failure output and input hashes.

Acceptance: the queue/head readback, baseline result, source/test inventory and bounded behavioral cases are recorded. No empty PR queue is reported as a merge, and no static pairing figure is reported as measured coverage.

### T2. Strengthen workflow report, boundary and snapshot assertions

Add `RecordEvidence_MirrorsCompleteAuthoredReportIntoMessageAndTaskComment`, asserting exact independently constructed multiline Markdown/Unicode report text in both the request `assistant-report` and task `Verification` comment, author/source/identity, unaccepted evidence and Review status.

Add parameterized `RecordEvidence_FieldLengthBoundary_PreservesOrRejectsAtomically` for Summary 20,000, Checks 20,000 and CodeReference 2,000, and `Conclude_FieldLengthBoundary_PreservesOrRejectsAtomically` for Conclusion 50,000 and Limitations 10,000. Exercise limit−1, limit and limit+1 for every field. Valid text remains exact. Rejection leaves request version, results/evidence, messages/comments, audit rows and receipt state unchanged. These are tests of existing limits, not new limits.

Add `ContextAsync_ConcurrentTaskEdit_DoesNotPairOldFieldsWithNewEditToken` using existing EF/SQLite interceptors and an independent bounded writer at the graph/snapshot boundary. Assert coherent returned fields/token, actual writer participation, and a fresh new description/different token after the writer completes. Use deterministic synchronization and public outcomes; do not use arbitrary sleeps or widen production code merely for a test.

Acceptance: each behavior has concrete named assertions and focused clean execution. Prove selected boundaries with temporary report-copy, limit and read-transaction mutations; record only actually executed controls. Review all new assertions for false positives and secondary state checks.

### T3. Repair and test production native-response correlation

The current production wrapper accepts plain JSON without ID/terminal validation and accepts the first same-ID SSE object or last unmatched object. Correct it to select an exact-type matching JSON-RPC 2.0 terminal response, distinguishing a result/error from a server request. Preserve valid structured-content preference, error code/message/data, nullable values and retry keys. A small predicate shared by this JSON/SSE path is sufficient; do not rewrite the wrapper.

Extend the existing real-wrapper HTTP responder to emit raw JSON/multiple SSE frames without manufacturing correct IDs. Collect request bytes and decode UTF-8 once so the test fixture cannot corrupt split Unicode characters.

Test families: `wrapper selects the matching terminal SSE response after unrelated traffic`; JSON/SSE `wrapper rejects native responses without a matching terminal ID` for wrong numeric, numeric-equivalent string, missing ID and request-only envelopes; and `wrapper preserves correlated native errors after unrelated SSE traffic`. Successful mixed traffic contains heartbeat/progress, foreign response, same-ID server request and exact full Markdown/Unicode result. Invalid responses produce error −32000 with no successful result; a later valid call succeeds with exactly one HTTP call per explicit client call and unchanged business key. Correlated errors retain exact code/message/nested data.

Acceptance: the production-wrapper regressions fail against the original parser, pass after the narrow fix, and existing wrapper cases remain green. Preserve raw red/green receipts and source hashes; a copied parser-only test is insufficient.

### T4. Correct source-proven browser fixture assumptions

Run the unchanged existing UI suite first. If the expected empty-project failure is reproduced, preserve its original receipt and compare the rendered recency with the actual API/creation audit. An API-created empty project has `ProjectCreated` activity: retain absence checks for progress, next-task and claim placeholders, but assert that displayed recency equals the real API instant. Keep the genuine zero-audit service case and existing keyboard behavior assertions intact.

Acceptance: the owning stale expectation is demonstrated, the smallest harness-only correction passes, and no Home/audit behavior or unrelated UI presentation is changed to satisfy the fixture.

### T5. Verify, review, publish and merge the exact change

Inspect the final diff and test requirement matrix. Run focused tests and representative controlled regressions, then the canonical warnings-as-errors gate, full serialized Node suite and corrected broad UI suite against frozen inputs. Use separate temporary folders/ports and sequential builds where shared outputs require it. Review the source independently and assess test quality, missing assertions and production consumers.

Publish the focused branch/PR under the current request. The PR body records problem, impact, documentation, scope and actual testing. Run the existing Copilot/CodeRabbit bot loop with bounded waits and exact-head binding; triage findings and reverify repairs without silently treating a skipped/failed review as approval. Real behavior defects remain blocking. Decline cosmetic changes with a reason; after at most three review rounds, report remaining material findings rather than merge them.

Acceptance: the exact current PR head has green executed local gates and no unaddressed material finding. Merge with an expected-head check through normal repository controls; do not bypass protection or alter settings. A primary branch advance requires checking its delta and affected validation before merge.

### T6. Verify the actual merged primary and record delivery

Fetch the actual merge result into an isolated primary checkout. Freshly build/run the .NET unit suite, using the installed coverlet collector when available, and run the Node unit suite on that merged source. Inspect actual coverage with its scope/limits rather than infer it from pairing or count. Verify all observed results, source/input hashes, queue state and published commit readbacks.

Acceptance: post-merge units have a successful process exit and actual counts on the read-back merged primary. A concise behavior-to-exact-test matrix, failed-attempt/counterexample evidence and limitations are recorded in app results and the repository's existing journal. Original work and task-external processes remain preserved.

## Verification and stop conditions

| Gate | Evidence |
|---|---|
| Focused workflow tests | Exact generated method names, assertions and successful command |
| Focused production-wrapper tests | Original-parser red, repaired green, unchanged request identities and complete content |
| Canonical gate | `scripts/verify.ps1` actual exit/counts; build warnings/errors fail; existing format drift is advisory |
| Full Node gate | `node --test --test-concurrency=1 'scripts/*.test.mjs'`, actual complete result |
| Existing UI gate | `UI_E2E_PORT` alternate port, fresh disposable app/database, actual full summary |
| Independent/hosted review | Exact head, findings, executed counterexamples and triage; bounded bot status |
| Post-merge unit verification | Fresh actual merged source, successful .NET/Node results and scoped coverage artifacts |

Do not broaden the change into a UI redesign, dependency update or generic hardening effort. Do not weaken failed checks or suppress audit history. If repository controls or a material unresolved finding prevent merge, preserve the verified PR and report that exact condition; continue independent verification that does not depend on it. The existing UI human usability gate stays pending until a person actually supplies feedback.
