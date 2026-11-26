import assert from "node:assert/strict";
import { execFile } from "node:child_process";
import { once } from "node:events";
import { createServer } from "node:http";
import test from "node:test";
import { fileURLToPath } from "node:url";
import { promisify } from "node:util";

const execFileAsync = promisify(execFile);
const smokePath = fileURLToPath(new URL("./smoke-mcp.mjs", import.meta.url));

async function runSelection(projects, ...args) {
  const requests = [];
  const api = createServer((req, res) => {
    requests.push(`${req.method} ${req.url}`);
    res.setHeader("Content-Type", "application/json");
    if (req.method === "GET" && req.url === "/api/projects") {
      res.end(JSON.stringify(projects));
      return;
    }
    // Stop the successful selection at its first project read. The real smoke suite
    // continues into fixture writes after this, which this adapter never accepts.
    res.statusCode = 409;
    res.end(JSON.stringify({ message: "Selection fixture stops after project discovery." }));
  });
  api.listen(0, "127.0.0.1");
  await once(api, "listening");

  let stderr;
  try {
    await assert.rejects(
      execFileAsync(process.execPath, [smokePath, ...args], {
        env: { ...process.env, KANBANFLOW_API_URL: `http://127.0.0.1:${api.address().port}` },
        timeout: 15000,
        windowsHide: true,
      }),
      (error) => {
        assert.equal(error.code, 1, error.message);
        stderr = error.stderr;
        return true;
      },
    );
  } finally {
    await new Promise((resolve) => api.close(resolve));
  }
  return { requests, stderr };
}

test("MCP smoke uses the lowest available ID before any fixture writes", async () => {
  const { requests } = await runSelection([{ id: 9 }, { id: 2 }, { id: 4 }]);
  assert.deepEqual(requests, ["GET /api/projects", "GET /api/projects/2"]);
});

test("MCP smoke honors an explicit project ID", async () => {
  const { requests } = await runSelection([{ id: 2 }, { id: 9 }], "9");
  assert.deepEqual(requests, ["GET /api/projects", "GET /api/projects/9"]);
});

test("MCP smoke rejects an absent explicit project without writing", async () => {
  const { requests, stderr } = await runSelection([{ id: 2 }], "99");
  assert.deepEqual(requests, ["GET /api/projects"]);
  assert.match(stderr, /list_projects did not include project 99/);
});

test("MCP smoke stops on an empty board before writing", async () => {
  const { requests, stderr } = await runSelection([]);
  assert.deepEqual(requests, ["GET /api/projects"]);
  assert.match(stderr, /board has no projects; run npm run gh:onboard/);
});
