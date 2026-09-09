import assert from "node:assert/strict";

// Real transport regression for #462. The caller owns the spawned stdio server and HTTP
// timeout; this check owns every fixture it creates. REST is both a persisted-state reader
// and an independent control for the existing service/audit boundary.
export async function verifyNullableTextUpdates({ callTool, restJson, apiBaseUrl, timeoutMs, marker }) {
  const failures = [];
  const controls = new Map();
  const cleanup = [];
  let passed = 0;

  async function check(label, action) {
    try {
      const detail = await action();
      passed++;
      console.log(JSON.stringify({ check: label, passed: true, ...detail }));
    } catch (error) {
      failures.push(`${label}: ${error.message}`);
      console.error(JSON.stringify({ check: label, passed: false, error: error.message }));
    }
  }

  try {
    const project = await callTool("create_project", {
      name: `${marker} clear project`, description: "", goals: "", readme: ""
    });
    cleanup.push(`/api/projects/${project.id}`);
    const feature = await callTool("create_feature", {
      projectId: project.id, name: `${marker} clear feature`, description: ""
    });
    const task = await callTool("create_task", {
      featureId: feature.id, name: `${marker} clear task`, description: "", assignee: ""
    });
    const idea = await callTool("create_idea", {
      title: `${marker} clear idea`, body: "", plan: "", tags: "", actor: ""
    });
    cleanup.push(`/api/ideas/${idea.id}`);

    const entities = [
      { kind: "Project", id: project.id, name: project.name, fields: ["description", "goals", "readme"],
        path: `/api/projects/${project.id}`, method: "PUT", tool: "update_project",
        args: { projectId: project.id, name: project.name },
        read: () => callTool("get_project", { projectId: project.id }) },
      { kind: "Feature", id: feature.id, name: feature.name, fields: ["description"],
        path: `/api/features/${feature.id}`, method: "PUT", tool: "update_feature",
        args: { featureId: feature.id, name: feature.name },
        read: async () => (await callTool("get_project_context", { projectId: project.id }))
          .features.find(item => item.id === feature.id) },
      { kind: "Task", id: task.id, name: task.name, fields: ["description", "assignee"],
        path: `/api/tasks/${task.id}`, method: "PUT", tool: "update_task",
        args: { taskId: task.id, name: task.name },
        read: () => callTool("get_task", { taskId: task.id }) },
      { kind: "Idea", id: idea.id, fields: ["body", "plan", "tags"],
        path: `/api/ideas/${idea.id}`, method: "PATCH", tool: "update_idea",
        args: { ideaId: idea.id }, read: () => callTool("get_idea", { ideaId: idea.id }) }
    ];

    for (const entity of entities) {
      await check(`${entity.kind}.create-empty-compatibility`, async () => {
        const stored = await restJson(entity.path);
        for (const field of entity.fields) assert.equal(stored[field], null, `${field} should start null`);
        if (entity.kind === "Task") {
          assert.equal(stored.status, "Backlog");
          assert.equal(stored.priority, "Low");
        }
      });
    }

    async function activity(entity) {
      const activityPath = entity.kind === "Idea" || entity.kind === "Task"
        ? `${entity.path}/activity?limit=500` : `/api/projects/${project.id}/activity?limit=500`;
      return (await restJson(activityPath)).filter(event => event.entityType === entity.kind && event.entityId === entity.id);
    }

    const cases = [
      { name: "populated-omitted", initial: "original", payload: {}, expected: "original" },
      { name: "populated-null", initial: "original", value: null, expected: "original" },
      { name: "populated-empty", initial: "original", value: "", expected: null },
      { name: "populated-whitespace", initial: "original", value: " \t\r\n ", expected: null },
      { name: "cleared-omitted", initial: "", payload: {}, expected: null },
      { name: "cleared-null", initial: "", value: null, expected: null },
      { name: "cleared-empty", initial: "", value: "", expected: null },
      { name: "nonempty-replace", initial: "original", value: "replacement", expected: "replacement" }
    ];

    for (const entity of entities) {
      for (const field of entity.fields) {
        // Independent controls run first. The native surface currently exposes update_idea,
        // but does not expose update_project, update_feature or update_task.
        const transports = entity.kind === "Idea" ? ["rest", "stdio", "native"] : ["rest", "stdio"];
        for (const transport of transports) {
          for (const testCase of cases) {
            const key = `${entity.kind}.${field}.${testCase.name}`;
            await check(`${transport}.${key}`, async () => {
              const restArgs = entity.name === undefined ? {} : { name: entity.name };
              await restJson(entity.path, {
                method: entity.method, body: { ...restArgs, [field]: testCase.initial }
              });
              const before = await restJson(entity.path);
              const priorEvents = new Set((await activity(entity)).map(event => event.id));
              const payload = "value" in testCase ? { [field]: testCase.value } : testCase.payload;
              if (transport === "rest") {
                await restJson(entity.path, { method: entity.method, body: { ...restArgs, ...payload } });
              } else if (transport === "stdio") {
                await callTool(entity.tool, { ...entity.args, ...payload });
              } else {
                await nativeToolCall(apiBaseUrl, timeoutMs, entity.tool, { ...entity.args, ...payload });
              }

              const after = await restJson(entity.path);
              const throughMcp = await entity.read();
              assert.equal(after[field], testCase.expected, "REST persisted field");
              assert.equal(throughMcp[field], testCase.expected, "stdio persisted field");
              for (const other of entity.fields.filter(item => item !== field)) {
                assert.equal(after[other], before[other], `omitted sibling ${other}`);
              }
              const changed = before[field] !== after[field];
              if (!changed) assert.equal(after.updatedAt, before.updatedAt, "no-op UpdatedAt");
              else assert.notEqual(after.updatedAt, before.updatedAt, "changed UpdatedAt");
              const events = (await activity(entity)).filter(event => !priorEvents.has(event.id));
              if (!changed) assert.equal(events.length, 0, "no-op audit count");
              else if (entity.kind === "Task" || entity.kind === "Idea") {
                assert.equal(events.length, 1, "exactly one update audit event");
              }
              // Project/feature auditing belongs to their receiving service. Compare with
              // REST rather than pinning a missing event as the permanent contract (#511).
              for (const event of events) {
                assert.equal(event.eventType, `${entity.kind}Updated`);
                assert.equal(JSON.parse(event.beforeJson)[field], before[field], "audited before value");
                assert.equal(JSON.parse(event.afterJson)[field], after[field], "audited after value");
              }
              const auditTypes = events.map(event => event.eventType).sort();
              if (transport === "rest") controls.set(key, auditTypes);
              else assert.deepEqual(auditTypes, controls.get(key), "REST/agent audit parity");
              return { auditEvents: events.length };
            });
          }
        }
      }
    }

    await check("list_ideas.empty-filter-compatibility", async () => {
      const ordinary = await callTool("list_ideas", { limit: 500 });
      const empty = await callTool("list_ideas", { status: "", tag: "", q: "", limit: 500 });
      assert.deepEqual(empty, ordinary, "empty filters must retain unfiltered results");
    });
    await check("release_task.empty-actor-default", async () => {
      await callTool("claim_task", { taskId: task.id, actor: "mcp-agent" });
      await callTool("release_task", { taskId: task.id, actor: "" });
      assert.equal((await restJson(`/api/tasks/${task.id}`)).claimedBy, null);
      const released = (await activity(entities[2])).filter(event => event.eventType === "TaskReleased");
      assert.equal(released.length, 1);
      assert.equal(released[0].actor, "mcp-agent");
    });

    for (const [tool, args, fields] of [
      ["create_task", { featureId: feature.id, name: `${marker} invalid task` }, ["status", "priority"]],
      ["update_task", { taskId: task.id, name: task.name }, ["status", "priority"]],
      ["create_idea", { title: `${marker} invalid idea` }, ["priority"]],
      ["update_idea", { ideaId: idea.id }, ["status", "priority"]]
    ]) {
      for (const field of fields) {
        for (const value of ["", " \t "]) {
          await check(`${tool}.${field}.${value === "" ? "empty" : "whitespace"}-rejected`, async () => {
            const beforeTask = await restJson(`/api/tasks/${task.id}`);
            const beforeIdea = await restJson(`/api/ideas/${idea.id}`);
            await assert.rejects(callTool(tool, { ...args, [field]: value }), error => error.code === -32602);
            assert.deepEqual(await restJson(`/api/tasks/${task.id}`), beforeTask, "invalid enum mutated task");
            assert.deepEqual(await restJson(`/api/ideas/${idea.id}`), beforeIdea, "invalid enum mutated idea");
          });
        }
      }
    }
  } finally {
    for (const resource of cleanup.reverse()) {
      await check(`cleanup.${resource}`, () => restJson(resource, { method: "DELETE", expectNoContent: true }));
    }
  }
  console.log(`Nullable-text transport checks: ${passed} passed, ${failures.length} failed.`);
  if (failures.length > 0) throw new AggregateError(failures.map(message => new Error(message)),
    `Nullable-text transport checks failed (${failures.length}); see every failed scenario above.`);
}

async function nativeToolCall(apiBaseUrl, timeoutMs, name, args) {
  const requestId = 462;
  const response = await fetch(`${apiBaseUrl}/mcp`, {
    method: "POST",
    headers: {
      "Content-Type": "application/json", Accept: "application/json, text/event-stream",
      "MCP-Protocol-Version": "2025-06-18"
    },
    body: JSON.stringify({ jsonrpc: "2.0", id: requestId, method: "tools/call", params: { name, arguments: args } }),
    signal: AbortSignal.timeout(timeoutMs)
  });
  const body = await response.text();
  assert.equal(response.ok, true, `Native MCP HTTP ${response.status}: ${body}`);
  const messages = response.headers.get("content-type")?.includes("text/event-stream")
    ? body.split(/\r?\n\r?\n/).map(event => event.split(/\r?\n/)
      .filter(line => line.startsWith("data:")).map(line => line.slice(5).trimStart()).join("\n"))
      .filter(Boolean).map(data => JSON.parse(data))
    : [JSON.parse(body)];
  const message = messages.find(item => item.id === requestId);
  assert.ok(message, "Native MCP matching JSON-RPC response");
  assert.equal(message.error, undefined, JSON.stringify(message.error));
  assert.ok(message.result && !message.result.isError, JSON.stringify(message.result));
}
