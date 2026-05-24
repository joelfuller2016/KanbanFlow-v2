## 🔧 Implementation plan

- **`.github/dependabot.yml`** — `nuget` ecosystem, directory `/src/KanbanFlow`, weekly schedule, group minor/patch updates into one PR. (Dependabot runs on GitHub's infrastructure — **no Actions minutes**.) CodeRabbit then reviews each Dependabot PR (already configured).
- **`libman.json`** (repo root) — declare the vendored client libs currently under `src/KanbanFlow/wwwroot/lib/` (Bootstrap) with pinned versions and `defaultProvider: cdnjs`, `defaultDestination: src/KanbanFlow/wwwroot/lib`. This makes `wwwroot/lib` restorable/updatable via `libman restore` instead of being an untracked manual copy.
- **README** — document `dotnet tool install -g Microsoft.Web.LibraryManager.Cli` then `libman restore`.

Acceptance: Dependabot opens grouped NuGet PRs; `wwwroot/lib` has a declared version + restore mechanism.
