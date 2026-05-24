## 🔧 Implementation plan

Add under `.github/`:
- **`ISSUE_TEMPLATE/enhancement.yml`** — issue form: priority (P1/P2 dropdown), area (ui / api / ai-agent / other), problem, proposed scope, acceptance criteria.
- **`ISSUE_TEMPLATE/bug.yml`** — repro steps, expected vs actual, environment.
- **`ISSUE_TEMPLATE/agent-task.yml`** — goal, context bundle, done-when criteria (machine-fillable for AI agents).
- **`ISSUE_TEMPLATE/config.yml`** — `blank_issues_enabled: false` + contact links.
- **`PULL_REQUEST_TEMPLATE.md`** — summary, linked issue, test evidence, checklist (feeds CodeRabbit's `pre_merge_checks` title/description signal).
- **`CODEOWNERS`** — `* @joelfuller2016`.

Acceptance: new issues/PRs prefill structured templates; CODEOWNERS present; the existing P1/P2 + area-label convention is enforced at intake.
