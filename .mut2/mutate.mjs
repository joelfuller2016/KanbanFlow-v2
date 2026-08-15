// Mutation harness for the three-thread fixes. Verifies in BOTH directions that the sabotage
// landed on disk before any result is trusted.
//
//   node mutate.mjs snapshot | dropimport | untrim | restore
import { readFile, writeFile } from "node:fs/promises";
import { createHash } from "node:crypto";

const TARGETS = {
  portfolio: new URL("../scripts/portfolio-sync.mjs", import.meta.url),
  backlog: new URL("../scripts/backlog-sync.mjs", import.meta.url),
};
const snapPath = (k) => new URL(`./${k}.PRISTINE`, import.meta.url);
const hashPath = (k) => new URL(`./${k}.sha256`, import.meta.url);
const sha = (s) => createHash("sha256").update(s, "utf8").digest("hex");
const mode = process.argv[2];

if (mode === "snapshot") {
  for (const [k, url] of Object.entries(TARGETS)) {
    const t = await readFile(url, "utf8");
    await writeFile(snapPath(k), t, "utf8");
    await writeFile(hashPath(k), sha(t), "utf8");
    console.log(`snapshot ${k}: ${t.length} bytes sha256=${sha(t).slice(0, 16)}…`);
  }
  process.exit(0);
}

if (mode === "restore") {
  for (const [k, url] of Object.entries(TARGETS)) {
    const t = await readFile(snapPath(k), "utf8");
    const want = (await readFile(hashPath(k), "utf8")).trim();
    await writeFile(url, t, "utf8");
    const got = sha(await readFile(url, "utf8"));
    if (got !== want) { console.error(`RESTORE FAILED ${k}: ${got} != ${want}`); process.exit(1); }
    console.log(`restore ${k} VERIFIED: sha256=${got.slice(0, 16)}… matches pristine`);
  }
  process.exit(0);
}

const SABOTAGE = {
  // Exactly the edit the comment used to invite: drop the "unused-looking" import.
  dropimport: {
    target: "portfolio",
    find: `import { derivePriority, priorityFromLabel } from "./priority-model.mjs";`,
    replace: `import { derivePriority } from "./priority-model.mjs";`,
  },
  // Undo the trim fix — back to master's behaviour.
  untrim: {
    target: "backlog",
    find: `  const title = String(raw).trim();`,
    replace: `  const title = raw;`,
  },
};

const s = SABOTAGE[mode];
if (!s) { console.error(`unknown mode: ${mode}`); process.exit(2); }

const url = TARGETS[s.target];
const before = await readFile(url, "utf8");
const EOL = before.includes("\r\n") ? "\r\n" : "\n";
const find = s.find.replace(/\n/g, EOL);
const replace = s.replace.replace(/\n/g, EOL);

const occurrences = before.split(find).length - 1;
if (occurrences !== 1) {
  console.error(`REFUSING: anchor found ${occurrences} times in ${s.target}, expected exactly 1.`);
  process.exit(1);
}
const after = before.replace(find, replace);
if (after === before) { console.error("REFUSING: replacement was a no-op."); process.exit(1); }
await writeFile(url, after, "utf8");

const onDisk = await readFile(url, "utf8");   // read BACK from disk — the whole point
if (onDisk === before) { console.error("SABOTAGE DID NOT LAND."); process.exit(1); }
console.log(`sabotage "${mode}" LANDED in ${s.target} — verified by re-reading the file:`);
console.log(`  bytes  ${before.length} -> ${onDisk.length}  (delta ${onDisk.length - before.length})`);
console.log(`  sha256 ${sha(before).slice(0, 16)}… -> ${sha(onDisk).slice(0, 16)}…`);
console.log(`  anchor occurrences after: ${onDisk.split(find).length - 1} (was ${occurrences})`);
