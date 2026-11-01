# CLAUDE.md

Instructions for Claude Code sessions working in this repository.

## Work journal (standing rule, owner instruction of 2026-09-27)

[`JOURNAL.md`](JOURNAL.md) at the repository root is the one running record of work on this
repository. Its header lists the owner's standing instructions. Read the header and the newest
entries before starting.

- **Add an entry for every instruction and every milestone.** Record what was done, with full
  commit SHAs and PR or issue numbers, what is still running, and the next step. An entry starts
  with a `- YYYY-MM-DD HH:MMZ` line (UTC).
- **Newest entry first.** Insert the new entry directly under the `## Entries (newest first)`
  heading. Never rewrite or reorder existing entries. A correction is a new entry that says what
  it corrects.
- **Commit and push it straight to `master`** after each entry. Fetch first and insert on top of
  the latest `master` copy, so entries from other sessions are kept. Push as a fast-forward, never
  forced. `master` requires pull requests for everyone except admins, so these pushes print a
  "Bypassed rule violations" notice. That is expected for this one file.
- **One file only.** Keep no second journal and no dated copies. Keep local absolute paths,
  session ids and credentials out of entries.
- **Everything else goes through a pull request,** one focused pull request per branch. Merges
  into `master` are the owner's.

## Repository rules

The repository-specific overrides in
[`.github/copilot-instructions.md`](.github/copilot-instructions.md) apply to all work here.
Schema changes are additive startup patchers rather than EF migrations, access is open by
design, and the gate is `scripts/verify.ps1` rather than GitHub Actions.
