import assert from 'node:assert/strict';
import test from 'node:test';
import { openWorkspaceStdio } from './workspace-transport.mjs';

test('actual stdio client lists tools, closes repeatedly and rejects later calls immediately', { timeout: 10000 }, async () => {
  const client = await openWorkspaceStdio('http://127.0.0.1:1');
  try {
    const list = await client.rpc('tools/list', {});
    assert.equal(list.error, undefined);
    assert.ok(list.result.tools.some(tool => tool.name === 'get_workspace_context'));
    await client.close();
    await assert.rejects(client.rpc('tools/list', {}), /not accepting calls/);
    await client.close();
  } finally { await client.close(); }
});

test('actual stdio requests settle when their owned process is closed during a call', { timeout: 10000 }, async () => {
  const client = await openWorkspaceStdio('http://127.0.0.1:1');
  const request = client.rpc('tools/list', {});
  const settledRequest = request.then(value => ({ value }), error => ({ error }));
  try {
    await client.close();
    const result = await settledRequest;
    if (result.error) assert.match(result.error.message, /exited|closed|EPIPE|write/i);
    else assert.ok(Array.isArray(result.value.result.tools));
    await assert.rejects(client.rpc('tools/list', {}), /not accepting calls/);
  } finally { await client.close(); }
});
