## 🔧 Implementation plan

Add `scripts/verify.ps1` (PowerShell-first), running in order and stopping on first failure:
1. `dotnet restore`
2. `dotnet build -warnaserror`
3. `dotnet test` — runs once #134 lands; skips gracefully if no test project is present yet
4. `dotnet format --verify-no-changes`

Implementation notes: `$ErrorActionPreference = 'Stop'`, check `$LASTEXITCODE` after each native call, `exit 1` on any failure so humans/agents can gate on it. Add a **"Before you push: `./scripts/verify.ps1`"** line to the README.

No GitHub Actions — this is the local-first quality gate that replaces CI, matching the single-trust-boundary model.
