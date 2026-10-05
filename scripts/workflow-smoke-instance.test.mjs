import assert from 'node:assert/strict';
import { link, mkdir, readFile, realpath, rm, unlink, writeFile } from 'node:fs/promises';
import { randomUUID } from 'node:crypto';
import { spawnSync } from 'node:child_process';
import { fileURLToPath, pathToFileURL } from 'node:url';
import path from 'node:path';
import test from 'node:test';
import { claimSmokeRun, prepareSmokeInstance, recordSmokeRun, smokeBase, validateSmokeInstance } from './workflow-smoke-instance.mjs';

const root = fileURLToPath(new URL('../', import.meta.url));
const base = 'http://127.0.0.1:5126';

async function cleanupOwned(directory) {
  const owned = await realpath(path.join(root, '_sandbox'));
  const resolved = await realpath(directory);
  assert.ok(resolved.startsWith(owned + path.sep), 'cleanup must stay within repository scratch space');
  await rm(resolved, { recursive: true, force: true });
}

async function fixture(t, realRepository = false) {
  const testRoot = realRepository ? root : path.join(root, '_sandbox', `smoke-guard-${randomUUID()}`);
  await mkdir(testRoot, { recursive: true });
  const run = await prepareSmokeInstance(base, testRoot);
  t.after(() => cleanupOwned(realRepository ? path.dirname(run.manifestPath) : testRoot));
  await writeFile(run.databasePath, 'Disposable SQLite stand-in for the client identity contract');
  const health = { app: 'KanbanFlow', startedAtUtc: new Date(Date.parse(run.createdAtUtc) + 10).toISOString(),
    db: { path: run.databasePath, source: 'env', exists: true, existedAtStartup: false, status: 'ok' } };
  const calls = [];
  const fetchImpl = async (url, options) => {
    calls.push({ url, ...options });
    assert.equal(options.method, 'GET');
    assert.equal(options.redirect, 'error');
    return { ok: true, json: async () => health };
  };
  return { run, health, calls, options: { root: testRoot, fetchImpl } };
}

for (const value of [undefined, '', 'not a url', 'https://example.com:5126', 'http://127.0.0.1:5123',
  'http://localhost', 'ftp://127.0.0.1:5126', 'http://user:password@127.0.0.1:5126',
  'http://127.0.0.1:5126/api', 'http://127.0.0.1:5126/?query=1', 'http://127.0.0.1:5126/#fragment']) {
  test(`smoke preflight rejects unsafe or malformed origin ${JSON.stringify(value)}`, async () => {
    let requests = 0;
    await assert.rejects(validateSmokeInstance(value, undefined, { fetchImpl: async () => { requests++; } }));
    assert.equal(requests, 0);
  });
}

test('smoke identity requires a manifest even on a non-default loopback port', async () => {
  await assert.rejects(validateSmokeInstance(base, undefined), /--instance/);
  assert.equal(smokeBase(base + '/'), base);
});

for (const change of ['database', 'source', 'existing', 'unreadable', 'old-process']) {
  test(`smoke identity refuses ${change} before writes`, async t => {
    const f = await fixture(t);
    if (change === 'database') f.health.db.path = path.join(root, 'kanbanflow.db');
    if (change === 'source') f.health.db.source = 'default';
    if (change === 'existing') f.health.db.existedAtStartup = true;
    if (change === 'unreadable') f.health.db.status = 'unreachable';
    if (change === 'old-process') f.health.startedAtUtc = '2000-01-01T00:00:00Z';
    await assert.rejects(validateSmokeInstance(base, f.run.manifestPath, f.options), /identity mismatch/);
    assert.equal(f.calls.length, 1);
    assert.equal(f.calls[0].method, 'GET');
  });
}

test('smoke identity checks the owned path before calling even the health endpoint', async t => {
  const f = await fixture(t);
  await writeFile(f.run.manifestPath, JSON.stringify({ ...f.run, databasePath: path.join(root, 'kanbanflow.db') }));
  await assert.rejects(validateSmokeInstance(base, f.run.manifestPath, f.options), /owned disposable database/);
  assert.equal(f.calls.length, 0);
});

test('smoke identity rejects a hard-linked database', async t => {
  const f = await fixture(t);
  const original = path.join(path.dirname(f.run.databasePath), 'original.db');
  await writeFile(original, 'Existing data must not be used as a disposable database');
  await unlink(f.run.databasePath);
  await link(original, f.run.databasePath);
  await assert.rejects(validateSmokeInstance(base, f.run.manifestPath, f.options), /hard links/);
  assert.equal(f.calls.length, 0);
});

test('verified instance is single-use and retains successful, failed or interrupted evidence', async t => {
  const f = await fixture(t);
  const run = await validateSmokeInstance(base, f.run.manifestPath, f.options);
  assert.equal(run.databasePath, f.run.databasePath);
  await claimSmokeRun(run);
  await assert.rejects(claimSmokeRun(run), { code: 'EEXIST' });
  for (const state of ['passed', 'failed', 'interrupted']) {
    await recordSmokeRun(run, state, { result: { projectId: 7 }, failure: state === 'failed' ? 'Exact failed check' : undefined });
    const recorded = JSON.parse(await readFile(run.manifestPath, 'utf8'));
    assert.equal(recorded.state, state);
    assert.equal(recorded.result.projectId, 7);
    assert.equal(recorded.databasePath, run.databasePath);
    assert.ok((await readFile(run.databasePath)).length > 0, 'recording must not clean up the database');
    await assert.rejects(validateSmokeInstance(base, run.manifestPath, f.options), /not a prepared run/);
  }
});

test('CLI rejects the wrong instance before starting its write-capable wrapper', async t => {
  const f = await fixture(t, true);
  const directory = path.dirname(f.run.manifestPath);
  const trace = path.join(directory, 'calls.jsonl');
  const hook = path.join(directory, 'observe.mjs');
  const health = { ...f.health, db: { ...f.health.db, path: path.join(root, 'kanbanflow.db') } };
  await writeFile(hook, `
    import childProcess from 'node:child_process';
    import { syncBuiltinESMExports } from 'node:module';
    import { appendFileSync } from 'node:fs';
    const trace = ${JSON.stringify(trace)};
    const log = value => appendFileSync(trace, JSON.stringify(value) + '\\n');
    childProcess.spawn = () => { log({ operation: 'spawn' }); throw new Error('Instrumented wrapper must not start'); };
    syncBuiltinESMExports();
    globalThis.fetch = async (url, options) => { log({ operation: options.method, url }); return { ok: true, json: async () => (${JSON.stringify(health)}) }; };
  `);
  for (const target of [base, 'http://127.0.0.1:5123', 'https://example.com:5126', 'malformed']) {
    await writeFile(trace, '');
    const child = spawnSync(process.execPath, ['--import', pathToFileURL(hook).href, path.join(root, 'scripts/smoke-workflow.mjs'), target, '--instance', f.run.manifestPath],
      { encoding: 'utf8', timeout: 10000 });
    assert.notEqual(child.status, 0);
    assert.equal(child.error, undefined);
    const operations = (await readFile(trace, 'utf8')).trim().split('\n').filter(Boolean).map(line => JSON.parse(line));
    assert.deepEqual(operations.map(op => op.operation), target === base ? ['GET'] : [], child.stderr);
    assert.ok(operations.every(op => op.url.endsWith('/api/admin/health')));
  }
});

test('CLI proves the accepted instance before wrapper startup and records a startup failure', async t => {
  const f = await fixture(t, true);
  const directory = path.dirname(f.run.manifestPath);
  const trace = path.join(directory, 'startup.jsonl');
  const hook = path.join(directory, 'startup.mjs');
  await writeFile(hook, `
    import childProcess from 'node:child_process';
    import { syncBuiltinESMExports } from 'node:module';
    import { appendFileSync } from 'node:fs';
    const log = value => appendFileSync(${JSON.stringify(trace)}, JSON.stringify(value) + '\\n');
    childProcess.spawn = () => { log('spawn'); throw new Error('Injected startup failure; no wrapper launched'); };
    syncBuiltinESMExports();
    globalThis.fetch = async (url, options) => { log(options.method); return { ok: true, json: async () => (${JSON.stringify(f.health)}) }; };
  `);
  const child = spawnSync(process.execPath, ['--import', pathToFileURL(hook).href, path.join(root, 'scripts/smoke-workflow.mjs'), base, '--instance', f.run.manifestPath],
    { windowsHide: true, encoding: 'utf8', timeout: 10000 });
  assert.notEqual(child.status, 0);
  assert.equal(child.error, undefined);
  assert.deepEqual((await readFile(trace, 'utf8')).trim().split('\n').map(JSON.parse), ['GET', 'spawn']);
  const recorded = JSON.parse(await readFile(f.run.manifestPath, 'utf8'));
  assert.equal(recorded.state, 'failed');
  assert.match(recorded.failure, /Injected startup failure/);
  assert.ok((await readFile(f.run.databasePath)).length > 0);
});

for (const mode of ['error', 'invalid-json', 'exit', 'interrupt']) {
  test(`CLI records ${mode} from the asynchronous wrapper lifecycle`, async t => {
    const f = await fixture(t, true);
    const hook = path.join(path.dirname(f.run.manifestPath), 'lifecycle.mjs');
    await writeFile(hook, `
      import childProcess from 'node:child_process';
      import { syncBuiltinESMExports } from 'node:module';
      import { EventEmitter } from 'node:events';
      import { PassThrough, Writable } from 'node:stream';
      childProcess.spawn = () => {
        const child = new EventEmitter();
        child.stdout = new PassThrough();
        child.stdin = new Writable({ write(chunk, encoding, callback) {
          callback();
          setImmediate(() => {
            const mode = ${JSON.stringify(mode)};
            if (mode === 'error') child.emit('error', new Error('Injected async ENOENT'));
            if (mode === 'invalid-json') child.stdout.write('not a JSON response\\n');
            if (mode === 'exit') child.emit('exit', 2, null);
            if (mode === 'interrupt') process.emit('SIGINT');
          });
        } });
        child.kill = () => { child.stdout.end(); child.emit('exit', null, 'SIGTERM'); return true; };
        return child;
      };
      syncBuiltinESMExports();
      globalThis.fetch = async (url, options) => {
        if (options.method !== 'GET') throw new Error('No application mutations are permitted in this fixture');
        return { ok: true, json: async () => (${JSON.stringify(f.health)}) };
      };
    `);
    const child = spawnSync(process.execPath, ['--import', pathToFileURL(hook).href, path.join(root, 'scripts/smoke-workflow.mjs'), base, '--instance', f.run.manifestPath],
      { windowsHide: true, encoding: 'utf8', timeout: 10000 });
    assert.notEqual(child.status, 0);
    assert.equal(child.error, undefined);
    const recorded = JSON.parse(await readFile(f.run.manifestPath, 'utf8'));
    assert.equal(recorded.state, mode === 'interrupt' ? 'interrupted' : 'failed', child.stderr);
    if (mode === 'interrupt') assert.equal(recorded.signal, 'SIGINT');
    else assert.match(recorded.failure, mode === 'error' ? /async ENOENT/ : mode === 'exit' ? /exited before completion/ : /Invalid workflow wrapper JSON/);
    assert.ok((await readFile(f.run.databasePath)).length > 0);
  });
}
