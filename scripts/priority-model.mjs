// The single definition of "what priority is this GitHub issue?".
//
// It lives in its own module because more than one consumer needs the answer and they must not
// each hold a private copy — that is the defect #304 describes for the comment-type policy
// ("declared 4 times, and the UI has already drifted"). `portfolio-sync.mjs` reads it to set
// TaskItem.Priority on the board; the backlog log book (#327) ranks with the same ordering.
//
// Resolution order — first non-null wins (#355):
//   1. an explicit `P0:`–`P3:` prefix in the issue TITLE  — a human's stated decision
//   2. a priority LABEL                                   — the cross-repo vocabulary
//   3. the MILESTONE band                                 — the execution phase as a proxy
//   4. "Medium"                                           — no signal at all
//
// Why the title outranks the label: #163 states the convention verbatim — *"Priority (P0–P3)
// lives in each issue title; the milestone is the execution phase."* KanbanFlow-v2 carries zero
// priority-signalling labels, so before #355 every one of its 91 synced cards rendered `Medium`
// and `/next-tasks` ranked against a constant.

export const SEVERITY_WORDS = { critical: "Critical", high: "High", medium: "Medium", low: "Low" };
export const SEVERITY_RANK = { Critical: 0, High: 1, Medium: 2, Low: 3 };
export const PN_LEVELS = ["Critical", "High", "Medium", "Low"]; // P0..P3

// Priority labels are a zoo across repos: bare `P0`; `P0-critical`; `priority:high`;
// `high-priority`; `priority: medium`; or a plain `critical`. Matching ONLY the bare `p0..p3`
// form silently mapped every issue in 5 of 6 repos to Medium — the whole 404-task portfolio
// came out one flat priority, so the /next-tasks direction engine was ranking against a
// constant. Recognise the real vocabularies, and be strict enough that a topical label which
// merely CONTAINS a severity word (`critical-path`, `size/medium`, `slow`) is not mistaken
// for a priority.
export function priorityFromLabel(name) {
  const n = String(name).trim().toLowerCase();

  // "p0", "P0-critical", "p1-high", "p2 medium"
  const pn = /^p([0-3])\b/.exec(n);
  if (pn) return PN_LEVELS[Number(pn[1])];

  // "priority:high", "priority: medium", "priority-critical", "high-priority"
  // Use anchored patterns so "priority:critical-path" or "not-priority-medium" are rejected.
  const priFirst = /^priority[:\-]\s*(critical|high|medium|low)$/.exec(n);
  if (priFirst) return SEVERITY_WORDS[priFirst[1]];
  const priSecond = /^(critical|high|medium|low)[:\-]priority$/.exec(n);
  if (priSecond) return SEVERITY_WORDS[priSecond[1]];

  // A label that IS a severity word, exactly — not one that merely contains it.
  // `Object.hasOwn` rather than a bare lookup: a plain-object index walks the prototype chain,
  // so a label literally named `__proto__` or `constructor` returned a truthy non-string
  // (Object.prototype / the Object function) and the sync would PUT that as the priority.
  return Object.hasOwn(SEVERITY_WORDS, n) ? SEVERITY_WORDS[n] : null;
}

// Anchored at the start and requiring a separator, so a title that merely CONTAINS the token is
// rejected: `P2P sync rewrite` (no separator), `HTTP3: …` and `SP1: …` (not at the start),
// `not-P1: …` (not at the start). Mirrors TITLE_PRIORITY in the backlog generator, including the
// en/em dash, so the two consumers accept exactly the same set of titles.
const TITLE_PRIORITY = /^\s*P([0-3])\s*[:\-–—]/i;

export function priorityFromTitle(title) {
  const m = TITLE_PRIORITY.exec(String(title ?? ""));
  return m ? PN_LEVELS[Number(m[1])] : null;
}

// Milestone -> band, for issues whose title states no priority. This is deliberately coarse: a
// milestone is an execution PHASE, not a priority, and #163 keeps the two separate. It earns its
// place as the last real signal because the #331 triage took unmilestoned from 44 -> 2, so it
// now covers 114 of 116 open issues.
//
// The mapping is not invented here — it is the same M1..M7 -> P0..P3 table the backlog log book
// ranks with. Change it in both places or the board and the log book disagree.
const MILESTONE_BANDS = new Map([
  ["M1: Correctness & Audit Trail", "Critical"], // P0
  ["M2: Tech-debt & Foundation", "High"],        // P1
  ["M3: Agent API Surface", "High"],             // P1
  ["M4: UI / UX Polish", "Medium"],              // P2
  ["M5: Docs & Process", "Medium"],              // P2
  ["M6: AI Memory & Ops Hub", "Medium"],         // P2
  ["M7: Self-Improving", "Low"],                 // P3
]);

// An unknown milestone returns null rather than guessing a band, so it falls through to the
// "Medium" default instead of being silently mis-ranked. A milestone added on GitHub but not
// here is invisible to this function by design — the log book raises it loudly; the sync must
// keep running, so it degrades quietly.
export function priorityFromMilestone(milestone) {
  const title = typeof milestone === "string" ? milestone : milestone?.title;
  if (!title) return null;
  return MILESTONE_BANDS.get(String(title).trim()) ?? null;
}

export function derivePriority(issue) {
  // 1. The title prefix is a human's explicit decision and outranks every inferred signal.
  const fromTitle = priorityFromTitle(issue.title);
  if (fromTitle) return fromTitle;

  // 2. Labels. Highest severity present wins: an issue tagged both `bug` and `P0-critical` is
  //    Critical. Unchanged from the pre-#355 behaviour, so repos that DO carry priority labels
  //    (ZeroRouter, job-scraper-mvp, idea-builder-v2, Job-Email-Tracker) resolve exactly as before.
  let best = null;
  for (const label of issue.labels || []) {
    const level = priorityFromLabel(label.name);
    if (level && (best === null || SEVERITY_RANK[level] < SEVERITY_RANK[best])) best = level;
  }
  if (best) return best;

  // 3. The execution phase, then 4. no signal at all.
  return priorityFromMilestone(issue.milestone) ?? "Medium";
}
