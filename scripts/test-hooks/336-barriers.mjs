// #336 crash/barrier harness — TEST-ONLY. NOT imported by portfolio-sync.mjs.
//
// These four file barriers exist so `scripts/verify-336-github.ps1` can stop a live
// `push-issue` at an exact instant, kill the process there, and prove the journal recovers.
// They used to live inside portfolio-sync.mjs, armed by mere env-var presence: setting one
// pair of environment variables silently turned a production run into a run that blocks on a
// file and, on timeout, threw into the create path's ambiguous-failure handler. That is test
// code with production reach, so it lives here instead and is loaded only when the operator
// asks for it TWICE — `--internal-test-hooks` on the command line AND
// KANBANFLOW_336_TEST_HOOKS naming this module. Env alone arms nothing.
//
// A barrier that gives up throws HookAbort. portfolio-sync.mjs re-raises HookAbort WITHOUT
// recording an ambiguous failure, because a harness timeout is not evidence about GitHub:
// the after-create barrier fires when the issue has already been created AND its marker
// verified, so classifying that as NeedsReview would slander an operation that succeeded.

import { access, writeFile } from "node:fs/promises";
import { setTimeout as delay } from "node:timers/promises";

// Barriers wait on a file that a test harness creates. `phase` names the instant so the
// signal file the harness reads is self-describing.
async function waitAtFileBarrier(HookAbort, signalVariable, releaseVariable, phase, context) {
  const signalPath = process.env[signalVariable];
  const releasePath = process.env[releaseVariable];
  if (!signalPath && !releasePath) return;
  if (!signalPath || !releasePath) {
    throw new HookAbort(`The #336 ${phase} barrier requires both ${signalVariable} and ${releaseVariable}.`);
  }

  await writeFile(signalPath, `${JSON.stringify({ ...context, phase }, null, 2)}\n`, { encoding: "utf8", flag: "wx" });
  const timeoutMs = Number.parseInt(process.env.KANBANFLOW_336_BARRIER_TIMEOUT_MS || "600000", 10);
  const deadline = Date.now() + Math.max(1000, timeoutMs);
  while (Date.now() < deadline) {
    try { await access(releasePath); return; } catch { await delay(100); }
  }
  throw new HookAbort(`Timed out waiting for #336 release file ${releasePath}`);
}

// `install` is the module contract portfolio-sync.mjs calls. It receives the HookAbort class
// so the abort type is the caller's, never a structurally-similar copy that `instanceof`
// would miss, and returns the hook set to merge over the production no-ops.
export function install({ HookAbort }) {
  return {
    beforePrepare: (context) => waitAtFileBarrier(
      HookAbort,
      "KANBANFLOW_336_BEFORE_PREPARE_SIGNAL",
      "KANBANFLOW_336_BEFORE_PREPARE_RELEASE",
      "before-prepare",
      context,
    ),
    afterDispatch: (context) => waitAtFileBarrier(
      HookAbort,
      "KANBANFLOW_336_AFTER_DISPATCH_SIGNAL",
      "KANBANFLOW_336_AFTER_DISPATCH_RELEASE",
      "after-dispatch-before-github-create",
      context,
    ),
    afterManualResume: (context) => waitAtFileBarrier(
      HookAbort,
      "KANBANFLOW_336_AFTER_MANUAL_RESUME_SIGNAL",
      "KANBANFLOW_336_AFTER_MANUAL_RESUME_RELEASE",
      "after-manual-retry-resume-and-scan-before-dispatch",
      context,
    ),
    afterCreate: (context) => waitAtFileBarrier(
      HookAbort,
      "KANBANFLOW_336_AFTER_CREATE_SIGNAL",
      "KANBANFLOW_336_AFTER_CREATE_RELEASE",
      "after-github-create-before-finalize",
      context,
    ),
  };
}
