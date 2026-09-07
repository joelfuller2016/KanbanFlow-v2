// Playwright CLI run-code file. Open the disposable URL printed by smoke-workflow.mjs first.
// This is an automated walkthrough, not human usability approval. See docs/test-cases/509-unified-workflow/.
async page => {
  const assert = (ok, message) => { if (!ok) throw new Error(message); };
  const entry = await page.evaluate(() => ({ href: location.href, origin: location.origin,
    hostname: location.hostname, port: location.port, requestId: new URLSearchParams(location.search).get('request') }));
  assert(['127.0.0.1', 'localhost'].includes(entry.hostname) && entry.port !== '5123',
    'Use the disposable verification app, not the normal board on port 5123.');
  const requestId = entry.requestId;
  assert(requestId, 'Open the board URL printed by smoke-workflow.mjs.');
  let rpcId = 0;
  async function mcp(name, args) {
    const response = await page.request.post(entry.origin + '/mcp', {
      timeout: 20000,
      headers: { Accept: 'application/json, text/event-stream', 'MCP-Protocol-Version': '2025-06-18' },
      data: { jsonrpc: '2.0', id: ++rpcId, method: 'tools/call', params: { name, arguments: args } }
    });
    assert(response.ok(), 'Native MCP HTTP request failed: ' + response.status());
    const body = await response.text();
    const data = body.split(/\r?\n/).find(line => line.startsWith('data: '));
    const payload = JSON.parse(data ? data.slice(6) : body);
    if (payload.error || payload.result?.isError) throw new Error(JSON.stringify(payload));
    return payload.result.structuredContent ?? JSON.parse(payload.result.content.find(c => c.type === 'text').text);
  }
  const read = () => mcp('get_request_context', { requestId });
  let context = await read();
  assert(context.title === 'Shared UI and MCP workflow' && context.tasks.length === 1,
    'Expected the single-task transport-smoke fixture.');
  const original = context.messages.find(m => m.kind === 'original');
  assert(original?.source === 'transport-smoke', 'Refusing a non-smoke request.');
  const taskId = context.tasks[0].taskId;
  const draftId = context.tasks[0].draftId;
  let operation = 0;
  async function command(input) {
    const receipt = await mcp('workflow_command', { input: {
      actor: 'Steering verification client',
      key: 'steering-' + Date.now() + '-' + (++operation),
      requestId, expectedVersion: context.version, ...input
    } });
    context = await read();
    return receipt;
  }
  async function refused(input, expected) {
    let error;
    try { await command(input); } catch (ex) { error = ex; }
    assert(error && expected.test(error.message), 'Expected refusal ' + expected + ', got ' + error?.message);
  }
  const baselines = () => Object.fromEntries(context.tasks.map(t => [t.taskId, t.editToken]));
  const beforeDraft = { ...context.tasks[0].specification, description: context.tasks[0].description };
  await command({ operation: 'save_plan', tasks: [beforeDraft], taskBaselines: baselines() });
  const oldPlan = context.plans.at(-1).id;
  const oldVersion = context.version;
  const priorBriefCount = context.briefs.length;
  const priorMessageCount = context.messages.length;

  await page.setViewportSize({ width: 1280, height: 900 });
  await page.goto(entry.href);
  const workspace = page.getByTestId('request-workspace');
  await page.locator('[data-testid="request-workspace"][data-interactive="true"]').waitFor();
  assert(await workspace.locator('pre').first().textContent() === original.text, 'Original request changed.');
  await page.screenshot({ path: 'steering-before.png', fullPage: true });
  await workspace.getByRole('button', { name: 'Brief', exact: true }).click();
  const unsavedBrief = 'Keep this unsaved brief while reading the agent response.';
  await workspace.getByLabel('Desired outcome', { exact: true }).fill(unsavedBrief);
  await workspace.getByRole('button', { name: 'Updates', exact: true }).click();
  await workspace.getByLabel('Author', { exact: true }).fill('Browser walkthrough');
  await workspace.getByLabel('Type', { exact: true }).selectOption('decision');
  const decision = 'Keep the existing board layout. Verify keyboard access to the task detail panel before accepting this task.';
  await workspace.getByLabel('Message', { exact: true }).fill(decision);
  await workspace.getByRole('button', { name: 'Add update', exact: true }).click();
  await workspace.getByRole('status').filter({ hasText: 'Saved to this board.' }).waitFor();
  context = await read();
  const savedDecision = context.messages.find(m => m.kind === 'decision' && m.text === decision);
  assert(savedDecision?.author === 'Browser walkthrough', 'Browser decision attribution missing from MCP.');
  assert(context.id === requestId && context.version > oldVersion, 'Decision changed request identity or lost its revision.');
  assert(context.needsBriefReview === true, 'Decision did not mark the brief for review.');
  assert(context.messages.length === priorMessageCount + 1, 'Saving one decision did not append exactly one message.');
  await refused({ operation: 'publish_plan', planId: oldPlan, expectedVersion: oldVersion }, /conflict/i);
  context = await read();
  await refused({ operation: 'publish_plan', planId: oldPlan }, /clarifications and decisions/i);
  context = await read();

  const acknowledgement = 'Read decision ' + savedDecision.id +
    '. I will keep the board layout and revise this same task to check keyboard access. No Codex process was launched.';
  await command({ operation: 'add_message', kind: 'assistant-report', text: acknowledgement });
  const priorBrief = context.briefs.at(-1);
  await command({ operation: 'save_brief', brief: {
    outcome: 'Verify keyboard access in the existing task detail panel',
    scope: priorBrief.scope, constraints: decision,
    acceptance: 'The existing task opens with the keyboard and retains the original request link.'
  } });
  await refused({ operation: 'publish_plan', planId: oldPlan }, /brief has changed/i);
  context = await read();
  const title = 'Verify keyboard access in the existing task panel';
  const acceptance = 'The existing task opens with keyboard activation\nThe original request remains reachable';
  await command({ operation: 'save_plan', tasks: [{
    ...beforeDraft, title, acceptance, description: 'Apply decision ' + savedDecision.id + ': ' + decision
  }], taskBaselines: baselines() });
  await command({ operation: 'publish_plan', planId: context.plans.at(-1).id });
  assert(context.tasks.length === 1 && context.tasks[0].taskId === taskId && context.tasks[0].draftId === draftId,
    'Redirecting the plan duplicated or replaced the existing task.');
  assert(context.tasks[0].status === 'Todo' && context.tasks[0].name === title, 'Revised task was not visible in the existing lifecycle.');
  assert(context.tasks[0].acceptanceCriteria.map(c => c.text).join('\n') === acceptance, 'Decision was not applied to actual criteria.');
  assert(context.needsBriefReview === false && context.briefs.length === priorBriefCount + 1, 'Revised brief did not acknowledge intent.');
  assert(context.messages.find(m => m.kind === 'original').text === original.text, 'Replanning rewrote the original.');
  assert(context.tasks[0].evidence.every(e => !e.current), 'Old reported evidence still covers changed scope.');

  const filter = page.getByPlaceholder('Search name or description...');
  await filter.fill('keyboard');
  await workspace.getByRole('button', { name: 'Refresh saved details', exact: true }).click();
  await workspace.getByText(acknowledgement, { exact: true }).waitFor();
  assert(await filter.inputValue() === 'keyboard', 'Refreshing reset the human board filter.');
  await workspace.getByRole('button', { name: 'Brief', exact: true }).click();
  assert(await workspace.getByLabel('Desired outcome', { exact: true }).inputValue() === unsavedBrief, 'Refreshing lost an unsaved human draft.');
  await workspace.getByRole('button', { name: 'Updates', exact: true }).click();
  await page.screenshot({ path: 'steering-acknowledged.png', fullPage: true });
  await workspace.locator('summary').first().click();
  const taskButton = page.getByTestId('board-card-' + taskId + '-title');
  await taskButton.filter({ hasText: title }).waitFor();
  assert(await taskButton.textContent() === title, 'Human board still shows the old task.');
  await taskButton.focus();
  await page.keyboard.press('Enter');
  const detail = page.getByTestId('task-detail-panel');
  await detail.waitFor();
  await detail.getByRole('region', { name: 'Task request context' }).getByRole('link').waitFor();
  assert((await detail.innerText()).includes(title), 'Keyboard did not open the revised task.');
  await page.waitForFunction(() => {
    const panel = document.querySelector('[data-testid="task-detail-panel"]');
    return panel && panel.getBoundingClientRect().right <= innerWidth + 1;
  });
  await page.screenshot({ path: 'steering-task-detail.png', fullPage: true });
  await page.getByTestId('task-detail-close-button').click();
  await page.goto(entry.href);
  await page.setViewportSize({ width: 390, height: 844 });
  await page.locator('[data-testid="request-workspace"][data-interactive="true"]').waitFor();
  await workspace.getByRole('button', { name: 'Updates', exact: true }).click();
  await workspace.getByText(acknowledgement, { exact: true }).waitFor();
  assert(await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth), 'Mobile page has horizontal overflow.');
  await page.screenshot({ path: 'steering-mobile.png', fullPage: true });
  await page.setViewportSize({ width: 1280, height: 900 });
  console.log(JSON.stringify({ passed: true, requestId, taskId, decisionId: savedDecision.id,
    decisionVisibleThroughMcp: true, oldPlanRefused: true, acknowledgementVisible: true,
    revisedSameCard: true, keyboardTaskAccess: true, humanUsabilityApproval: 'pending' }));
}
