import assert from 'node:assert/strict';
import { openWorkspaceStdio, workspaceNativePacket } from './workspace-transport.mjs';

const argument = process.argv[2];
assert.ok(argument, 'Pass an explicit running disposable app URL.');
const address = new URL(argument);
assert.ok(['http:', 'https:'].includes(address.protocol) && !address.username && !address.password,
  'Use an HTTP(S) app URL without embedded credentials.');
const base = address.href.replace(/\/$/, '');
const response = await fetch(base + '/openapi/v1.json', { signal: AbortSignal.timeout(20000) });
assert.equal(response.status, 200, 'Read the actual app OpenAPI document.');
const document = await response.json();
const methods = new Set(['get', 'post', 'put', 'patch', 'delete', 'head', 'options', 'trace']);
let application = 0, admin = 0;
for (const [path, operations] of Object.entries(document.paths)) {
  if (!path.startsWith('/api/')) continue;
  const count = Object.keys(operations).filter(method => methods.has(method)).length;
  if (path.startsWith('/api/admin/')) admin += count;
  else application += count;
}
await workspaceNativePacket(base, 'initialize', { protocolVersion: '2025-06-18', capabilities: {}, clientInfo: { name: 'surface-inventory', version: '1' } });
const native = await workspaceNativePacket(base, 'tools/list', {});
assert.equal(native.error, undefined);
assert.ok(Array.isArray(native.result.tools));
const stdio = await openWorkspaceStdio(base);
try {
  const wrapper = await stdio.rpc('tools/list', {});
  assert.equal(wrapper.error, undefined);
  assert.ok(Array.isArray(wrapper.result.tools));
  console.log(JSON.stringify({ openApiDocument: '/openapi/v1.json', rest: { application, admin, total: application + admin },
    nativeMcpTools: native.result.tools.length, stdioMcpTools: wrapper.result.tools.length }, null, 2));
} finally { await stdio.close(); }
