// Replicates ActivityTimelineModel.Format()'s exact logic (pre-fix) and measures, per
// DisplayFields entry, whether the 60-char cap can collapse two DISTINCT values to one string.
import fs from "node:fs";

const SP = "C:/Users/joelf/AppData/Local/Temp/claude/C--Users-joelf/f62ba5a8-f002-408b-98a4-145d989c32f7/scratchpad";
const tasks = JSON.parse(fs.readFileSync(SP + "/corpus.json", "utf8"));

const MAX = 60;
// Format(), verbatim: null/whitespace -> em dash; timestamps -> local; else collapse + cut at 60.
const TIMESTAMP_FIELDS = new Set(["completedAt", "claimedAt"]);
const fmt = (jsonName, raw) => {
  if (raw === null || raw === undefined || String(raw).trim() === "") return "\u2014";
  if (TIMESTAMP_FIELDS.has(jsonName)) return "<local-datetime-minute-precision>";
  const text = String(raw).replace(/\r\n|\n|\r/g, " ").trim();
  if (text.length === 0) return "\u2014";
  return text.length <= MAX ? text : text.slice(0, MAX) + "\u2026";
};

// DisplayFields, in declaration order (ActivityTimelineModel.cs:140-152).
const FIELDS = [
  "status", "priority", "assignee", "claimedBy", "name",
  "featureId", "description", "sortOrder", "completedAt", "claimedAt",
];

const rows = [];
for (const f of FIELDS) {
  const values = tasks
    .map((t) => t[f])
    .filter((v) => v !== null && v !== undefined && String(v).trim() !== "");
  const collapsed = values.map((v) => String(v).replace(/\r\n|\n|\r/g, " ").trim());
  const lens = collapsed.map((s) => s.length);
  const overCap = collapsed.filter((s) => s.length > MAX).length;

  // The collision test: group DISTINCT collapsed values by their rendered form. A group of
  // size > 1 means two genuinely different values render as one string.
  const byRendered = new Map();
  for (const [i, v] of collapsed.entries()) {
    const r = fmt(f, values[i]);
    if (!byRendered.has(r)) byRendered.set(r, new Set());
    byRendered.get(r).add(v);
  }
  let collidingGroups = 0, collidingDistinctValues = 0, biggest = 0, biggestKey = "";
  for (const [r, set] of byRendered) {
    if (set.size > 1) {
      collidingGroups++;
      collidingDistinctValues += set.size;
      if (set.size > biggest) { biggest = set.size; biggestKey = r; }
    }
  }
  rows.push({
    field: f,
    present: values.length,
    distinct: new Set(collapsed).size,
    maxLen: lens.length ? Math.max(...lens) : 0,
    medLen: lens.length ? lens.slice().sort((a, b) => a - b)[Math.floor(lens.length / 2)] : 0,
    overCap,
    collidingGroups,
    collidingDistinctValues,
    biggest,
    biggestKey: biggestKey.slice(0, 66),
  });
}

console.log("corpus tasks:", tasks.length);
console.log("");
console.log("field        present distinct maxLen medLen >60ch  collGroups collValues largestGroup");
for (const r of rows) {
  console.log(
    r.field.padEnd(12),
    String(r.present).padStart(7),
    String(r.distinct).padStart(8),
    String(r.maxLen).padStart(6),
    String(r.medLen).padStart(6),
    String(r.overCap).padStart(5),
    String(r.collidingGroups).padStart(11),
    String(r.collidingDistinctValues).padStart(10),
    String(r.biggest).padStart(12),
  );
}
console.log("");
for (const r of rows) {
  if (r.collidingGroups > 0) {
    console.log(`COLLIDES: ${r.field} -- ${r.collidingDistinctValues} distinct values collapse into ${r.collidingGroups} rendered string(s); largest group ${r.biggest}`);
    console.log(`          largest rendered form: ${JSON.stringify(r.biggestKey)}`);
  }
}
console.log("");
// Named colliders from the brief.
const ids = [351, 240, 238];
const picked = ids.map((id) => tasks.find((t) => t.id === id)).filter(Boolean);
console.log("named colliders (task ids 351/240/238):");
for (const t of picked) {
  console.log(`  #${t.id} descLen=${String(t.description || "").length} -> ${JSON.stringify(fmt("description", t.description))}`);
}
const rendered = picked.map((t) => fmt("description", t.description));
console.log("  all three descriptions render identically?", new Set(rendered).size === 1);
