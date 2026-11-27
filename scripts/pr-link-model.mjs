// GitHub's local closing-keyword syntax, for example "Closes #12" or "fixes: #34".
const CLOSING_KEYWORDS = /\b(?:close[sd]?|fix(?:e[sd])?|resolve[sd]?)\b[:\s]*#(\d+)/gi;

export function mapIssueToPrs(prs) {
  const byIssue = new Map();
  for (const pr of [...prs].sort((a, b) => a.number - b.number)) {
    for (const match of (pr.body || "").matchAll(CLOSING_KEYWORDS)) {
      const issueNumber = Number(match[1]);
      if (!Number.isSafeInteger(issueNumber) || issueNumber <= 0) continue;
      const linked = byIssue.get(issueNumber) || [];
      if (linked.at(-1)?.number === pr.number) continue;
      linked.push(pr);
      byIssue.set(issueNumber, linked);
    }
  }
  return byIssue;
}

export function summarizePrLinks(prsByIssue, issues) {
  const linkedPrNumbers = new Set();
  let issuesWithLinkedPrs = 0;
  for (const number of new Set(issues.map(issue => issue.number))) {
    const prs = prsByIssue.get(number);
    if (!prs) continue;
    issuesWithLinkedPrs++;
    for (const pr of prs) linkedPrNumbers.add(pr.number);
  }
  return { issuesWithLinkedPrs, linkedPrNumbers };
}
