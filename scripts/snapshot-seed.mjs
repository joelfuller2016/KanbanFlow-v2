#!/usr/bin/env node
// Refresh the tracked seed database from the live one (#477).
//
// `seed/kanbanflow.seed.db` is the committed starting point that `scripts/start-kanbanflow.ps1`
// restores when no live database exists. This script is the only sanctioned way to update it,
// so the seed never drifts on its own: a commit that changes it is always a deliberate act.
//
// Why VACUUM INTO and not a file copy: the app runs SQLite in WAL mode, so the newest committed
// pages live in `kanbanflow.db-wal`, not in the `.db` file. Copying the `.db` while the app holds
// it captures a stale, torn snapshot — silently, with no error, and no symptom until someone
// restores it. Stopping the app first does not fix that either: a forced kill leaves the WAL
// unmerged. VACUUM INTO runs inside a read transaction against the live database, so it sees the
// WAL, writes one compact file with no sidecars, and needs no downtime at all.
//
// Reads the live DB. The only file it writes is the seed.
//
//   node scripts/snapshot-seed.mjs
//   node scripts/snapshot-seed.mjs --db D:/data/kanbanflow.db --out seed/other.db
//   node scripts/snapshot-seed.mjs --check     # verify the tracked seed, write nothing

import { createRequire } from "node:module";
import { existsSync, mkdirSync, statSync, rmSync } from "node:fs";
import { dirname, join, resolve } from "node:path";
import { fileURLToPath } from "node:url";
import { homedir } from "node:os";

const repoRoot = resolve(dirname(fileURLToPath(import.meta.url)), "..");

// node:sqlite landed in Node 22.5. The package targets >=18 because nothing at *runtime*
// needs it — this is a maintainer tool, so it guards rather than raising the floor for everyone.
let DatabaseSync;
try {
  ({ DatabaseSync } = createRequire(import.meta.url)("node:sqlite"));
} catch {
  console.error(
    `This script needs node:sqlite (Node >= 22.5). Running Node ${process.version}.\n` +
      "Nothing else in the repo requires it; upgrade Node to refresh the seed.",
  );
  process.exit(1);
}

const argv = process.argv.slice(2);
const flag = (name) => {
  const i = argv.indexOf(name);
  return i === -1 ? null : argv[i + 1] || null;
};
const check = argv.includes("--check");

const dbPath = resolve(
  flag("--db") || process.env.KANBANFLOW_DB_PATH || join(homedir(), ".kanbanflow", "kanbanflow.db"),
);
const seedPath = resolve(flag("--out") || join(repoRoot, "seed", "kanbanflow.seed.db"));

const TABLES = [
  "Projects",
  "Features",
  "Tasks",
  "Subtasks",
  "AcceptanceCriteria",
  "Requirements",
  "TaskDependencies",
  "TaskComments",
  "ActivityEvents",
  "Ideas",
  "MemoryNodes",
];

function summarize(path) {
  const db = new DatabaseSync(path, { readOnly: true });
  try {
    const counts = {};
    for (const t of TABLES) {
      try {
        counts[t] = db.prepare(`select count(*) c from ${t}`).get().c;
      } catch {
        counts[t] = "—"; // table absent in an older snapshot; report rather than throw
      }
    }
    const integrity = db.prepare("pragma integrity_check").get().integrity_check;
    return { counts, integrity };
  } finally {
    db.close();
  }
}

function report(label, path) {
  const { counts, integrity } = summarize(path);
  const kb = (statSync(path).size / 1024).toFixed(1);
  console.log(`${label}  ${path}`);
  console.log(`  size ${kb} KB · integrity ${integrity}`);
  const rows = Object.entries(counts)
    .filter(([, c]) => c !== 0)
    .map(([t, c]) => `${t}=${c}`);
  console.log(`  ${rows.join(" · ") || "(empty)"}`);
  return integrity;
}

if (check) {
  if (!existsSync(seedPath)) {
    console.error(`No seed at ${seedPath}`);
    process.exit(1);
  }
  const integrity = report("seed", seedPath);
  process.exit(integrity === "ok" ? 0 : 1);
}

if (!existsSync(dbPath)) {
  console.error(
    `No live database at ${dbPath}.\n` +
      "Start the app first (scripts/start-kanbanflow.ps1), or pass --db.",
  );
  process.exit(1);
}

mkdirSync(dirname(seedPath), { recursive: true });

// VACUUM INTO refuses to overwrite; clear the old seed only once the new one is about to be built.
for (const stale of [seedPath, `${seedPath}-wal`, `${seedPath}-shm`]) {
  if (existsSync(stale)) rmSync(stale);
}

const live = new DatabaseSync(dbPath, { readOnly: true });
try {
  live.exec(`VACUUM INTO '${seedPath.replace(/'/g, "''")}'`);
} finally {
  live.close();
}

const integrity = report("seed written", seedPath);
if (integrity !== "ok") {
  console.error(`integrity_check returned "${integrity}" — refusing to call this a good seed.`);
  process.exit(1);
}

console.log("\nReview and commit deliberately:");
console.log("  git add seed/kanbanflow.seed.db");
console.log('  git commit -m "chore: refresh board seed"');
