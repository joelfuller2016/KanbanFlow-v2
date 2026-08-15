# Mutation proof for #359.
#
# Sabotages what the FIX ACTUALLY CHANGES -- the pair-aware routing, the divergence anchor, the
# two surrogate guards, and the DisplayFields set the sweep enumerates. It deliberately does NOT
# touch `MaxValueLength = 60`, because the fix never touches it either: mutating that constant
# would be a no-op sabotage and a guaranteed false green.
#
# Every round asserts the sabotage LANDED (a grep count that must move) before trusting any RED,
# and restores by byte-copy + SHA256 comparison afterwards.

$ErrorActionPreference = "Stop"
$root  = "C:\Users\joelf\KanbanFlow-v2\.claude\worktrees\agent-a16d1542e15012b16"
$model = Join-Path $root "src\KanbanFlow\Components\Shared\ActivityTimelineModel.cs"
$backup = Join-Path $root ".scratch-359\ActivityTimelineModel.cs.orig"

Copy-Item $model $backup -Force
$origHash = (Get-FileHash $model -Algorithm SHA256).Hash
Write-Output "BASELINE subject sha256: $origHash"
Write-Output ""

function Count-Needle([string]$needle) {
    $text = Get-Content $model -Raw
    ([regex]::Matches($text, [regex]::Escape($needle))).Count
}

function Invoke-Round([string]$name, [string]$needle, [string]$replacement, [string]$expectRed) {
    Write-Output "======================================================================"
    Write-Output "ROUND: $name"
    Write-Output "expect RED: $expectRed"

    $before = Count-Needle $needle
    $text = Get-Content $model -Raw
    $text = $text.Replace($needle, $replacement)
    Set-Content -Path $model -Value $text -NoNewline -Encoding UTF8
    $after = Count-Needle $needle

    Write-Output ("SABOTAGE-LANDED CHECK: occurrences of the mutated text {0} -> {1}" -f $before, $after)
    if ($before -eq 0) { Write-Output "!! ABORT: needle not present before mutation - the sabotage could never land"; }
    if ($after -ne 0)  { Write-Output "!! ABORT: needle still present after mutation - the sabotage did NOT land"; }

    Push-Location $root
    $out = dotnet test tests/KanbanFlow.Tests --filter "FullyQualifiedName~ActivityTimelineTests" 2>&1
    Pop-Location

    $summary = $out | Select-String -Pattern "^(Passed!|Failed!)" | Select-Object -First 1
    Write-Output "RESULT: $summary"
    $failed = $out | Select-String -Pattern "^\s*(Failed|X)\s+KanbanFlow\.Tests\.ActivityTimelineTests\.[A-Za-z_]+" -AllMatches
    $names = @{}
    foreach ($line in $out) {
        if ($line -match "ActivityTimelineTests\.([A-Za-z_]+)" -and $line -match "(?i)(failed|error message|\[FAIL\])") {
            $names[$Matches[1]] = $true
        }
    }
    if ($names.Count -gt 0) {
        Write-Output "FAILING TESTS:"
        foreach ($k in ($names.Keys | Sort-Object)) { Write-Output "  - $k" }
    } else {
        Write-Output "FAILING TESTS: (none)"
    }

    Copy-Item $backup $model -Force
    $restored = (Get-FileHash $model -Algorithm SHA256).Hash
    Write-Output ("RESTORED byte-identical: {0}" -f ($restored -eq $origHash))
    Write-Output ""
}

# --- S1: un-route the pair. The exact pre-#359 wiring: two independent Format() calls. -----------
Invoke-Round "S1 un-route the pair (revert DiffFields to two independent Format calls)" `
    "var (beforeText, afterText) = FormatChange(json, b.Text, a.Text);`r`n                    changes.Add(new TimelineFieldChange(label, beforeText, afterText));" `
    "changes.Add(new TimelineFieldChange(label, Format(json, b.Text), Format(json, a.Text)));" `
    "the collision sweep at every position >= 60, plus the description/name/markup/surrogate cases. Positions 0/30/59 and the under-cap cases must stay GREEN."

# --- S2: neuter the divergence anchor so the window always opens at 0 (= plain truncation). ------
Invoke-Round "S2 neuter the divergence anchor (window always opens at offset 0)" `
    "Math.Max(0, i - DiffContextLength)" `
    "Math.Max(0, 0 - DiffContextLength)" `
    "the collision cases. Under-cap cases must stay GREEN."

# --- S3: drop the END-edge surrogate guard. ------------------------------------------------------
Invoke-Round "S3 drop the window END-edge surrogate guard" `
    "if (end < text.Length && char.IsLowSurrogate(text[end])) end--;" `
    "" `
    "ONLY the surrogate test, and only at the offsets that straddle the END edge. The collision sweep must stay GREEN."

# --- S4: drop the START-edge surrogate guard. ----------------------------------------------------
Invoke-Round "S4 drop the window START-edge surrogate guard" `
    "index > 0 && index < text.Length && char.IsLowSurrogate(text[index]) ? index - 1 : index" `
    "index > 0 && index < text.Length && false ? index - 1 : index" `
    "ONLY the surrogate test, and only at the offsets that straddle the START edge."

# --- S5: narrow DisplayFields. The sweep is parameterised over it, so narrowing it silently -----
# --- shrinks the guard. The lock test exists to make that impossible. ----------------------------
Invoke-Round "S5 narrow DisplayFields (drop the `name` entry the sweep enumerates)" `
    "        (`"name`", `"Name`"),`r`n" `
    "" `
    "the DisplayFields lock test, AND the sweep must lose 9 of its 90 cases (the `name` row) plus the synced-name case."

Write-Output "======================================================================"
$finalHash = (Get-FileHash $model -Algorithm SHA256).Hash
Write-Output ("FINAL subject sha256: {0}  (identical to baseline: {1})" -f $finalHash, ($finalHash -eq $origHash))
