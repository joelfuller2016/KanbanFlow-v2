# Copilot / AI reviewer instructions — KanbanFlow-v2

These are the shared house rules for every AI bot that touches this repo (GitHub Copilot,
CodeRabbit, Gemini Code Assist). **Review aggressively.** Surface every real defect —
correctness, security, performance, accessibility, and maintainability. Do not soften or
withhold findings; prefer concrete, copy-pasteable fixes over vague advice. Assume code was
AI-generated and may not actually work — verify the logic, don't trust the prose.

## What this project is

KanbanFlow-v2 is an **AI-first Kanban project-management app**: nested boards, an MCP server
so AI agents can drive it, plus a human UI and REST API.

- **Stack:** .NET 10, Blazor Server, EF Core + SQLite, ASP.NET Core REST API (OpenAPI at
  `/openapi.json`), an MCP server exposing authoring tools, plus JS/CSS and PowerShell scripts.
- **Layout:** application code lives under `src/KanbanFlow`; docs under `docs/`; scripts under `scripts/`.
- **Patterns in play:** a service layer (e.g. `ITaskService`) over EF Core, MCP tool handlers,
  Markdig + HtmlSanitizer for user-supplied markdown.

## Review priorities (in order)

1. **Correctness** — does the change actually do what it claims? Trace the logic and edge cases.
2. **Security** — XSS, injection, auth/authz, CORS, secret leakage, unsafe deserialization.
3. **Accessibility** — this is a UI product; a11y regressions are bugs, not nits.
4. **Performance** — EF Core query shape (N+1, tracking), async/blocking, render churn.
5. **Tests & maintainability** — real coverage, clear naming, no dead code.

## C# / .NET

- Nullable reference types are expected; flag possible null dereferences and missing guards.
- **Async:** no `async void` (except event handlers); never `.Result` / `.Wait()` /
  `.GetAwaiter().GetResult()` (deadlock + thread-pool starvation); thread `CancellationToken`
  through; `ConfigureAwait(false)` in library code.
- **DI:** correct lifetimes; no captive dependencies (a singleton holding a scoped/transient).
- **EF Core:** no N+1; `AsNoTracking()` for read-only queries; no client-side evaluation of
  large sets; parameterized queries only; **schema changes go through migrations, not manual
  SQL/DDL.**
- Dispose owned `IDisposable`/`IAsyncDisposable`; prefer `using`.
- Validate all external/API input; never trust client data.

## Blazor (.razor / .razor.cs)

- No blocking/synchronous I/O in `OnInitialized`; use `OnInitializedAsync` and guard re-entrancy.
- Dispose event subscriptions, timers, and `IDisposable`; implement `IDisposable` when subscribing.
- Treat `[Parameter]` values as immutable.
- **Accessibility is required:** semantic HTML, ARIA roles/labels, full keyboard operability,
  visible focus, label/control association, focus trapping in dialogs.
- **XSS:** any `MarkupString` / raw HTML must be sanitized (HtmlSanitizer). Never render
  untrusted HTML.
- Use `@key` in loops; avoid unnecessary `StateHasChanged`.

## JavaScript / CSS

- JS: prevent XSS (sanitize before DOM insertion); no `eval` / `new Function`; no `innerHTML`
  with untrusted data; handle promise rejections; no leftover `console.*` in shipped code.
- CSS: avoid `!important`; keep specificity low; reuse existing design tokens; responsive and
  dark-mode safe; meet WCAG AA contrast.

## PowerShell (scripts/)

- Strict error handling (`$ErrorActionPreference = 'Stop'` / `-ErrorAction Stop`) with try/catch.
- No plaintext secrets; validate parameters; never `Invoke-Expression` untrusted input;
  use approved verbs; keep operations idempotent.

## Security specifics for this app

- **CORS** must not be wide-open (`AllowAnyOrigin` + credentials). Restrict origins or
  explicitly document the local-only assumption.
- The MCP surface and REST API take agent/automation input — validate tool arguments and DTOs;
  reject undefined enum values; enforce project-scoped authorization on every mutation.
- No secrets in source or committed config.

## Tests

Per project policy, **tests must exercise real code paths.** Reject fake/placeholder tests,
assertions against mocks only, and trivially-true assertions. Cover edge cases, error paths,
and boundaries; keep tests deterministic; mock only true external boundaries; flag new or
changed behavior that ships without coverage.

## GitHub Actions (if/when workflows are added)

Least-privilege `permissions:`; pin third-party actions to a full commit SHA; never interpolate
untrusted `${{ github.event.* }}` text into `run:` shells; beware `pull_request_target` with PR
checkout; set timeouts.
